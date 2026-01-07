import { BookLevel, Side, OrderBookSnapshot, CanvasColors, DEFAULT_COLORS, RenderMetrics } from './types';

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
    // private centerPrice: number = 0; // Reserved for viewport scrolling

    constructor(
        canvas: HTMLCanvasElement,
        width: number,
        height: number,
        rowHeight: number = 24,
        colors: CanvasColors = DEFAULT_COLORS,
        showVolumeBars: boolean = true,
        showOrderCount: boolean = true
    ) {
        this.canvas = canvas;
        this.width = width;
        this.height = height;
        this.rowHeight = rowHeight;
        this.colors = colors;
        this.showVolumeBars = showVolumeBars;
        this.showOrderCount = showOrderCount;
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

        const midRow = Math.floor(this.visibleRows / 2);

        // Render ask levels (top half, lowest to highest)
        const visibleAsks = Math.min(midRow, snapshot.asks.length);
        for (let i = 0; i < visibleAsks; i++) {
            const rowIndex = midRow - visibleAsks + i;
            const level = snapshot.asks[visibleAsks - i - 1]; // Reverse order
            this.renderRow(rowIndex, level);
        }

        // Render bid levels (bottom half, highest to lowest)
        const visibleBids = Math.min(this.visibleRows - midRow, snapshot.bids.length);
        for (let i = 0; i < visibleBids; i++) {
            const rowIndex = midRow + i;
            const level = snapshot.bids[snapshot.bids.length - i - 1]; // Reverse order
            this.renderRow(rowIndex, level);
        }

        this.copyToMainCanvas();
    }

    private renderRow(rowIndex: number, level: BookLevel): void {
        const y = rowIndex * this.rowHeight;

        // Prepare text values
        const priceText = level.price.toFixed(2);
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
            if (this.showVolumeBars && barWidth > 0) {
                this.offscreenCtx.fillStyle = this.colors.bidBar;
                this.offscreenCtx.fillRect(COL_WIDTH * barCol, y, barWidth, this.rowHeight);
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
            if (this.showVolumeBars && barWidth > 0) {
                this.offscreenCtx.fillStyle = this.colors.askBar;
                this.offscreenCtx.fillRect(COL_WIDTH * barCol, y, barWidth, this.rowHeight);
            }
        }

        // Own order indicator
        if (level.hasOwnOrders) {
            this.offscreenCtx.strokeStyle = this.colors.ownOrderBorder;
            this.offscreenCtx.lineWidth = 2;
            this.offscreenCtx.strokeRect(0, y + 1, this.width, this.rowHeight - 2);
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
