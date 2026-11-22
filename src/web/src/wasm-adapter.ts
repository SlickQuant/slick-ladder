import { PriceLevel, OrderBookSnapshot, OrderUpdate, OrderUpdateType, PriceLadderConfig, Side, Trade } from './types';
import type { WorkerRequest, WorkerResponse } from './wasm-types';
import { PriceLadder } from './main';
import { aggregateLevels } from './aggregation';
import { parseWasmSnapshot } from './wasm-snapshot';

/**
 * WASM-powered Price Ladder component
 * Extends PriceLadder and replaces data processing with WASM backend
 */
export class WasmPriceLadder extends PriceLadder {
    private worker: Worker;
    private isReady: boolean = false;
    private readyPromise: Promise<void>;
    private generation: number = 0;
    private priceBatchInFlight: boolean = false;
    private orderBatchInFlight: boolean = false;
    private priceBatchQueue: PriceLevel[][] = [];
    private orderBatchQueue: Array<Array<{ update: OrderUpdate; type: OrderUpdateType }>> = [];
    private lastSnapshot: OrderBookSnapshot | null = null;
    private lastWorkerSnapshot: OrderBookSnapshot | null = null; // Before aggregation
    // key: `${side}-${price}` — levels that should have hasOwnOrders=true
    private ownOrderOverrides: Map<string, true> = new Map();
    private cellsFrame: number = 0; // rAF that re-renders new working orders or trades, 0 when none

    constructor(config: PriceLadderConfig) {
        super(config);

        // Stop the base class render loop - WASM will drive rendering via worker messages
        this.stopBaseRenderLoop();

        // Set up interaction handler to request re-render on user interactions
        this.setupWasmInteractions();

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

        const tickSize = config.tickSize || 0.01;
        const maxLevels = config.visibleLevels || 50;
        this.postMessage({ type: 'init', maxLevels, tickSize });
    }

    /**
     * Setup WASM-specific interactions
     * In WASM mode, user interactions (scroll, zoom) need to trigger a re-render
     */
    private setupWasmInteractions(): void {
        // Access the interaction handler through type assertion since it's private in base class
        const interactionHandler = (this as any).interactionHandler;
        if (interactionHandler) {
            // When user interactions change the viewport (scroll, zoom), re-render with last snapshot
            interactionHandler.onRenderNeeded = () => {
                this.reRenderLastSnapshot();
            };
        }
    }

    /**
     * Re-render the last received snapshot
     * Used when viewport changes (scroll, zoom) but data hasn't changed
     */
    private reRenderLastSnapshot(): void {
        if (this.lastSnapshot) {
            this.renderer.render(this.lastSnapshot);
        }
    }

    /**
     * Stop the base class render loop since WASM worker will drive rendering
     */
    private stopBaseRenderLoop(): void {
        // Access the private rafId through type assertion
        const rafId = (this as any).rafId;
        if (rafId) {
            cancelAnimationFrame(rafId);
            (this as any).rafId = 0;
        }
    }

    /**
     * Wait for WASM to be ready
     */
    public async waitForReady(): Promise<void> {
        return this.readyPromise;
    }

    /**
     * Process a single price level update
     * Overrides base class to use WASM worker
     */
    public override processUpdate(update: PriceLevel): void {
        if (!this.isReady) {
            console.warn('WASM not ready, update ignored');
            return;
        }

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
     * Overrides base class to use WASM worker
     */
    public override processBatch(updates: PriceLevel[]): void {
        if (!this.isReady) {
            console.warn('WASM not ready, batch ignored');
            return;
        }

        this.enqueuePriceBatch(updates);
    }

    /**
     * Process a single order update (MBO mode)
     * Overrides base class to use WASM worker
     */
    public override processOrderUpdate(update: OrderUpdate, type: OrderUpdateType): void {
        if (!this.isReady) {
            console.warn('WASM not ready, order update ignored');
            return;
        }

        this.postMessage({
            type: 'orderUpdate',
            orderId: update.orderId,
            side: update.side,
            price: update.price,
            quantity: update.quantity,
            priority: update.priority,
            updateType: type,
            isOwnOrder: update.isOwnOrder ? 1 : 0,
            generation: this.generation
        });
    }

    /**
     * Process multiple order updates in batch (MBO mode)
     * Overrides base class to use WASM worker
     */
    public override processOrderBatch(updates: Array<{ update: OrderUpdate; type: OrderUpdateType }>): void {
        if (!this.isReady) {
            console.warn('WASM not ready, order batch ignored');
            return;
        }

        this.enqueueOrderBatch(updates);
    }

    /**
     * Re-render the last snapshot after a layout toggle (getSnapshot() would be empty: the book
     * lives in the worker)
     */
    protected override rerender(): void {
        this.reRenderLastSnapshot();
    }

    /**
     * Working orders and trades stay on this thread and are added to each worker snapshot. A
     * change re-renders the last snapshot on the next frame, so it shows while the book is idle
     */
    public override setWorkingOrders(price: number, side: Side, quantity: number, count: number): void {
        super.setWorkingOrders(price, side, quantity, count);
        this.scheduleCellsRender();
    }

    public override processTrade(trade: Trade): void {
        super.processTrade(trade);
        this.scheduleCellsRender();
    }

    // One render per frame however many trades arrive
    private scheduleCellsRender(): void {
        if (this.cellsFrame !== 0 || !this.lastSnapshot) {
            return;
        }
        this.cellsFrame = requestAnimationFrame(() => {
            this.cellsFrame = 0;
            if (this.lastSnapshot) {
                this.lastSnapshot = this.attachCells({ ...this.lastSnapshot });
                this.renderer.render(this.lastSnapshot);
            }
        });
    }

    /**
     * Set data mode (PriceLevel or MBO)
     * Overrides base class to notify WASM worker
     */
    public override setDataMode(mode: 'PriceLevel' | 'MBO'): void {
        if (!this.isReady) {
            console.warn('WASM not ready, mode change ignored');
            return;
        }

        // CRITICAL: Set mode in WASM worker BEFORE calling super.setDataMode
        // because super.setDataMode calls clear() which will send data to worker
        const modeValue = mode === 'MBO' ? 1 : 0;
        this.postMessage({ type: 'setDataMode', mode: modeValue });

        // Now call base class which will clear local state
        super.setDataMode(mode);
    }

    /**
     * Clear all data
     * Overrides base class to clear WASM worker state
     */
    public override clear(): void {
        super.clear();
        this.postMessage({ type: 'clear' });
    }

    /**
     * Destroy the ladder and clean up resources
     * Overrides base class to terminate worker
     */
    public override destroy(): void {
        super.destroy();
        cancelAnimationFrame(this.cellsFrame);
        this.worker.terminate();
    }

    /**
     * Mark a price level as having (or not having) own orders.
     * Overrides base class: stores the override and immediately re-renders.
     * The override is applied to each snapshot received from the WASM worker.
     */
    public override setHasOwnOrders(price: number, side: Side, value: boolean): void {
        const key = `${side}-${price}`;
        if (value) {
            this.ownOrderOverrides.set(key, true);
        } else {
            this.ownOrderOverrides.delete(key);
        }
        this.showWorkerSnapshot();
    }

    /**
     * Update display tick size at runtime.
     * Overrides base class to re-aggregate and re-render the last WASM snapshot.
     */
    public override updateConfig(partial: Partial<Pick<PriceLadderConfig, 'tickSize' | 'displayTickSize'>>): void {
        super.updateConfig(partial);
        this.showWorkerSnapshot();
    }

    /**
     * Show the worker's last snapshot: own-order flags first (the worker's snapshots don't carry
     * them), then display tick aggregation, then our working orders and last trade. Every change
     * rebuilds from the worker's snapshot this way, so no flag is lost and nothing is aggregated twice
     */
    private showWorkerSnapshot(): void {
        const snapshot = this.lastWorkerSnapshot;
        if (!snapshot) {
            return;
        }
        this.applyOwnOrderOverrides(snapshot);
        const config = (this as any).config;
        const dts: number = config.displayTickSize ?? config.tickSize ?? 0.01;
        const ts: number = config.tickSize ?? 0.01;
        this.lastSnapshot = this.attachCells(dts > ts ? this.applyAggregation(snapshot, dts, ts) : { ...snapshot });
        this.renderer.render(this.lastSnapshot);
    }

    private applyAggregation(snapshot: OrderBookSnapshot, dts: number, ts: number): OrderBookSnapshot {
        const bids = aggregateLevels(snapshot.bids, dts, ts, false);
        const asks = aggregateLevels(snapshot.asks, dts, ts, true);
        const bestBid = bids.length > 0 ? bids[bids.length - 1].price : null;
        const bestAsk = asks.length > 0 ? asks[0].price : null;
        const midPrice = bestBid !== null && bestAsk !== null ? (bestBid + bestAsk) / 2 : null;
        const aggregated: OrderBookSnapshot = { ...snapshot, bids, asks, bestBid, bestAsk, midPrice };
        this.aggregateOrders(aggregated);
        return aggregated;
    }

    private applyOwnOrderOverrides(snapshot: OrderBookSnapshot): void {
        for (const level of snapshot.bids) {
            level.hasOwnOrders = this.ownOrderOverrides.has(`${level.side}-${level.price}`);
        }
        for (const level of snapshot.asks) {
            level.hasOwnOrders = this.ownOrderOverrides.has(`${level.side}-${level.price}`);
        }
    }

    /**
     * Drop pending batched updates (for testing/demo purposes)
     */
    public dropPendingUpdates(): void {
        this.priceBatchQueue = [];
        this.orderBatchQueue = [];
        this.priceBatchInFlight = false;
        this.orderBatchInFlight = false;
        this.postMessage({ type: 'clearPending' });
    }

    private enqueuePriceBatch(updates: PriceLevel[]): void {
        this.priceBatchQueue.push(updates);
        if (!this.priceBatchInFlight) {
            this.processPriceBatchQueue();
        }
    }

    private enqueueOrderBatch(updates: Array<{ update: OrderUpdate; type: OrderUpdateType }>): void {
        this.orderBatchQueue.push(updates);
        if (!this.orderBatchInFlight) {
            this.processOrderBatchQueue();
        }
    }

    private processPriceBatchQueue(): void {
        if (this.priceBatchQueue.length === 0) {
            this.priceBatchInFlight = false;
            return;
        }

        this.priceBatchInFlight = true;
        const batch = this.priceBatchQueue.shift()!;

        this.postMessage({
            type: 'batch',
            updates: batch.map(u => ({
                side: u.side,
                price: u.price,
                quantity: u.quantity,
                numOrders: u.numOrders
            })),
            generation: this.generation
        });
    }

    private processOrderBatchQueue(): void {
        if (this.orderBatchQueue.length === 0) {
            this.orderBatchInFlight = false;
            return;
        }

        this.orderBatchInFlight = true;
        const batch = this.orderBatchQueue.shift()!;

        this.postMessage({
            type: 'orderBatch',
            updates: batch.map(item => ({
                orderId: item.update.orderId,
                side: item.update.side,
                price: item.update.price,
                quantity: item.update.quantity,
                priority: item.update.priority,
                updateType: item.type,
                isOwnOrder: item.update.isOwnOrder ? 1 : 0
            })),
            generation: this.generation
        });
    }

    private handleWorkerMessage(e: MessageEvent<WorkerResponse>): void {
        const response = e.data;

        switch (response.type) {
            case 'snapshot':
                // Render the snapshot from WASM
                this.renderSnapshot(response.data);
                break;

            case 'batchProcessed':
                if (response.generation !== this.generation) {
                    break;
                }

                if (response.kind === 'price') {
                    this.processPriceBatchQueue();
                } else if (response.kind === 'order') {
                    this.processOrderBatchQueue();
                }
                break;

            case 'error':
                console.error('[WASM Worker Error]', response.message);
                break;

            case 'metrics':
                // Optionally handle metrics
                break;
        }
    }

    private handleWorkerError(event: ErrorEvent): void {
        console.error('[WASM Worker Error]', event.message, event.error);
    }

    private postMessage(message: WorkerRequest): void {
        try {
            this.worker.postMessage(message);
        } catch (error) {
            console.error('[WASM Worker] Failed to post message:', error);
        }
    }

    /**
     * Render a snapshot received from WASM worker
     */
    private renderSnapshot(snapshotData: any): void {
        // Unchanged levels keep the last worker snapshot's order arrays, so the renderer and the
        // order buckets tell them by reference. That snapshot is the one every rebuild starts from
        this.lastWorkerSnapshot = parseWasmSnapshot(snapshotData, this.lastWorkerSnapshot);
        this.renderer.recordFrame();
        this.showWorkerSnapshot();
    }
}
