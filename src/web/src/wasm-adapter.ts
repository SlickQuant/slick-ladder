import { PriceLevel, OrderBookSnapshot, OrderUpdate, OrderUpdateType, PriceLadderConfig } from './types';
import type { WorkerRequest, WorkerResponse } from './wasm-types';
import { PriceLadder } from './main';
import { CanvasRenderer } from './canvas-renderer';

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
     * Set volume bars visibility
     * Overrides base class to re-render last snapshot instead of calling getSnapshot()
     */
    public override setShowVolumeBars(show: boolean): void {
        const config = (this as any).config;
        config.showVolumeBars = show;
        // Update width based on new settings
        const newWidth = (this as any).calculateWidth();
        config.width = newWidth;

        // Recreate renderer with new settings
        this.renderer = new CanvasRenderer(
            (this as any).canvas,
            config.width,
            config.height,
            config.rowHeight,
            config.colors!,
            config.showVolumeBars,
            config.showOrderCount,
            config.tickSize
        );

        // Update interaction handler with new renderer
        const interactionHandler = (this as any).interactionHandler;
        interactionHandler.setRenderer(this.renderer);

        // Re-render last snapshot (not getSnapshot() which would be empty)
        this.reRenderLastSnapshot();
    }

    /**
     * Set order count visibility
     * Overrides base class to re-render last snapshot instead of calling getSnapshot()
     */
    public override setShowOrderCount(show: boolean): void {
        const config = (this as any).config;
        config.showOrderCount = show;
        // Update width based on new settings
        const newWidth = (this as any).calculateWidth();
        config.width = newWidth;

        // Recreate renderer with new settings
        this.renderer = new CanvasRenderer(
            (this as any).canvas,
            config.width,
            config.height,
            config.rowHeight,
            config.colors!,
            config.showVolumeBars,
            config.showOrderCount,
            config.tickSize
        );

        // Update interaction handler with new renderer
        const interactionHandler = (this as any).interactionHandler;
        interactionHandler.setRenderer(this.renderer);

        // Re-render last snapshot (not getSnapshot() which would be empty)
        this.reRenderLastSnapshot();
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
        this.worker.terminate();
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
        // Parse JSON string if needed
        let parsedData = snapshotData;
        if (typeof snapshotData === 'string') {
            parsedData = JSON.parse(snapshotData);
        }

        // Transform C# PascalCase to TypeScript camelCase
        const bidsArray = parsedData.Bids ?? parsedData.bids ?? [];
        const asksArray = parsedData.Asks ?? parsedData.asks ?? [];

        // Transform BookLevel objects
        const transformLevel = (level: any) => ({
            price: level.Price ?? level.price,
            quantity: level.Quantity ?? level.quantity,
            numOrders: level.NumOrders ?? level.numOrders ?? 0,
            side: level.Side ?? level.side,
            isDirty: level.IsDirty ?? level.isDirty ?? false,
            hasOwnOrders: level.HasOwnOrders ?? level.hasOwnOrders ?? false
        });

        // Transform order maps from plain objects to Maps (MBO mode)
        const transformOrderMap = (orderMapData: any): Map<number, OrderUpdate[]> | null => {
            if (!orderMapData) return null;

            const orderMap = new Map<number, OrderUpdate[]>();
            // Handle both PascalCase and camelCase property names
            const entries = Object.entries(orderMapData);

            for (const [priceStr, ordersArray] of entries) {
                const price = parseFloat(priceStr);
                const orders = (ordersArray as any[]).map((order: any) => ({
                    orderId: order.OrderId ?? order.orderId,
                    side: order.Side ?? order.side,
                    price: order.Price ?? order.price,
                    quantity: order.Quantity ?? order.quantity,
                    priority: order.Priority ?? order.priority,
                    isOwnOrder: order.IsOwnOrder ?? order.isOwnOrder ?? false
                }));
                orderMap.set(price, orders);
            }

            return orderMap;
        };

        const bidOrdersData = parsedData.BidOrders ?? parsedData.bidOrders;
        const askOrdersData = parsedData.AskOrders ?? parsedData.askOrders;

        const snapshot: OrderBookSnapshot = {
            bestBid: parsedData.BestBid ?? parsedData.bestBid ?? null,
            bestAsk: parsedData.BestAsk ?? parsedData.bestAsk ?? null,
            midPrice: parsedData.MidPrice ?? parsedData.midPrice ?? null,
            bids: bidsArray.map(transformLevel),
            asks: asksArray.map(transformLevel),
            timestamp: parsedData.Timestamp ?? parsedData.timestamp ?? Date.now(),
            bidOrders: transformOrderMap(bidOrdersData),
            askOrders: transformOrderMap(askOrdersData),
            dirtyChanges: parsedData.DirtyChanges ?? parsedData.dirtyChanges,
            structuralChange: parsedData.StructuralChange ?? parsedData.structuralChange
        };

        // Store the last snapshot for re-rendering on viewport changes
        this.lastSnapshot = snapshot;

        this.renderer.render(snapshot);
    }
}
