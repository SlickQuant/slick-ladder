/**
 * TypeScript type definitions for WASM exports
 */

export interface WasmExports {
    Initialize(maxLevels: number, tickSize: number): void;
    HasNewSnapshot(): boolean;
    GetLatestSnapshot(): string;
    ProcessPriceLevelUpdate(side: number, price: number, quantity: number, numOrders: number): void;
    ProcessPriceLevelUpdateNoFlush(side: number, price: number, quantity: number, numOrders: number): void;
    SetDataMode(mode: number): void;
    ProcessOrderUpdate(orderId: number, side: number, price: number, quantity: number, priority: number, updateType: number): void;
    ProcessOrderUpdateNoFlush(orderId: number, side: number, price: number, quantity: number, priority: number, updateType: number): void;
    Flush(): void;
    GetBestBid(): number;
    GetBestAsk(): number;
    GetMidPrice(): number;
    GetSpread(): number;
    GetBidCount(): number;
    GetAskCount(): number;
    Clear(): void;
    ClearPendingUpdates(): void;
    GetMetrics(): string;
}

export interface DotNetRuntime {
    MONO: any;
    BINDING: any;
    Module: any;
    getAssemblyExports(assemblyName: string): Promise<WasmExports>;
}

export interface WasmModule {
    dotnet: DotNetRuntime;
    exports: WasmExports;
}

/**
 * Messages sent to the WASM worker
 */
export type WorkerRequest =
    | { type: 'init'; maxLevels: number; tickSize: number }
    | { type: 'update'; side: number; price: number; quantity: number; numOrders: number; generation: number }
    | { type: 'batch'; updates: Array<{ side: number; price: number; quantity: number; numOrders: number }>; generation: number }
    | { type: 'setDataMode'; mode: number }
    | { type: 'orderUpdate'; orderId: number; side: number; price: number; quantity: number; priority: number; updateType: number; generation: number }
    | { type: 'orderBatch'; updates: Array<{ orderId: number; side: number; price: number; quantity: number; priority: number; updateType: number }>; generation: number }
    | { type: 'flush'; generation: number }
    | { type: 'clear' }
    | { type: 'clearPending' }
    | { type: 'setGeneration'; generation: number }
    | { type: 'getMetrics' };

/**
 * Messages received from the WASM worker
 */
export type WorkerResponse =
    | { type: 'ready' }
    | { type: 'snapshot'; data: string }
    | { type: 'batchProcessed'; kind: 'price' | 'order'; durationMs: number; generation: number }
    | { type: 'metrics'; data: string }
    | { type: 'error'; message: string };
