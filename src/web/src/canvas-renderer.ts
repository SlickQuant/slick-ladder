import { BookLevel, Side, OrderBookSnapshot, CanvasColors, DEFAULT_COLORS, RenderMetrics, Order } from './types';

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

    // Layout configuration
    private showVolumeBars: boolean = true;
    private showOrderCount: boolean = true;

    // Performance tracking
    private lastFrameTime: number = 0;
    private frameCount: number = 0;
    private fps: number = 60;

    // Current snapshot
    private currentSnapshot: OrderBookSnapshot | null = null;

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

    private renderBackground(): void {
        this.offscreenCtx.fillStyle = this.colors.background;
        this.offscreenCtx.fillRect(0, 0, this.width, this.height);

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

        this.copyToMainCanvas();
    }

    /**
     * Render a price ladder snapshot with dirty region optimization
     */
    public render(snapshot: OrderBookSnapshot): void {
        const startTime = performance.now();

        this.currentSnapshot = snapshot;

        // Update center price (reserved for viewport scrolling)
        // if (snapshot.midPrice !== null) {
        //     this.centerPrice = snapshot.midPrice;
        // }

        // Mark all rows as potentially dirty
        // In a real implementation, we'd track which specific rows changed
        this.markAllDirty();

        // Render only dirty regions
        if (this.minDirtyRow !== Infinity) {
            this.renderDirtyRegions(snapshot);
        }

        // Clear dirty state
        this.clearDirtyState();

        // Update performance metrics
        const frameTime = performance.now() - startTime;
        this.updateFPS(frameTime);
    }

    private renderDirtyRegions(snapshot: OrderBookSnapshot): void {
        // Clear the entire canvas with background color
        this.offscreenCtx.fillStyle = this.colors.background;
        this.offscreenCtx.fillRect(0, 0, this.width, this.height);

        // Use fixed column width
        const COL_WIDTH = 66.7;

        // Calculate column indices
        let bidQtyCol = this.showOrderCount ? 1 : 0;
        let priceCol = this.showOrderCount ? 2 : 1;
        let askQtyCol = this.showOrderCount ? 3 : 2;

        // Draw column backgrounds across ALL rows
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

        // Delegate to mode-specific rendering
        if (this.removalMode === 'removeRow') {
            this.renderDensePacking(snapshot);
        } else {
            this.renderShowEmpty(snapshot);
        }

        this.copyToMainCanvas();
    }

    private renderDensePacking(snapshot: OrderBookSnapshot): void {
        // Filter out empty levels
        const nonEmptyAsks = snapshot.asks.filter(l => l.quantity > 0);
        const nonEmptyBids = snapshot.bids.filter(l => l.quantity > 0);

        const midRow = Math.floor(this.visibleRows / 2);
        const totalLevels = nonEmptyAsks.length + nonEmptyBids.length;

        // Stable scroll formula (matches desktop SkiaRenderer.cs:116)
        const virtualTopRow = nonEmptyAsks.length - midRow + this.scrollOffset;

        let startAskIndex: number, askRowsToRender: number;
        let startBidIndex: number, bidRowsToRender: number;
        let topOffset: number = 0;

        // Calculate which portion to show (matches desktop logic lines 126-161)
        if (virtualTopRow < 0) {
            // Scrolled above all data
            topOffset = -virtualTopRow * this.rowHeight;
            startAskIndex = 0;
            askRowsToRender = Math.min(nonEmptyAsks.length, Math.max(0, this.visibleRows + virtualTopRow));
            startBidIndex = 0;
            const bidRowsAvailable = Math.max(0, this.visibleRows + virtualTopRow - nonEmptyAsks.length);
            bidRowsToRender = Math.min(nonEmptyBids.length, bidRowsAvailable);
        } else if (virtualTopRow < nonEmptyAsks.length) {
            // Showing some asks at top
            startAskIndex = virtualTopRow;
            askRowsToRender = Math.min(nonEmptyAsks.length - startAskIndex, this.visibleRows);
            startBidIndex = 0;
            const bidRowsAvailable = this.visibleRows - askRowsToRender;
            bidRowsToRender = Math.min(nonEmptyBids.length, bidRowsAvailable);
        } else if (virtualTopRow < totalLevels) {
            // Past all asks, showing only bids
            startAskIndex = nonEmptyAsks.length;
            askRowsToRender = 0;
            const bidStartRow = virtualTopRow - nonEmptyAsks.length;
            startBidIndex = bidStartRow;
            bidRowsToRender = Math.min(nonEmptyBids.length - startBidIndex, this.visibleRows);
        } else {
            // Scrolled past all data
            startAskIndex = nonEmptyAsks.length;
            askRowsToRender = 0;
            startBidIndex = nonEmptyBids.length;
            bidRowsToRender = 0;
        }

        // Render asks (highest to lowest)
        let currentY = topOffset;
        for (let i = 0; i < askRowsToRender; i++) {
            const askIndex = nonEmptyAsks.length - 1 - startAskIndex - i;
            if (askIndex >= 0 && askIndex < nonEmptyAsks.length) {
                const y = currentY + (i * this.rowHeight);
                if (y >= 0 && y < this.height) {
                    const level = nonEmptyAsks[askIndex];
                    const orders = snapshot.askOrders?.get(level.price);
                    this.renderRow(Math.floor(y / this.rowHeight), level, orders);
                }
            }
        }

        // Render bids (highest to lowest)
        currentY = topOffset + (askRowsToRender * this.rowHeight);
        for (let i = 0; i < bidRowsToRender; i++) {
            const bidIndex = nonEmptyBids.length - 1 - startBidIndex - i;
            if (bidIndex >= 0 && bidIndex < nonEmptyBids.length) {
                const y = currentY + (i * this.rowHeight);
                if (y >= 0 && y < this.height) {
                    const level = nonEmptyBids[bidIndex];
                    const orders = snapshot.bidOrders?.get(level.price);
                    this.renderRow(Math.floor(y / this.rowHeight), level, orders);
                }
            }
        }
    }

    private renderShowEmpty(snapshot: OrderBookSnapshot): void {
        // PRICE-TO-ROW MAPPING MODE: Each price maps to fixed row (shows gaps for empty levels)
        // First, render all price rows (including empty ones with just price labels)
        // Then, overlay data where it exists

        // Determine reference price (viewport center or mid market)
        let referencePrice = this.centerPrice;

        // Reset centerPrice if order book is empty (after clear)
        const isEmpty = snapshot.bids.length === 0 && snapshot.asks.length === 0;
        if (isEmpty) {
            this.centerPrice = 0;
            referencePrice = 0;
        } else {
            // Initialize centerPrice if not set (first render or after clear)
            if (this.centerPrice === 0) {
                const midPrice = snapshot.midPrice ?? 100.00;
                // Round to tick size to ensure proper alignment
                this.centerPrice = Math.round(midPrice / this.tickSize) * this.tickSize;
            }
            // In Show Empty Rows mode, centerPrice is FIXED after initialization
            // The viewport does NOT track mid-price - it shows actual price levels
            // If market drifts away, empty rows are displayed (that's the point!)
            referencePrice = this.centerPrice;
        }

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
        const levelMap = new Map<string, BookLevel>();

        for (const level of snapshot.asks) {
            if (level.quantity > 0) {
                // Round price to tick size to ensure exact match
                const roundedPrice = Math.round(level.price / this.tickSize) * this.tickSize;
                const priceKey = this.formatPrice(roundedPrice);
                levelMap.set(priceKey, level);
            }
        }

        for (const level of snapshot.bids) {
            if (level.quantity > 0) {
                // Round price to tick size to ensure exact match
                const roundedPrice = Math.round(level.price / this.tickSize) * this.tickSize;
                const priceKey = this.formatPrice(roundedPrice);
                levelMap.set(priceKey, level);
            }
        }

        // Step 3: Overlay data on rows that have levels
        for (let rowIndex = 0; rowIndex <= this.visibleRows; rowIndex++) {
            const rowOffset = rowIndex - midRow;
            const price = referencePrice - (rowOffset * this.tickSize);
            const roundedPrice = Math.round(price / this.tickSize) * this.tickSize;
            const priceKey = this.formatPrice(roundedPrice);

            const level = levelMap.get(priceKey);
            if (level) {
                // Get individual orders if available (MBO mode)
                const orderMap = level.side === Side.BID ? snapshot.bidOrders : snapshot.askOrders;
                const orders = orderMap?.get(level.price);

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

        // Fixed column width (matches TypeScript backend)
        const COL_WIDTH = 66.7;

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
        const BAR_MAX_WIDTH = COL_WIDTH - 5;
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
                    this.drawIndividualOrders(orders, Side.BID, maxQty, y, level.quantity);
                } else if (barWidth > 0) {
                    // PriceLevel mode: Draw single aggregated bar
                    this.offscreenCtx.fillStyle = this.colors.bidBar;
                    this.offscreenCtx.fillRect(COL_WIDTH * barCol, y, barWidth, this.rowHeight);
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
                    this.drawIndividualOrders(orders, Side.ASK, maxQty, y, level.quantity);
                } else if (barWidth > 0) {
                    // PriceLevel mode: Draw single aggregated bar
                    this.offscreenCtx.fillStyle = this.colors.askBar;
                    this.offscreenCtx.fillRect(COL_WIDTH * barCol, y, barWidth, this.rowHeight);
                }
            }
        }

        // Own order indicator
        if (level.hasOwnOrders) {
            this.offscreenCtx.strokeStyle = this.colors.ownOrderBorder;
            this.offscreenCtx.lineWidth = 2;
            this.offscreenCtx.strokeRect(0, y + 1, this.width, this.rowHeight - 2);
        }
    }

    /**
     * Render only the price label for a row (used in Show Empty mode)
     */
    private renderPriceOnly(rowIndex: number, price: number): void {
        const y = rowIndex * this.rowHeight;
        const priceText = this.formatPrice(price);
        const COL_WIDTH = 66.7;

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
        const COL_WIDTH = 66.7;

        // Calculate column indices
        const bidOrderCol = this.showOrderCount ? 0 : -1;
        const bidQtyCol = this.showOrderCount ? 1 : 0;
        const askQtyCol = this.showOrderCount ? 3 : 2;
        const askOrderCol = this.showOrderCount ? 4 : -1;

        let columnCount = 3;
        if (this.showOrderCount) columnCount += 2;
        const barCol = columnCount;

        const maxQty = this.showVolumeBars ? this.calculateMaxQuantity() : 0;
        const BAR_MAX_WIDTH = COL_WIDTH - 5;
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
                    this.drawIndividualOrders(orders, Side.BID, maxQty, y, level.quantity);
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
                    this.drawIndividualOrders(orders, Side.ASK, maxQty, y, level.quantity);
                } else if (barWidth > 0) {
                    // PriceLevel mode: Draw single aggregated bar
                    this.offscreenCtx.fillStyle = this.colors.askBar;
                    this.offscreenCtx.fillRect(COL_WIDTH * barCol, y + 4, barWidth, this.rowHeight - 8);
                }
            }
        }

        // Own order indicator
        if (level.hasOwnOrders) {
            this.offscreenCtx.strokeStyle = this.colors.ownOrderBorder;
            this.offscreenCtx.lineWidth = 2;
            this.offscreenCtx.strokeRect(0, y + 1, this.width, this.rowHeight - 2);
        }
    }

    /**
     * Draw individual order bars (MBO mode)
     * Draws bars as horizontally adjacent segments within the row
     */
    private drawIndividualOrders(
        orders: Order[],
        side: Side,
        maxQty: number,
        y: number,
        levelTotalQuantity: number
    ): void {
        if (!orders || orders.length === 0 || !this.showVolumeBars) return;

        const COL_WIDTH = 66.7;
        let columnCount = 3;
        if (this.showOrderCount) columnCount += 2;
        const barCol = columnCount;
        const barStartX = COL_WIDTH * barCol;
        const BAR_MAX_WIDTH = COL_WIDTH - 5;

        // Same height as single bar (with padding)
        const barHeight = this.rowHeight - 8;
        const fillColor = side === Side.BID ? this.colors.bidBar : this.colors.askBar;
        const segmentGap = 1; // 1px gap between order segments for visual separation

        // Calculate the total bar width for this level (proportional to maxQty)
        const levelBarWidth = maxQty > 0 ? (levelTotalQuantity / maxQty) * BAR_MAX_WIDTH : 0;

        let xOffset = barStartX;  // Start at left edge of volume bar column
        for (let i = 0; i < orders.length; i++) {
            const order = orders[i];

            // Calculate segment width as proportion of levelBarWidth (not BAR_MAX_WIDTH)
            // This ensures all orders at this level fit within the level's total bar
            const segmentProportion = levelTotalQuantity > 0 ? order.quantity / levelTotalQuantity : 0;
            const barWidth = segmentProportion * levelBarWidth;

            // Subtract gap from bar width to create visual separation
            const segmentWidth = Math.max(0, barWidth - segmentGap);

            // Draw individual order bar as a segment with gap
            if (segmentWidth > 0) {
                this.offscreenCtx.fillStyle = fillColor;
                this.offscreenCtx.fillRect(xOffset, y + 4, segmentWidth, barHeight);
            }

            // Move to the right for next bar segment (full barWidth includes gap)
            xOffset += barWidth;

            // Stop if we've exceeded the level's total bar width
            if (xOffset >= barStartX + levelBarWidth)
                break;
        }
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

    /**
     * Get performance metrics
     */
    public getMetrics(): RenderMetrics {
        return {
            fps: this.fps,
            frameTime: 1000 / this.fps,
            dirtyRowCount: this.maxDirtyRow - this.minDirtyRow + 1,
            totalRows: this.visibleRows
        };
    }

    /**
     * Convert screen X coordinate to column index
     */
    public screenXToColumn(x: number): number {
        const COL_WIDTH = 66.7; // Fixed column width
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
        if (!this.currentSnapshot) return null;

        const midRow = Math.floor(this.visibleRows / 2);

        if (rowIndex < midRow) {
            // Ask side
            const askIndex = midRow - rowIndex - 1;
            if (askIndex >= 0 && askIndex < this.currentSnapshot.asks.length) {
                return this.currentSnapshot.asks[askIndex].price;
            }
        } else {
            // Bid side
            const bidIndex = this.currentSnapshot.bids.length - (rowIndex - midRow) - 1;
            if (bidIndex >= 0 && bidIndex < this.currentSnapshot.bids.length) {
                return this.currentSnapshot.bids[bidIndex].price;
            }
        }

        return null;
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
        const COL_WIDTH = 66.7; // Fixed column width (~400px / 6 columns)
        let columnCount = 6;

        // Remove order count columns when disabled
        if (!this.showOrderCount) {
            columnCount -= 2; // Remove bid_orders and ask_orders
        }

        // Remove bars column when disabled
        if (!this.showVolumeBars) {
            columnCount -= 1; // Remove bars
        }

        return Math.round(COL_WIDTH * columnCount);
    }
}
