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
    MIN_BAR_COLUMN_WIDTH,
    DEFAULT_MBO_ORDER_SIZE_FILTER,
    DEFAULT_MIN_QUANTITY_THRESHOLD
} from './types';
import { MBOManager } from './mbo-manager';
import { SortedLevels } from './sorted-levels';
import { aggregateLevels } from './aggregation';

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
    private bookChanged: boolean = true; // Since the last render
    private sortedBids: SortedLevels = new SortedLevels(); // The maps' levels in price order, for snapshots
    private sortedAsks: SortedLevels = new SortedLevels();

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
            displayTickSize: Math.max(config.tickSize || 0.01, config.displayTickSize || config.tickSize || 0.01),
            mode: config.mode || 'PriceLevel',
            readOnly: config.readOnly || false,
            showVolumeBars,
            showOrderCount,
            mboOrderSizeFilter: Math.max(0, config.mboOrderSizeFilter ?? DEFAULT_MBO_ORDER_SIZE_FILTER),
            minQuantityThreshold: config.minQuantityThreshold ?? DEFAULT_MIN_QUANTITY_THRESHOLD,
            removalMode: config.removalMode || 'removeRow',
            autoCenter: config.autoCenter ?? true,
            colors: config.colors || DEFAULT_COLORS,
            onLevelClick: config.onLevelClick || (() => {}),
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
        this.renderer = this.createRenderer();

        // Create interaction handler
        this.interactionHandler = new InteractionHandler(
            this.canvas,
            this.renderer,
            this.config.readOnly
        );

        this.setupInteractions();
        this.startRenderLoop();
    }

    /**
     * Create a renderer from the current config. Runtime settings (removal mode,
     * auto-center) come from config and the view position from the renderer being
     * replaced, so both survive renderer recreation.
     */
    protected createRenderer(): CanvasRenderer {
        const previous = this.renderer as CanvasRenderer | undefined; // undefined on first create
        const renderer = new CanvasRenderer(
            this.canvas,
            this.config.width,
            this.config.height,
            this.config.rowHeight,
            this.config.colors,
            this.config.showVolumeBars,
            this.config.showOrderCount,
            this.config.tickSize,
            this.config.mboOrderSizeFilter,
            this.config.minQuantityThreshold,
            this.config.displayTickSize
        );
        renderer.setRemovalMode(this.config.removalMode);
        renderer.setAutoCenter(this.config.autoCenter);
        if (previous) {
            // Safe with auto-center on: the next render re-pins the view
            renderer.setScrollOffset(previous.getScrollOffset());
            renderer.setCenterPrice(previous.getCenterPrice());
        }
        return renderer;
    }

    private setupInteractions(): void {
        this.interactionHandler.onPriceClick = (price, side) => {
            this.config.onLevelClick?.(price, side);
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
            this.renderNow();
        }
    }

    /**
     * Process a price level update
     */
    public processUpdate(update: PriceLevel): void {
        if (this.dataMode !== 'PriceLevel') {
            return;
        }

        // Round price to tick size to ensure consistent map keys
        const roundedPrice = this.roundToTick(update.price);
        const map = update.side === Side.BID ? this.bids : this.asks;
        const level = map.get(roundedPrice);

        const sorted = update.side === Side.BID ? this.sortedBids : this.sortedAsks;

        if (update.quantity > 0) {
            if (level) {
                // In place: snapshots share level objects, and the renderer copies what it draws
                level.quantity = update.quantity;
                level.numOrders = update.numOrders;
                level.isDirty = true;
            } else {
                const added: BookLevel = {
                    price: roundedPrice,
                    quantity: update.quantity,
                    numOrders: update.numOrders,
                    side: update.side,
                    isDirty: true,
                    hasOwnOrders: false
                };
                map.set(roundedPrice, added);
                sorted.add(added);
            }
            this.bookChanged = true;
        } else if (level) {
            // Quantity 0 removes the level. One that doesn't exist changes nothing
            map.delete(roundedPrice);
            sorted.remove(roundedPrice);
            this.bookChanged = true;
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
        if (this.mboManager.processOrderUpdate(roundedUpdate, type) !== null) {
            this.bookChanged = true;
        }
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
            if (this.mboManager.processOrderUpdate(roundedUpdate, item.type) !== null) {
                this.bookChanged = true;
            }
            this.updateCount++;
        }
    }

    /**
     * Get current snapshot
     */
    private getSnapshot(): OrderBookSnapshot {
        const dts = this.config.displayTickSize;
        const ts = this.config.tickSize;
        const isAggregating = dts > ts;

        if (this.dataMode === 'MBO') {
            const bids = aggregateLevels(this.mboManager.getBidLevels(), dts, ts, false);
            const asks = aggregateLevels(this.mboManager.getAskLevels(), dts, ts, true);
            const bestBid = bids.length > 0 ? bids[bids.length - 1].price : null;
            const bestAsk = asks.length > 0 ? asks[0].price : null;
            const midPrice = bestBid !== null && bestAsk !== null
                ? (bestBid + bestAsk) / 2
                : null;

            return {
                bestBid,
                bestAsk,
                midPrice,
                bids,
                asks,
                timestamp: Date.now(),
                bidOrders: isAggregating ? null : this.mboManager.getBidOrders(),
                askOrders: isAggregating ? null : this.mboManager.getAskOrders()
            };
        }

        // The maps hold only levels with quantity. Levels below the threshold show as "<threshold"
        const bids = aggregateLevels(this.sortedBids.share(), dts, ts, false);
        const asks = aggregateLevels(this.sortedAsks.share(), dts, ts, true);

        const bestBid = bids.length > 0 ? bids[bids.length - 1].price : null;
        const bestAsk = asks.length > 0 ? asks[0].price : null;

        const midPrice = bestBid !== null && bestAsk !== null
            ? (bestBid + bestAsk) / 2
            : null;

        return {
            bestBid,
            bestAsk,
            midPrice,
            bids,
            asks,
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
                this.renderFrame();
                this.lastRenderTime = timestamp;
            }

            this.rafId = requestAnimationFrame(render);
        };

        this.rafId = requestAnimationFrame(render);
    }

    /**
     * One frame: snapshot and render only when the book or the view changed. Idle ladders skip
     * both
     */
    private renderFrame(): void {
        this.renderer.recordFrame();
        if (this.bookChanged || this.renderer.needsRender()) {
            this.renderNow();
        }
    }

    /**
     * Snapshot the book and render it. Every render goes through here, so bookChanged can't be
     * cleared by a snapshot that never got drawn
     */
    private renderNow(): void {
        this.bookChanged = false;
        this.renderer.render(this.getSnapshot());
    }

    /**
     * Mark a price level as having (or not having) own orders.
     * The flag is preserved across subsequent market data updates.
     */
    public setHasOwnOrders(price: number, side: Side, value: boolean): void {
        const roundedPrice = this.roundToTick(price);
        const map = side === Side.BID ? this.bids : this.asks;
        const level = map.get(roundedPrice);
        if (level && level.hasOwnOrders !== value) {
            level.hasOwnOrders = value;
            this.bookChanged = true;
        }
    }

    /**
     * Get best bid
     */
    public getBestBid(): number | null {
        const bids = this.dataMode === 'MBO' ? this.mboManager.levels(Side.BID) : this.sortedBids.levels;
        return bids.length > 0 ? bids[bids.length - 1].price : null;
    }

    /**
     * Get best ask
     */
    public getBestAsk(): number | null {
        const asks = this.dataMode === 'MBO' ? this.mboManager.levels(Side.ASK) : this.sortedAsks.levels;
        return asks.length > 0 ? asks[0].price : null;
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
                bidLevels: this.mboManager.levels(Side.BID).length,
                askLevels: this.mboManager.levels(Side.ASK).length
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
        this.renderer = this.createRenderer();

        // Update interaction handler with new renderer
        this.interactionHandler.setRenderer(this.renderer);

        // Re-render current snapshot
        this.renderNow();
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
        this.renderer = this.createRenderer();

        // Update interaction handler with new renderer
        this.interactionHandler.setRenderer(this.renderer);

        // Re-render current snapshot
        this.renderNow();
    }

    /**
     * Set MBO order size filter (only show orders with qty > filter)
     */
    public setMboOrderSizeFilter(filter: number): void {
        const normalized = Math.max(0, filter);
        this.config.mboOrderSizeFilter = normalized;
        this.renderer.setMboOrderSizeFilter(normalized);
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
        this.renderNow();
    }

    /**
     * Set removal mode (showEmpty or removeRow)
     */
    public setRemovalMode(mode: 'showEmpty' | 'removeRow'): void {
        this.config.removalMode = mode;
        this.renderer.setRemovalMode(mode);
    }

    /**
     * Keep the bid/ask midpoint centered. When off, the user scrolls freely and
     * Space or middle-click re-centers.
     */
    public setAutoCenter(enabled: boolean): void {
        this.config.autoCenter = enabled;
        this.renderer.setAutoCenter(enabled);
    }

    public getAutoCenter(): boolean {
        return this.config.autoCenter;
    }

    /**
     * Move the bid/ask midpoint to the middle of the ladder once
     */
    public recenter(): void {
        this.renderer.recenter();
    }

    /**
     * Get current tick size
     */
    public getTickSize(): number {
        return this.config.tickSize;
    }

    /**
     * Update display tick size at runtime without recreating the ladder.
     */
    public updateConfig(partial: Partial<Pick<PriceLadderConfig, 'tickSize' | 'displayTickSize'>>): void {
        if (partial.tickSize !== undefined) {
            this.config.tickSize = partial.tickSize;
        }
        const ts = this.config.tickSize;
        const dts = Math.max(ts, partial.displayTickSize ?? this.config.displayTickSize ?? ts);
        this.config.displayTickSize = dts;
        this.renderer.setDisplayTickSize(dts);
        this.bookChanged = true; // Levels aggregate differently
    }

    /**
     * Clear all data
     */
    public clear(): void {
        this.bids.clear();
        this.asks.clear();
        this.sortedBids = new SortedLevels();
        this.sortedAsks = new SortedLevels();
        this.mboManager.reset();
        this.updateCount = 0;
        this.bookChanged = true;
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
