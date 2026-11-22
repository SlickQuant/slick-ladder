// WasmPriceLadder shows the worker's snapshots with own-order flags, display tick aggregation and our
// cells added on this thread. Run: npm test (in src/web)
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { Side, fakeContainer } = require('../test-utils/ladder-harness');
const { WasmPriceLadder, FakeWorker } = require('../test-utils/wasm-ladder');

const order = (orderId, quantity) => ({ orderId, quantity, priority: orderId, isOwnOrder: false });

// Bids 100.00 and 99.97, asks 100.02 and 100.05, with their orders
const workerSnapshot = () => ({
    bestBid: 100, bestAsk: 100.02, midPrice: 100.01, timestamp: 1,
    bids: [{ price: 99.97, quantity: 5, numOrders: 1, side: Side.BID }, { price: 100, quantity: 30, numOrders: 2, side: Side.BID }],
    asks: [{ price: 100.02, quantity: 10, numOrders: 1, side: Side.ASK }, { price: 100.05, quantity: 5, numOrders: 1, side: Side.ASK }],
    bidOrders: { '99.97': [order(1, 5)], '100': [order(2, 10), order(3, 20)] },
    askOrders: { '100.02': [order(4, 10)], '100.05': [order(5, 5)] }
});

function createLadder(config) {
    const ladder = new WasmPriceLadder({ container: fakeContainer(), height: 600, tickSize: 0.01, ...config });
    FakeWorker.last.emit({ type: 'ready' });
    FakeWorker.last.emit({ type: 'snapshot', data: workerSnapshot() });
    return ladder;
}

// Prices of the levels on screen flagged as having own orders
const flagged = (ladder) => ladder.renderer.rows.flatMap((row) => [row.ask, row.bid])
    .filter((level) => level.present && level.hasOwnOrders).map((level) => level.price);

// Bug: updateConfig rebuilt the snapshot from the worker's, which setHasOwnOrders never flagged, so
// own-order highlights vanished until the worker's next snapshot, even at the same display tick
test('own-order flags survive updateConfig', () => {
    for (const displayTickSize of [0.01, 0.1]) {
        const ladder = createLadder({ displayTickSize });
        ladder.setHasOwnOrders(100.00, Side.BID, true); // A bucket edge, so its row is 100 at any display tick
        assert.deepEqual(flagged(ladder), [100], `${displayTickSize}: set`);
        ladder.updateConfig({ displayTickSize });
        assert.deepEqual(flagged(ladder), [100], `${displayTickSize}: the same display tick`);
        ladder.updateConfig({ displayTickSize: 0.05 });
        assert.deepEqual(flagged(ladder), [100], `${displayTickSize}: another display tick`);
        FakeWorker.last.emit({ type: 'snapshot', data: workerSnapshot() });
        assert.deepEqual(flagged(ladder), [100], `${displayTickSize}: the next worker snapshot`);
        ladder.setHasOwnOrders(100.00, Side.BID, false);
        assert.deepEqual(flagged(ladder), [], `${displayTickSize}: cleared`);
        ladder.destroy();
    }
});

// Bug: with the display tick aggregating, setHasOwnOrders matched its price against the bucket
// prices on screen, so a level off a bucket edge showed no flag until the worker's next snapshot
test('an aggregated row shows the own-order flag of a level off its bucket edge', () => {
    const ladder = createLadder({ displayTickSize: 0.1 });
    ladder.setHasOwnOrders(99.97, Side.BID, true);
    assert.deepEqual(flagged(ladder), [99.9]);
    ladder.destroy();
});

test('aggregated rows show the orders of every level in their bucket', () => {
    const ladder = createLadder({ mode: 'MBO', displayTickSize: 0.1 });
    const bucket = ladder.renderer.rows.map((row) => row.bid).find((level) => level.present && level.price === 99.9);
    assert.deepEqual(bucket.orders.map((o) => o.orderId), [1], '99.97 alone in the 99.9 bucket');
    const asks = ladder.renderer.rows.map((row) => row.ask).find((level) => level.present && level.price === 100.1);
    assert.deepEqual(asks.orders.map((o) => o.orderId), [4, 5], '100.02 before 100.05');
    assert.deepEqual(asks.breaks, [1]);
    ladder.destroy();
});
