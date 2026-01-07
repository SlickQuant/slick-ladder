/**
 * TypeScript type definitions for WASM exports
 */

export interface WasmExports {
    Initialize(maxLevels: number): void;
    HasNewSnapshot(): boolean;
    GetLatestSnapshot(): string;
    ProcessPriceLevelUpdate(side: number, price: number, quantity: number, numOrders: number): void;
    Flush(): void;
    GetBestBid(): number;
    GetBestAsk(): number;
    GetMidPrice(): number;
    GetSpread(): number;
    GetBidCount(): number;
    GetAskCount(): number;
    Clear(): void;
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
    | { type: 'init'; maxLevels: number }
    | { type: 'update'; side: number; price: number; quantity: number; numOrders: number }
    | { type: 'batch'; updates: Array<{ side: number; price: number; quantity: number; numOrders: number }> }
    | { type: 'flush' }
    | { type: 'clear' }
    | { type: 'getMetrics' };

/**
 * Messages received from the WASM worker
 */
export type WorkerResponse =
    | { type: 'ready' }
    | { type: 'snapshot'; data: string }
    | { type: 'metrics'; data: string }
    | { type: 'error'; message: string };
