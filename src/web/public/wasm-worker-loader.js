/**
 * Classic Worker loader for WASM
 * This file loads the .NET WASM runtime in a classic (non-module) worker context
 */

let wasmExports = null;
let isInitialized = false;

// Listen for messages from main thread
self.onmessage = async (e) => {
    const request = e.data;

    try {
        switch (request.type) {
            case 'init':
                await initializeWasm(request.maxLevels);
                break;

            case 'update':
                if (wasmExports) {
                    wasmExports.ProcessPriceLevelUpdate(
                        request.side,
                        request.price,
                        request.quantity,
                        request.numOrders
                    );
                }
                break;

            case 'batch':
                if (wasmExports) {
                    for (const update of request.updates) {
                        wasmExports.ProcessPriceLevelUpdate(
                            update.side,
                            update.price,
                            update.quantity,
                            update.numOrders
                        );
                    }
                    wasmExports.Flush();
                }
                break;

            case 'flush':
                if (wasmExports) {
                    wasmExports.Flush();
                }
                break;

            case 'clear':
                if (wasmExports) {
                    wasmExports.Clear();
                }
                break;

            case 'getMetrics':
                if (wasmExports) {
                    const metrics = wasmExports.GetMetrics();
                    postMessage({ type: 'metrics', data: metrics });
                }
                break;
        }
    } catch (error) {
        postMessage({
            type: 'error',
            message: error instanceof Error ? error.message : String(error)
        });
    }
};

async function initializeWasm(maxLevels) {
    if (isInitialized) {
        return;
    }

    try {
        // Load the .NET WASM runtime files
        // dotnet.runtime.js is the main runtime (classic script, not ES module)
        // dotnet.native.js contains the native bindings
        importScripts('/wasm/dotnet.runtime.js');
        importScripts('/wasm/dotnet.native.js');

        // Wait for the runtime to initialize
        const { dotnet } = await globalThis.getDotnetRuntime(0);

        // Get the assembly exports
        const exports = await dotnet.getAssemblyExports('SlickLadder.Core.dll');
        wasmExports = exports;

        // Set up snapshot callback
        wasmExports.SetSnapshotCallback((json) => {
            postMessage({ type: 'snapshot', data: json });
        });

        // Initialize the ladder
        wasmExports.Initialize(maxLevels);

        isInitialized = true;

        // Notify main thread that worker is ready
        postMessage({ type: 'ready' });
    } catch (error) {
        postMessage({
            type: 'error',
            message: `Failed to initialize WASM: ${error instanceof Error ? error.message : String(error)}`
        });
    }
}

// Error handling
self.onerror = (event) => {
    const message = typeof event === 'string'
        ? event
        : event instanceof ErrorEvent
            ? event.message
            : 'Unknown error';

    postMessage({
        type: 'error',
        message: `Worker error: ${message}`
    });
};
