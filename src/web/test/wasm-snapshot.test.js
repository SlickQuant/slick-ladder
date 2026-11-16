// parseWasmSnapshot turns worker snapshots (C# JSON) into OrderBookSnapshots. Run: npm test (in src/web)
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseWasmSnapshot } = require('../.test-build/wasm-snapshot');

const order = (OrderId, Quantity, extra = {}) => ({ OrderId, Side: 0, Price: 100, Quantity, Priority: OrderId, IsOwnOrder: false, ...extra });
const worker = (bidOrders) => JSON.stringify({
    BestBid: 100, BestAsk: 100.01, MidPrice: 100.005, Timestamp: 1,
    Bids: [{ Price: 99.99, Quantity: 5, NumOrders: 1, Side: 0 }, { Price: 100, Quantity: 30, NumOrders: 2, Side: 0 }],
    Asks: [{ Price: 100.01, Quantity: 7, NumOrders: 1, Side: 1 }],
    BidOrders: bidOrders,
    AskOrders: null
});

test('reads PascalCase and camelCase snapshots alike', () => {
    const pascal = parseWasmSnapshot(worker({ '100': [order(1, 10)] }), null);
    const camel = parseWasmSnapshot({
        bestBid: 100, bestAsk: 100.01, midPrice: 100.005, timestamp: 1,
        bids: [{ price: 99.99, quantity: 5, numOrders: 1, side: 0 }, { price: 100, quantity: 30, numOrders: 2, side: 0 }],
        asks: [{ price: 100.01, quantity: 7, numOrders: 1, side: 1 }],
        bidOrders: { '100': [{ orderId: 1, side: 0, price: 100, quantity: 10, priority: 1, isOwnOrder: false }] },
        askOrders: null
    }, null);
    assert.deepEqual(camel, pascal);
    assert.equal(pascal.bids[1].quantity, 30);
    assert.deepEqual(pascal.bidOrders.get(100), [{ orderId: 1, side: 0, price: 100, quantity: 10, priority: 1, isOwnOrder: false }]);
    assert.equal(pascal.askOrders, null);
});

// Every worker snapshot is new JSON, so the order arrays were always new: the renderer compared
// each level's orders one by one. Unchanged levels now keep the previous array
test('a level with unchanged orders keeps the previous array', () => {
    const first = parseWasmSnapshot(worker({ '99.99': [order(1, 5, { Price: 99.99 })], '100': [order(2, 10), order(3, 20)] }), null);
    const second = parseWasmSnapshot(worker({ '99.99': [order(1, 5, { Price: 99.99 })], '100': [order(2, 10), order(3, 21)] }), first);
    assert.equal(second.bidOrders.get(99.99), first.bidOrders.get(99.99));
    assert.notEqual(second.bidOrders.get(100), first.bidOrders.get(100));
    assert.equal(second.bidOrders.get(100)[1].quantity, 21);

    const third = parseWasmSnapshot(worker({ '99.99': [order(1, 5, { Price: 99.99, IsOwnOrder: true })], '100': [order(2, 10), order(3, 21)] }), second);
    assert.notEqual(third.bidOrders.get(99.99), second.bidOrders.get(99.99), 'own-order flag changed');
    assert.equal(third.bidOrders.get(100), second.bidOrders.get(100));
});

// A reused array keeps the old segments on screen, so these must not count as unchanged
test('a level whose orders were added, removed or reordered gets a new array', () => {
    // Same priority and quantity, so only the order ids tell the queues apart
    const queued = (id) => order(id, 10, { Priority: 1 });
    const first = parseWasmSnapshot(worker({ '100': [queued(2), queued(3)] }), null);
    const ids = (snapshot) => snapshot.bidOrders.get(100).map((entry) => entry.orderId);

    const added = parseWasmSnapshot(worker({ '100': [queued(2), queued(3), queued(4)] }), first);
    assert.notEqual(added.bidOrders.get(100), first.bidOrders.get(100));
    assert.deepEqual(ids(added), [2, 3, 4]);

    const removed = parseWasmSnapshot(worker({ '100': [queued(2)] }), first);
    assert.notEqual(removed.bidOrders.get(100), first.bidOrders.get(100));
    assert.deepEqual(ids(removed), [2]);

    const reordered = parseWasmSnapshot(worker({ '100': [queued(3), queued(2)] }), first);
    assert.notEqual(reordered.bidOrders.get(100), first.bidOrders.get(100));
    assert.deepEqual(ids(reordered), [3, 2]);
});

// The shape WasmHelpers.SerializeSnapshot writes: camelCase, orders without side or price
test('worker JSON orders are reused when unchanged', () => {
    const json = (quantity) => `{"bestBid":100,"bestAsk":100.01,"midPrice":100.005,` +
        `"bids":[{"price":100,"quantity":${10 + quantity},"numOrders":2,"side":0}],"asks":[],` +
        `"bidOrders":{"100":[{"orderId":1,"quantity":10,"priority":1,"isOwnOrder":false},` +
        `{"orderId":2,"quantity":${quantity},"priority":2,"isOwnOrder":false}]},"timestamp":1}`;
    const first = parseWasmSnapshot(json(5), null);
    const same = parseWasmSnapshot(json(5), first);
    const changed = parseWasmSnapshot(json(6), same);
    assert.equal(same.bidOrders.get(100), first.bidOrders.get(100));
    assert.notEqual(changed.bidOrders.get(100), same.bidOrders.get(100));
    assert.equal(changed.bidOrders.get(100)[1].quantity, 6);
});
