// SortedLevels keeps one side's levels in price order for snapshots. Run: npm test (in src/web)
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { SortedLevels } = require('../.test-build/sorted-levels');

const level = (price, quantity = 1) => ({ price, quantity, numOrders: 1, side: 0, isDirty: false, hasOwnOrders: false });
const prices = (levels) => levels.map((entry) => entry.price);

function listOf(...levelPrices) {
    const list = new SortedLevels();
    for (const price of levelPrices) {
        list.add(level(price));
    }
    return list;
}

test('keeps levels in price order as they come and go', () => {
    const list = listOf(100.02, 99.98, 100, 100.05, 99.99);
    assert.deepEqual(prices(list.levels), [99.98, 99.99, 100, 100.02, 100.05]);
    list.remove(100);
    list.remove(99.98);
    list.remove(100.05);
    assert.deepEqual(prices(list.levels), [99.99, 100.02]);
});

test('a shared array keeps its levels while the list changes', () => {
    const list = listOf(100, 101);
    const first = list.share();
    list.add(level(100.5));
    list.remove(100);
    assert.deepEqual(prices(first), [100, 101]);
    assert.deepEqual(prices(list.levels), [100.5, 101]);

    const second = list.share();
    assert.notEqual(second, first);
    list.add(level(99));
    assert.deepEqual(prices(second), [100.5, 101]);
    assert.deepEqual(prices(list.levels), [99, 100.5, 101]);
});

// Bug: remove() spliced at the insertion point, so a missing price took the next level with it
test('removing a price not in the list changes nothing', () => {
    const list = listOf(100, 101);
    const shared = list.share();
    list.remove(100.5);
    list.remove(99);
    list.remove(102);
    assert.deepEqual(prices(list.levels), [100, 101]);
    assert.equal(list.levels, shared, 'not copied either');
});

test('adding a price already in the list replaces its level', () => {
    const list = listOf(100, 101);
    list.add(level(100, 5));
    assert.deepEqual(prices(list.levels), [100, 101]);
    assert.equal(list.levels[0].quantity, 5);
});
