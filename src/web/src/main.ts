import { CanvasRenderer } from './canvas-renderer';
import { InteractionHandler } from './interaction-handler';
import {
    PriceLadderConfig,
    OrderBookSnapshot,
    PriceLevel,
    BookLevel,
    Side,
    OrderUpdate,
    OrderUpdateType,
    DEFAULT_COLORS,
    COL_WIDTH,
    VOLUME_BAR_WIDTH_MULTIPLIER,
    DirtyLevelChange,
    MIN_BAR_COLUMN_WIDTH
} from './types';
import { MBOManager } from './mbo-manager';

/**
 * Main Price Ladder component for web.
 * Currently uses pure TypeScript implementation.
 * Will be upgraded to use WASM core when available.
 */
export class PriceLadder {
    private container: HTMLElement;
    private canvas: HTMLCanvasElement;
    protected renderer: CanvasRenderer;
    private interactionHandler: InteractionHandler;
    private config: Required<PriceLadderConfig>;
    private dataMode: 'PriceLevel' | 'MBO';

    // Simulated order book (will be replaced by WASM)
    private bids: Map<number, BookLevel> = new Map();
    private asks: Map<number, BookLevel> = new Map();
    private mboManager: MBOManager = new MBOManager();
    private updateCount: number = 0;
    private lastRenderTime: number = 0;
    private dirtyChanges: DirtyLevelChange[] = [];
    private hasStructuralChange: boolean = false;

    // Animation frame request
    private rafId: number = 0;

    // Responsive layout
    private barColumnWidth: number = 0;
    private resizeObserver?: ResizeObserver;

    constructor(config: PriceLadderConfig) {
        this.container = config.container;

        const showVolumeBars = config.showVolumeBars !== undefined ? config.showVolumeBars : true;
        const showOrderCount = config.showOrderCount !== undefined ? config.showOrderCount : true;

        // Fixed data columns width
        const fixedDataColumns = showOrderCount ? 5 : 3;
        const fixedColumnsWidth = fixedDataColumns * COL_WIDTH;

        // Calculate bar column width from container (or use default)
        const containerWidth = config.container.clientWidth || 800;
        const availableWidth = containerWidth - fixedColumnsWidth;
        this.barColumnWidth = showVolumeBars
            ? Math.max(MIN_BAR_COLUMN_WIDTH, availableWidth)
            : 0;

        const defaultWidth = Math.round(fixedColumnsWidth + this.barColumnWidth);

        // Apply defaults
        this.config = {
            container: config.container,
            width: config.width || defaultWidth,
            height: config.height || 600,
            rowHeight: config.rowHeight || 24,
            visibleLevels: config.visibleLevels || 50,
            tickSize: config.tickSize || 0.01,
            mode: config.mode || 'PriceLevel',
            readOnly: config.readOnly || false,
            showVolumeBars,
            showOrderCount,
            colors: config.colors || DEFAULT_COLORS,
            onTrade: config.onTrade || (() => {}),
            onPriceHover: config.onPriceHover || (() => {})
        };
        this.dataMode = this.config.mode;

        // Create canvas
        this.canvas = document.createElement('canvas');
        this.canvas.style.display = 'block';
        // // Prevent canvas from stretching to fill container
        // this.canvas.style.maxWidth = '100%';
        this.container.appendChild(this.canvas);

        // Add resize observer for responsive layout
        this.resizeObserver = new ResizeObserver(() => {
            this.handleResize();
        });
        this.resizeObserver.observe(this.container);

        // Create renderer
        this.renderer = new CanvasRenderer(
            this.canvas,
            this.config.width,
            this.config.height,
            this.config.rowHeight,
            this.config.colors,
            this.config.showVolumeBars,
            this.config.showOrderCount,
            this.config.tickSize
        );

        // Create interaction handler
        this.interactionHandler = new InteractionHandler(
            this.canvas,
            this.renderer,
            this.config.readOnly
        );

        this.setupInteractions();
        this.startRenderLoop();
    }

    private setupInteractions(): void {
        this.interactionHandler.onPriceClick = (price, side) => {
            this.config.onTrade?.(price, side);
        };

        this.interactionHandler.onPriceHover = (price) => {
            this.config.onPriceHover?.(price);
        };

        this.interactionHandler.onScroll = (scrollTicks) => {
            const currentOffset = this.renderer.getScrollOffset();
            this.renderer.setScrollOffset(currentOffset + scrollTicks);
        };
    }

    /**
     * Handle container resize for responsive layout
     */
    private handleResize(): void {
        const showOrderCount = this.config.showOrderCount;
        const showVolumeBars = this.config.showVolumeBars;
        const fixedDataColumns = showOrderCount ? 5 : 3;
        const fixedColumnsWidth = fixedDataColumns * COL_WIDTH;

        const containerWidth = this.container.clientWidth || 800;
        const availableWidth = containerWidth - fixedColumnsWidth;
        const newBarColumnWidth = showVolumeBars
            ? Math.max(MIN_BAR_COLUMN_WIDTH, availableWidth)
            : 0;

        if (newBarColumnWidth !== this.barColumnWidth) {
            this.barColumnWidth = newBarColumnWidth;
            this.renderer.updateBarColumnWidth(newBarColumnWidth);
            const newWidth = Math.round(fixedColumnsWidth + newBarColumnWidth);
            this.canvas.width = newWidth;
            // Also update CSS width to prevent stretching
            this.canvas.style.width = `${newWidth}px`;
            // Trigger a re-render with current snapshot
            const snapshot = this.getSnapshot();
            this.renderer.render(snapshot);
        }
    }

    /**
     * Process a price level update
     */
    public processUpdate(update: PriceLevel): void {
        if (this.dataMode !== 'PriceLevel') {
            return;
        }

        const map = update.side === Side.BID ? this.bids : this.asks;
        const existed = map.has(update.price);
        let isAddition = false;
        let isRemoval = false;

        const level: BookLevel = {
            price: update.price,
            quantity: update.quantity,
            numOrders: update.numOrders,
            side: update.side,
            isDirty: true,
            hasOwnOrders: false
        };

        if (update.quantity > 0) {
            map.set(update.price, level);
            if (!existed) {
                isAddition = true;
                this.hasStructuralChange = true;
            }
        } else if (existed) {
            map.delete(update.price);
            isRemoval = true;
            this.hasStructuralChange = true;
        }

        if (update.quantity > 0 || existed) {
            this.dirtyChanges.push({
                price: update.price,
                side: update.side,
                isRemoval,
                isAddition
            });
        }

        this.updateCount++;
    }

    /**
     * Process multiple updates in batch
     */
    public processBatch(updates: PriceLevel[]): void {
        if (this.dataMode !== 'PriceLevel') {
            return;
        }

        for (const update of updates) {
            this.processUpdate(update);
        }
    }

    /**
     * Process binary update (simulated - will use WASM)
     */
    public processUpdateBinary(_data: Uint8Array): void {
        // TODO: When WASM is available, pass to worker
        // For now, simulate parsing
        console.warn('Binary updates not yet implemented, use processUpdate() instead');
    }

    /**
     * Process a single order update (MBO mode)
     */
    public processOrderUpdate(update: OrderUpdate, type: OrderUpdateType): void {
        if (this.dataMode !== 'MBO') {
            return;
        }

        const roundedUpdate: OrderUpdate = {
            ...update,
            price: this.roundToTick(update.price)
        };
        this.mboManager.processOrderUpdate(roundedUpdate, type);
        this.updateCount++;
    }

    /**
     * Process multiple order updates in batch (MBO mode)
     */
    public processOrderBatch(updates: Array<{ update: OrderUpdate; type: OrderUpdateType }>): void {
        if (this.dataMode !== 'MBO') {
            return;
        }

        for (const item of updates) {
            const roundedUpdate: OrderUpdate = {
                ...item.update,
                price: this.roundToTick(item.update.price)
            };
            this.mboManager.processOrderUpdate(roundedUpdate, item.type);
            this.updateCount++;
        }
    }

    /**
     * Get current snapshot
     */
    private getSnapshot(): OrderBookSnapshot {
        if (this.dataMode === 'MBO') {
            const bids = this.mboManager.getBidLevels();
            const asks = this.mboManager.getAskLevels();
            const bestBid = bids.length > 0 ? bids[bids.length - 1].price : null;
            const bestAsk = asks.length > 0 ? asks[0].price : null;
            const midPrice = bestBid !== null && bestAsk !== null
                ? (bestBid + bestAsk) / 2
                : null;
            const dirtyState = this.mboManager.consumeDirtyState();

            return {
                bestBid,
                bestAsk,
                midPrice,
                bids,
                asks,
                timestamp: Date.now(),
                bidOrders: this.mboManager.getBidOrders(),
                askOrders: this.mboManager.getAskOrders(),
                dirtyChanges: dirtyState.dirtyChanges,
                structuralChange: dirtyState.structuralChange
            };
        }

        const sortedBids = Array.from(this.bids.values())
            .sort((a, b) => a.price - b.price);

        const sortedAsks = Array.from(this.asks.values())
            .sort((a, b) => a.price - b.price);

        const bestBid = sortedBids.length > 0 ? sortedBids[sortedBids.length - 1].price : null;
        const bestAsk = sortedAsks.length > 0 ? sortedAsks[0].price : null;

        const midPrice = bestBid !== null && bestAsk !== null
            ? (bestBid + bestAsk) / 2
            : null;

        const dirtyChanges = this.dirtyChanges;
        const structuralChange = this.hasStructuralChange;
        this.dirtyChanges = [];
        this.hasStructuralChange = false;

        return {
            bestBid,
            bestAsk,
            midPrice,
            bids: sortedBids,
            asks: sortedAsks,
            timestamp: Date.now(),
            dirtyChanges,
            structuralChange
        };
    }

    /**
     * Render loop (60 FPS)
     */
    private startRenderLoop(): void {
        const render = (timestamp: number) => {
            // Throttle to 60 FPS
            if (timestamp - this.lastRenderTime >= 16.67) {
                const snapshot = this.getSnapshot();
                this.renderer.render(snapshot);
                this.lastRenderTime = timestamp;
            }

            this.rafId = requestAnimationFrame(render);
        };

        this.rafId = requestAnimationFrame(render);
    }

    /**
     * Get best bid
     */
    public getBestBid(): number | null {
        if (this.dataMode === 'MBO') {
            const bids = this.mboManager.getBidLevels();
            return bids.length > 0 ? bids[bids.length - 1].price : null;
        }

        const sortedBids = Array.from(this.bids.values())
            .sort((a, b) => a.price - b.price);
        return sortedBids.length > 0 ? sortedBids[sortedBids.length - 1].price : null;
    }

    /**
     * Get best ask
     */
    public getBestAsk(): number | null {
        if (this.dataMode === 'MBO') {
            const asks = this.mboManager.getAskLevels();
            return asks.length > 0 ? asks[0].price : null;
        }

        const sortedAsks = Array.from(this.asks.values())
            .sort((a, b) => a.price - b.price);
        return sortedAsks.length > 0 ? sortedAsks[0].price : null;
    }

    /**
     * Get mid price
     */
    public getMidPrice(): number | null {
        const bid = this.getBestBid();
        const ask = this.getBestAsk();
        return bid !== null && ask !== null ? (bid + ask) / 2 : null;
    }

    /**
     * Get spread
     */
    public getSpread(): number | null {
        const bid = this.getBestBid();
        const ask = this.getBestAsk();
        return bid !== null && ask !== null ? ask - bid : null;
    }

    /**
     * Get render metrics
     */
    public getMetrics() {
        if (this.dataMode === 'MBO') {
            return {
                ...this.renderer.getMetrics(),
                updateCount: this.updateCount,
                bidLevels: this.mboManager.getBidLevels().length,
                askLevels: this.mboManager.getAskLevels().length
            };
        }

        return {
            ...this.renderer.getMetrics(),
            updateCount: this.updateCount,
            bidLevels: this.bids.size,
            askLevels: this.asks.size
        };
    }

    /**
     * Resize the ladder
     */
    public resize(width?: number, height?: number): void {
        if (width) this.config.width = width;
        if (height) this.config.height = height;

        this.renderer.resize(this.config.width, this.config.height);
    }

    /**
     * Set read-only mode
     */
    public setReadOnly(readOnly: boolean): void {
        this.config.readOnly = readOnly;
        this.interactionHandler.setReadOnly(readOnly);
    }

    /**
     * Calculate width based on visible columns
     * Base 6 columns: [bid_orders][bid_qty][price][ask_qty][ask_orders][bars]
     * Remove columns when features are disabled
     */
    private calculateWidth(): number {
        const dataColumns = this.config.showOrderCount ? 5 : 3;
        const barWidth = this.config.showVolumeBars ? COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER : 0;
        return Math.round((dataColumns * COL_WIDTH) + barWidth);
    }

    /**
     * Set volume bars visibility
     */
    public setShowVolumeBars(show: boolean): void {
        this.config.showVolumeBars = show;
        // Update width based on new settings
        const newWidth = this.calculateWidth();
        this.config.width = newWidth;

        // Recreate renderer with new settings
        this.renderer = new CanvasRenderer(
            this.canvas,
            this.config.width,
            this.config.height,
            this.config.rowHeight,
            this.config.colors!,
            this.config.showVolumeBars,
            this.config.showOrderCount,
            this.config.tickSize
        );

        // Update interaction handler with new renderer
        this.interactionHandler.setRenderer(this.renderer);

        // Re-render current snapshot
        const snapshot = this.getSnapshot();
        this.renderer.render(snapshot);
    }

    /**
     * Set order count visibility
     */
    public setShowOrderCount(show: boolean): void {
        this.config.showOrderCount = show;
        // Update width based on new settings
        const newWidth = this.calculateWidth();
        this.config.width = newWidth;

        // Recreate renderer with new settings
        this.renderer = new CanvasRenderer(
            this.canvas,
            this.config.width,
            this.config.height,
            this.config.rowHeight,
            this.config.colors!,
            this.config.showVolumeBars,
            this.config.showOrderCount,
            this.config.tickSize
        );

        // Update interaction handler with new renderer
        this.interactionHandler.setRenderer(this.renderer);

        // Re-render current snapshot
        const snapshot = this.getSnapshot();
        this.renderer.render(snapshot);
    }

    /**
     * Set data mode (PriceLevel or MBO)
     */
    public setDataMode(mode: 'PriceLevel' | 'MBO'): void {
        if (this.dataMode === mode) {
            return;
        }

        this.dataMode = mode;
        this.config.mode = mode;
        this.clear();

        const snapshot = this.getSnapshot();
        this.renderer.render(snapshot);
    }

    /**
     * Set removal mode (showEmpty or removeRow)
     */
    public setRemovalMode(mode: 'showEmpty' | 'removeRow'): void {
        this.renderer.setRemovalMode(mode);
    }

    /**
     * Get current tick size
     */
    public getTickSize(): number {
        return this.config.tickSize;
    }

    /**
     * Clear all data
     */
    public clear(): void {
        this.bids.clear();
        this.asks.clear();
        this.mboManager.reset();
        this.updateCount = 0;
        this.dirtyChanges = [];
        this.hasStructuralChange = false;
        // Reset renderer's centerPrice for Show Empty Rows mode
        this.renderer.resetCenterPrice();
    }

    private roundToTick(price: number): number {
        const tickSize = this.config.tickSize || 0.01;
        return Math.round(price / tickSize) * tickSize;
    }

    /**
     * Destroy the ladder and clean up resources
     */
    public destroy(): void {
        cancelAnimationFrame(this.rafId);
        this.resizeObserver?.disconnect();
        this.interactionHandler.destroy();
        this.container.removeChild(this.canvas);
    }
}

// Export for UMD
if (typeof window !== 'undefined') {
    (window as any).PriceLadder = PriceLadder;
}

export * from './types';
export { CanvasRenderer } from './canvas-renderer';
export { InteractionHandler } from './interaction-handler';
