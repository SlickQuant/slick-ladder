// PriceLadder unit tests (node:test). Browser APIs are stubbed by the harness. Run: npm test (in src/web)
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { PriceLadder, Side, ClickButton, OrderUpdateType, fakeContainer, render } = require('../test-utils/ladder-harness');
const { Feed, OrderFeed, seededRandom, TICK_SIZE } = require('../test-utils/random-feed');

// Default book: bid 100.00 / ask 100.02 -> mid 100.01
function createLadder(config = {}, bid = 100.00, ask = 100.02) {
    const ladder = new PriceLadder({ container: fakeContainer(), ...config });
    ladder.processUpdate({ side: Side.BID, price: bid, quantity: 10, numOrders: 1 });
    ladder.processUpdate({ side: Side.ASK, price: ask, quantity: 10, numOrders: 1 });
    render(ladder);
    return ladder;
}

const event = (fields) => ({ preventDefault: () => {}, ...fields });
const assertNear = (actual, expected) => assert.ok(Math.abs(actual - expected) < 1e-9, `${actual} != ${expected}`);

// Bug: recreating the renderer (volume bars / order count toggle) reset removal mode to 'removeRow'
for (const toggle of ['setShowVolumeBars', 'setShowOrderCount']) {
    test(`${toggle} keeps removal mode set at runtime`, () => {
        const ladder = createLadder();
        ladder.setRemovalMode('showEmpty');
        ladder[toggle](false);
        assert.equal(ladder.renderer.getRemovalMode(), 'showEmpty');
    });

    test(`${toggle} keeps removal mode and auto-center from config`, () => {
        const ladder = createLadder({ removalMode: 'showEmpty', autoCenter: false });
        ladder[toggle](true);
        assert.equal(ladder.renderer.getRemovalMode(), 'showEmpty');
        assert.equal(ladder.renderer.getAutoCenter(), false);
    });

    // Bug: the new renderer also started at the default view position
    test(`${toggle} keeps the scroll position when auto-center is off`, () => {
        const ladder = createLadder({ autoCenter: false });
        ladder.renderer.setScrollOffset(5);
        ladder[toggle](false);
        assert.equal(ladder.renderer.getScrollOffset(), 5);

        ladder.setRemovalMode('showEmpty');
        ladder.renderer.setCenterPrice(105);
        ladder[toggle](true);
        assertNear(ladder.renderer.getCenterPrice(), 105);
    });
}

// Bug: formatQuantity built an Intl.NumberFormat per call, most of the frame time with many
// ladders (multi-ladder.perf.test.js). The cached formatters must give the same text
test('formatQuantity text is unchanged by cached formatters', () => {
    const cases = {
        1: [[0, '0'], [1234567, '1,234,567'], [0.5, '<1'], [12.5, '13']],
        0.01: [[1000000, '1,000,000'], [1234.567, '1234.57'], [2.5, '2.5'], [3.10, '3.1'], [0.005, '<0.01']],
        0.001: [[1.23456, '1.235'], [2.5, '2.5']] // Another precision alongside 0.01 in the shared cache
    };
    for (const [threshold, expected] of Object.entries(cases)) {
        const { renderer } = createLadder({ minQuantityThreshold: Number(threshold) });
        for (const [quantity, text] of expected) {
            assert.equal(renderer.formatQuantity(quantity), text, `${quantity} at threshold ${threshold}`);
        }
    }
});

// formatQuantity groups integer digits by hand, ~50x faster than Intl's format(). Same text
test('integer quantities read as Intl.NumberFormat writes them', () => {
    const { renderer } = createLadder();
    const intl = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
    const values = [0, 1, 12, 123, 999, 1000, 9999, 10000, 999999, 1000000, 2 ** 53, 1e20, 1e21, 1e22, -1, -1234567];
    const random = seededRandom(1);
    for (let i = 0; i < 3000; i++) {
        values.push(Math.floor(random() * 10 ** (1 + (i % 16))));
    }
    for (const value of values) {
        assert.equal(renderer.formatQuantity(value), intl.format(value), String(value));
    }
});

// getColumnPositions keeps its result until a column width or toggle changes. Wider quantity and
// price text must move the columns
test('column positions follow the column widths', () => {
    const ladder = createLadder();
    const { renderer } = ladder;
    const fresh = () => {
        const cached = renderer.getColumnPositions();
        renderer.columns = null;
        assert.deepEqual(cached, renderer.getColumnPositions());
        return cached;
    };
    const narrow = fresh();
    ladder.processUpdate({ side: Side.ASK, price: 100.02, quantity: 123456789, numOrders: 1 });
    render(ladder);
    assert.ok(fresh().askQtyX > narrow.askQtyX, 'quantity column widened');
    ladder.processUpdate({ side: Side.ASK, price: 1234567.89, quantity: 5, numOrders: 1 });
    render(ladder);
    assert.ok(fresh().askQtyX > narrow.askQtyX, 'price column widened');
    assert.ok(renderer.priceColWidth > 70);
});

// Bug: the price column fit the highest price anywhere in the book, so a full exchange book
// (Coinbase BTC-USD has asks out at 138,991,023.41) made it wide enough for prices never on
// screen. Now it fits the prices on screen and changes as the view scrolls or re-centers
for (const removalMode of ['removeRow', 'showEmpty']) {
    test(`${removalMode}: price column fits the prices on screen`, () => {
        const ladder = new PriceLadder({ container: fakeContainer(), removalMode, autoCenter: false });
        for (let i = 0; i < 30; i++) {
            ladder.processUpdate({ side: Side.BID, price: 64964.81 - i * 0.01, quantity: 1, numOrders: 1 });
            ladder.processUpdate({ side: Side.ASK, price: 64964.82 + i * 0.01, quantity: 1, numOrders: 1 });
        }
        ladder.processUpdate({ side: Side.ASK, price: 138991023.41, quantity: 1, numOrders: 1 });
        render(ladder);
        const { renderer } = ladder;
        const near = renderer.columnWidthFor('64964.82');
        assert.equal(renderer.priceColWidth, near);

        // Scrolled to the far ask, the column fits it. Re-centering narrows the column and canvas
        if (removalMode === 'removeRow') {
            renderer.setScrollOffset(-30);
        } else {
            renderer.setCenterPrice(138991023.41);
        }
        assert.equal(renderer.priceColWidth, renderer.columnWidthFor('138991023.41'));
        const wide = ladder.canvas.width;
        renderer.recenter();
        assert.equal(renderer.priceColWidth, near);
        assert.ok(ladder.canvas.width < wide, `canvas ${ladder.canvas.width} not narrower than ${wide}`);
    });

    // Bug: only the highest price was measured, starting from 0, so a book entirely below zero
    // (a calendar spread) kept the default width and "-1234.50" was clipped
    test(`${removalMode}: price column fits negative prices`, () => {
        const { renderer } = createLadder({ removalMode }, -1234.75, -1234.50);
        assert.equal(renderer.priceColWidth, renderer.columnWidthFor('-1234.50'));
        assert.ok(renderer.priceColWidth > renderer.columnWidthFor('0.00'), 'wider than the default');
    });
}

// Snapshots reuse sorted level arrays (updated as levels come and go, no per-frame sort) and MBO
// order maps updated only for changed levels. Both must hold the feed's book after every batch,
// and a snapshot's level list must stay as it was taken
for (const mode of ['PriceLevel', 'MBO']) {
    test(`${mode}: every snapshot holds the feed's book`, () => {
        const mbo = mode === 'MBO';
        const feed = mbo ? new OrderFeed(5) : new Feed(5);
        const ladder = new PriceLadder({ container: fakeContainer(400), height: 600, tickSize: TICK_SIZE, mode });
        const ingest = (batch) => mbo ? ladder.processOrderBatch(batch) : ladder.processBatch(batch);
        const ticks = (levels) => levels.map((level) => [Math.round(level.price / TICK_SIZE), level.quantity]);
        const ordersOf = (orderMap) => [...orderMap].map(([price, orders]) => [Math.round(price / TICK_SIZE),
            orders.map((order) => [order.orderId, order.quantity]).sort((a, b) => a[0] - b[0])]).sort((a, b) => a[0] - b[0]);

        ingest(feed.seed());
        let kept = null;
        for (let i = 0; i < 300; i++) {
            for (const batch of feed.advance((i + 1) * 50)) ingest(batch);
            const snapshot = ladder.getSnapshot();
            assert.deepEqual(ticks(snapshot.bids), [...feed.bids].sort((a, b) => a[0] - b[0]), `bids after batch ${i}`);
            assert.deepEqual(ticks(snapshot.asks), [...feed.asks].sort((a, b) => a[0] - b[0]), `asks after batch ${i}`);
            if (mbo) {
                for (const side of [Side.BID, Side.ASK]) {
                    const expected = new Map();
                    for (const order of feed.orders.values()) {
                        if (order.side === side) expected.set(order.price, [...(expected.get(order.price) ?? []), order]);
                    }
                    assert.deepEqual(ordersOf(side === Side.BID ? snapshot.bidOrders : snapshot.askOrders),
                        ordersOf(expected), `side ${side} orders after batch ${i}`);
                }
            }
            if (kept) {
                assert.deepEqual(kept.levels.map((level) => level.price), kept.prices, `snapshot ${i - 1} kept its levels`);
            }
            kept = { levels: snapshot.asks, prices: snapshot.asks.map((level) => level.price) };
        }
        assert.ok(feed.updateCount > 5000, `${feed.updateCount} updates`);
    });
}

// Bar widths on the offscreen canvas by row. A background fill wipes the rows it covers
function trackBars(renderer) {
    const { offscreenCtx: ctx, colors, rowHeight } = renderer;
    const screen = new Map();
    ctx.fillRect = function (x, y, width, height) {
        const row = Math.floor(y / rowHeight);
        if (this.fillStyle === colors.background) {
            for (let r = row; r * rowHeight < y + height; r++) screen.delete(r);
        } else if (this.fillStyle === colors.bidBar || this.fillStyle === colors.askBar) {
            screen.set(row, [...(screen.get(row) ?? []), width]);
        }
    };
    return screen;
}

// Tracks bars from a full repaint on (the first render may resize, which swaps the context)
function renderTracked(ladder) {
    render(ladder);
    const screen = trackBars(ladder.renderer);
    ladder.renderer.needsFullRedraw = true;
    render(ladder);
    return screen;
}

// [side, price, quantity] levels, or [side, price, quantity, orderId] orders in MBO mode
function apply(ladder, rows, type = OrderUpdateType.Add) {
    for (const [side, price, quantity, orderId] of rows) {
        if (orderId === undefined) {
            ladder.processUpdate({ side, price, quantity, numOrders: 1 });
        } else {
            ladder.processOrderUpdate({ orderId, side, price, quantity, priority: orderId }, type);
        }
    }
}

// Bars after `update` repaints the ladder incrementally, and after a fresh ladder draws the same book in full
function barsAfterUpdate(config, book, update, type) {
    const create = () => new PriceLadder({ container: fakeContainer(400), height: 600, tickSize: 0.01, ...config });
    const ladder = create();
    apply(ladder, book);
    const screen = renderTracked(ladder);
    apply(ladder, update, type);
    render(ladder);

    const fresh = create();
    apply(fresh, book);
    apply(fresh, update, type);
    const expected = renderTracked(fresh);
    assert.equal(expected.size, new Set(book.map(([, price]) => price)).size, 'every level has bars');
    return { screen, expected };
}

const BOOK = [[Side.BID, 100.00, 10], [Side.BID, 99.99, 20], [Side.ASK, 100.01, 10], [Side.ASK, 100.02, 20]];
// Two 100-lots make 100.02 the largest level, so one order can rescale segments without a new largest level
const ORDERS = [[Side.BID, 100.00, 10, 1], [Side.BID, 99.99, 20, 2], [Side.ASK, 100.01, 10, 3],
    [Side.ASK, 100.02, 100, 4], [Side.ASK, 100.02, 100, 5]];

for (const removalMode of ['showEmpty', 'removeRow']) {
    // Bug: bars scale with the largest level, but only rows with a dirty change were repainted,
    // so the other rows kept bars at the old scale
    test(`${removalMode}: every volume bar rescales when the largest level changes`, () => {
        const { screen, expected } = barsAfterUpdate({ removalMode }, BOOK, [[Side.BID, 99.99, 200]]);
        assert.deepEqual(screen, expected);
    });

    // Bug: same for MBO segments, which scale with the largest order
    test(`${removalMode}: every MBO segment rescales when the largest order changes`, () => {
        const { screen, expected } = barsAfterUpdate({ removalMode, mode: 'MBO' }, ORDERS,
            [[Side.BID, 99.99, 150, 2]], OrderUpdateType.Modify);
        assert.deepEqual(screen, expected);
    });

    // Bug: an MBO modify recorded no dirty change, so its row was never repainted
    test(`${removalMode}: a modified MBO order repaints its row`, () => {
        const { screen, expected } = barsAfterUpdate({ removalMode, mode: 'MBO' }, ORDERS,
            [[Side.BID, 100.00, 12, 1]], OrderUpdateType.Modify);
        assert.deepEqual(screen, expected);
    });
}

// Bug: Show Empty Rows kept one level per row (looked up by formatted price), so a bid and an
// ask on the same row showed only the bid, and a second price rounding to a row was dropped.
// Desktop draws them all
test('showEmpty: every level that maps to a row is drawn', () => {
    const locked = createLadder({ removalMode: 'showEmpty', height: 600 }, 100.00, 100.00);
    const lockedBars = renderTracked(locked);
    assert.deepEqual([...lockedBars.values()].filter((widths) => widths.length === 2).length, 1, 'bid and ask on 100.00');

    // Prices off the 0.01 grid, as a WASM snapshot can carry: 100.012 shares 100.01's row
    const { renderer } = createLadder({ removalMode: 'showEmpty', height: 600 });
    const level = (price, quantity) => ({ price, quantity, numOrders: 1, side: Side.ASK, isDirty: false, hasOwnOrders: false });
    const snapshot = { bestBid: null, bestAsk: 100.01, midPrice: 100.01, bids: [], asks: [level(100.01, 10), level(100.012, 20)], timestamp: 0 };
    renderer.render(snapshot);
    const screen = trackBars(renderer);
    renderer.needsFullRedraw = true;
    renderer.render(snapshot);
    assert.deepEqual([...screen.values()].map((widths) => widths.length), [2]);
});

// Show Empty reads ascending levels only around the rows' prices. A deep book with off-grid
// prices on both edge rows' boundaries must land where a full scan puts them (out-of-order
// input still gets that scan)
test('showEmpty: a deep book places the same levels as a full scan', () => {
    const height = 600;
    const { renderer } = createLadder({ removalMode: 'showEmpty', height });
    const snapshotOf = (asks, bids) => ({ bestBid: 100, bestAsk: 100.01, midPrice: 100.005, bids, asks, timestamp: 0 });
    const level = (side, price, quantity) => ({ price, quantity, numOrders: 1, side, isDirty: false, hasOwnOrders: false });
    renderer.render(snapshotOf([level(Side.ASK, 100.01, 1)], [level(Side.BID, 100, 1)]));
    const reference = renderer.getCenterPrice();
    const midRow = Math.floor(renderer.visibleRows / 2);
    const bottom = midRow - Math.ceil(height / renderer.rowHeight) + 1; // Ticks from the reference price
    const ticks = [];
    for (let tick = -1000; tick <= 1000; tick++) ticks.push(tick);
    ticks.push(midRow + 0.49, midRow + 0.5, midRow + 0.51, bottom - 0.49, bottom - 0.5, bottom - 0.51);
    const side = (sideValue, firstQuantity) => ticks.map((tick) => reference + tick * TICK_SIZE).sort((a, b) => a - b)
        .map((price, i) => level(sideValue, price, firstQuantity + i));
    const asks = side(Side.ASK, 1);
    const bids = side(Side.BID, 5000);

    const bars = (snapshot) => {
        renderer.render(snapshot); // May resize, which swaps the context
        const screen = trackBars(renderer);
        renderer.needsFullRedraw = true;
        renderer.render(snapshot);
        return new Map([...screen].map(([row, widths]) => [row, [...widths].sort((a, b) => a - b)]));
    };
    const windowed = bars(snapshotOf(asks, bids));
    const scanned = bars(snapshotOf([...asks].reverse(), [...bids].reverse()));
    assert.deepEqual(windowed, scanned);
    assert.equal(windowed.size, Math.ceil(height / renderer.rowHeight), 'a bar on every row');
    assert.equal(windowed.get(0).length, 4, 'top row: its own price and the one 0.49 ticks above, both sides');
});

// A queue of 8+ orders keeps its largest order by array, so a changed queue must be scanned again
test('MBO: segments rescale when a long queue\'s largest order changes', () => {
    const ladder = new PriceLadder({ container: fakeContainer(), mode: 'MBO' });
    const scale = () => ladder.renderer.segmentState.basePixelsPerUnit;
    apply(ladder, [...Array.from({ length: 12 }, (_, i) => [Side.BID, 100, 100 + i, i + 1]), [Side.ASK, 100.01, 10, 100]]);
    render(ladder);
    const first = scale();
    apply(ladder, [[Side.BID, 100, 1000, 5]], OrderUpdateType.Modify);
    render(ladder);
    assertNear(scale() / first, 111 / 1000);
    apply(ladder, [[Side.BID, 100, 0, 5]], OrderUpdateType.Delete);
    render(ladder);
    assertNear(scale(), first);
});

// Bug: the segment drag limit was found only when the segment scale changed by 20% or more, so a
// level that grew wider without a new largest order couldn't be dragged to its end
test('MBO: a segment drag reaches the end of the widest level as the book changes', () => {
    const ladder = new PriceLadder({ container: fakeContainer(), mode: 'MBO' });
    const { renderer } = ladder;
    apply(ladder, [[Side.BID, 100, 100, 1], [Side.ASK, 100.01, 100, 2]]);
    render(ladder);
    renderer.adjustHorizontalScroll(1e6);
    assert.equal(renderer.segmentState.horizontalScrollOffset, 0, 'one order per level fits');

    // Orders no bigger than the largest: the scale stays, and the level outgrows the bar column
    apply(ladder, Array.from({ length: 40 }, (_, i) => [Side.BID, 99.99, 100, 10 + i]));
    render(ladder);
    renderer.adjustHorizontalScroll(1e6);
    const { horizontalScrollOffset, basePixelsPerUnit, userScaleFactor } = renderer.segmentState;
    assert.ok(horizontalScrollOffset > 0, 'scrolls');
    assert.ok(horizontalScrollOffset < 40 * 100 * basePixelsPerUnit * userScaleFactor + 40 * 10, 'to the end, not past it');
});

// Bug: destroy() removed the unbound methods, not the bound listeners that were added
test('destroy removes every canvas listener', () => {
    const ladder = createLadder();
    ladder.destroy();
    assert.deepEqual(Object.keys(ladder.canvas.listeners), []);
});

test('setAutoCenter(true) re-centers immediately', () => {
    const ladder = createLadder({ autoCenter: false });
    ladder.renderer.setScrollOffset(5);
    ladder.setAutoCenter(true);
    assert.equal(ladder.renderer.getScrollOffset(), 0);

    ladder.setAutoCenter(false);
    ladder.setRemovalMode('showEmpty');
    ladder.renderer.setCenterPrice(105);
    ladder.setAutoCenter(true);
    assertNear(ladder.renderer.getCenterPrice(), 100.01);
});

test('auto-center pins the view to the bid/ask midpoint', () => {
    const ladder = createLadder({ removalMode: 'showEmpty' });
    ladder.renderer.setCenterPrice(105);
    assertNear(ladder.renderer.getCenterPrice(), 100.01);

    ladder.setRemovalMode('removeRow');
    ladder.renderer.setScrollOffset(10);
    assert.equal(ladder.renderer.getScrollOffset(), 0);
});

// Bug: 100.05 / 0.1 is 1000.4999... in float, so Math.round centered one row below desktop (decimal)
for (const [bid, ask, expected] of [[100.00, 100.10, 100.10], [-100.10, -100.00, -100.10]]) {
    test(`auto-center snaps half-tick mid of ${bid}/${ask} to ${expected} like desktop`, () => {
        const ladder = createLadder({ removalMode: 'showEmpty', tickSize: 0.01, displayTickSize: 0.10 }, bid, ask);
        assertNear(ladder.renderer.getCenterPrice(), expected);
    });
}

test('auto-center on ignores vertical wheel scroll', () => {
    for (const removalMode of ['removeRow', 'showEmpty']) {
        const ladder = createLadder({ removalMode });
        // Spy on both scroll paths: checking the offset can't catch a missing guard,
        // since the scroll's own render re-pins the view
        const scrolls = [];
        ladder.interactionHandler.onScroll = (ticks) => scrolls.push(ticks);
        ladder.renderer.scrollByPrice = (delta) => scrolls.push(delta);
        ladder.canvas.listeners.wheel(event({ deltaY: 100, shiftKey: false }));
        assert.deepEqual(scrolls, [], removalMode);
    }
});

test('auto-center off: wheel scrolls, Space and middle-click re-center', () => {
    const ladder = createLadder({ autoCenter: false });
    const { listeners } = ladder.canvas;

    listeners.wheel(event({ deltaY: 100, shiftKey: false }));
    assert.equal(ladder.renderer.getScrollOffset(), 5);
    listeners.keydown(event({ code: 'Space' }));
    assert.equal(ladder.renderer.getScrollOffset(), 0);

    ladder.setRemovalMode('showEmpty');
    ladder.renderer.setCenterPrice(105);
    listeners.mousedown(event({ button: 1, clientX: 0, clientY: 0 }));
    assertNear(ladder.renderer.getCenterPrice(), 100.01);
});

// Level clicks carry the mouse button. A middle-click on a qty column re-centers unless
// onLevelClick returns true (handled)
function clickLadder(onLevelClick, config = {}) {
    const ladder = createLadder({ autoCenter: false, onLevelClick, ...config });
    const { bidQtyX, askQtyX } = ladder.renderer.getColumnPositions();
    return { ladder, bidX: bidQtyX + 1, askX: askQtyX + 1, listeners: ladder.canvas.listeners };
}

test('level click passes the mouse button, back/forward buttons are ignored', () => {
    const calls = [];
    const { ladder, bidX, askX, listeners } = clickLadder((...args) => { calls.push(args); return true; });
    const row = [...Array(30).keys()].find((r) => ladder.renderer.rowToLevelInfo(r) !== null);
    const price = ladder.renderer.rowToLevelInfo(row).price;
    const at = (button, clientX) => event({ button, clientX, clientY: row * 24 + 1 });

    listeners.click(at(0, bidX));
    listeners.auxclick(at(1, askX));
    listeners.auxclick(at(2, askX));
    listeners.auxclick(at(3, askX));
    listeners.click(at(0, 0)); // Order count column
    assert.deepEqual(calls, [
        [price, Side.BID, ClickButton.Left],
        [price, Side.ASK, ClickButton.Middle],
        [price, Side.ASK, ClickButton.Right]
    ]);
});

for (const [label, onLevelClick, recenters] of [
    ['returns true', () => true, false],
    ['returns nothing', () => {}, true],
    ['is not set', undefined, true]
]) {
    test(`middle-click on a qty column when onLevelClick ${label}: ${recenters ? 're-centers' : 'stays'}`, () => {
        const { ladder, bidX, listeners } = clickLadder(onLevelClick, { removalMode: 'showEmpty' });
        ladder.renderer.setCenterPrice(105);
        listeners.mousedown(event({ button: 1, clientX: bidX, clientY: 0 }));
        assertNear(ladder.renderer.getCenterPrice(), 105); // Decided on auxclick
        listeners.auxclick(event({ button: 1, clientX: bidX, clientY: 0 }));
        assertNear(ladder.renderer.getCenterPrice(), recenters ? 100.01 : 105);
    });
}

test('read-only: middle-click on a qty column re-centers without a level click', () => {
    const calls = [];
    const { ladder, askX, listeners } = clickLadder(() => { calls.push(1); return true; }, { removalMode: 'showEmpty', readOnly: true });
    ladder.renderer.setCenterPrice(105);
    listeners.mousedown(event({ button: 1, clientX: askX, clientY: 0 }));
    assertNear(ladder.renderer.getCenterPrice(), 100.01);
    listeners.auxclick(event({ button: 1, clientX: askX, clientY: 0 }));
    assert.deepEqual(calls, []);
});
