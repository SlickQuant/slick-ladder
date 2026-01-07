/**
 * Web Worker for WASM execution
 * Runs the C# SlickLadder core in a separate thread
 */

import type { WorkerRequest, WorkerResponse, WasmExports } from './wasm-types';

let wasmExports: WasmExports | null = null;
let isInitialized = false;

// Listen for messages from main thread
self.onmessage = async (e: MessageEvent<WorkerRequest>) => {
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
                wasmExports?.Flush();
                break;

            case 'clear':
                wasmExports?.Clear();
                break;

            case 'getMetrics':
                if (wasmExports) {
                    const metrics = wasmExports.GetMetrics();
                    postMessage({ type: 'metrics', data: metrics } as WorkerResponse);
                }
                break;
        }
    } catch (error) {
        postMessage({
            type: 'error',
            message: error instanceof Error ? error.message : String(error)
        } as WorkerResponse);
    }
};

async function initializeWasm(maxLevels: number): Promise<void> {
    if (isInitialized) {
        return;
    }

    try {
        // Load the .NET WASM runtime
        // Use fetch + eval to load the dotnet.js script in the worker
        const response = await fetch('/wasm/dotnet.js');
        const scriptContent = await response.text();

        // Evaluate the script in the worker global scope
        // Use indirect eval to evaluate in global scope
        (0, eval)(scriptContent);

        // Wait for the runtime to initialize
        // @ts-ignore - getDotnetRuntime is added by dotnet.js
        const { dotnet } = await globalThis.getDotnetRuntime(0);

        // Get the assembly exports
        const exports = await dotnet.getAssemblyExports('SlickLadder.Core.dll');
        wasmExports = exports as WasmExports;

        // Initialize the ladder
        wasmExports.Initialize(maxLevels);

        isInitialized = true;

        // Start polling for snapshots (check every 16ms ~= 60fps)
        startSnapshotPolling();

        // Notify main thread that worker is ready
        postMessage({ type: 'ready' } as WorkerResponse);
    } catch (error) {
        postMessage({
            type: 'error',
            message: `Failed to initialize WASM: ${error instanceof Error ? error.message : String(error)}`
        } as WorkerResponse);
    }
}

// Polling function to check for new snapshots
function startSnapshotPolling(): void {
    setInterval(() => {
        if (wasmExports && wasmExports.HasNewSnapshot()) {
            const snapshot = wasmExports.GetLatestSnapshot();
            postMessage({ type: 'snapshot', data: snapshot } as WorkerResponse);
        }
    }, 16); // Poll at ~60fps
}

// Error handling
self.onerror = (event: string | Event) => {
    const message = typeof event === 'string'
        ? event
        : event instanceof ErrorEvent
            ? event.message
            : 'Unknown error';

    postMessage({
        type: 'error',
        message: `Worker error: ${message}`
    } as WorkerResponse);
};

export {};
