import { Side, PriceLevel, OrderBookSnapshot, Order, OrderUpdate, OrderUpdateType, DirtyLevelChange } from './types';
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
    private tickSize: number;
    private generation: number = 0;
    private priceBatchInFlight: boolean = false;
    private orderBatchInFlight: boolean = false;
    private priceBatchQueue: PriceLevel[][] = [];
    private orderBatchQueue: Array<Array<{ update: OrderUpdate; type: OrderUpdateType }>> = [];

    constructor(maxLevels: number = 200, tickSize: number = 0.01) {
        this.tickSize = tickSize;
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
            numOrders: update.numOrders,
            generation: this.generation
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

        this.enqueuePriceBatch(updates);
    }

    /**
     * Set data mode (PriceLevel or MBO)
     */
    public setDataMode(mode: 'PriceLevel' | 'MBO'): void {
        if (!this.isReady) {
            console.warn('WASM not ready, mode change ignored');
            return;
        }

        const modeValue = mode === 'MBO' ? 1 : 0;
        this.postMessage({ type: 'setDataMode', mode: modeValue });
    }

    /**
     * Process a single order update (MBO mode)
     */
    public processOrderUpdate(update: OrderUpdate, type: OrderUpdateType): void {
        if (!this.isReady) {
            console.warn('WASM not ready, order update ignored');
            return;
        }

        this.updateCount++;
        this.postMessage({
            type: 'orderUpdate',
            orderId: update.orderId,
            side: update.side,
            price: this.roundToTick(update.price),
            quantity: update.quantity,
            priority: update.priority,
            updateType: type,
            isOwnOrder: update.isOwnOrder ? 1 : 0,
            generation: this.generation
        });
    }

    /**
     * Process multiple order updates in batch (MBO mode)
     */
    public processOrderBatch(updates: Array<{ update: OrderUpdate; type: OrderUpdateType }>): void {
        if (!this.isReady) {
            console.warn('WASM not ready, order batch ignored');
            return;
        }

        this.enqueueOrderBatch(updates);
    }

    /**
     * Flush pending updates
     */
    public flush(): void {
        if (this.isReady) {
            this.postMessage({ type: 'flush', generation: this.generation });
        }
    }

    /**
     * Clear all data
     */
    public clear(): void {
        if (this.isReady) {
            this.updateCount = 0;
            this.bumpGeneration();
            this.postMessage({ type: 'clear' });
        }
    }

    public dropPendingUpdates(): void {
        if (this.isReady) {
            this.bumpGeneration();
            this.postMessage({ type: 'clearPending' });
        }
    }

    public isBackpressured(): boolean {
        return this.priceBatchInFlight
            || this.orderBatchInFlight
            || this.priceBatchQueue.length > 0
            || this.orderBatchQueue.length > 0;
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

    private bumpGeneration(): void {
        this.generation += 1;
        this.priceBatchInFlight = false;
        this.orderBatchInFlight = false;
        this.priceBatchQueue = [];
        this.orderBatchQueue = [];
        this.postMessage({ type: 'setGeneration', generation: this.generation });
    }

    private handleWorkerMessage(e: MessageEvent<WorkerResponse>): void {
        const response = e.data;

        switch (response.type) {
            case 'snapshot':
                if (this.snapshotCallback) {
                    const data = JSON.parse(response.data);
                    const bidOrders = this.parseOrderMap(data.bidOrders);
                    const askOrders = this.parseOrderMap(data.askOrders);
                    const dirtyChanges: DirtyLevelChange[] | undefined = Array.isArray(data.dirtyChanges)
                        ? data.dirtyChanges.map((change: any) => ({
                            price: this.roundToTick(change.price),
                            side: change.side as Side,
                            isRemoval: !!change.isRemoval,
                            isAddition: !!change.isAddition
                        }))
                        : undefined;
                    const structuralChange = typeof data.structuralChange === 'boolean'
                        ? data.structuralChange
                        : undefined;
                    const snapshot: OrderBookSnapshot = {
                        bestBid: data.bestBid,
                        bestAsk: data.bestAsk,
                        midPrice: data.midPrice,
                        bids: data.bids.map((b: any) => ({
                            price: this.roundToTick(b.price),
                            quantity: b.quantity,
                            numOrders: b.numOrders,
                            side: b.side as Side,
                            isDirty: true,
                            hasOwnOrders: false
                        })),
                        asks: data.asks.map((a: any) => ({
                            price: this.roundToTick(a.price),
                            quantity: a.quantity,
                            numOrders: a.numOrders,
                            side: a.side as Side,
                            isDirty: true,
                            hasOwnOrders: false
                        })),
                        timestamp: new Date(data.timestamp / 10000).getTime(),
                        bidOrders,
                        askOrders,
                        dirtyChanges,
                        structuralChange
                    };
                    this.snapshotCallback(snapshot);
                }
                break;

            case 'batchProcessed':
                if (response.generation !== this.generation) {
                    break;
                }
                if (response.kind === 'price') {
                    this.priceBatchInFlight = false;
                    this.sendNextPriceBatch();
                } else {
                    this.orderBatchInFlight = false;
                    this.sendNextOrderBatch();
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

    private parseOrderMap(source: any): Map<number, Order[]> | null {
        if (!source) {
            return null;
        }

        const map = new Map<number, Order[]>();
        for (const key of Object.keys(source)) {
            const price = Number(key);
            if (!Number.isFinite(price)) {
                continue;
            }

            const roundedPrice = this.roundToTick(price);
            const orders = (source[key] as any[]).map((order) => ({
                orderId: order.orderId,
                quantity: order.quantity,
                priority: order.priority,
                isOwnOrder: order.isOwnOrder ?? false
            }));
            map.set(roundedPrice, orders);
        }

        return map;
    }

    private roundToTick(price: number): number {
        return Math.round(price / this.tickSize) * this.tickSize;
    }

    private enqueuePriceBatch(updates: PriceLevel[]): void {
        if (updates.length === 0) {
            return;
        }

        this.updateCount += updates.length;
        this.priceBatchQueue.push(updates);
        if (!this.priceBatchInFlight) {
            this.sendNextPriceBatch();
        }
    }

    private sendNextPriceBatch(): void {
        if (this.priceBatchInFlight) {
            return;
        }

        const updates = this.priceBatchQueue.shift();
        if (!updates) {
            return;
        }

        this.priceBatchInFlight = true;
        this.postMessage({
            type: 'batch',
            updates: updates.map(u => ({
                side: u.side,
                price: u.price,
                quantity: u.quantity,
                numOrders: u.numOrders
            })),
            generation: this.generation
        });
    }

    private enqueueOrderBatch(updates: Array<{ update: OrderUpdate; type: OrderUpdateType }>): void {
        if (updates.length === 0) {
            return;
        }

        this.updateCount += updates.length;
        this.orderBatchQueue.push(updates);
        if (!this.orderBatchInFlight) {
            this.sendNextOrderBatch();
        }
    }

    private sendNextOrderBatch(): void {
        if (this.orderBatchInFlight) {
            return;
        }

        const updates = this.orderBatchQueue.shift();
        if (!updates) {
            return;
        }

        this.orderBatchInFlight = true;
        this.postMessage({
            type: 'orderBatch',
            updates: updates.map(item => ({
                orderId: item.update.orderId,
                side: item.update.side,
                price: this.roundToTick(item.update.price),
                quantity: item.update.quantity,
                priority: item.update.priority,
                updateType: item.type,
                isOwnOrder: item.update.isOwnOrder ? 1 : 0
            })),
            generation: this.generation
        });
    }
}
