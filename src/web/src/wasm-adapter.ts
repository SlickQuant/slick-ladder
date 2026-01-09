import { Side, PriceLevel, OrderBookSnapshot } from './types';
import type { WorkerRequest, WorkerResponse } from './wasm-types';

/**
 * WASM backend adapter for PriceLadder
 * Provides the same API as the TypeScript implementation but uses WASM for processing
 */
export class WasmPriceLadder {
    private worker: Worker;
    private isReady: boolean = false;
    private readyPromise: Promise<void>;
    private snapshotCallback?: (snapshot: OrderBookSnapshot) => void;
    private updateCount: number = 0;

    constructor(maxLevels: number = 200, tickSize: number = 0.01) {
        // Create the Web Worker as a module worker
        // This allows it to import the .NET WASM runtime as an ES module
        this.worker = new Worker(new URL('./wasm-worker-module.ts', import.meta.url), {
            type: 'module'
        });

        // Set up message handler
        this.worker.onmessage = this.handleWorkerMessage.bind(this);
        this.worker.onerror = this.handleWorkerError.bind(this);

        // Initialize the worker
        this.readyPromise = new Promise<void>((resolve, reject) => {
            const timeout = setTimeout(() => {
                reject(new Error('WASM worker initialization timeout'));
            }, 10000);

            this.worker.addEventListener('message', (e: MessageEvent<WorkerResponse>) => {
                if (e.data.type === 'ready') {
                    clearTimeout(timeout);
                    this.isReady = true;
                    resolve();
                } else if (e.data.type === 'error') {
                    clearTimeout(timeout);
                    reject(new Error(e.data.message));
                }
            }, { once: true });
        });

        this.postMessage({ type: 'init', maxLevels, tickSize });
    }

    /**
     * Wait for WASM to be ready
     */
    public async waitForReady(): Promise<void> {
        return this.readyPromise;
    }

    /**
     * Set snapshot callback
     */
    public onSnapshot(callback: (snapshot: OrderBookSnapshot) => void): void {
        this.snapshotCallback = callback;
    }

    /**
     * Process a single price level update
     */
    public processUpdate(update: PriceLevel): void {
        if (!this.isReady) {
            console.warn('WASM not ready, update ignored');
            return;
        }

        this.updateCount++;
        this.postMessage({
            type: 'update',
            side: update.side,
            price: update.price,
            quantity: update.quantity,
            numOrders: update.numOrders
        });
    }

    /**
     * Process multiple updates in batch
     */
    public processBatch(updates: PriceLevel[]): void {
        if (!this.isReady) {
            console.warn('WASM not ready, batch ignored');
            return;
        }

        this.updateCount += updates.length;
        this.postMessage({
            type: 'batch',
            updates: updates.map(u => ({
                side: u.side,
                price: u.price,
                quantity: u.quantity,
                numOrders: u.numOrders
            }))
        });
    }

    /**
     * Flush pending updates
     */
    public flush(): void {
        if (this.isReady) {
            this.postMessage({ type: 'flush' });
        }
    }

    /**
     * Clear all data
     */
    public clear(): void {
        if (this.isReady) {
            this.updateCount = 0;
            this.postMessage({ type: 'clear' });
        }
    }

    /**
     * Get metrics
     */
    public async getMetrics(): Promise<any> {
        if (!this.isReady) {
            return {
                updateCount: 0,
                bidLevels: 0,
                askLevels: 0
            };
        }

        return new Promise((resolve) => {
            const handler = (e: MessageEvent<WorkerResponse>) => {
                if (e.data.type === 'metrics') {
                    this.worker.removeEventListener('message', handler);
                    const metrics = JSON.parse(e.data.data);
                    resolve({
                        ...metrics,
                        updateCount: this.updateCount
                    });
                }
            };

            this.worker.addEventListener('message', handler);
            this.postMessage({ type: 'getMetrics' });
        });
    }

    /**
     * Terminate the worker
     */
    public destroy(): void {
        this.worker.terminate();
    }

    private postMessage(message: WorkerRequest): void {
        this.worker.postMessage(message);
    }

    private handleWorkerMessage(e: MessageEvent<WorkerResponse>): void {
        const response = e.data;

        switch (response.type) {
            case 'snapshot':
                if (this.snapshotCallback) {
                    const data = JSON.parse(response.data);
                    const snapshot: OrderBookSnapshot = {
                        bestBid: data.bestBid,
                        bestAsk: data.bestAsk,
                        midPrice: data.midPrice,
                        bids: data.bids.map((b: any) => ({
                            price: b.price,
                            quantity: b.quantity,
                            numOrders: b.numOrders,
                            side: b.side as Side,
                            isDirty: true,
                            hasOwnOrders: false
                        })),
                        asks: data.asks.map((a: any) => ({
                            price: a.price,
                            quantity: a.quantity,
                            numOrders: a.numOrders,
                            side: a.side as Side,
                            isDirty: true,
                            hasOwnOrders: false
                        })),
                        timestamp: new Date(data.timestamp / 10000).getTime()
                    };
                    this.snapshotCallback(snapshot);
                }
                break;

            case 'error':
                console.error('WASM worker error:', response.message);
                break;
        }
    }

    private handleWorkerError(error: ErrorEvent): void {
        console.error('WASM worker error event:', error.message);
    }
}
