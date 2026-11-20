// Loads the test build (.test-build) with browser APIs stubbed. Lives outside test/ so
// node --test doesn't run it as a test file
const noop = () => {};
const measureText = (text) => ({ width: String(text).length * 7 });

// Context members the renderer doesn't set resolve to a no-op through this prototype
const contextFallback = new Proxy({}, { get: () => noop });

let screenOptions = null; // Set by trackScreens: new contexts then keep a screen model

// Canvas 2D context stub: properties are stored, every method is a no-op. Methods the
// renderer calls are own properties, so perf tests don't pay a proxy trap per call
function fakeContext(canvas) {
    const ctx = Object.create(contextFallback);
    for (const name of ['fillRect', 'fillText', 'strokeRect', 'beginPath', 'moveTo', 'lineTo',
        'stroke', 'rect', 'clip', 'save', 'restore', 'drawImage']) {
        ctx[name] = noop;
    }
    ctx.measureText = measureText;
    if (screenOptions) {
        trackScreen(ctx, canvas, screenOptions);
    }
    return ctx;
}

// Screen model: per row of a canvas, the text (with its color), bars, LTQ cell fills and borders
// on it, each keyed by its x. Background fills wipe what they cover (inside the clip) and
// drawImage copies whole rows, so two canvases' models compare incremental rendering with a full
// redraw without real pixels. Row 0 is the header when the ladder has one
function trackScreen(ctx, canvas, { rowHeight, colors }) {
    // Bars and LTQ fills share colors, so both are recorded as fills
    const fills = new Set([colors.bidBar, colors.askBar, colors.ltqBuyBackground, colors.ltqSellBackground,
        colors.ltqUnknownBackground]);
    const rows = canvas.rows = new Map();
    const saved = [];
    let clip = null;
    let path = null;
    const rowsIn = (y0, y1, visit) => {
        for (let row = Math.floor(y0 / rowHeight); row * rowHeight < y1; row++) visit(row);
    };
    const add = (x, y, item) => {
        if (clip && (x < clip.x0 || x >= clip.x1 || y < clip.y0 || y >= clip.y1)) return;
        const row = Math.floor(y / rowHeight);
        rows.set(row, [...(rows.get(row) ?? []), `${x.toFixed(2)} ${item}`]);
    };

    ctx.save = () => saved.push(clip);
    ctx.restore = () => { clip = saved.pop() ?? null; };
    ctx.beginPath = () => { path = null; };
    ctx.rect = (x, y, width, height) => { path = { x0: x, y0: y, x1: x + width, y1: y + height }; };
    ctx.clip = () => {
        clip = !clip ? path : {
            x0: Math.max(clip.x0, path.x0), y0: Math.max(clip.y0, path.y0),
            x1: Math.min(clip.x1, path.x1), y1: Math.min(clip.y1, path.y1)
        };
    };
    ctx.fillRect = function (x, y, width, height) {
        if (this.fillStyle === colors.background) {
            const x0 = Math.max(x, clip?.x0 ?? -Infinity);
            const x1 = Math.min(x + width, clip?.x1 ?? Infinity);
            rowsIn(Math.max(y, clip?.y0 ?? -Infinity), Math.min(y + height, clip?.y1 ?? Infinity), (row) => {
                const items = rows.get(row);
                if (items) rows.set(row, items.filter((item) => parseFloat(item) < x0 || parseFloat(item) >= x1));
            });
        } else if (fills.has(this.fillStyle)) {
            add(x, y, `fill ${this.fillStyle} ${width.toFixed(2)}`);
        }
    };
    ctx.fillText = function (text, x, y) { add(x, y, `text ${this.font} ${this.fillStyle} ${text}`); };
    ctx.strokeRect = function (x, y) { add(x, y, `border ${this.strokeStyle}`); };
    // Row-aligned, full-width copies: drawImage(source, dx, dy) or the 8-argument form
    ctx.drawImage = (source, ...args) => {
        const from = new Map(source.rows);
        if (args.length === 2) {
            rows.clear();
            for (const [row, items] of from) rows.set(row + args[1] / rowHeight, [...items]);
            return;
        }
        const [, sy, , height, , dy] = args;
        rowsIn(dy, dy + height, (row) => rows.set(row, [...(from.get(row + (sy - dy) / rowHeight) ?? [])]));
    };
}

// From now on, every canvas a ladder creates keeps a screen model in canvas.rows
function trackScreens(rowHeight = 24, colors = require('../.test-build/main.js').DEFAULT_COLORS) {
    screenOptions = { rowHeight, colors };
}

// What rows [0, rowCount) of a canvas show, each row's items sorted
const screenRows = (canvas, rowCount) =>
    Array.from({ length: rowCount }, (_, row) => [...(canvas.rows.get(row) ?? [])].sort());

// Canvas stub that records listeners so tests can dispatch input events
function fakeCanvas() {
    const listeners = {};
    let width = 0;
    const canvas = {
        // Setting the width clears a canvas
        get width() { return width; },
        set width(value) { width = value; canvas.rows?.clear(); },
        height: 0,
        tabIndex: -1,
        style: {},
        listeners,
        getContext: () => fakeContext(canvas),
        addEventListener: (type, fn) => { listeners[type] = fn; },
        removeEventListener: (type, fn) => { if (listeners[type] === fn) delete listeners[type]; },
        focus: noop,
        getBoundingClientRect: () => ({ left: 0, top: 0 })
    };
    return canvas;
}

globalThis.document = { createElement: () => fakeCanvas() };
globalThis.OffscreenCanvas = class {
    getContext() { return fakeContext(this); }
};
// Observers are kept so resizeContainers() can fire them, as a container resize does
const resizeCallbacks = new Set();
globalThis.ResizeObserver = class {
    constructor(callback) { this.callback = callback; }
    observe() { resizeCallbacks.add(this.callback); }
    disconnect() { resizeCallbacks.delete(this.callback); }
};
const resizeContainers = () => { for (const callback of resizeCallbacks) callback([]); };
globalThis.requestAnimationFrame = () => 0; // Tests render explicitly, no loop
globalThis.cancelAnimationFrame = noop;
globalThis.localStorage = { getItem: () => null, setItem: noop };

const fakeContainer = (clientWidth = 800) => ({ clientWidth, appendChild: noop, removeChild: noop });

// Snapshot the book and render it, even when nothing changed
const render = (ladder) => ladder.renderer.render(ladder.getSnapshot());

// One rAF frame as the render loop runs it: idle ladders skip the snapshot and render
const frame = (ladder) => ladder.renderFrame();

module.exports = { ...require('../.test-build/main.js'), fakeContainer, render, frame, trackScreens, screenRows, resizeContainers };
