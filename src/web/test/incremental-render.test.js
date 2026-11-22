// CanvasRenderer redraws only rows that changed, copies rows that only moved and shifts the canvas
// when Show Empty Rows recenters. Each test feeds two ladders the same book: one renders
// incrementally, the other redraws in full, and their screens must match every frame.
// Mirrors tests/SlickUI.Core.Tests/IncrementalRenderTests.cs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { PriceLadder, Side, OrderUpdateType, fakeContainer, frame, render, trackScreens, screenRows, resizeContainers } =
    require('../test-utils/ladder-harness');
const { Feed, seededRandom, TICK_SIZE } = require('../test-utils/random-feed');

trackScreens();

const HEIGHT = 600;
const ROWS = Math.ceil(HEIGHT / 24);
const FRAME_MS = 1000 / 60;

const create = (config) => new PriceLadder({ container: fakeContainer(400), height: HEIGHT, tickSize: TICK_SIZE, ...config });

// Counts what the incremental ladder did, so a pass means those paths ran
function spy(renderer) {
    const counts = { full: 0, moved: 0, shifted: 0, work: 0, ltq: 0, merged: 0 };
    for (const [name, key] of [['renderFull', 'full'], ['moveRows', 'moved']]) {
        const original = renderer[name].bind(renderer);
        renderer[name] = (...args) => { counts[key]++; return original(...args); };
    }
    const tryShiftRows = renderer.tryShiftRows.bind(renderer);
    renderer.tryShiftRows = (referencePrice) => {
        const moved = referencePrice !== renderer.lastReferencePrice;
        const shifted = tryShiftRows(referencePrice);
        if (moved && shifted && renderer.removalMode === 'showEmpty') counts.shifted++;
        return shifted;
    };
    return counts;
}

// Working orders and a trade near the mid tick, so Work and LTQ cells come and go, move rows and
// resize their columns
function workAndTrades(ladder, i, mid) {
    if (i % 5 === 0) {
        const side = i % 2 ? Side.BID : Side.ASK;
        const tick = side === Side.BID ? mid - 1 - (i % 4) : mid + 1 + (i % 4);
        const quantity = i % 15 === 0 ? 0 : 10 ** (i % 6);
        ladder.setWorkingOrders(tick * TICK_SIZE, side, quantity, 1 + (i % 3));
    }
    if (i % 3 === 0) {
        const aggressor = [Side.BID, Side.ASK, null][i % 9 / 3];
        ladder.processTrade({ price: (mid + (i % 7) - 3) * TICK_SIZE, quantity: 1 + (i * 37) % 2000, aggressor });
    }
}

// Random MBO orders 15 ticks either side of 10000, plus working orders and trades. Each ladder gets
// the same orders from its own seeded book
function randomOrders() {
    const books = new Map();
    return (ladder, i) => {
        if (!books.has(ladder)) books.set(ladder, { random: seededRandom(11), orders: new Map(), nextId: 1 });
        const book = books.get(ladder);
        const int = (min, max) => min + Math.floor(book.random() * (max - min + 1));
        for (let n = int(0, 6); n > 0; n--) {
            const ids = [...book.orders.keys()];
            const op = ids.length < 40 ? 0 : int(0, 2);
            if (op === 0) {
                const side = book.random() < 0.5 ? Side.BID : Side.ASK;
                const tick = side === Side.BID ? 10000 - int(0, 15) : 10001 + int(0, 15);
                const order = { orderId: book.nextId++, side, price: tick * TICK_SIZE, quantity: int(1, 500),
                    priority: book.nextId, isOwnOrder: book.random() < 0.1 };
                book.orders.set(order.orderId, order);
                ladder.processOrderUpdate(order, OrderUpdateType.Add);
            } else {
                const order = book.orders.get(ids[int(0, ids.length - 1)]);
                if (op === 1) {
                    order.quantity = int(1, 500);
                    ladder.processOrderUpdate({ ...order }, OrderUpdateType.Modify);
                } else {
                    book.orders.delete(order.orderId);
                    ladder.processOrderUpdate({ ...order }, OrderUpdateType.Delete);
                }
            }
        }
        workAndTrades(ladder, i, 10000);
    };
}

// Runs `frames` frames: step(ladder, frame) changes both ladders' books the same way, then the
// incremental ladder must show what a full redraw of the same book shows
function assertMatchesFullRedraw(config, frames, step) {
    const ladder = create(config);
    const reference = create(config);
    const counts = spy(ladder.renderer);
    for (let i = 0; i < frames; i++) {
        step(ladder, i);
        step(reference, i);
        frame(ladder);
        reference.renderer.needsFullRedraw = true;
        frame(reference);
        assert.deepEqual(screenRows(ladder.canvas, ROWS), screenRows(reference.canvas, ROWS), `frame ${i}`);
        // Frames with Work or LTQ cells on screen
        counts.work += ladder.renderer.rows.some((row) => row.workCount > 0) ? 1 : 0;
        counts.ltq += ladder.renderer.rows.some((row) => row.ltqQuantity > 0) ? 1 : 0;
        counts.merged += ladder.renderer.rows.some((row) => row.bid.breaks || row.ask.breaks) ? 1 : 0; // Aggregated MBO rows of several levels
    }
    assert.ok(counts.full < frames / 2, `${counts.full} of ${frames} frames were full redraws`);
    return counts;
}

for (const removalMode of ['removeRow', 'showEmpty']) {
    for (const [showOrderCount, showHeader] of [[true, true], [false, false]]) {
        // Header off also hides the Work and LTQ columns: the layout before they existed
        const config = { removalMode, showOrderCount, showHeader, showWorkColumn: showHeader, showLtqColumn: showHeader };
        const name = `${removalMode}, order count ${showOrderCount}, header ${showHeader}`;

        // Levels come and go, the mid walks, and own orders get flagged: rows move (Remove Rows)
        // or the canvas shifts (Show Empty Rows), and bars rescale. The second height cuts off
        // the bottom row
        for (const height of [HEIGHT, HEIGHT - 10]) {
            test(`${name}, height ${height}: random feed matches a full redraw every frame`, () => {
                const feeds = new Map();
                const counts = assertMatchesFullRedraw({ ...config, height }, 300, (ladder, i) => {
                    if (!feeds.has(ladder)) {
                        feeds.set(ladder, new Feed(7));
                        ladder.processBatch(feeds.get(ladder).seed());
                    }
                    const feed = feeds.get(ladder);
                    for (const batch of feed.advance((i + 1) * FRAME_MS * 4)) ladder.processBatch(batch);
                    if (i % 7 === 0) {
                        // Own-order border on a quoted level near the mid
                        const side = i % 2 ? Side.BID : Side.ASK;
                        const tick = side === Side.BID ? feed.mid - 1 - (i % 5) : feed.mid + 1 + (i % 5);
                        ladder.setHasOwnOrders(tick * TICK_SIZE, side, i % 3 !== 0);
                    }
                    workAndTrades(ladder, i, feed.mid);
                });
                assert.ok(removalMode === 'removeRow' ? counts.moved > 0 : counts.shifted > 0, JSON.stringify(counts));
                assert.ok(!showHeader || (counts.work > 30 && counts.ltq > 30), JSON.stringify(counts));
            });
        }

        test(`${name}: random MBO orders match a full redraw every frame`, () => {
            const counts = assertMatchesFullRedraw({ ...config, mode: 'MBO' }, 200, randomOrders());
            assert.ok(!showHeader || (counts.work > 30 && counts.ltq > 30), JSON.stringify(counts));
        });

        // Aggregated, a row shows the orders of every level in its bucket, with a gap between levels
        test(`${name}: random MBO orders on aggregated rows match a full redraw every frame`, () => {
            const counts = assertMatchesFullRedraw({ ...config, mode: 'MBO', displayTickSize: 5 * TICK_SIZE }, 200, randomOrders());
            assert.ok(counts.merged > 150, JSON.stringify(counts));
        });
    }
}

// Bug: Remove Rows filled whole rows only, so a row cut off at the bottom stayed blank. Show
// Empty Rows and desktop draw the part that fits
test('a row cut off at the bottom shows its level', () => {
    for (const removalMode of ['removeRow', 'showEmpty']) {
        const ladder = create({ removalMode, height: 10 * 24 + 10 });
        ladder.processBatch(new Feed(3).seed());
        frame(ladder);
        const { renderer } = ladder;
        const last = renderer.rowCount() - 1;
        assert.ok(renderer.rowTop(last) + renderer.rowHeight > renderer.height, 'last row is cut off');
        const cutOff = screenRows(ladder.canvas, 11)[renderer.rowTop(last) / renderer.rowHeight];
        assert.ok(cutOff.some((item) => item.includes(' text ')), `${removalMode}: ${cutOff}`);
        assert.notEqual(renderer.rowToLevelInfo(last), null, `${removalMode}: click on it`);
    }
});

test('idle frames skip the snapshot and the render', () => {
    const ladder = create();
    ladder.processUpdate({ side: Side.BID, price: 100.00, quantity: 10, numOrders: 1 });
    ladder.processUpdate({ side: Side.ASK, price: 100.01, quantity: 10, numOrders: 1 });
    let snapshots = 0;
    let frames = 0;
    const getSnapshot = ladder.getSnapshot.bind(ladder);
    ladder.getSnapshot = () => { snapshots++; return getSnapshot(); };
    ladder.renderer.recordFrame = () => { frames++; };

    frame(ladder);
    assert.equal(snapshots, 1, 'first frame');
    frame(ladder);
    frame(ladder);
    assert.equal(snapshots, 1, 'idle frames');

    ladder.processUpdate({ side: Side.BID, price: 100.00, quantity: 20, numOrders: 1 });
    frame(ladder);
    assert.equal(snapshots, 2, 'book changed');

    ladder.processUpdate({ side: Side.BID, price: 99.50, quantity: 0, numOrders: 0 }); // No such level
    frame(ladder);
    assert.equal(snapshots, 2, 'removing a missing level changes nothing');

    ladder.renderer.adjustHorizontalScroll(10); // A view change with no render of its own
    frame(ladder);
    assert.equal(snapshots, 3, 'view changed');
    assert.equal(frames, 6, 'FPS counts skipped frames too');
});

// Bug: setHasOwnOrders flagged the level in place with no dirty change, so its row kept the old
// look until something else redrew it
test('setHasOwnOrders shows on the next frame', () => {
    for (const removalMode of ['removeRow', 'showEmpty']) {
        const ladder = create({ removalMode });
        ladder.processUpdate({ side: Side.BID, price: 100.00, quantity: 10, numOrders: 1 });
        ladder.processUpdate({ side: Side.ASK, price: 100.01, quantity: 10, numOrders: 1 });
        render(ladder);
        const borders = () => screenRows(ladder.canvas, ROWS).flat().filter((item) => item.includes('border')).length;

        ladder.setHasOwnOrders(100.00, Side.BID, true);
        render(ladder);
        assert.equal(borders(), removalMode === 'removeRow' ? 1 : 0, removalMode); // Show Empty Rows draws no border

        ladder.setHasOwnOrders(100.00, Side.BID, false);
        render(ladder);
        assert.equal(borders(), 0, removalMode);
    }
});

// Two orders trade size at one level: its total and order count stay the same, so only the
// orders show the change
test('MBO segments repaint when a level total stays the same', () => {
    for (const removalMode of ['removeRow', 'showEmpty']) {
        const add = (ladder) => {
            for (const [orderId, side, price, quantity] of [[1, Side.BID, 100.00, 100], [2, Side.BID, 100.00, 100],
                [3, Side.ASK, 100.01, 300]]) {
                ladder.processOrderUpdate({ orderId, side, price, quantity, priority: orderId }, OrderUpdateType.Add);
            }
        };
        const trade = (ladder) => {
            ladder.processOrderUpdate({ orderId: 1, side: Side.BID, price: 100.00, quantity: 150, priority: 1 }, OrderUpdateType.Modify);
            ladder.processOrderUpdate({ orderId: 2, side: Side.BID, price: 100.00, quantity: 50, priority: 2 }, OrderUpdateType.Modify);
        };
        let step = 0;
        assertMatchesFullRedraw({ removalMode, mode: 'MBO' }, 4, (ladder, i) => {
            if (i === 0) add(ladder);
            if (i === 2) trade(ladder);
            step = i;
        });
        assert.equal(step, 3);
    }
});

// Bug: a container resize set the canvas width itself, stretching the bar column to the
// container, but the renderer kept drawing at its old width and the rest of the canvas showed
// black. The ladder now keeps its own width (its columns) whatever the container does
test('a container resize leaves the canvas at the width the renderer draws', () => {
    for (const removalMode of ['removeRow', 'showEmpty']) {
        const container = fakeContainer(400);
        const ladder = new PriceLadder({ container, height: HEIGHT, tickSize: TICK_SIZE, removalMode });
        ladder.processBatch(new Feed(3).seed());
        frame(ladder);
        const { renderer } = ladder;
        const width = ladder.canvas.width;
        assert.equal(width, renderer.width, removalMode);
        assert.equal(width, renderer.calculateCanvasWidth(), `${removalMode}: its columns`);

        for (const clientWidth of [1200, 300]) {
            container.clientWidth = clientWidth;
            resizeContainers();
            frame(ladder);
            assert.equal(ladder.canvas.width, width, `${removalMode}: container ${clientWidth}`);
            assert.equal(renderer.width, width, `${removalMode}: container ${clientWidth}`);
        }
    }
});

// needsRender compares the view with the last render, like desktop NeedsRender, so the frame loop
// repaints a view change even when nothing rendered it (the public setters render on their own)
test('a view change with no render of its own repaints on the next frame', () => {
    const views = [
        ['removeRow', (renderer) => { renderer.scrollOffset = 3; }],
        ['showEmpty', (renderer) => { renderer.centerPrice = renderer.centerPrice + 5 * TICK_SIZE; }],
        ['showEmpty', (renderer) => {
            renderer.setCenterPrice(renderer.centerPrice + 5 * TICK_SIZE); // Renders
            renderer.autoCenter = true; // Doesn't: the next frame must re-center
        }]
    ];
    for (const [removalMode, change] of views) {
        const config = { removalMode, autoCenter: false };
        const [ladder, reference] = [create(config), create(config)];
        for (const l of [ladder, reference]) {
            l.processBatch(new Feed(3).seed());
            frame(l);
        }

        change(ladder.renderer);
        change(reference.renderer);
        frame(ladder);
        reference.renderer.needsFullRedraw = true;
        frame(reference);
        assert.deepEqual(screenRows(ladder.canvas, ROWS), screenRows(reference.canvas, ROWS), `${removalMode} ${change}`);
        assert.equal(ladder.renderer.centerPrice, reference.renderer.centerPrice);
        assert.equal(ladder.renderer.needsRender(), false, 'then idle');
    }
});

// The book-changed flag is cleared by rendering, not by taking a snapshot, so a snapshot taken
// outside the frame loop can't hide a change from it
test('a snapshot taken outside a frame does not swallow the next render', () => {
    const ladder = create();
    ladder.processBatch(new Feed(3).seed());
    frame(ladder);
    let renders = 0;
    const render = ladder.renderer.render.bind(ladder.renderer);
    ladder.renderer.render = (snapshot) => { renders++; render(snapshot); };

    ladder.processUpdate({ side: Side.BID, price: 50, quantity: 7, numOrders: 1 });
    ladder.getSnapshot();
    frame(ladder);
    assert.equal(renders, 1);
});

// Price labels are cached per display tick: a new tick size must not show the old labels
test('price labels follow a display tick change', () => {
    const ladder = create({ removalMode: 'showEmpty' });
    ladder.processBatch(new Feed(3).seed());
    frame(ladder);
    const labels = () => screenRows(ladder.canvas, ROWS).flat().filter((item) => item.includes(' text '))
        .map((item) => item.split(' ').pop()).filter((text) => /^\d+\.\d+$/.test(text));
    assert.ok(labels().every((text) => /\.\d\d$/.test(text)), labels().join());

    ladder.updateConfig({ displayTickSize: 0.1 });
    frame(ladder);
    assert.ok(labels().length > 0 && labels().every((text) => /\.\d$/.test(text)), labels().join());
});
