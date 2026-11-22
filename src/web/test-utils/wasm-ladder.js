// Loads WasmPriceLadder (src/wasm-adapter.ts) for node tests, with a stub Worker the test drives.
// The test build is CommonJS, which can't compile the adapter's import.meta.url (webpack's worker
// URL), so the source is transpiled here with that replaced. Its imports resolve to the test build
const fs = require('fs');
const path = require('path');
const Module = require('module');
const ts = require('typescript');
require('./ladder-harness'); // Browser stubs, and the test build

// Records what the ladder posts. emit() delivers a message from the worker
class FakeWorker {
    constructor() {
        this.posted = [];
        this.listeners = [];
        this.onmessage = null;
        FakeWorker.last = this;
    }

    postMessage(message) { this.posted.push(message); }
    addEventListener(type, listener) { if (type === 'message') this.listeners.push(listener); }
    terminate() {}

    emit(data) {
        const event = { data };
        const listeners = this.listeners;
        this.listeners = []; // The adapter's only listener is { once: true }
        for (const listener of listeners) listener(event);
        this.onmessage?.(event);
    }
}
globalThis.Worker = FakeWorker;

const source = path.join(__dirname, '../src/wasm-adapter.ts');
const { outputText } = ts.transpileModule(
    fs.readFileSync(source, 'utf8').replace(/import\.meta\.url/g, JSON.stringify('file:///wasm-worker-module.js')),
    { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } });
const adapter = new Module(source);
adapter.filename = path.join(__dirname, '../.test-build/wasm-adapter.js'); // So './main' is the test build's
adapter.paths = Module._nodeModulePaths(path.dirname(adapter.filename));
adapter._compile(outputText, adapter.filename);

module.exports = { WasmPriceLadder: adapter.exports.WasmPriceLadder, FakeWorker };
