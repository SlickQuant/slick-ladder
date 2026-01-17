import { BookLevel, Side, OrderBookSnapshot, CanvasColors, DEFAULT_COLORS, RenderMetrics, Order, DirtyLevelChange, COL_WIDTH, VOLUME_BAR_WIDTH_MULTIPLIER, ORDER_SEGMENT_GAP, MIN_ORDER_SEGMENT_WIDTH } from './types';

type DensePackingLayout = {
    nonEmptyAsks: BookLevel[];
    nonEmptyBids: BookLevel[];
    startAskIndex: number;
    askRowsToRender: number;
    startBidIndex: number;
    bidRowsToRender: number;
    topOffset: number;
    firstRowIndex: number;
};

/**
 * Ultra-fast Canvas 2D renderer optimized for <1ms rendering of dirty regions.
 * Uses dirty region tracking to minimize redraw operations.
 */
export class CanvasRenderer {
    private canvas: HTMLCanvasElement;
    private ctx: CanvasRenderingContext2D;
    private offscreenCanvas: OffscreenCanvas;
    private offscreenCtx: OffscreenCanvasRenderingContext2D;

    private width: number;
    private height: number;
    private rowHeight: number;
    private visibleRows: number;

    private colors: CanvasColors;
    // private textCache: Map<string, TextMetrics>; // Reserved for future optimization

    // Dirty region tracking
    private dirtyRows: Set<number>;
    private minDirtyRow: number = Infinity;
    private maxDirtyRow: number = -1;
    private needsFullRedraw: boolean = true;
    private lastRemovalMode: 'showEmpty' | 'removeRow' = 'removeRow';
    private lastShowOrderCount: boolean = true;
    private lastShowVolumeBars: boolean = true;
    private lastTickSize: number = 0.01;
    private lastDensePackingScrollOffset: number = 0;
    private lastReferencePrice: number = 0;
    private lastCenterPrice: number = 0;

    // Layout configuration
    private showVolumeBars: boolean = true;
    private showOrderCount: boolean = true;

    // Performance tracking
    private lastFrameTime: number = 0;
    private frameCount: number = 0;
    private fps: number = 60;

    // Current snapshot
    private currentSnapshot: OrderBookSnapshot | null = null;
    private bidOrdersByPriceKey: Map<string, Order[]> | null = null;
    private askOrdersByPriceKey: Map<string, Order[]> | null = null;

    // Scroll state
    private scrollOffset: number = 0;              // Row-based scroll for dense packing
    private centerPrice: number = 0;               // Price-based scroll for show empty mode
    private removalMode: 'showEmpty' | 'removeRow' = 'removeRow';

    // Price configuration
    private tickSize: number;

    constructor(
        canvas: HTMLCanvasElement,
        width: number,
        height: number,
        rowHeight: number = 24,
        colors: CanvasColors = DEFAULT_COLORS,
        showVolumeBars: boolean = true,
        showOrderCount: boolean = true,
        tickSize: number = 0.01
    ) {
        this.canvas = canvas;
        this.width = width;
        this.height = height;
        this.rowHeight = rowHeight;
        this.colors = colors;
        this.showVolumeBars = showVolumeBars;
        this.showOrderCount = showOrderCount;
        this.tickSize = tickSize;
        this.visibleRows = Math.floor(height / rowHeight);

        // Set canvas size
        canvas.width = width;
        canvas.height = height;
        canvas.style.width = `${width}px`;
        canvas.style.height = `${height}px`;

        // Get context with alpha disabled for performance
        const ctx = canvas.getContext('2d', { alpha: false });
        if (!ctx) {
            throw new Error('Failed to get 2D context');
        }
        this.ctx = ctx;

        // Create offscreen canvas for double buffering
        this.offscreenCanvas = new OffscreenCanvas(width, height);
        const offscreenCtx = this.offscreenCanvas.getContext('2d', { alpha: false });
        if (!offscreenCtx) {
            throw new Error('Failed to get offscreen 2D context');
        }
        this.offscreenCtx = offscreenCtx;

        // this.textCache = new Map(); // Reserved for future optimization
        this.dirtyRows = new Set();

        // Configure rendering
        this.setupContext(this.ctx);
        this.setupContext(this.offscreenCtx);

        // Initial render
        this.renderBackground();
    }

    private setupContext(ctx: CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D): void {
        ctx.font = '14px monospace';
        ctx.textBaseline = 'middle';
        ctx.imageSmoothingEnabled = false;
    }

    /**
     * Format price based on tick size
     */
    private formatPrice(price: number): string {
        // Determine decimal places needed based on tick size
        // Find the number of decimal places required to represent the tick size
        let decimalPlaces = 0;
        let tickSizeTest = this.tickSize;

        // Keep multiplying by 10 until we get a value >= 1
        while (tickSizeTest < 1 && decimalPlaces < 10) {
            tickSizeTest *= 10;
            decimalPlaces++;
        }

        // Verify if this number of decimals is sufficient
        // by checking if tickSize * 10^decimalPlaces is a whole number
        while (decimalPlaces < 10) {
            const multiplier = Math.pow(10, decimalPlaces);
            const rounded = Math.round(this.tickSize * multiplier);

            // If close enough to a whole number, we have the right decimal places
            if (Math.abs((rounded / multiplier) - this.tickSize) < 1e-10) {
                break;
            }

            decimalPlaces++;
        }

        return price.toFixed(decimalPlaces);
    }

    private buildOrderLookup(orderMap: Map<number, Order[]> | null | undefined): Map<string, Order[]> | null {
        if (!orderMap) {
            return null;
        }

        const lookup = new Map<string, Order[]>();
        for (const [price, orders] of orderMap.entries()) {
            lookup.set(this.formatPrice(price), orders);
        }
        return lookup;
    }

    private drawFullBackground(): void {
        this.offscreenCtx.fillStyle = this.colors.background;
        this.offscreenCtx.fillRect(0, 0, this.width, this.height);

        // Calculate column indices
        const bidQtyCol = this.showOrderCount ? 1 : 0;
        const priceCol = this.showOrderCount ? 2 : 1;
        const askQtyCol = this.showOrderCount ? 3 : 2;

        // BID qty column (blue)
        this.offscreenCtx.fillStyle = this.colors.bidQtyBackground;
        this.offscreenCtx.fillRect(COL_WIDTH * bidQtyCol, 0, COL_WIDTH, this.height);

        // Price column (light gray)
        this.offscreenCtx.fillStyle = this.colors.priceBackground;
        this.offscreenCtx.fillRect(COL_WIDTH * priceCol, 0, COL_WIDTH, this.height);

        // ASK qty column (red)
        this.offscreenCtx.fillStyle = this.colors.askQtyBackground;
        this.offscreenCtx.fillRect(COL_WIDTH * askQtyCol, 0, COL_WIDTH, this.height);

        // Draw grid lines
        this.offscreenCtx.strokeStyle = this.colors.gridLine;
        this.offscreenCtx.lineWidth = 1;

        for (let i = 0; i <= this.visibleRows; i++) {
            const y = i * this.rowHeight;
            this.offscreenCtx.beginPath();
            this.offscreenCtx.moveTo(0, y);
            this.offscreenCtx.lineTo(this.width, y);
            this.offscreenCtx.stroke();
        }
    }

    private renderBackground(): void {
        this.drawFullBackground();
        this.copyToMainCanvas();
        this.needsFullRedraw = true;
    }

    /**
     * Render a price ladder snapshot with dirty region optimization
     */
    public render(snapshot: OrderBookSnapshot): void {
        const startTime = performance.now();

        this.currentSnapshot = snapshot;
        this.bidOrdersByPriceKey = this.buildOrderLookup(snapshot.bidOrders);
        this.askOrdersByPriceKey = this.buildOrderLookup(snapshot.askOrders);

        this.clearDirtyState();

        const referencePrice = this.removalMode === 'showEmpty'
            ? this.resolveReferencePrice(snapshot)
            : 0;

        const fullRedraw = this.shouldFullRedraw(snapshot, referencePrice);
        if (fullRedraw) {
            this.renderFull(snapshot, referencePrice);
            this.markAllDirty();
        } else {
            this.renderDirty(snapshot, referencePrice);
        }

        this.updateLastState(referencePrice);

        // Update performance metrics
        const frameTime = performance.now() - startTime;
        this.updateFPS(frameTime);
    }

    private renderFull(snapshot: OrderBookSnapshot, referencePrice: number): void {
        this.drawFullBackground();

        if (this.removalMode === 'removeRow') {
            this.renderDensePacking(snapshot);
        } else {
            const levelMap = this.buildLevelMap(snapshot);
            this.renderShowEmpty(snapshot, referencePrice, levelMap);
        }

        this.copyToMainCanvas();
    }

    private resolveReferencePrice(snapshot: OrderBookSnapshot): number {
        let referencePrice = this.centerPrice;
        const isEmpty = snapshot.bids.length === 0 && snapshot.asks.length === 0;

        if (isEmpty) {
            this.centerPrice = 0;
            referencePrice = 0;
        } else if (this.centerPrice === 0) {
            const midPrice = snapshot.midPrice ?? 100.0;
            this.centerPrice = Math.round(midPrice / this.tickSize) * this.tickSize;
            referencePrice = this.centerPrice;
        } else {
            referencePrice = this.centerPrice;
        }

        return referencePrice;
    }

    private shouldFullRedraw(snapshot: OrderBookSnapshot, referencePrice: number): boolean {
        if (this.needsFullRedraw || !snapshot.dirtyChanges) {
            return true;
        }

        if (snapshot.bids.length === 0 && snapshot.asks.length === 0) {
            return true;
        }

        if (this.lastRemovalMode !== this.removalMode ||
            this.lastShowOrderCount !== this.showOrderCount ||
            this.lastShowVolumeBars !== this.showVolumeBars ||
            this.lastTickSize !== this.tickSize) {
            return true;
        }

        if (this.removalMode === 'removeRow') {
            if (this.lastDensePackingScrollOffset !== this.scrollOffset) {
                return true;
            }
        } else if (this.lastReferencePrice !== referencePrice || this.lastCenterPrice !== this.centerPrice) {
            return true;
        }

        return false;
    }

    private updateLastState(referencePrice: number): void {
        this.needsFullRedraw = false;
        this.lastRemovalMode = this.removalMode;
        this.lastShowOrderCount = this.showOrderCount;
        this.lastShowVolumeBars = this.showVolumeBars;
        this.lastTickSize = this.tickSize;
        this.lastDensePackingScrollOffset = this.scrollOffset;
        this.lastReferencePrice = referencePrice;
        this.lastCenterPrice = this.centerPrice;
    }

    private markRowDirty(rowIndex: number): void {
        if (rowIndex < 0 || rowIndex > this.visibleRows) {
            return;
        }

        this.dirtyRows.add(rowIndex);
        if (rowIndex < this.minDirtyRow) {
            this.minDirtyRow = rowIndex;
        }
        if (rowIndex > this.maxDirtyRow) {
            this.maxDirtyRow = rowIndex;
        }
    }

    private renderDirty(snapshot: OrderBookSnapshot, referencePrice: number): void {
        if (!snapshot.dirtyChanges || snapshot.dirtyChanges.length === 0) {
            return;
        }

        const midRow = Math.floor(this.visibleRows / 2);
        let denseLayout: DensePackingLayout | null = null;
        let levelMap: Map<string, BookLevel> | null = null;

        if (this.removalMode === 'removeRow') {
            denseLayout = this.buildDensePackingLayout(snapshot);
        } else {
            levelMap = this.buildLevelMap(snapshot);
        }

        for (const change of snapshot.dirtyChanges) {
            let rowIndex: number | null = null;
            if (this.removalMode === 'removeRow') {
                if (denseLayout) {
                    rowIndex = this.getDenseRowIndexForChange(change, denseLayout);
                }
            } else {
                rowIndex = this.priceToRowIndex(change.price, referencePrice, midRow);
            }

            if (rowIndex !== null) {
                this.markRowDirty(rowIndex);
            }
        }

        if (this.removalMode === 'removeRow' && snapshot.structuralChange && denseLayout) {
            let minRow = this.visibleRows;
            let hasRow = false;

            for (const change of snapshot.dirtyChanges) {
                if (!change.isRemoval && !change.isAddition) {
                    continue;
                }

                const rowIndex = this.getDenseRowIndexForChange(change, denseLayout);
                if (rowIndex !== null) {
                    minRow = Math.min(minRow, rowIndex);
                    hasRow = true;
                }
            }

            if (!hasRow) {
                this.renderFull(snapshot, referencePrice);
                this.markAllDirty();
                return;
            }

            for (let row = minRow; row <= this.visibleRows; row++) {
                this.markRowDirty(row);
            }
        }

        if (this.dirtyRows.size === 0) {
            return;
        }

        for (const rowIndex of this.dirtyRows) {
            const y = rowIndex * this.rowHeight;
            if (y < 0 || y >= this.height) {
                continue;
            }

            this.drawRowBackground(rowIndex);
            this.drawRowGridLines(rowIndex);

            if (this.removalMode === 'removeRow') {
                if (!denseLayout) {
                    continue;
                }

                const denseResult = this.tryGetDenseLevelForRow(rowIndex, denseLayout);
                if (denseResult) {
                    const normalizedPrice = Math.round(denseResult.level.price / this.tickSize) * this.tickSize;
                    const orders = denseResult.side === Side.ASK
                        ? this.getOrdersForLevel(snapshot.askOrders, normalizedPrice, this.askOrdersByPriceKey)
                        : this.getOrdersForLevel(snapshot.bidOrders, normalizedPrice, this.bidOrdersByPriceKey);
                    this.renderRow(rowIndex, denseResult.level, orders);
                }
            } else {
                const rowOffset = rowIndex - midRow;
                const price = referencePrice - (rowOffset * this.tickSize);
                const roundedPrice = Math.round(price / this.tickSize) * this.tickSize;
                const priceKey = this.formatPrice(roundedPrice);

                this.renderPriceOnly(rowIndex, roundedPrice);

                const level = levelMap?.get(priceKey);
                if (level) {
                    const orderMap = level.side === Side.BID ? snapshot.bidOrders : snapshot.askOrders;
                    const orders = this.getOrdersForLevel(
                        orderMap,
                        roundedPrice,
                        level.side === Side.BID ? this.bidOrdersByPriceKey : this.askOrdersByPriceKey
                    );
                    this.renderDataOverlay(rowIndex, level, orders);
                }
            }
        }

        for (const rowIndex of this.dirtyRows) {
            const y = rowIndex * this.rowHeight;
            if (y < 0 || y >= this.height) {
                continue;
            }

            this.ctx.drawImage(
                this.offscreenCanvas as any,
                0,
                y,
                this.width,
                this.rowHeight,
                0,
                y,
                this.width,
                this.rowHeight
            );
        }
    }

    private buildLevelMap(snapshot: OrderBookSnapshot): Map<string, BookLevel> {
        const levelMap = new Map<string, BookLevel>();

        for (const level of snapshot.asks) {
            if (level.quantity > 0) {
                const roundedPrice = Math.round(level.price / this.tickSize) * this.tickSize;
                const priceKey = this.formatPrice(roundedPrice);
                levelMap.set(priceKey, level);
            }
        }

        for (const level of snapshot.bids) {
            if (level.quantity > 0) {
                const roundedPrice = Math.round(level.price / this.tickSize) * this.tickSize;
                const priceKey = this.formatPrice(roundedPrice);
                levelMap.set(priceKey, level);
            }
        }

        return levelMap;
    }

    private renderDensePacking(snapshot: OrderBookSnapshot): void {
        const layout = this.buildDensePackingLayout(snapshot);

        // Render asks (highest to lowest)
        let currentY = layout.topOffset;
        for (let i = 0; i < layout.askRowsToRender; i++) {
            const askIndex = layout.nonEmptyAsks.length - 1 - layout.startAskIndex - i;
            if (askIndex >= 0 && askIndex < layout.nonEmptyAsks.length) {
                const y = currentY + (i * this.rowHeight);
                if (y >= 0 && y < this.height) {
                    const level = layout.nonEmptyAsks[askIndex];
                    const normalizedPrice = Math.round(level.price / this.tickSize) * this.tickSize;
                    const orders = this.getOrdersForLevel(snapshot.askOrders, normalizedPrice, this.askOrdersByPriceKey);
                    this.renderRow(Math.floor(y / this.rowHeight), level, orders);
                }
            }
        }

        // Render bids (highest to lowest)
        currentY = layout.topOffset + (layout.askRowsToRender * this.rowHeight);
        for (let i = 0; i < layout.bidRowsToRender; i++) {
            const bidIndex = layout.nonEmptyBids.length - 1 - layout.startBidIndex - i;
            if (bidIndex >= 0 && bidIndex < layout.nonEmptyBids.length) {
                const y = currentY + (i * this.rowHeight);
                if (y >= 0 && y < this.height) {
                    const level = layout.nonEmptyBids[bidIndex];
                    const normalizedPrice = Math.round(level.price / this.tickSize) * this.tickSize;
                    const orders = this.getOrdersForLevel(snapshot.bidOrders, normalizedPrice, this.bidOrdersByPriceKey);
                    this.renderRow(Math.floor(y / this.rowHeight), level, orders);
                }
            }
        }
    }

    private buildDensePackingLayout(snapshot: OrderBookSnapshot): DensePackingLayout {
        const nonEmptyAsks = snapshot.asks.filter(l => l.quantity > 0);
        const nonEmptyBids = snapshot.bids.filter(l => l.quantity > 0);
        const midRow = Math.floor(this.visibleRows / 2);
        const totalLevels = nonEmptyAsks.length + nonEmptyBids.length;

        // Stable scroll formula (matches desktop SkiaRenderer.cs)
        const virtualTopRow = nonEmptyAsks.length - midRow + this.scrollOffset;

        let startAskIndex: number;
        let askRowsToRender: number;
        let startBidIndex: number;
        let bidRowsToRender: number;
        let topOffset = 0;

        if (virtualTopRow < 0) {
            topOffset = -virtualTopRow * this.rowHeight;
            startAskIndex = 0;
            askRowsToRender = Math.min(nonEmptyAsks.length, Math.max(0, this.visibleRows + virtualTopRow));
            startBidIndex = 0;
            const bidRowsAvailable = Math.max(0, this.visibleRows + virtualTopRow - nonEmptyAsks.length);
            bidRowsToRender = Math.min(nonEmptyBids.length, bidRowsAvailable);
        } else if (virtualTopRow < nonEmptyAsks.length) {
            startAskIndex = virtualTopRow;
            askRowsToRender = Math.min(nonEmptyAsks.length - startAskIndex, this.visibleRows);
            startBidIndex = 0;
            const bidRowsAvailable = this.visibleRows - askRowsToRender;
            bidRowsToRender = Math.min(nonEmptyBids.length, bidRowsAvailable);
        } else if (virtualTopRow < totalLevels) {
            startAskIndex = nonEmptyAsks.length;
            askRowsToRender = 0;
            const bidStartRow = virtualTopRow - nonEmptyAsks.length;
            startBidIndex = bidStartRow;
            bidRowsToRender = Math.min(nonEmptyBids.length - startBidIndex, this.visibleRows);
        } else {
            startAskIndex = nonEmptyAsks.length;
            askRowsToRender = 0;
            startBidIndex = nonEmptyBids.length;
            bidRowsToRender = 0;
        }

        const firstRowIndex = Math.floor(topOffset / this.rowHeight);

        return {
            nonEmptyAsks,
            nonEmptyBids,
            startAskIndex,
            askRowsToRender,
            startBidIndex,
            bidRowsToRender,
            topOffset,
            firstRowIndex
        };
    }

    private getDenseRowIndexForChange(change: DirtyLevelChange, layout: DensePackingLayout): number | null {
        if (change.side === Side.ASK) {
            let askIndex = this.indexOfPrice(layout.nonEmptyAsks, change.price);
            if (askIndex < 0) {
                askIndex = this.lowerBoundPrice(layout.nonEmptyAsks, change.price);
            }

            const rowOffset = (layout.nonEmptyAsks.length - 1 - layout.startAskIndex) - askIndex;
            if (rowOffset >= 0 && rowOffset < layout.askRowsToRender) {
                return layout.firstRowIndex + rowOffset;
            }
            return null;
        }

        let bidIndex = this.indexOfPrice(layout.nonEmptyBids, change.price);
        if (bidIndex < 0) {
            bidIndex = this.lowerBoundPrice(layout.nonEmptyBids, change.price);
        }

        const bidRowOffset = (layout.nonEmptyBids.length - 1 - layout.startBidIndex) - bidIndex;
        if (bidRowOffset >= 0 && bidRowOffset < layout.bidRowsToRender) {
            return layout.firstRowIndex + layout.askRowsToRender + bidRowOffset;
        }

        return null;
    }

    private tryGetDenseLevelForRow(rowIndex: number, layout: DensePackingLayout): { level: BookLevel; side: Side } | null {
        const relativeRow = rowIndex - layout.firstRowIndex;
        if (relativeRow < 0) {
            return null;
        }

        if (relativeRow < layout.askRowsToRender) {
            const askIndex = layout.nonEmptyAsks.length - 1 - layout.startAskIndex - relativeRow;
            if (askIndex >= 0 && askIndex < layout.nonEmptyAsks.length) {
                return { level: layout.nonEmptyAsks[askIndex], side: Side.ASK };
            }
            return null;
        }

        const bidRow = relativeRow - layout.askRowsToRender;
        if (bidRow < layout.bidRowsToRender) {
            const bidIndex = layout.nonEmptyBids.length - 1 - layout.startBidIndex - bidRow;
            if (bidIndex >= 0 && bidIndex < layout.nonEmptyBids.length) {
                return { level: layout.nonEmptyBids[bidIndex], side: Side.BID };
            }
        }

        return null;
    }

    private priceToRowIndex(price: number, referencePrice: number, midRow: number): number {
        const priceDelta = price - referencePrice;
        const rowOffset = -Math.round(priceDelta / this.tickSize);
        return midRow + rowOffset;
    }

    private indexOfPrice(levels: BookLevel[], price: number): number {
        for (let i = 0; i < levels.length; i++) {
            if (levels[i].price === price) {
                return i;
            }
        }

        return -1;
    }

    private lowerBoundPrice(levels: BookLevel[], price: number): number {
        let low = 0;
        let high = levels.length;

        while (low < high) {
            const mid = Math.floor((low + high) / 2);
            if (levels[mid].price < price) {
                low = mid + 1;
            } else {
                high = mid;
            }
        }

        return low;
    }

    private renderShowEmpty(
        snapshot: OrderBookSnapshot,
        referencePrice: number,
        levelMap?: Map<string, BookLevel>
    ): void {
        // PRICE-TO-ROW MAPPING MODE: Each price maps to fixed row (shows gaps for empty levels)
        // First, render all price rows (including empty ones with just price labels)
        // Then, overlay data where it exists

        const midRow = Math.floor(this.visibleRows / 2);

        // Step 1: Render all price labels for visible rows (shows gaps)
        for (let rowIndex = 0; rowIndex <= this.visibleRows; rowIndex++) {
            // Calculate what price this row represents
            const rowOffset = rowIndex - midRow;
            const price = referencePrice - (rowOffset * this.tickSize);
            // Round to tick size to avoid floating-point precision issues
            const roundedPrice = Math.round(price / this.tickSize) * this.tickSize;

            // Render just the price label (no quantity/orders)
            this.renderPriceOnly(rowIndex, roundedPrice);
        }

        // Step 2: Create a map of price to level data for quick lookup
        // Use string keys to avoid floating-point precision issues
        const levelsByPrice = levelMap ?? this.buildLevelMap(snapshot);

        // Step 3: Overlay data on rows that have levels
        for (let rowIndex = 0; rowIndex <= this.visibleRows; rowIndex++) {
            const rowOffset = rowIndex - midRow;
            const price = referencePrice - (rowOffset * this.tickSize);
            const roundedPrice = Math.round(price / this.tickSize) * this.tickSize;
            const priceKey = this.formatPrice(roundedPrice);

            const level = levelsByPrice.get(priceKey);
            if (level) {
                // Get individual orders if available (MBO mode)
                const orderMap = level.side === Side.BID ? snapshot.bidOrders : snapshot.askOrders;
                const orders = this.getOrdersForLevel(
                    orderMap,
                    roundedPrice,
                    level.side === Side.BID ? this.bidOrdersByPriceKey : this.askOrdersByPriceKey
                );

                // Render data overlay (quantity, orders, bars)
                this.renderDataOverlay(rowIndex, level, orders);
            }
        }
    }

    private renderRow(rowIndex: number, level: BookLevel, orders?: Order[]): void {
        const y = rowIndex * this.rowHeight;

        // Prepare text values
        const priceText = this.formatPrice(level.price);
        const qtyText = level.quantity.toLocaleString();
        const ordersText = `(${level.numOrders})`;

        // Calculate column indices based on which features are enabled
        let bidOrderCol = this.showOrderCount ? 0 : -1;
        let bidQtyCol = this.showOrderCount ? 1 : 0;
        let priceCol = this.showOrderCount ? 2 : 1;
        let askQtyCol = this.showOrderCount ? 3 : 2;
        let askOrderCol = this.showOrderCount ? 4 : -1;

        // Bar column calculation
        let columnCount = 3; // Base columns: bid_qty, price, ask_qty
        if (this.showOrderCount) columnCount += 2; // Add bid_orders and ask_orders
        let barCol = columnCount; // Bars always after the last data column

        const maxQty = this.showVolumeBars ? this.calculateMaxQuantity() : 0;
        const BAR_MAX_WIDTH = (COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER) - 5;
        const barWidth = maxQty > 0 ? (level.quantity / maxQty) * BAR_MAX_WIDTH : 0;

        // Set text style
        this.offscreenCtx.fillStyle = this.colors.text;
        this.offscreenCtx.textAlign = 'center';

        if (level.side === Side.BID) {
            // BID: bid_order_count, bid_qty, price, bars

            // bid_order_count (if enabled)
            if (bidOrderCol >= 0) {
                this.offscreenCtx.fillText(ordersText, COL_WIDTH * (bidOrderCol + 0.5), y + this.rowHeight / 2);
            }

            // bid_qty
            this.offscreenCtx.fillText(qtyText, COL_WIDTH * (bidQtyCol + 0.5), y + this.rowHeight / 2);

            // price
            this.offscreenCtx.fillText(priceText, COL_WIDTH * (priceCol + 0.5), y + this.rowHeight / 2);

            // bars (if enabled)
            if (this.showVolumeBars) {
                if (orders && orders.length > 0) {
                    // MBO mode: Draw individual order bars
                    this.drawIndividualOrders(orders, Side.BID, maxQty, y);
                } else if (barWidth > 0) {
                    // PriceLevel mode: Draw single aggregated bar
                    this.offscreenCtx.fillStyle = this.colors.bidBar;
                    this.offscreenCtx.fillRect(COL_WIDTH * barCol, y + 4, barWidth, this.rowHeight - 8);
                }
            }
        } else {
            // ASK: price, ask_qty, ask_order_count, bars

            // price
            this.offscreenCtx.fillText(priceText, COL_WIDTH * (priceCol + 0.5), y + this.rowHeight / 2);

            // ask_qty
            this.offscreenCtx.fillText(qtyText, COL_WIDTH * (askQtyCol + 0.5), y + this.rowHeight / 2);

            // ask_order_count (if enabled)
            if (askOrderCol >= 0) {
                this.offscreenCtx.fillText(ordersText, COL_WIDTH * (askOrderCol + 0.5), y + this.rowHeight / 2);
            }

            // bars (if enabled)
            if (this.showVolumeBars) {
                if (orders && orders.length > 0) {
                    // MBO mode: Draw individual order bars
                    this.drawIndividualOrders(orders, Side.ASK, maxQty, y);
                } else if (barWidth > 0) {
                    // PriceLevel mode: Draw single aggregated bar
                    this.offscreenCtx.fillStyle = this.colors.askBar;
                    this.offscreenCtx.fillRect(COL_WIDTH * barCol, y + 4, barWidth, this.rowHeight - 8);
                }
            }
        }

        // Own order indicator removed - now drawn on individual segments in drawIndividualOrders
    }

    private drawRowBackground(rowIndex: number): void {
        const y = rowIndex * this.rowHeight;

        // Base row background
        this.offscreenCtx.fillStyle = this.colors.background;
        this.offscreenCtx.fillRect(0, y, this.width, this.rowHeight);

        const bidQtyCol = this.showOrderCount ? 1 : 0;
        const priceCol = this.showOrderCount ? 2 : 1;
        const askQtyCol = this.showOrderCount ? 3 : 2;

        // Bid qty column
        this.offscreenCtx.fillStyle = this.colors.bidQtyBackground;
        this.offscreenCtx.fillRect(COL_WIDTH * bidQtyCol, y, COL_WIDTH, this.rowHeight);

        // Price column
        this.offscreenCtx.fillStyle = this.colors.priceBackground;
        this.offscreenCtx.fillRect(COL_WIDTH * priceCol, y, COL_WIDTH, this.rowHeight);

        // Ask qty column
        this.offscreenCtx.fillStyle = this.colors.askQtyBackground;
        this.offscreenCtx.fillRect(COL_WIDTH * askQtyCol, y, COL_WIDTH, this.rowHeight);
    }

    private drawRowGridLines(rowIndex: number): void {
        const y = rowIndex * this.rowHeight;
        const bottomY = y + this.rowHeight;

        this.offscreenCtx.strokeStyle = this.colors.gridLine;
        this.offscreenCtx.lineWidth = 1;
        this.offscreenCtx.beginPath();
        this.offscreenCtx.moveTo(0, y);
        this.offscreenCtx.lineTo(this.width, y);
        this.offscreenCtx.stroke();

        if (bottomY <= this.height) {
            this.offscreenCtx.beginPath();
            this.offscreenCtx.moveTo(0, bottomY);
            this.offscreenCtx.lineTo(this.width, bottomY);
            this.offscreenCtx.stroke();
        }
    }

    /**
     * Render only the price label for a row (used in Show Empty mode)
     */
    private renderPriceOnly(rowIndex: number, price: number): void {
        const y = rowIndex * this.rowHeight;
        const priceText = this.formatPrice(price);

        // Calculate price column index
        const priceCol = this.showOrderCount ? 2 : 1;

        // Set text style
        this.offscreenCtx.fillStyle = this.colors.text;
        this.offscreenCtx.textAlign = 'center';

        // Render price only
        this.offscreenCtx.fillText(priceText, COL_WIDTH * (priceCol + 0.5), y + this.rowHeight / 2);
    }

    /**
     * Render data overlay (quantity, orders, bars) for a row that has a level
     */
    private renderDataOverlay(rowIndex: number, level: BookLevel, orders?: Order[]): void {
        const y = rowIndex * this.rowHeight;
        const qtyText = level.quantity.toLocaleString();
        const ordersText = `(${level.numOrders})`;

        // Calculate column indices
        const bidOrderCol = this.showOrderCount ? 0 : -1;
        const bidQtyCol = this.showOrderCount ? 1 : 0;
        const askQtyCol = this.showOrderCount ? 3 : 2;
        const askOrderCol = this.showOrderCount ? 4 : -1;

        let columnCount = 3;
        if (this.showOrderCount) columnCount += 2;
        const barCol = columnCount;

        const maxQty = this.showVolumeBars ? this.calculateMaxQuantity() : 0;
        const BAR_MAX_WIDTH = (COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER) - 5;
        const barWidth = maxQty > 0 ? (level.quantity / maxQty) * BAR_MAX_WIDTH : 0;

        // Set text style
        this.offscreenCtx.fillStyle = this.colors.text;
        this.offscreenCtx.textAlign = 'center';

        if (level.side === Side.BID) {
            // BID: render bid_order_count, bid_qty, and bars

            // bid_order_count (if enabled)
            if (bidOrderCol >= 0) {
                this.offscreenCtx.fillText(ordersText, COL_WIDTH * (bidOrderCol + 0.5), y + this.rowHeight / 2);
            }

            // bid_qty
            this.offscreenCtx.fillText(qtyText, COL_WIDTH * (bidQtyCol + 0.5), y + this.rowHeight / 2);

            // bars (if enabled)
            if (this.showVolumeBars) {
                if (orders && orders.length > 0) {
                    // MBO mode: Draw individual order bars
                    this.drawIndividualOrders(orders, Side.BID, maxQty, y);
                } else if (barWidth > 0) {
                    // PriceLevel mode: Draw single aggregated bar
                    this.offscreenCtx.fillStyle = this.colors.bidBar;
                    this.offscreenCtx.fillRect(COL_WIDTH * barCol, y + 4, barWidth, this.rowHeight - 8);
                }
            }
        } else {
            // ASK: render ask_qty, ask_order_count, and bars

            // ask_qty
            this.offscreenCtx.fillText(qtyText, COL_WIDTH * (askQtyCol + 0.5), y + this.rowHeight / 2);

            // ask_order_count (if enabled)
            if (askOrderCol >= 0) {
                this.offscreenCtx.fillText(ordersText, COL_WIDTH * (askOrderCol + 0.5), y + this.rowHeight / 2);
            }

            // bars (if enabled)
            if (this.showVolumeBars) {
                if (orders && orders.length > 0) {
                    // MBO mode: Draw individual order bars
                    this.drawIndividualOrders(orders, Side.ASK, maxQty, y);
                } else if (barWidth > 0) {
                    // PriceLevel mode: Draw single aggregated bar
                    this.offscreenCtx.fillStyle = this.colors.askBar;
                    this.offscreenCtx.fillRect(COL_WIDTH * barCol, y + 4, barWidth, this.rowHeight - 8);
                }
            }
        }

        // Own order indicator removed - now drawn on individual segments in drawIndividualOrders
    }

    /**
     * Draw individual order bars (MBO mode)
     * Draws bars as horizontally adjacent segments within the row
     */
    private drawIndividualOrders(
        orders: Order[],
        side: Side,
        maxQty: number,
        y: number
    ): void {
        if (!orders || orders.length === 0 || !this.showVolumeBars) return;

        let columnCount = 3;
        if (this.showOrderCount) columnCount += 2;
        const barCol = columnCount;
        const barStartX = COL_WIDTH * barCol;
        const BAR_MAX_WIDTH = (COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER) - 5;

        // Same height as single bar (with padding)
        const barHeight = this.rowHeight - 8;
        const fillColor = side === Side.BID ? this.colors.bidBar : this.colors.askBar;
        const segmentGap = ORDER_SEGMENT_GAP;
        const minSegmentWidth = MIN_ORDER_SEGMENT_WIDTH;

        let totalOrderQuantity = 0;
        for (const order of orders) {
            totalOrderQuantity += order.quantity;
        }

        if (totalOrderQuantity <= 0) {
            return;
        }

        // Calculate the total bar width for this level (proportional to maxQty)
        let levelBarWidth = maxQty > 0 ? (totalOrderQuantity / maxQty) * BAR_MAX_WIDTH : 0;
        const minTotalWidth = (orders.length * minSegmentWidth) + (segmentGap * Math.max(0, orders.length - 1));
        levelBarWidth = Math.max(levelBarWidth, Math.min(minTotalWidth, BAR_MAX_WIDTH));

        if (levelBarWidth <= 0) {
            return;
        }

        // Always use consistent gap between segments
        const gap = orders.length > 1 ? segmentGap : 0;
        const totalGapWidth = gap * (orders.length - 1);
        const availableWidth = Math.max(0, levelBarWidth - totalGapWidth);

        const effectiveMinWidth = Math.min(minSegmentWidth, availableWidth / orders.length);
        let deficit = 0;
        let surplus = 0;
        for (const order of orders) {
            const baseWidth = (order.quantity / totalOrderQuantity) * availableWidth;
            if (baseWidth < effectiveMinWidth) {
                deficit += effectiveMinWidth - baseWidth;
            } else if (baseWidth > effectiveMinWidth) {
                surplus += baseWidth - effectiveMinWidth;
            }
        }

        const reductionFactor = deficit > 0 && surplus > 0 ? deficit / surplus : 0;
        let xOffset = barStartX;  // Start at left edge of volume bar column

        for (let i = 0; i < orders.length; i++) {
            const order = orders[i];

            // Calculate segment width as proportion of levelBarWidth (not BAR_MAX_WIDTH)
            // This ensures all orders at this level fit within the level's total bar
            let barWidth = (order.quantity / totalOrderQuantity) * availableWidth;
            if (barWidth < effectiveMinWidth) {
                barWidth = effectiveMinWidth;
            } else if (reductionFactor > 0) {
                barWidth -= (barWidth - effectiveMinWidth) * reductionFactor;
            }

            // Draw individual order bar as a segment with gap
            if (barWidth > 0) {
                this.offscreenCtx.fillStyle = fillColor;
                this.offscreenCtx.fillRect(xOffset, y + 4, barWidth, barHeight);

                // Draw order quantity text centered in segment
                const qtyText = this.formatQuantity(order.quantity);
                this.offscreenCtx.font = '10px monospace';
                const textWidth = this.offscreenCtx.measureText(qtyText).width;
                if (textWidth < barWidth - 4) {  // Only draw if text fits
                    this.offscreenCtx.fillStyle = this.colors.text;
                    this.offscreenCtx.textAlign = 'center';
                    this.offscreenCtx.textBaseline = 'middle';
                    this.offscreenCtx.fillText(qtyText, xOffset + barWidth / 2, y + 4 + barHeight / 2);
                }
                // Restore font for subsequent text rendering
                this.offscreenCtx.font = '14px monospace';
                this.offscreenCtx.textAlign = 'left';
                this.offscreenCtx.textBaseline = 'middle';

                // Draw gold border if this is an own order
                if (order.isOwnOrder) {
                    this.offscreenCtx.strokeStyle = this.colors.ownOrderBorder;
                    this.offscreenCtx.lineWidth = 2;
                    this.offscreenCtx.strokeRect(xOffset, y + 4, barWidth, barHeight);
                }
            }

            // Move to the right for next bar segment
            xOffset += barWidth;
            if (gap > 0 && i < orders.length - 1) {
                xOffset += gap;
            }

            // Stop if we've exceeded the level's total bar width
            if (xOffset >= barStartX + levelBarWidth) {
                break;
            }
        }
    }

    /**
     * Format quantity for display in segment (e.g., "500", "1K", "2M")
     */
    private formatQuantity(quantity: number): string {
        if (quantity >= 1000000) return `${Math.floor(quantity / 1000000)}M`;
        if (quantity >= 1000) return `${Math.floor(quantity / 1000)}K`;
        return quantity.toString();
    }

    private calculateMaxQuantity(): number {
        if (!this.currentSnapshot) return 1;

        let maxQty = 0;
        for (const bid of this.currentSnapshot.bids) {
            maxQty = Math.max(maxQty, bid.quantity);
        }
        for (const ask of this.currentSnapshot.asks) {
            maxQty = Math.max(maxQty, ask.quantity);
        }

        return maxQty || 1;
    }

    private copyToMainCanvas(): void {
        // Copy offscreen canvas to main canvas (hardware accelerated)
        this.ctx.drawImage(this.offscreenCanvas as any, 0, 0);
    }

    private markAllDirty(): void {
        this.dirtyRows.clear();
        this.minDirtyRow = 0;
        this.maxDirtyRow = this.visibleRows - 1;
    }

    private clearDirtyState(): void {
        this.dirtyRows.clear();
        this.minDirtyRow = Infinity;
        this.maxDirtyRow = -1;
    }

    private updateFPS(_frameTime: number): void {
        this.frameCount++;
        const now = performance.now();

        if (now - this.lastFrameTime >= 1000) {
            this.fps = this.frameCount;
            this.frameCount = 0;
            this.lastFrameTime = now;
        }
    }

    private getOrdersForLevel(
        orderMap: Map<number, Order[]> | null | undefined,
        price: number,
        orderMapByPriceKey?: Map<string, Order[]> | null
    ): Order[] | undefined {
        if (!orderMap) return undefined;

        if (orderMapByPriceKey) {
            const keyedOrders = orderMapByPriceKey.get(this.formatPrice(price));
            if (keyedOrders) return keyedOrders;
        }

        let orders = orderMap.get(price);
        if (orders) return orders;

        const roundedPrice = Math.round(price / this.tickSize) * this.tickSize;
        orders = orderMap.get(roundedPrice);
        if (orders) return orders;

        const epsilon = Math.max(this.tickSize / 1000, 1e-6);
        for (const [key, value] of orderMap.entries()) {
            if (Math.abs(key - price) <= epsilon) {
                return value;
            }
        }

        const targetTick = Math.round(roundedPrice / this.tickSize);
        for (const [key, value] of orderMap.entries()) {
            const keyTick = Math.round(key / this.tickSize);
            if (keyTick === targetTick) {
                return value;
            }
        }

        const targetKey = this.formatPrice(roundedPrice);
        for (const [key, value] of orderMap.entries()) {
            if (this.formatPrice(key) === targetKey) {
                return value;
            }
        }

        return undefined;
    }

    /**
     * Get performance metrics
     */
    public getMetrics(): RenderMetrics {
        const dirtyRowCount = this.minDirtyRow === Infinity
            ? 0
            : Math.max(0, this.maxDirtyRow - this.minDirtyRow + 1);

        return {
            fps: this.fps,
            frameTime: 1000 / this.fps,
            dirtyRowCount,
            totalRows: this.visibleRows
        };
    }

    /**
     * Convert screen X coordinate to column index
     */
    public screenXToColumn(x: number): number {
        return Math.floor(x / COL_WIDTH);
    }

    /**
     * Get the price column index in the RENDERED layout
     */
    public getPriceColumn(): number {
        // When orders enabled: columns are [0:bid_orders][1:bid_qty][2:price][3:ask_qty][4:ask_orders][5:bars]
        // When orders disabled: columns 0 and 4 removed, so [0:bid_qty][1:price][2:ask_qty][3:bars]
        // Price is always after bid_qty, so:
        // - With orders: price at rendered column 2
        // - Without orders: price at rendered column 1
        return this.showOrderCount ? 2 : 1;
    }

    /**
     * Get the BID quantity column index
     */
    public getBidQtyColumn(): number {
        return this.showOrderCount ? 1 : 0;
    }

    /**
     * Get the ASK quantity column index
     */
    public getAskQtyColumn(): number {
        return this.showOrderCount ? 3 : 2;
    }

    /**
     * Convert screen Y coordinate to row index
     */
    public screenYToRow(y: number): number {
        return Math.floor(y / this.rowHeight);
    }

    /**
     * Convert row index to price
     */
    public rowToPrice(rowIndex: number): number | null {
        const levelInfo = this.rowToLevelInfo(rowIndex);
        return levelInfo?.price ?? null;
    }

    /**
     * Convert row index to level info (price and quantity)
     */
    public rowToLevelInfo(rowIndex: number): { price: number; quantity: number; side: Side } | null {
        if (!this.currentSnapshot) return null;

        if (this.removalMode === 'removeRow') {
            // Dense packing mode: use the same layout logic as rendering
            const layout = this.buildDensePackingLayout(this.currentSnapshot);

            // Convert row to relative row accounting for topOffset
            const relativeRow = rowIndex - layout.firstRowIndex;

            // console.log(`Dense Click Debug: rowIndex=${rowIndex}, topOffset=${layout.topOffset}, firstRowIndex=${layout.firstRowIndex}, relativeRow=${relativeRow}, askRows=${layout.askRowsToRender}, startAskIdx=${layout.startAskIndex}`);

            if (relativeRow < 0) {
                return null; // Clicked above visible data
            }

            if (relativeRow < layout.askRowsToRender) {
                // Clicked on an ask row
                const askIndex = layout.nonEmptyAsks.length - 1 - layout.startAskIndex - relativeRow;
                // console.log(`Ask click: askIndex=${askIndex}, nonEmptyAsks.length=${layout.nonEmptyAsks.length}`);
                if (askIndex >= 0 && askIndex < layout.nonEmptyAsks.length) {
                    const level = layout.nonEmptyAsks[askIndex];
                    // console.log(`Selected ask: price=${level.price}, qty=${level.quantity}`);
                    return { price: level.price, quantity: level.quantity, side: Side.ASK };
                }
                return null;
            }

            // Clicked on a bid row
            const bidRow = relativeRow - layout.askRowsToRender;
            if (bidRow >= 0 && bidRow < layout.bidRowsToRender) {
                const bidIndex = layout.nonEmptyBids.length - 1 - layout.startBidIndex - bidRow;
                if (bidIndex >= 0 && bidIndex < layout.nonEmptyBids.length) {
                    const level = layout.nonEmptyBids[bidIndex];
                    return { price: level.price, quantity: level.quantity, side: Side.BID };
                }
            }

            return null;
        } else {
            // ShowEmpty mode: price-to-row mapping
            const midRow = Math.floor(this.visibleRows / 2);
            const referencePrice = this.centerPrice !== 0
                ? this.centerPrice
                : (this.currentSnapshot.midPrice ?? 50000);

            const rowOffset = rowIndex - midRow;
            const price = referencePrice - (rowOffset * this.tickSize);
            const roundedPrice = Math.round(price / this.tickSize) * this.tickSize;

            // Find level at this price
            const askLevel = this.currentSnapshot.asks.find(a => Math.abs(a.price - roundedPrice) < this.tickSize * 0.5);
            if (askLevel && askLevel.quantity > 0) {
                return { price: askLevel.price, quantity: askLevel.quantity, side: Side.ASK };
            }

            const bidLevel = this.currentSnapshot.bids.find(b => Math.abs(b.price - roundedPrice) < this.tickSize * 0.5);
            if (bidLevel && bidLevel.quantity > 0) {
                return { price: bidLevel.price, quantity: bidLevel.quantity, side: Side.BID };
            }

            return null;
        }
    }

    /**
     * Resize the canvas
     */
    public resize(width: number, height: number): void {
        this.width = width;
        this.height = height;
        this.visibleRows = Math.floor(height / this.rowHeight);

        this.canvas.width = width;
        this.canvas.height = height;
        this.canvas.style.width = `${width}px`;
        this.canvas.style.height = `${height}px`;

        this.offscreenCanvas = new OffscreenCanvas(width, height);
        const ctx = this.offscreenCanvas.getContext('2d', { alpha: false });
        if (ctx) {
            this.offscreenCtx = ctx;
            this.setupContext(this.offscreenCtx);
        }

        this.renderBackground();

        if (this.currentSnapshot) {
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Set whether to show volume bars
     */
    public setShowVolumeBars(show: boolean): void {
        if (this.showVolumeBars !== show) {
            this.showVolumeBars = show;
            // Recalculate and resize canvas based on new column count
            const newWidth = this.calculateCanvasWidth();
            this.resize(newWidth, this.height);
        }
    }

    /**
     * Set whether to show order count
     */
    public setShowOrderCount(show: boolean): void {
        if (this.showOrderCount !== show) {
            this.showOrderCount = show;
            // Recalculate and resize canvas based on new column count
            const newWidth = this.calculateCanvasWidth();
            this.resize(newWidth, this.height);
        }
    }

    /**
     * Set scroll offset for dense packing mode
     */
    public setScrollOffset(offset: number): void {
        this.scrollOffset = offset;
        if (this.currentSnapshot) {
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Get current scroll offset
     */
    public getScrollOffset(): number {
        return this.scrollOffset;
    }

    /**
     * Set center price for show empty mode (price-based scrolling)
     */
    public setCenterPrice(price: number): void {
        this.centerPrice = price;
        if (this.currentSnapshot) {
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Get current center price
     */
    public getCenterPrice(): number {
        return this.centerPrice;
    }

    /**
     * Reset center price (called when order book is cleared)
     */
    public resetCenterPrice(): void {
        this.centerPrice = 0;
        this.needsFullRedraw = true;
    }

    /**
     * Scroll by price delta (for show empty mode)
     */
    public scrollByPrice(delta: number): void {
        // Initialize center price from mid price if not set
        if (this.centerPrice === 0 && this.currentSnapshot) {
            const midPrice = this.currentSnapshot.midPrice;
            if (midPrice !== null) {
                // Round to tick size to ensure proper alignment
                this.centerPrice = Math.round(midPrice / this.tickSize) * this.tickSize;
            }
        }

        // Apply scroll delta and round to tick size
        this.centerPrice = Math.round((this.centerPrice + delta) / this.tickSize) * this.tickSize;

        if (this.currentSnapshot) {
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Set removal mode (affects how empty levels are handled)
     */
    public setRemovalMode(mode: 'showEmpty' | 'removeRow'): void {
        this.removalMode = mode;
        this.scrollOffset = 0; // Reset scroll when changing modes
        this.centerPrice = 0;  // Reset center price when changing modes
        this.needsFullRedraw = true;
        if (this.currentSnapshot) {
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Get current removal mode
     */
    public getRemovalMode(): 'showEmpty' | 'removeRow' {
        return this.removalMode;
    }

    /**
     * Get configured tick size
     */
    public getTickSize(): number {
        return this.tickSize;
    }

    /**
     * Calculate canvas width based on enabled features
     */
    private calculateCanvasWidth(): number {
        const dataColumns = this.showOrderCount ? 5 : 3;
        const barWidth = this.showVolumeBars ? COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER : 0;
        return Math.round((dataColumns * COL_WIDTH) + barWidth);
    }
}
