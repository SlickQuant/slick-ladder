import { CanvasRenderer } from './canvas-renderer';
import { InteractionHandler } from './interaction-handler';
import {
    PriceLadderConfig,
    OrderBookSnapshot,
    PriceLevel,
    BookLevel,
    Side,
    DEFAULT_COLORS
} from './types';

/**
 * Main Price Ladder component for web.
 * Currently uses pure TypeScript implementation.
 * Will be upgraded to use WASM core when available.
 */
export class PriceLadder {
    private container: HTMLElement;
    private canvas: HTMLCanvasElement;
    private renderer: CanvasRenderer;
    private interactionHandler: InteractionHandler;
    private config: Required<PriceLadderConfig>;

    // Simulated order book (will be replaced by WASM)
    private bids: Map<number, BookLevel> = new Map();
    private asks: Map<number, BookLevel> = new Map();
    private updateCount: number = 0;
    private lastRenderTime: number = 0;

    // Animation frame request
    private rafId: number = 0;

    constructor(config: PriceLadderConfig) {
        this.container = config.container;

        // Apply defaults
        this.config = {
            container: config.container,
            width: config.width || 400,
            height: config.height || 600,
            rowHeight: config.rowHeight || 24,
            visibleLevels: config.visibleLevels || 50,
            mode: config.mode || 'PriceLevel',
            readOnly: config.readOnly || false,
            showVolumeBars: config.showVolumeBars !== undefined ? config.showVolumeBars : true,
            showOrderCount: config.showOrderCount !== undefined ? config.showOrderCount : true,
            colors: config.colors || DEFAULT_COLORS,
            onTrade: config.onTrade || (() => {}),
            onPriceHover: config.onPriceHover || (() => {})
        };

        // Create canvas
        this.canvas = document.createElement('canvas');
        this.canvas.style.display = 'block';
        this.container.appendChild(this.canvas);

        // Create renderer
        this.renderer = new CanvasRenderer(
            this.canvas,
            this.config.width,
            this.config.height,
            this.config.rowHeight,
            this.config.colors,
            this.config.showVolumeBars,
            this.config.showOrderCount
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
     * Process a price level update
     */
    public processUpdate(update: PriceLevel): void {
        const level: BookLevel = {
            price: update.price,
            quantity: update.quantity,
            numOrders: update.numOrders,
            side: update.side,
            isDirty: true,
            hasOwnOrders: false
        };

        if (update.side === Side.BID) {
            if (update.quantity > 0) {
                this.bids.set(update.price, level);
            } else {
                this.bids.delete(update.price);
            }
        } else {
            if (update.quantity > 0) {
                this.asks.set(update.price, level);
            } else {
                this.asks.delete(update.price);
            }
        }

        this.updateCount++;
    }

    /**
     * Process multiple updates in batch
     */
    public processBatch(updates: PriceLevel[]): void {
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
     * Mark a price level as having own orders
     */
    public markOwnOrder(price: number, side: Side, hasOwnOrder: boolean = true): void {
        const map = side === Side.BID ? this.bids : this.asks;
        const level = map.get(price);

        if (level) {
            level.hasOwnOrders = hasOwnOrder;
            level.isDirty = true;
        }
    }

    /**
     * Get current snapshot
     */
    private getSnapshot(): OrderBookSnapshot {
        const sortedBids = Array.from(this.bids.values())
            .sort((a, b) => a.price - b.price);

        const sortedAsks = Array.from(this.asks.values())
            .sort((a, b) => a.price - b.price);

        const bestBid = sortedBids.length > 0 ? sortedBids[sortedBids.length - 1].price : null;
        const bestAsk = sortedAsks.length > 0 ? sortedAsks[0].price : null;

        const midPrice = bestBid !== null && bestAsk !== null
            ? (bestBid + bestAsk) / 2
            : null;

        return {
            bestBid,
            bestAsk,
            midPrice,
            bids: sortedBids,
            asks: sortedAsks,
            timestamp: Date.now()
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
        const sortedBids = Array.from(this.bids.values())
            .sort((a, b) => a.price - b.price);
        return sortedBids.length > 0 ? sortedBids[sortedBids.length - 1].price : null;
    }

    /**
     * Get best ask
     */
    public getBestAsk(): number | null {
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
        const COL_WIDTH = 66.7; // ~400px / 6 columns
        let columnCount = 6;

        // Remove order count columns (columns 0 and 4) when disabled
        if (!this.config.showOrderCount) {
            columnCount -= 2; // Remove both bid_orders and ask_orders
        }

        // Remove bars column (column 5) when disabled
        if (!this.config.showVolumeBars) {
            columnCount -= 1; // Remove bars
        }

        return Math.round(COL_WIDTH * columnCount);
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
            this.config.showOrderCount
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
            this.config.showOrderCount
        );

        // Update interaction handler with new renderer
        this.interactionHandler.setRenderer(this.renderer);

        // Re-render current snapshot
        const snapshot = this.getSnapshot();
        this.renderer.render(snapshot);
    }

    /**
     * Clear all data
     */
    public clear(): void {
        this.bids.clear();
        this.asks.clear();
        this.updateCount = 0;
    }

    /**
     * Destroy the ladder and clean up resources
     */
    public destroy(): void {
        cancelAnimationFrame(this.rafId);
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
