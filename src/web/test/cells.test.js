// Work column (our working orders), LTQ column (last trade) and the header row.
// Mirrors tests/SlickUI.Core.Tests/CellColumnTests.cs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { PriceLadder, Side, ClickButton, OrderUpdateType, COL_WIDTH, VOLUME_BAR_WIDTH_MULTIPLIER, SEGMENT_GAP_PX, ORDER_LEVEL_GAP_PX,
    DEFAULT_COLORS, fakeContainer, render, trackScreens, screenRows } = require('../test-utils/ladder-harness');

trackScreens();

const LEVELS = [[Side.BID, 100.00], [Side.BID, 99.99], [Side.ASK, 100.02], [Side.ASK, 100.03]];

function createLadder(config = {}) {
    const ladder = new PriceLadder({ container: fakeContainer(), height: 600, ...config });
    LEVELS.forEach(([side, price], i) => {
        if (config.mode === 'MBO') {
            ladder.processOrderUpdate({ orderId: i + 1, side, price, quantity: 10, priority: i + 1 }, OrderUpdateType.Add);
        } else {
            ladder.processUpdate({ side, price, quantity: 10, numOrders: 1 });
        }
    });
    render(ladder);
    return ladder;
}

const event = (fields) => ({ preventDefault: () => {}, ...fields });

// Screen items on the row showing price, after a full repaint
function itemsAt(ladder, price) {
    render(ladder); // May resize, which clears the canvas
    ladder.renderer.needsFullRedraw = true;
    render(ladder);
    const { renderer } = ladder;
    const row = [...Array(renderer.rowCount()).keys()].find((r) => renderer.rowToLevelInfo(r)?.price === price);
    assert.notEqual(row, undefined, `no row for ${price}`);
    return screenRows(ladder.canvas, renderer.rowCount() + 1)[renderer.rowTop(row) / renderer.rowHeight];
}

const at = (x) => `${x.toFixed(2)} `;
const textAt = (items, x) => items.filter((item) => item.startsWith(at(x)) && item.includes(' text '));
const fillAt = (items, x) => items.filter((item) => item.startsWith(at(x)) && item.includes(' fill '));

test('columns read Work | Bid | Price | Ask | LTQ | Orders, and toggle off to Bid | Price | Ask', () => {
    const { renderer } = createLadder({ mode: 'MBO' });
    const cols = renderer.getColumnPositions();
    assert.equal(cols.workX, 0);
    assert.equal(cols.bidQtyX, renderer.workColWidth);
    assert.equal(cols.ltqX, cols.askQtyX + renderer.qtyColWidth);
    assert.equal(cols.barX, cols.ltqX + renderer.ltqColWidth);
    const columnAt = (x, width) => renderer.screenXToColumn(x + width / 2);
    assert.deepEqual([
        columnAt(cols.workX, renderer.workColWidth),
        columnAt(cols.bidQtyX, renderer.qtyColWidth),
        columnAt(cols.priceX, renderer.priceColWidth),
        columnAt(cols.askQtyX, renderer.qtyColWidth),
        columnAt(cols.ltqX, renderer.ltqColWidth),
        columnAt(cols.barX, 10)
    ], [renderer.getWorkColumn(), renderer.getBidQtyColumn(), renderer.getPriceColumn(), renderer.getAskQtyColumn(),
        renderer.getLtqColumn(), 3]);
    assert.equal(renderer.screenYToRow(0), -1, 'header');

    const off = createLadder({ mode: 'MBO', showWorkColumn: false, showLtqColumn: false, showHeader: false, showOrders: false }).renderer;
    const plain = off.getColumnPositions();
    assert.deepEqual([plain.workX, plain.bidQtyX, plain.ltqX, plain.barX], [-1, 0, -1, -1]);
    assert.equal(off.screenYToRow(0), 0, 'rows start at the top');
});

// The Orders column draws each order, so it shows in MBO mode only, and comes and goes with the
// mode, widening and narrowing the canvas
test('the Orders column shows in MBO mode only', () => {
    const ladder = createLadder();
    const { renderer } = ladder;
    const titles = () => {
        renderer.needsFullRedraw = true;
        render(ladder);
        return screenRows(ladder.canvas, 1)[0].filter((item) => item.includes(' text ')).map((item) => item.split(' ').pop());
    };
    assert.equal(renderer.getColumnPositions().barX, -1, 'price levels');
    assert.ok(!titles().includes('Orders'));
    const priceLevelWidth = ladder.canvas.width;

    ladder.setDataMode('MBO');
    ladder.processOrderUpdate({ orderId: 1, side: Side.BID, price: 100, quantity: 10, priority: 1 }, OrderUpdateType.Add);
    render(ladder);
    const { barX, ltqX } = renderer.getColumnPositions();
    assert.equal(barX, ltqX + renderer.ltqColWidth, 'MBO');
    assert.ok(titles().includes('Orders'));
    assert.equal(ladder.canvas.width, Math.round(barX + COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER), 'the canvas widens by the Orders column');
    assert.equal(renderer.getBarColumnWidth(), ladder.canvas.width - barX, 'the bars get the new room');

    ladder.setDataMode('PriceLevel');
    render(ladder);
    assert.equal(renderer.getColumnPositions().barX, -1, 'back to price levels');
    assert.equal(ladder.canvas.width, priceLevelWidth);
});

test('the header titles the columns that are shown', () => {
    const titles = (config) => {
        const ladder = createLadder(config);
        ladder.renderer.needsFullRedraw = true;
        render(ladder);
        return screenRows(ladder.canvas, 1)[0].filter((item) => item.includes(' text ')).map((item) => item.split(' ').pop()).sort();
    };
    assert.deepEqual(titles({}), ['Ask', 'Bid', 'LTQ', 'Price', 'Work']);
    assert.deepEqual(titles({ mode: 'MBO' }), ['Ask', 'Bid', 'LTQ', 'Orders', 'Price', 'Work']);
    assert.deepEqual(titles({ mode: 'MBO', showWorkColumn: false, showLtqColumn: false, showOrders: false }), ['Ask', 'Bid', 'Price']);
    assert.ok(!titles({ showHeader: false }).includes('Bid'), 'no header');
});

test('the header separates its columns with a line on each edge', () => {
    const separators = (config) => {
        const ladder = createLadder(config);
        const { renderer } = ladder;
        const xs = [];
        const ctx = renderer.offscreenCtx;
        const fillRect = ctx.fillRect;
        ctx.fillRect = function (x, y, width, height) {
            if (this.fillStyle === DEFAULT_COLORS.gridLine && width === 1) xs.push(x); // Rows' grid lines are full width
            return fillRect.call(this, x, y, width, height);
        };
        renderer.needsFullRedraw = true;
        render(ladder);
        const cols = renderer.getColumnPositions();
        // Both sides, then the column edges inside them
        const edges = (...xs) => [0, renderer.width - 1, ...xs.map(Math.floor)];
        return { xs, cols, edges };
    };
    const all = separators({ mode: 'MBO' });
    assert.deepEqual(all.xs, all.edges(all.cols.bidQtyX, all.cols.priceX, all.cols.askQtyX, all.cols.ltqX, all.cols.barX));
    const levels = separators({});
    assert.deepEqual(levels.xs, levels.edges(levels.cols.bidQtyX, levels.cols.priceX, levels.cols.askQtyX, levels.cols.ltqX),
        'no Orders column for price levels');
    const plain = separators({ mode: 'MBO', showWorkColumn: false, showLtqColumn: false });
    assert.deepEqual(plain.xs, plain.edges(plain.cols.priceX, plain.cols.askQtyX, plain.cols.barX), 'bid qty starts on the left side');
    assert.deepEqual(separators({ showHeader: false }).xs, []);
});

for (const removalMode of ['removeRow', 'showEmpty']) {
    test(`${removalMode}: Work cell shows quantity and count, colored by side`, () => {
        const ladder = createLadder({ removalMode });
        const { renderer } = ladder;
        const workCenter = () => renderer.getColumnPositions().workX + renderer.workColWidth / 2;

        ladder.setWorkingOrders(100.00, Side.BID, 10, 2);
        ladder.setWorkingOrders(100.02, Side.ASK, 1500, 3);
        assert.deepEqual(textAt(itemsAt(ladder, 100.00), workCenter()).map((item) => item.split(' ').slice(-3).join(' ')),
            [`${DEFAULT_COLORS.bidBar} 10 (2)`]);
        assert.deepEqual(textAt(itemsAt(ladder, 100.02), workCenter()).map((item) => item.split(' ').slice(-3).join(' ')),
            [`${DEFAULT_COLORS.askBar} 1,500 (3)`]);

        ladder.setWorkingOrders(100.00, Side.BID, 0, 0);
        assert.deepEqual(textAt(itemsAt(ladder, 100.00), workCenter()), [], 'cleared');
    });

    test(`${removalMode}: LTQ cell sits on the traded price, colored by aggressor`, () => {
        const ladder = createLadder({ removalMode });
        const { renderer } = ladder;
        const ltq = (price) => {
            const items = itemsAt(ladder, price);
            const x = renderer.getColumnPositions().ltqX;
            return [...fillAt(items, x).map((item) => item.split(' ')[2]),
                ...textAt(items, x + renderer.ltqColWidth / 2).map((item) => item.split(' ').slice(-2).join(' '))];
        };

        ladder.processTrade({ price: 100.02, quantity: 7, aggressor: Side.BID });
        assert.deepEqual(ltq(100.02), [DEFAULT_COLORS.ltqBuyBackground, `${DEFAULT_COLORS.ltqText} 7`]);
        ladder.processTrade({ price: 100.02, quantity: 8, aggressor: Side.ASK });
        assert.deepEqual(ltq(100.02), [DEFAULT_COLORS.ltqSellBackground, `${DEFAULT_COLORS.ltqText} 8`]);
        ladder.processTrade({ price: 100.00, quantity: 9 });
        assert.deepEqual(ltq(100.00), [DEFAULT_COLORS.ltqUnknownBackground, `${DEFAULT_COLORS.ltqText} 9`]);
        assert.deepEqual(ltq(100.02), [], 'only the last trade');
    });
}

test('showEmpty: buy and sell working orders on one row add up in the text color', () => {
    const ladder = createLadder({ removalMode: 'showEmpty' });
    const { renderer } = ladder;
    ladder.setWorkingOrders(100.02, Side.BID, 5, 1);
    ladder.setWorkingOrders(100.02, Side.ASK, 6, 2);
    const x = renderer.getColumnPositions().workX + renderer.workColWidth / 2;
    assert.deepEqual(textAt(itemsAt(ladder, 100.02), x).map((item) => item.split(' ').slice(-3).join(' ')),
        [`${DEFAULT_COLORS.text} 11 (3)`]);
});

test('removeRow: a trade on a price with no level shows nowhere', () => {
    const ladder = createLadder({ removalMode: 'removeRow' });
    ladder.processTrade({ price: 100.01, quantity: 5, aggressor: Side.BID });
    render(ladder);
    assert.ok(ladder.renderer.rows.every((row) => row.ltqQuantity === 0));
});

test('Work and LTQ columns widen to fit their text', () => {
    const ladder = createLadder();
    const { renderer } = ladder;
    ladder.setWorkingOrders(100.00, Side.BID, 123456789, 12);
    ladder.processTrade({ price: 100.02, quantity: 987654321, aggressor: Side.ASK });
    render(ladder);
    assert.equal(renderer.workColWidth, renderer.columnWidthFor('123,456,789 (12)'));
    assert.equal(renderer.ltqColWidth, renderer.columnWidthFor('987,654,321'));
    assert.equal(renderer.getColumnPositions().bidQtyX, renderer.workColWidth);
});

test('display tick buckets working orders like their side, and the trade by its aggressor', () => {
    const ladder = createLadder({ tickSize: 0.01, displayTickSize: 0.1 });
    ladder.setWorkingOrders(100.03, Side.BID, 1, 1);
    ladder.setWorkingOrders(100.07, Side.BID, 2, 1);
    ladder.setWorkingOrders(100.13, Side.ASK, 4, 2);
    const snapshot = ladder.getSnapshot();
    assert.deepEqual([...snapshot.bidWork], [[100, { quantity: 3, count: 2 }]]);
    assert.deepEqual([...snapshot.askWork], [[100.2, { quantity: 4, count: 2 }]]);

    const bucket = (price, aggressor) => {
        ladder.processTrade({ price, quantity: 1, aggressor });
        return ladder.getSnapshot().lastTrade.price;
    };
    assert.equal(bucket(100.13, Side.BID), 100.2, 'buyer lifted an ask: up like asks');
    assert.equal(bucket(100.13, Side.ASK), 100.1, 'seller hit a bid: down like bids');
    assert.equal(bucket(100.13, null), 100.1, 'unknown: nearest');
    assert.equal(bucket(100.16, undefined), 100.2, 'unknown: nearest');
});

// Bug: float division put 100.3 / 0.1 at 1002.9999999999999, so Math.floor dropped a seller's
// trade (and a bid level or working order) a bucket low. Desktop's decimal math puts it at 100.3
test('prices on a bucket edge stay in that bucket', () => {
    const { bucketPrice, tradeBucketPrice } = require('../.test-build/aggregation.js');
    const trade = (price, aggressor) => tradeBucketPrice({ price, quantity: 1, aggressor }, 0.1);
    for (let cents = 9000; cents <= 11000; cents++) {
        const down = Math.floor(cents / 10) / 10; // Integer math, exact
        const up = Math.ceil(cents / 10) / 10;
        for (const price of [cents / 100, cents * 0.01]) { // Parsed, and as roundToTick leaves it
            assert.equal(bucketPrice(price, 0.1, false), down, `bid ${price}`);
            assert.equal(bucketPrice(price, 0.1, true), up, `ask ${price}`);
            assert.equal(trade(price, Side.ASK), down, `seller ${price}`);
            assert.equal(trade(price, Side.BID), up, `buyer ${price}`);
        }
    }
});

// Bug: the Work column fit the largest quantity's text, but a smaller quantity can have longer
// text, so "0.12345678 (1)" overflowed a column sized for "1 (1)"
test('the Work column fits its widest cell, not its largest quantity', () => {
    const ladder = createLadder({ minQuantityThreshold: 0.00000001 });
    ladder.setWorkingOrders(100.00, Side.BID, 1, 1);
    ladder.setWorkingOrders(99.99, Side.BID, 0.12345678, 1);
    render(ladder);
    const { renderer } = ladder;
    assert.equal(renderer.workColWidth, renderer.columnWidthFor('0.12345678 (1)'));
});

// With the display tick aggregating, an MBO row shows the orders of every price level in its
// bucket, the level nearest the spread first, with breaks where each next level starts. Buckets
// whose levels didn't change keep their arrays, and the maps stay while nothing changed
test('aggregated MBO merges every level\'s orders, nearest the spread first', () => {
    const { OrderBuckets } = require('../.test-build/aggregation.js');
    const order = (orderId) => ({ orderId, quantity: 10, priority: orderId });
    const ids = (orders) => orders.map((o) => o.orderId);
    const bids = new Map([[100.00, [order(1), order(2)]], [99.95, [order(3)]], [99.97, [order(4)]]]);
    const buckets = new OrderBuckets(false);
    const first = buckets.merge(bids, 0.1);
    assert.equal(first.orders.get(100), bids.get(100.00), 'one level: its own array');
    assert.deepEqual(ids(first.orders.get(99.9)), [4, 3], '99.97 before 99.95');
    assert.deepEqual(first.breaks.get(99.9), [1]);
    assert.equal(first.breaks.has(100), false);

    const asks = new OrderBuckets(true).merge(new Map([[100.04, [order(5)]], [100.01, [order(6), order(7)]]]), 0.1);
    assert.deepEqual(ids(asks.orders.get(100.1)), [6, 7, 5], '100.01 before 100.04');
    assert.deepEqual(asks.breaks.get(100.1), [2]);

    assert.equal(buckets.merge(new Map(bids), 0.1).orders, first.orders, 'no change: the same maps');
    bids.set(100.00, [order(1)]);
    const next = buckets.merge(bids, 0.1);
    assert.notEqual(next.orders, first.orders);
    assert.equal(next.orders.get(99.9), first.orders.get(99.9), 'its levels did not change');
});

// Price levels in an aggregated MBO row are set apart by a wider gap than orders of one level
test('aggregated MBO sets each level\'s orders apart in the Orders column', () => {
    const ladder = new PriceLadder({ container: fakeContainer(), width: 800, height: 600, mode: 'MBO', tickSize: 0.01, displayTickSize: 0.1 });
    // The 1000 lot sets the scale (200px), so 100 lots are 20px
    [[1, Side.BID, 100.00, 1000], [2, Side.ASK, 100.01, 100], [3, Side.ASK, 100.04, 100], [4, Side.ASK, 100.04, 100]].forEach(
        ([orderId, side, price, quantity]) => ladder.processOrderUpdate({ orderId, side, price, quantity, priority: orderId }, OrderUpdateType.Add));
    render(ladder);
    ladder.renderer.needsFullRedraw = true;
    render(ladder);
    const { renderer } = ladder;
    const row = [...Array(renderer.rowCount()).keys()].find((r) => renderer.rowToLevelInfo(r)?.price === 100.1);
    const segments = screenRows(ladder.canvas, renderer.rowCount() + 1)[renderer.rowTop(row) / renderer.rowHeight]
        .filter((item) => item.includes(` fill ${DEFAULT_COLORS.askBar} `))
        .map((item) => { const parts = item.split(' '); return [parseFloat(parts[0]), parseFloat(parts[3])]; })
        .sort((a, b) => a[0] - b[0]);
    assert.equal(segments.length, 3);
    const gap = (i) => segments[i + 1][0] - (segments[i][0] + segments[i][1]);
    assert.ok(Math.abs(gap(0) - ORDER_LEVEL_GAP_PX) < 1e-6, `between levels: ${gap(0)}`);
    assert.ok(Math.abs(gap(1) - SEGMENT_GAP_PX) < 1e-6, `within a level: ${gap(1)}`);
});

test('clear() drops working orders and the last trade', () => {
    const ladder = createLadder();
    ladder.setWorkingOrders(100.00, Side.BID, 10, 1);
    ladder.processTrade({ price: 100.02, quantity: 7, aggressor: Side.BID });
    ladder.clear();
    const snapshot = ladder.getSnapshot();
    assert.equal(snapshot.bidWork.size, 0);
    assert.equal(snapshot.lastTrade, null);
});

// Work cells click like qty cells, with their row's price: its level's, or in Show Empty Rows the
// row's own price, where working orders can sit without a level
for (const removalMode of ['removeRow', 'showEmpty']) {
    test(`${removalMode}: a Work cell click passes its row's price and the button`, () => {
        const work = [];
        const levels = [];
        const ladder = createLadder({
            removalMode,
            onWorkClick: (...args) => { work.push(args); return true; },
            onLevelClick: (...args) => { levels.push(args); return true; }
        });
        const { renderer } = ladder;
        const { listeners } = ladder.canvas;
        const workX = renderer.getColumnPositions().workX + 1;
        const rowOf = (price) => [...Array(renderer.rowCount()).keys()].find((r) => renderer.rowToLevelInfo(r)?.price === price);
        const at = (button, row) => event({ button, clientX: workX, clientY: renderer.rowTop(row) + 1 });

        listeners.click(at(ClickButton.Left, rowOf(100.00)));
        listeners.auxclick(at(ClickButton.Right, rowOf(100.02)));
        listeners.click(event({ button: ClickButton.Left, clientX: workX, clientY: 1 })); // Header
        assert.deepEqual(work, [[100.00, ClickButton.Left], [100.02, ClickButton.Right]]);
        assert.deepEqual(levels, [], 'not a level click');

        // The row above the best bid: the spread (100.01) in Show Empty Rows, the best ask in Remove Rows
        listeners.click(at(ClickButton.Left, rowOf(100.00) - 1));
        assert.deepEqual(work.at(-1), [removalMode === 'showEmpty' ? 100.01 : 100.02, ClickButton.Left]);
    });
}

// Bug: with the display tick aggregating, a Work click passes its bucket row's price (100.0 for a
// bid at 100.03), which no order has, so the demo's cancel found nothing. displayPrice maps an
// order to the row price its Work click passes
test('displayPrice maps an order to the price its Work cell click passes', () => {
    const clicks = [];
    const ladder = createLadder({ removalMode: 'showEmpty', tickSize: 0.01, displayTickSize: 0.1,
        onWorkClick: (price) => { clicks.push(price); return true; } });
    ladder.setWorkingOrders(100.03, Side.BID, 5, 1);
    ladder.setWorkingOrders(100.03, Side.ASK, 6, 1);
    render(ladder);
    const { renderer } = ladder;
    const workX = renderer.getColumnPositions().workX + 1;
    for (const [side, workSides] of [[Side.BID, 1], [Side.ASK, 2]]) { // Rows with only buys, only sells
        const row = renderer.rows.findIndex((r) => r.workSides === workSides);
        ladder.canvas.listeners.click(event({ button: ClickButton.Left, clientX: workX, clientY: renderer.rowTop(row) + 1 }));
        assert.equal(clicks.at(-1), ladder.displayPrice(100.03, side), `side ${side}`);
    }
    assert.deepEqual(clicks, [100, 100.1], 'bids round down, asks up');
    assert.ok(Math.abs(createLadder().displayPrice(100.03, Side.ASK) - 100.03) < 1e-9, 'not aggregating: the price');
});

test('middle-click on a Work cell re-centers unless onWorkClick handles it', () => {
    for (const [onWorkClick, recenters] of [[() => true, false], [() => {}, true]]) {
        const ladder = createLadder({ removalMode: 'showEmpty', autoCenter: false, onWorkClick });
        const { renderer } = ladder;
        const at = event({ button: ClickButton.Middle, clientX: renderer.getColumnPositions().workX + 1, clientY: renderer.rowTop(0) + 1 });
        renderer.setCenterPrice(105);
        ladder.canvas.listeners.mousedown(at);
        assert.ok(Math.abs(renderer.getCenterPrice() - 105) < 1e-9, 'decided on auxclick');
        ladder.canvas.listeners.auxclick(at);
        assert.ok(Math.abs(renderer.getCenterPrice() - (recenters ? 100.01 : 105)) < 1e-9, `recenters ${recenters}`);
    }
});

test('clicks on the header are not level clicks, a middle-click there re-centers', () => {
    const calls = [];
    const ladder = createLadder({ removalMode: 'showEmpty', autoCenter: false, onLevelClick: (...args) => { calls.push(args); return true; } });
    const { renderer } = ladder;
    const { listeners } = ladder.canvas;
    const bidX = renderer.getColumnPositions().bidQtyX + 1;

    listeners.click(event({ button: ClickButton.Left, clientX: bidX, clientY: 1 }));
    assert.deepEqual(calls, []);

    renderer.setCenterPrice(105);
    listeners.mousedown(event({ button: ClickButton.Middle, clientX: bidX, clientY: 1 }));
    listeners.auxclick(event({ button: ClickButton.Middle, clientX: bidX, clientY: 1 }));
    assert.deepEqual(calls, []);
    assert.ok(Math.abs(renderer.getCenterPrice() - 100.01) < 1e-9, 're-centered');

    listeners.click(event({ button: ClickButton.Left, clientX: bidX, clientY: renderer.rowTop(0) + 1 }));
    assert.equal(calls.length, 1, 'the first row is clickable');
});
