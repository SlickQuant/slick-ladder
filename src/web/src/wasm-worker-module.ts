/**
 * Module Web Worker for WASM execution
 * This worker loads the .NET WASM runtime as an ES module
 */

import type { WorkerRequest, WorkerResponse, WasmExports } from './wasm-types';

let wasmExports: WasmExports | null = null;
let isInitialized = false;
let currentGeneration = 0;
const SLOW_BATCH_MS = 16;
let lastSlowBatchLogMs = 0;

// Filter out known harmless .NET runtime diagnostic messages
const originalConsoleError = console.error;
console.error = (...args: any[]) => {
    const message = args.join(' ');
    // Suppress the JsonSerializerIsReflectionDisabled diagnostic from .NET runtime
    if (message.includes('JsonSerializerIsReflectionDisabled')) {
        // This is a harmless diagnostic from the .NET WASM runtime internals
        // Our code doesn't use JsonSerializer (we use manual JSON serialization)
        return;
    }
    originalConsoleError.apply(console, args);
};

// Listen for messages from main thread
self.onmessage = async (e: MessageEvent<WorkerRequest>) => {
    const request = e.data;

    try {
        switch (request.type) {
            case 'init':
                await initializeWasm(request.maxLevels, request.tickSize);
                break;

            case 'update':
                if (request.generation !== currentGeneration) {
                    break;
                }
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
                if (request.generation !== currentGeneration) {
                    break;
                }
                if (wasmExports) {
                    const startTime = performance.now();
                    for (const update of request.updates) {
                        wasmExports.ProcessPriceLevelUpdateNoFlush(
                            update.side,
                            update.price,
                            update.quantity,
                            update.numOrders
                        );
                    }
                    wasmExports.Flush();
                    const endTime = performance.now();
                    const durationMs = endTime - startTime;
                    const now = performance.now();
                    if (durationMs >= SLOW_BATCH_MS && now - lastSlowBatchLogMs > 1000) {
                        console.log(`[WASM Worker] Batch processed in ${durationMs.toFixed(3)}ms`);
                        lastSlowBatchLogMs = now;
                    }
                    postMessage({
                        type: 'batchProcessed',
                        kind: 'price',
                        durationMs,
                        generation: currentGeneration
                    } as WorkerResponse);
                }
                break;

            case 'setDataMode':
                wasmExports?.SetDataMode(request.mode);
                break;

            case 'orderUpdate':
                if (request.generation !== currentGeneration) {
                    break;
                }
                if (wasmExports) {
                    wasmExports.ProcessOrderUpdate(
                        request.orderId,
                        request.side,
                        request.price,
                        request.quantity,
                        request.priority,
                        request.updateType
                    );
                }
                break;

            case 'orderBatch':
                if (request.generation !== currentGeneration) {
                    break;
                }
                if (wasmExports) {
                    const startTime = performance.now();
                    for (const update of request.updates) {
                        wasmExports.ProcessOrderUpdateNoFlush(
                            update.orderId,
                            update.side,
                            update.price,
                            update.quantity,
                            update.priority,
                            update.updateType
                        );
                    }
                    wasmExports.Flush();
                    const endTime = performance.now();
                    const durationMs = endTime - startTime;
                    const now = performance.now();
                    if (durationMs >= SLOW_BATCH_MS && now - lastSlowBatchLogMs > 1000) {
                        console.log(`[WASM Worker] Order batch processed in ${durationMs.toFixed(3)}ms`);
                        lastSlowBatchLogMs = now;
                    }
                    postMessage({
                        type: 'batchProcessed',
                        kind: 'order',
                        durationMs,
                        generation: currentGeneration
                    } as WorkerResponse);
                }
                break;

            case 'flush':
                if (request.generation !== currentGeneration) {
                    break;
                }
                wasmExports?.Flush();
                break;

            case 'clearPending':
                wasmExports?.ClearPendingUpdates();
                break;

            case 'setGeneration':
                currentGeneration = request.generation;
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
        const details = error instanceof Error
            ? (error.stack ?? error.message)
            : String(error);
        postMessage({
            type: 'error',
            message: `[${request.type}] ${details}`
        } as WorkerResponse);
    }
};

async function initializeWasm(maxLevels: number, tickSize: number): Promise<void> {
    if (isInitialized) {
        return;
    }

    try {
        console.log('[WASM Worker] Starting initialization...');

        // Dynamically import the .NET WASM runtime module
        // Use webpackIgnore to prevent webpack from trying to bundle it
        // @ts-ignore - dotnet.js is loaded at runtime, not bundled
        const { dotnet } = await import(/* webpackIgnore: true */ '/wasm/dotnet.js');
        console.log('[WASM Worker] Dotnet runtime imported');

        // Create the .NET runtime using the blazor.boot.json config
        // The AppBundle build output includes a proper boot config for Native AOT WASM
        // Disable integrity checks for development since hashes change on rebuild
        const runtime = await dotnet
            .withConfigSrc('/wasm/blazor.boot.json')
            .withConfig({
                disableIntegrityCheck: true,
                // Suppress runtime diagnostic messages
                diagnosticTracing: false,
                // @ts-ignore - runtime options may not be fully typed
                runtimeOptions: ['--runtime-arg', '--no-diagnostics']
            })
            .create();
        console.log('[WASM Worker] Runtime created');

        // Get the assembly exports from the runtime
        // Need to access the specific namespace and class that has JSExport methods
        const assembly = await runtime.getAssemblyExports('SlickLadder.Core.dll');
        const exports = assembly.SlickLadder.Core.Interop.WasmExports;
        wasmExports = exports as WasmExports;
        console.log('[WASM Worker] Assembly exports loaded');

        // Initialize the ladder with tick size
        wasmExports.Initialize(maxLevels, tickSize);
        console.log(`[WASM Worker] Ladder initialized with tickSize=${tickSize}`);

        isInitialized = true;

        // Start polling for snapshots (check every 16ms ~= 60fps)
        startSnapshotPolling();
        console.log('[WASM Worker] Snapshot polling started');

        // Notify main thread that worker is ready
        postMessage({ type: 'ready' } as WorkerResponse);
        console.log('[WASM Worker] Ready!');
    } catch (error) {
        console.error('[WASM Worker] Initialization error:', error);
        postMessage({
            type: 'error',
            message: `Failed to initialize WASM: ${error instanceof Error ? error.message : String(error)}`
        } as WorkerResponse);
    }
}

// Polling function to check for new snapshots
let snapshotCount = 0;
function startSnapshotPolling(): void {
    setInterval(() => {
        if (wasmExports && wasmExports.HasNewSnapshot()) {
            const snapshot = wasmExports.GetLatestSnapshot();
            snapshotCount++;
            if (snapshotCount % 60 === 0) {
                console.log(`[WASM Worker] Sent ${snapshotCount} snapshots`);
            }
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
