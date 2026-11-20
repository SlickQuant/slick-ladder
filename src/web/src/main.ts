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
    CanvasColors,
    Trade,
    WorkingOrders,
    DEFAULT_COLORS,
    COL_WIDTH,
    VOLUME_BAR_WIDTH_MULTIPLIER,
    DEFAULT_MBO_ORDER_SIZE_FILTER,
    DEFAULT_MIN_QUANTITY_THRESHOLD
} from './types';
import { MBOManager } from './mbo-manager';
import { SortedLevels } from './sorted-levels';
import { aggregateLevels, aggregateWork, bucketPrice, tradeBucketPrice } from './aggregation';

// The ladder's width with every column at its default width: bid qty, price, ask qty, and the
// Work, LTQ and bar columns when shown. The renderer widens columns to fit their text and the
// canvas with them
const defaultLadderWidth = (showVolumeBars: boolean, showWorkColumn: boolean, showLtqColumn: boolean): number =>
    Math.round((3 + (showWorkColumn ? 1 : 0) + (showLtqColumn ? 1 : 0)) * COL_WIDTH +
        (showVolumeBars ? COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER : 0));

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
    private bidWork: Map<number, WorkingOrders> = new Map(); // Our working orders by price
    private askWork: Map<number, WorkingOrders> = new Map();
    private lastTrade: Trade | null = null;

    // Animation frame request
    private rafId: number = 0;

    constructor(config: PriceLadderConfig) {
        this.container = config.container;

        const showVolumeBars = config.showVolumeBars !== undefined ? config.showVolumeBars : true;
        const showOrderCount = config.showOrderCount !== undefined ? config.showOrderCount : true;
        const showWorkColumn = config.showWorkColumn ?? true;
        const showLtqColumn = config.showLtqColumn ?? true;

        // Apply defaults
        this.config = {
            container: config.container,
            width: config.width || defaultLadderWidth(showVolumeBars, showWorkColumn, showLtqColumn),
            height: config.height || 600,
            rowHeight: config.rowHeight || 24,
            visibleLevels: config.visibleLevels || 50,
            tickSize: config.tickSize || 0.01,
            displayTickSize: Math.max(config.tickSize || 0.01, config.displayTickSize || config.tickSize || 0.01),
            mode: config.mode || 'PriceLevel',
            readOnly: config.readOnly || false,
            showVolumeBars,
            showOrderCount,
            showWorkColumn,
            showLtqColumn,
            showHeader: config.showHeader ?? true,
            mboOrderSizeFilter: Math.max(0, config.mboOrderSizeFilter ?? DEFAULT_MBO_ORDER_SIZE_FILTER),
            minQuantityThreshold: config.minQuantityThreshold ?? DEFAULT_MIN_QUANTITY_THRESHOLD,
            removalMode: config.removalMode || 'removeRow',
            autoCenter: config.autoCenter ?? true,
            colors: { ...DEFAULT_COLORS, ...config.colors },
            onLevelClick: config.onLevelClick || (() => {}),
            onWorkClick: config.onWorkClick || (() => {}),
            onPriceHover: config.onPriceHover || (() => {})
        };
        this.dataMode = this.config.mode;

        // Create canvas
        this.canvas = document.createElement('canvas');
        this.canvas.style.display = 'block';
        // // Prevent canvas from stretching to fill container
        // this.canvas.style.maxWidth = '100%';
        this.container.appendChild(this.canvas);

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
            this.config.colors as CanvasColors, // Merged with DEFAULT_COLORS in the constructor
            this.config.showVolumeBars,
            this.config.showOrderCount,
            this.config.tickSize,
            this.config.mboOrderSizeFilter,
            this.config.minQuantityThreshold,
            this.config.displayTickSize,
            {
                showWorkColumn: this.config.showWorkColumn,
                showLtqColumn: this.config.showLtqColumn,
                showHeader: this.config.showHeader
            }
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
        this.interactionHandler.onPriceClick = (price, side, button) =>
            this.config.onLevelClick(price, side, button) === true;

        this.interactionHandler.onWorkClick = (price, button) =>
            this.config.onWorkClick(price, button) === true;

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

            return this.attachCells({
                bestBid,
                bestAsk,
                midPrice,
                bids,
                asks,
                timestamp: Date.now(),
                bidOrders: isAggregating ? null : this.mboManager.getBidOrders(),
                askOrders: isAggregating ? null : this.mboManager.getAskOrders()
            });
        }

        // The maps hold only levels with quantity. Levels below the threshold show as "<threshold"
        const bids = aggregateLevels(this.sortedBids.share(), dts, ts, false);
        const asks = aggregateLevels(this.sortedAsks.share(), dts, ts, true);

        const bestBid = bids.length > 0 ? bids[bids.length - 1].price : null;
        const bestAsk = asks.length > 0 ? asks[0].price : null;

        const midPrice = bestBid !== null && bestAsk !== null
            ? (bestBid + bestAsk) / 2
            : null;

        return this.attachCells({
            bestBid,
            bestAsk,
            midPrice,
            bids,
            asks,
            timestamp: Date.now()
        });
    }

    /**
     * Add our working orders and the last trade to a snapshot, in display buckets when the
     * display tick aggregates levels
     */
    protected attachCells(snapshot: OrderBookSnapshot): OrderBookSnapshot {
        const dts = this.config.displayTickSize;
        const trade = this.lastTrade;
        if (dts > this.config.tickSize) {
            snapshot.bidWork = aggregateWork(this.bidWork, dts, false);
            snapshot.askWork = aggregateWork(this.askWork, dts, true);
            snapshot.lastTrade = trade && { ...trade, price: tradeBucketPrice(trade, dts) };
        } else {
            // Read only by the render that takes the snapshot, so the maps are shared
            snapshot.bidWork = this.bidWork;
            snapshot.askWork = this.askWork;
            snapshot.lastTrade = trade;
        }
        return snapshot;
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
     * Set our own working orders at a price: their total quantity and how many there are, shown
     * in the Work column as "10 (2)". Quantity or count 0 clears the price. Kept apart from the
     * book, so it shows before the market data has the level
     */
    /**
     * Price of the row an order at price on side shows on: its display bucket when displayTickSize
     * aggregates levels (bids round down, asks up), else the price rounded to tick. Match a Work
     * click's price with your orders through it
     */
    public displayPrice(price: number, side: Side): number {
        const dts = this.config.displayTickSize;
        const rounded = this.roundToTick(price);
        return dts > this.config.tickSize ? bucketPrice(rounded, dts, side === Side.ASK) : rounded;
    }

    public setWorkingOrders(price: number, side: Side, quantity: number, count: number): void {
        const roundedPrice = this.roundToTick(price);
        const work = side === Side.BID ? this.bidWork : this.askWork;
        if (quantity > 0 && count > 0) {
            work.set(roundedPrice, { quantity, count });
        } else if (!work.delete(roundedPrice)) {
            return; // Nothing to clear
        }
        this.bookChanged = true;
    }

    /**
     * A trade from the feed: the LTQ column shows its quantity on its price, green when the buyer
     * was the aggressor, red for the seller, white when unknown. Only the last trade is shown
     */
    public processTrade(trade: Trade): void {
        this.lastTrade = { price: this.roundToTick(trade.price), quantity: trade.quantity, aggressor: trade.aggressor ?? null };
        this.bookChanged = true;
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
     * Calculate width based on visible columns: [work][bid_qty][price][ask_qty][ltq][bars], optional ones when enabled
     */
    private calculateWidth(): number {
        return defaultLadderWidth(this.config.showVolumeBars, this.config.showWorkColumn, this.config.showLtqColumn);
    }

    /**
     * After a column or header toggle in config: a renderer with the new layout, then a render
     */
    protected applyLayoutChange(): void {
        this.config.width = this.calculateWidth();
        this.renderer = this.createRenderer();
        this.interactionHandler.setRenderer(this.renderer);
        this.rerender();
    }

    /**
     * Render the book again (WASM re-renders its last snapshot instead)
     */
    protected rerender(): void {
        this.renderNow();
    }

    /**
     * Set volume bars visibility
     */
    public setShowVolumeBars(show: boolean): void {
        this.config.showVolumeBars = show;
        this.applyLayoutChange();
    }

    /**
     * Show or hide the order count in the qty column
     */
    public setShowOrderCount(show: boolean): void {
        this.config.showOrderCount = show;
        this.applyLayoutChange();
    }

    /**
     * Show or hide the Work column (our working orders, left of Bid)
     */
    public setShowWorkColumn(show: boolean): void {
        this.config.showWorkColumn = show;
        this.applyLayoutChange();
    }

    /**
     * Show or hide the LTQ column (last traded quantity, right of Ask)
     */
    public setShowLtqColumn(show: boolean): void {
        this.config.showLtqColumn = show;
        this.applyLayoutChange();
    }

    /**
     * Show or hide the column titles above the rows
     */
    public setShowHeader(show: boolean): void {
        this.config.showHeader = show;
        this.applyLayoutChange();
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
        this.bidWork.clear();
        this.askWork.clear();
        this.lastTrade = null;
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
