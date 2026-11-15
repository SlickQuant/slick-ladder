// PriceLadder unit tests (node:test). Browser APIs are stubbed below. Run: npm test (in src/web)
const { test } = require('node:test');
const assert = require('node:assert/strict');

// Canvas 2D context stub: properties are stored, every method is a no-op
function fakeContext() {
    return new Proxy({}, {
        get: (target, prop) => {
            if (prop in target) return target[prop];
            if (prop === 'measureText') return (text) => ({ width: String(text).length * 7 });
            return () => {};
        },
        set: (target, prop, value) => {
            target[prop] = value;
            return true;
        }
    });
}

// Canvas stub that records listeners so tests can dispatch input events
function fakeCanvas() {
    const listeners = {};
    return {
        width: 0,
        height: 0,
        tabIndex: -1,
        style: {},
        listeners,
        getContext: () => fakeContext(),
        addEventListener: (type, fn) => { listeners[type] = fn; },
        removeEventListener: (type, fn) => { if (listeners[type] === fn) delete listeners[type]; },
        focus: () => {},
        getBoundingClientRect: () => ({ left: 0, top: 0 })
    };
}

globalThis.document = { createElement: () => fakeCanvas() };
globalThis.OffscreenCanvas = class {
    getContext() { return fakeContext(); }
};
globalThis.ResizeObserver = class {
    observe() {}
    disconnect() {}
};
globalThis.requestAnimationFrame = () => 0; // Tests render explicitly, no loop
globalThis.cancelAnimationFrame = () => {};
globalThis.localStorage = { getItem: () => null, setItem: () => {} };

const { PriceLadder, Side } = require('../.test-build/main.js');

// Default book: bid 100.00 / ask 100.02 -> mid 100.01
function createLadder(config = {}, bid = 100.00, ask = 100.02) {
    const container = { clientWidth: 800, appendChild: () => {}, removeChild: () => {} };
    const ladder = new PriceLadder({ container, ...config });
    ladder.processUpdate({ side: Side.BID, price: bid, quantity: 10, numOrders: 1 });
    ladder.processUpdate({ side: Side.ASK, price: ask, quantity: 10, numOrders: 1 });
    render(ladder);
    return ladder;
}

const render = (ladder) => ladder.renderer.render(ladder.getSnapshot());
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
