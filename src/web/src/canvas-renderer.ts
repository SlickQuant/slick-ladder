import { BookLevel, Side, OrderBookSnapshot, CanvasColors, DEFAULT_COLORS, RenderMetrics, Order, COL_WIDTH, VOLUME_BAR_WIDTH_MULTIPLIER, SegmentRenderState, DEFAULT_SEGMENT_CONFIG, DEFAULT_MBO_ORDER_SIZE_FILTER, DEFAULT_MIN_QUANTITY_THRESHOLD, SEGMENT_SCALE_MIN, SEGMENT_SCALE_MAX, SEGMENT_SCALE_STEP, MIN_SEGMENT_WIDTH_PX, SEGMENT_GAP_PX, MIN_BAR_COLUMN_WIDTH, TARGET_MAX_SEGMENT_WIDTH } from './types';

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

// Tolerance (in ticks) for binary float error when snapping a price to the tick grid
const TICK_EPSILON = 1e-6;

const MAX_ROW_MOVE = 4; // How far findMovedRows looks for a row's previous position

/**
 * One side's level on a row (mirrors RowLevel in SkiaRenderer.cs)
 */
type RowLevel = {
    present: boolean;
    side: Side;
    price: number;
    quantity: number;
    numOrders: number;
    hasOwnOrders: boolean;
    orders: Order[] | undefined;
    bar: number;                  // Aggregated bar width: a volume scale change redraws the bar column
};

/**
 * What one row shows (mirrors RowState in SkiaRenderer.cs). Under the same layout, rows that are
 * the same have identical pixels, so only rows that differ from the new frame are redrawn
 */
type RowState = {
    drawn: boolean;               // False: pixels unknown (not drawn yet, or shifted in)
    label: number;                // Show Empty Rows price label, NaN in Remove Rows
    ask: RowLevel;
    bid: RowLevel;
    overlaps: RowLevel[] | null;  // More levels on this row, always redrawn
    workQuantity: number;         // Our working orders on this row's price, 0 for none
    workCount: number;
    workSides: number;            // WORK_BUY | WORK_SELL
    ltqQuantity: number;          // Last trade on this row's price, 0 for none
    ltqAggressor: number;         // Side, -1 when unknown
};

const WORK_BUY = 1;
const WORK_SELL = 2;

const createLevel = (side: Side): RowLevel => ({
    present: false, side, price: 0, quantity: 0, numOrders: 0, hasOwnOrders: false, orders: undefined, bar: 0
});

const createRow = (): RowState => ({
    drawn: false, label: NaN, ask: createLevel(Side.ASK), bid: createLevel(Side.BID), overlaps: null,
    workQuantity: 0, workCount: 0, workSides: 0, ltqQuantity: 0, ltqAggressor: -1
});

// Overlaps are never changed once built, so they can be shared
const copyRow = (row: RowState): RowState => ({ ...row, ask: { ...row.ask }, bid: { ...row.bid } });

function resetRow(row: RowState, label: number): void {
    row.drawn = true;
    row.label = label;
    row.ask.present = false;
    row.ask.orders = undefined;
    row.bid.present = false;
    row.bid.orders = undefined;
    row.overlaps = null;
    row.workQuantity = 0;
    row.workCount = 0;
    row.workSides = 0;
    row.ltqQuantity = 0;
    row.ltqAggressor = -1;
}

// Rows that draw the same pixels
function sameRow(a: RowState, b: RowState, compareBars: boolean): boolean {
    return a.drawn && b.drawn && a.overlaps === null && b.overlaps === null && Object.is(a.label, b.label) &&
        a.workQuantity === b.workQuantity && a.workCount === b.workCount && a.workSides === b.workSides &&
        a.ltqQuantity === b.ltqQuantity && a.ltqAggressor === b.ltqAggressor &&
        sameLevel(a.ask, b.ask, compareBars) && sameLevel(a.bid, b.bid, compareBars);
}

// Orders compare by value: MBOManager keeps a level's array until it changes, but WASM snapshots
// may copy them
function sameLevel(a: RowLevel, b: RowLevel, compareBars: boolean): boolean {
    if (a.present !== b.present) {
        return false;
    }
    return !a.present || (a.price === b.price && a.quantity === b.quantity && a.numOrders === b.numOrders &&
        a.hasOwnOrders === b.hasOwnOrders && sameOrders(a.orders, b.orders) && (!compareBars || a.bar === b.bar));
}

function sameOrders(a: Order[] | undefined, b: Order[] | undefined): boolean {
    if (a === b) {
        return true;
    }
    if (!a || !b || a.length !== b.length) {
        return false;
    }
    for (let i = 0; i < a.length; i++) {
        const x = a[i];
        const y = b[i];
        if (x !== y && (x.orderId !== y.orderId || x.quantity !== y.quantity || x.isOwnOrder !== y.isOwnOrder)) {
            return false;
        }
    }
    return true;
}

// toLocaleString(locale, options) builds an Intl.NumberFormat on every call (~20us), most of
// the frame time with many ladders. These give the same output and are built once
const INTEGER_QUANTITY_FORMAT = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
const decimalQuantityFormats: Intl.NumberFormat[] = []; // Indexed by max decimal places

function decimalQuantityFormat(maxDecimalPlaces: number): Intl.NumberFormat {
    return decimalQuantityFormats[maxDecimalPlaces] ??= new Intl.NumberFormat('en-US', {
        minimumFractionDigits: 0,
        maximumFractionDigits: maxDecimalPlaces,
        useGrouping: false  // No thousands separator for decimals to simplify trimming
    });
}

// Even a cached formatter's format() takes ~1us. Grouping the digits by hand gives the same text
// ~50x faster
function formatInteger(value: number): string {
    if (!(value >= 0 && value < 1e21)) {
        return INTEGER_QUANTITY_FORMAT.format(value); // Negative, or String() would use an exponent
    }
    const digits = String(value);
    let end = digits.length % 3 || 3;
    let text = digits.slice(0, end);
    for (; end < digits.length; end += 3) {
        text += ',' + digits.slice(end, end + 3);
    }
    return text;
}

// Decimal places a tick size or threshold needs (0.01 -> 2, 0.5 -> 1, 5 -> 0)
function decimalPlaces(value: number): number {
    let places = 0;
    let scaled = value;

    // Keep multiplying by 10 until we get a value >= 1
    while (scaled < 1 && places < 10) {
        scaled *= 10;
        places++;
    }

    // Then add places until value * 10^places is a whole number
    while (places < 10) {
        const multiplier = Math.pow(10, places);
        if (Math.abs((Math.round(value * multiplier) / multiplier) - value) < 1e-10) {
            break;
        }
        places++;
    }
    return places;
}

/**
 * What a render needs from all of one side's levels, found in one pass (mirrors
 * SkiaRenderer.LevelStats). Everything after it reads only the levels it draws
 */
type LevelStats = {
    maxQuantity: number;
    maxOrders: number; // Most orders on a level with quantity
    nonEmpty: number; // Levels with quantity
    nonEmptyAscending: boolean; // Those strictly ascending by price
    ascending: boolean; // All levels ascending by price, so the visible ones can be found by binary search
};

function createStats(): LevelStats {
    return { maxQuantity: 0, maxOrders: 0, nonEmpty: 0, nonEmptyAscending: true, ascending: true };
}

function scanLevels(levels: BookLevel[], stats: LevelStats): LevelStats {
    let maxQuantity = 0;
    let maxOrders = 0;
    let nonEmpty = 0;
    let nonEmptyAscending = true;
    let ascending = true;
    let lastPrice = -Infinity;
    let lastNonEmptyPrice = 0;
    for (let i = 0; i < levels.length; i++) {
        const level = levels[i];
        const price = level.price;
        const quantity = level.quantity;
        if (price < lastPrice) ascending = false;
        lastPrice = price;
        if (quantity > maxQuantity) maxQuantity = quantity;
        if (quantity > 0) {
            if (nonEmpty > 0 && price <= lastNonEmptyPrice) nonEmptyAscending = false;
            if (level.numOrders > maxOrders) maxOrders = level.numOrders;
            lastNonEmptyPrice = price;
            nonEmpty++;
        }
    }
    stats.maxQuantity = maxQuantity;
    stats.maxOrders = maxOrders;
    stats.nonEmpty = nonEmpty;
    stats.nonEmptyAscending = nonEmptyAscending;
    stats.ascending = ascending;
    return stats;
}

// First index whose price is at or above price, in levels ascending by price
function firstAtOrAbove(levels: BookLevel[], price: number): number {
    let low = 0;
    let high = levels.length;
    while (low < high) {
        const mid = (low + high) >>> 1;
        if (levels[mid].price < price) {
            low = mid + 1;
        } else {
            high = mid;
        }
    }
    return low;
}

/**
 * Levels with quantity, ascending by price, one per price (the last one wins). Snapshots arrive
 * sorted and unique, so the dedupe + sort only runs for input that isn't (mirrors
 * SkiaRenderer.NonEmptyLevels)
 */
function nonEmptyLevels(levels: BookLevel[], stats: LevelStats): BookLevel[] {
    if (!stats.nonEmptyAscending) {
        const byPrice = new Map<number, BookLevel>();
        for (const level of levels) {
            if (level.quantity > 0) byPrice.set(level.price, level);
        }
        return Array.from(byPrice.values()).sort((a, b) => a.price - b.price);
    }
    // Read-only below, so the snapshot's array is reused
    return stats.nonEmpty === levels.length ? levels : levels.filter((level) => level.quantity > 0);
}

// Largest order quantity above minQty, 0 if none
function maxOrderQuantity(orders: Order[], minQty: number): number {
    let max = 0;
    for (let i = 0; i < orders.length; i++) {
        const quantity = orders[i].quantity;
        if (quantity > max && quantity > minQty) {
            max = quantity;
        }
    }
    return max;
}

// Order arrays at least this long keep their largest order in MaxOrderScan. Shorter ones scan
// faster than a lookup
const CACHED_ORDERS_MIN = 8;

/**
 * Largest order above the size filter across a snapshot's order maps. A level's order array is
 * replaced when its orders change (MBOManager, WASM snapshots), so a long queue's largest order is
 * kept by array and the queue isn't scanned again while it's quiet
 */
class MaxOrderScan {
    private maxByOrders = new WeakMap<Order[], number>();
    private minQty = 0;

    max(orderMap: Map<number, Order[]> | null | undefined, minQty: number): number {
        if (minQty !== this.minQty) {
            this.maxByOrders = new WeakMap();
            this.minQty = minQty;
        }
        let max = 0;
        if (orderMap) {
            for (const orders of orderMap.values()) {
                let levelMax: number | undefined;
                if (orders.length < CACHED_ORDERS_MIN) {
                    levelMax = maxOrderQuantity(orders, minQty);
                } else {
                    levelMax = this.maxByOrders.get(orders);
                    if (levelMax === undefined) {
                        levelMax = maxOrderQuantity(orders, minQty);
                        this.maxByOrders.set(orders, levelMax);
                    }
                }
                if (levelMax > max) {
                    max = levelMax;
                }
            }
        }
        return max;
    }
}

type ColumnPositions = {
    readonly workX: number;       // -1 when hidden
    readonly bidQtyX: number;
    readonly priceX: number;
    readonly askQtyX: number;
    readonly ltqX: number;        // -1 when hidden
    readonly barX: number;        // -1 when hidden
    // What they were computed from
    readonly showVolumeBars: boolean;
    readonly qtyColWidth: number;
    readonly priceColWidth: number;
    readonly workColWidth: number;
    readonly ltqColWidth: number;
};

/**
 * Optional columns and the header, fixed for a renderer's life (the ladder makes a new renderer
 * when one is toggled)
 */
export type LayoutOptions = {
    showWorkColumn?: boolean; // Default true
    showLtqColumn?: boolean;  // Default true
    showHeader?: boolean;     // Default true
};

/**
 * Canvas 2D renderer. It keeps what each row shows and redraws only rows that changed, the same
 * algorithm as the desktop SkiaRenderer
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

    // What the canvas shows: a state per row (rows) and the layout they were drawn under (last*).
    // nextRows is the frame being built
    private rows: RowState[] = [];
    private nextRows: RowState[] = [];
    private moveSource: Int32Array = new Int32Array(0); // See findMovedRows
    private minDirtyRow: number = Infinity;
    private maxDirtyRow: number = -1;
    private needsFullRedraw: boolean = true;
    private lastRemovalMode: 'showEmpty' | 'removeRow' = 'removeRow';
    private lastShowOrderCount: boolean = true;
    private lastShowVolumeBars: boolean = true;
    private lastTickSize: number = 0.01;
    private lastDisplayTickSize: number = 0.01;
    private lastDensePackingScrollOffset: number = 0;
    private lastReferencePrice: number = 0;
    private lastWidth: number = 0;
    private lastHeight: number = 0;
    private lastQtyColWidth: number = 0;
    private lastPriceColWidth: number = 0;
    private lastWorkColWidth: number = 0;
    private lastLtqColWidth: number = 0;
    private lastBarColumnWidth: number = 0;
    private lastMboOrderSizeFilter: number = 0;
    private lastSegmentPixelsPerUnit: number = 0;
    private lastSegmentScrollOffset: number = 0;
    private lastCenterPrice: number = 0;
    private lastAutoCenter: boolean = true;

    // Text caches. Price labels repeat every frame: priceTexts holds them for the current display tick
    private priceDecimals: number = 2;
    private priceTexts: Map<number, string> = new Map();
    private quantityDecimals: number;                           // For minQuantityThreshold
    private textWidths: Map<string, number> = new Map();        // measureText in the 14px font
    private columns: ColumnPositions | null = null;             // See getColumnPositions

    // Layout configuration
    private showVolumeBars: boolean = true;
    private showOrderCount: boolean = true; // In the qty column: "(5) 102" on bids, "102 (5)" on asks
    private readonly showWorkColumn: boolean;
    private readonly showLtqColumn: boolean;
    private readonly headerHeight: number; // 0 without a header. Rows start below it

    private mboOrderSizeFilter: number;
    private minQuantityThreshold: number;

    // Dynamic column widths
    private qtyColWidth: number = COL_WIDTH; // Dynamic quantity column width
    private priceColWidth: number = COL_WIDTH; // Dynamic price column width
    private workColWidth: number = COL_WIDTH;
    private ltqColWidth: number = COL_WIDTH;

    // Largest level quantity, the volume bar scale (0 when bars are hidden)
    private maxQuantity: number = 0;
    private askStats: LevelStats = createStats(); // The snapshot being rendered, see scanLevels
    private bidStats: LevelStats = createStats();

    // Performance tracking
    private lastFrameTime: number = 0;
    private frameCount: number = 0;
    private fps: number = 60;

    // Current snapshot
    private currentSnapshot: OrderBookSnapshot | null = null;

    // Scroll state
    private scrollOffset: number = 0;              // Row-based scroll for dense packing
    private centerPrice: number = 0;               // Price-based scroll for show empty mode
    private autoCenter: boolean = true;            // Keep bid/ask midpoint on the middle row
    private removalMode: 'showEmpty' | 'removeRow' = 'removeRow';

    // Price configuration
    private tickSize: number;
    private displayTickSize: number;

    // Segment rendering state
    private barColumnWidth: number;
    private segmentState: SegmentRenderState;
    private orderScan = new MaxOrderScan();

    constructor(
        canvas: HTMLCanvasElement,
        width: number,
        height: number,
        rowHeight: number = 24,
        colors: CanvasColors = DEFAULT_COLORS,
        showVolumeBars: boolean = true,
        showOrderCount: boolean = true,
        tickSize: number = 0.01,
        mboOrderSizeFilter: number = DEFAULT_MBO_ORDER_SIZE_FILTER,
        minQuantityThreshold: number = DEFAULT_MIN_QUANTITY_THRESHOLD,
        displayTickSize: number = tickSize,
        layout: LayoutOptions = {}
    ) {
        this.canvas = canvas;
        this.width = width;
        this.height = height;
        this.rowHeight = rowHeight;
        this.colors = colors;
        this.showVolumeBars = showVolumeBars;
        this.showOrderCount = showOrderCount;
        this.showWorkColumn = layout.showWorkColumn ?? true;
        this.showLtqColumn = layout.showLtqColumn ?? true;
        this.headerHeight = (layout.showHeader ?? true) ? rowHeight : 0;
        this.tickSize = tickSize;
        this.displayTickSize = Math.max(tickSize, displayTickSize);
        this.updatePriceFormat();
        this.mboOrderSizeFilter = Math.max(0, mboOrderSizeFilter);
        this.minQuantityThreshold = minQuantityThreshold;
        this.quantityDecimals = decimalPlaces(minQuantityThreshold);
        this.visibleRows = this.fullRows();

        // Calculate initial bar column width
        this.barColumnWidth = this.calculateBarColumnWidth();

        // Initialize segment state
        this.segmentState = { ...DEFAULT_SEGMENT_CONFIG };

        // Load persisted user scale factor from localStorage
        const savedScale = localStorage.getItem('segmentScaleFactor');
        if (savedScale) {
            const scale = parseFloat(savedScale);
            if (!isNaN(scale) && scale >= SEGMENT_SCALE_MIN && scale <= SEGMENT_SCALE_MAX) {
                this.segmentState.userScaleFactor = scale;
            }
        }

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
     * Format price based on display tick size
     */
    private formatPrice(price: number): string {
        let text = this.priceTexts.get(price);
        if (text === undefined) {
            if (this.priceTexts.size >= 4096) {
                this.priceTexts.clear(); // The mid wandered far: start over
            }
            text = price.toFixed(this.priceDecimals);
            this.priceTexts.set(price, text);
        }
        return text;
    }

    // Call after displayTickSize changes
    private updatePriceFormat(): void {
        this.priceDecimals = decimalPlaces(this.displayTickSize);
        this.priceTexts.clear();
    }

    /**
     * Format quantity based on minimum quantity threshold
     */
    private formatQuantity(quantity: number): string {
        // If quantity is below threshold, show "<threshold" instead
        if (quantity > 0 && quantity < this.minQuantityThreshold) {
            if (this.quantityDecimals === 0) {
                return `<${this.minQuantityThreshold}`;
            }
            return `<${this.minQuantityThreshold.toFixed(this.quantityDecimals)}`;
        }

        // If integer, use thousands separator (e.g., "1,234")
        if (quantity % 1 === 0) {
            return formatInteger(quantity);
        }

        // Decimal: show decimal places based on minQuantityThreshold precision, trim trailing zeros
        const formatted = decimalQuantityFormat(this.quantityDecimals).format(quantity);

        // If the result still has a decimal point, remove trailing zeros
        if (formatted.includes('.')) {
            return formatted.replace(/\.?0+$/, '');
        }
        return formatted;
    }

    /**
     * Qty cell text. With order count on, the count sits on the outer side: "(5) 102" on bids,
     * "102 (5)" on asks (mirrors SkiaRenderer.QuantityText)
     */
    private quantityText(quantity: number, numOrders: number, side: Side): string {
        const text = this.formatQuantity(quantity);
        if (!this.showOrderCount) {
            return text;
        }
        return side === Side.BID ? `(${numOrders}) ${text}` : `${text} (${numOrders})`;
    }

    // Work cell text: "10 (2)" (mirrors SkiaRenderer.WorkText)
    private workText(quantity: number, count: number): string {
        return `${this.formatQuantity(quantity)} (${count})`;
    }

    /**
     * Width of a column that fits text: at least COL_WIDTH, with 10px padding on each side.
     * Measuring is cached, since the widest text rarely changes
     */
    private columnWidthFor(text: string): number {
        let textWidth = this.textWidths.get(text);
        if (textWidth === undefined) {
            if (this.textWidths.size >= 256) {
                this.textWidths.clear();
            }
            this.offscreenCtx.font = '14px monospace';
            textWidth = this.offscreenCtx.measureText(text).width;
            this.textWidths.set(text, textWidth);
        }
        return Math.max(COL_WIDTH, Math.ceil(textWidth + 20));
    }

    /**
     * Quantity column width that fits the largest quantity and order count in the snapshot. The
     * font is monospace and both sides' text has the same length, so the ask text is measured
     */
    private calculateQuantityColumnWidth(maxQty: number, maxOrders: number): number {
        // If no levels, use default width
        return maxQty === 0 ? COL_WIDTH : this.columnWidthFor(this.quantityText(maxQty, maxOrders, Side.ASK));
    }

    /**
     * Price column width that fits the prices on this frame's rows (nextRows). Sizing it from the
     * whole book would let a deep book's far levels widen it for prices that are never drawn. The
     * widest text is the highest or the lowest price (which may carry a minus sign), so only those
     * two are measured.
     * Mirrors SkiaRenderer.CalculatePriceColumnWidth
     */
    private calculatePriceColumnWidth(): number {
        let high = -Infinity;
        let low = Infinity;
        for (const row of this.nextRows) {
            // Show Empty Rows draws the row's label, Remove Rows its one level's price
            const price = !Number.isNaN(row.label) ? row.label
                : row.ask.present ? row.ask.price : row.bid.present ? row.bid.price : NaN;
            if (price > high) high = price;
            if (price < low) low = price;
        }

        // No prices on screen
        if (high < low) {
            return COL_WIDTH;
        }
        return Math.max(this.columnWidthFor(this.formatPrice(high)), this.columnWidthFor(this.formatPrice(low)));
    }

    /**
     * Work and LTQ column widths that fit the cells on this frame's rows (mirrors
     * SkiaRenderer.CalculateCellColumnWidths)
     */
    private calculateCellColumnWidths(): [number, number] {
        // Each cell's text is measured: a larger quantity can have shorter text ("1" vs "0.12345678").
        // Few rows have cells, and measuring is cached
        let workWidth = COL_WIDTH;
        let ltqWidth = COL_WIDTH;
        for (const row of this.nextRows) {
            if (row.workCount > 0) {
                workWidth = Math.max(workWidth, this.columnWidthFor(this.workText(row.workQuantity, row.workCount)));
            }
            if (row.ltqQuantity > 0) {
                ltqWidth = Math.max(ltqWidth, this.columnWidthFor(this.formatQuantity(row.ltqQuantity)));
            }
        }
        return [workWidth, ltqWidth];
    }

    /**
     * Get X positions for all columns. The same object until a column width or toggle changes
     */
    private getColumnPositions(): ColumnPositions {
        const cached = this.columns;
        if (cached !== null && cached.showVolumeBars === this.showVolumeBars && cached.qtyColWidth === this.qtyColWidth &&
            cached.priceColWidth === this.priceColWidth && cached.workColWidth === this.workColWidth &&
            cached.ltqColWidth === this.ltqColWidth) {
            return cached;
        }

        // Work | Bid qty | Price | Ask qty | LTQ | Volume bars, optional ones when enabled. Each x
        // is the sum of the widths left of it, so the column fills tile edge to edge
        const workX = this.showWorkColumn ? 0 : -1;
        const bidQtyX = this.showWorkColumn ? this.workColWidth : 0;
        const priceX = bidQtyX + this.qtyColWidth;
        const askQtyX = priceX + this.priceColWidth;
        const askQtyEnd = askQtyX + this.qtyColWidth;
        const ltqX = this.showLtqColumn ? askQtyEnd : -1;
        const columnsEnd = this.showLtqColumn ? ltqX + this.ltqColWidth : askQtyEnd;
        const barX = this.showVolumeBars ? columnsEnd : -1;

        return this.columns = {
            workX, bidQtyX, priceX, askQtyX, ltqX, barX,
            showVolumeBars: this.showVolumeBars, qtyColWidth: this.qtyColWidth, priceColWidth: this.priceColWidth,
            workColWidth: this.workColWidth, ltqColWidth: this.ltqColWidth
        };
    }

    /**
     * Calculate bar column width based on canvas width and other columns
     */
    private calculateBarColumnWidth(): number {
        const cols = this.getColumnPositions();
        if (!this.showVolumeBars) {
            return 0;
        }
        // Bar column starts where barX indicates, and extends to the end of canvas
        const barWidth = this.width - cols.barX;
        return Math.max(MIN_BAR_COLUMN_WIDTH, barWidth);
    }

    /**
     * Calculate base scale dynamically based on max order quantity
     */
    private calculateBaseScale(): void {
        if (!this.currentSnapshot) return;

        // Find max individual order quantity across all levels
        const minQty = this.mboOrderSizeFilter;
        const maxOrderQty = Math.max(this.orderScan.max(this.currentSnapshot.bidOrders, minQty),
            this.orderScan.max(this.currentSnapshot.askOrders, minQty));

        // Calculate base scale to make largest order ~200px wide
        if (maxOrderQty > 0) {
            const newBaseScale = TARGET_MAX_SEGMENT_WIDTH / maxOrderQty;

            // Only update if significantly different (avoid constant tiny adjustments)
            const changeRatio = Math.abs(newBaseScale - this.segmentState.basePixelsPerUnit)
                               / this.segmentState.basePixelsPerUnit;

            if (changeRatio > 0.2 || this.segmentState.basePixelsPerUnit === 1.0) {
                // More than 20% change, or initial calculation
                this.segmentState.basePixelsPerUnit = newBaseScale;
            }
        }
    }

    /**
     * Recalculate max horizontal scroll offset based on current scale
     */
    private recalculateMaxScroll(): void {
        if (!this.currentSnapshot) return;
        if (!this.currentSnapshot.bidOrders && !this.currentSnapshot.askOrders) {
            this.segmentState.maxScrollOffset = 0; // Price levels only: no segments to scroll
            return;
        }

        const pixelsPerUnit = this.segmentState.basePixelsPerUnit * this.segmentState.userScaleFactor;
        let maxWidth = 0;

        const minQty = this.mboOrderSizeFilter;

        // Check all price levels for widest segment set
        const allLevels = [
            ...this.currentSnapshot.bids,
            ...this.currentSnapshot.asks
        ];

        for (const level of allLevels) {
            const orders = level.side === Side.BID
                ? this.currentSnapshot.bidOrders?.get(level.price)
                : this.currentSnapshot.askOrders?.get(level.price);

            if (orders && orders.length > 0) {
                const totalWidth = orders.reduce((sum, order) =>
                    order.quantity > minQty
                        ? sum + (order.quantity * pixelsPerUnit) + SEGMENT_GAP_PX
                        : sum, 0);
                maxWidth = Math.max(maxWidth, totalWidth);
            }
        }

        this.segmentState.maxScrollOffset = Math.max(0, maxWidth - this.barColumnWidth);
    }

    /**
     * Update bar column width (called on resize)
     */
    public updateBarColumnWidth(width: number): void {
        this.barColumnWidth = width;

        // Reset horizontal scroll if segments now fit
        this.recalculateMaxScroll();
        if (this.segmentState.horizontalScrollOffset > this.segmentState.maxScrollOffset) {
            this.segmentState.horizontalScrollOffset = this.segmentState.maxScrollOffset;
        }
    }

    private drawFullBackground(): void {
        this.offscreenCtx.fillStyle = this.colors.background;
        this.offscreenCtx.fillRect(0, 0, this.width, this.height);
        this.fillColumns(0, this.height);

        // Each row's top grid line, as drawRowBackground draws it
        this.offscreenCtx.fillStyle = this.colors.gridLine;
        for (let y = this.headerHeight; y < this.height; y += this.rowHeight) {
            this.offscreenCtx.fillRect(0, y, this.width, 1);
        }
        this.drawHeader();
    }

    /**
     * Column backgrounds from y down height pixels (mirrors SkiaRenderer.DrawColumns)
     */
    private fillColumns(y: number, height: number): void {
        const ctx = this.offscreenCtx;
        const cols = this.getColumnPositions();
        if (cols.workX >= 0) {
            ctx.fillStyle = this.colors.workBackground;
            ctx.fillRect(cols.workX, y, this.workColWidth, height);
        }
        ctx.fillStyle = this.colors.bidQtyBackground;
        ctx.fillRect(cols.bidQtyX, y, this.qtyColWidth, height);
        ctx.fillStyle = this.colors.priceBackground;
        ctx.fillRect(cols.priceX, y, this.priceColWidth, height);
        ctx.fillStyle = this.colors.askQtyBackground;
        ctx.fillRect(cols.askQtyX, y, this.qtyColWidth, height);
        if (cols.ltqX >= 0) {
            ctx.fillStyle = this.colors.ltqBackground;
            ctx.fillRect(cols.ltqX, y, this.ltqColWidth, height);
        }
    }

    /**
     * Column titles over the rows (mirrors SkiaRenderer.DrawHeader). Only full redraws draw it:
     * anything that moves a column redraws everything
     */
    private drawHeader(): void {
        if (this.headerHeight === 0) {
            return;
        }
        const ctx = this.offscreenCtx;
        const cols = this.getColumnPositions();
        ctx.fillStyle = this.colors.headerBackground;
        ctx.fillRect(0, 0, this.width, this.headerHeight);

        // A separator on both sides and each column edge, pixel aligned so it stays one sharp pixel
        ctx.fillStyle = this.colors.gridLine;
        const right = this.width - 1;
        ctx.fillRect(0, 0, 1, this.headerHeight);
        ctx.fillRect(right, 0, 1, this.headerHeight);
        for (const x of [cols.bidQtyX, cols.priceX, cols.askQtyX, cols.ltqX, cols.barX]) {
            if (x > 0 && x < right) {
                ctx.fillRect(Math.floor(x), 0, 1, this.headerHeight);
            }
        }

        ctx.fillStyle = this.colors.text;
        ctx.textAlign = 'center';
        const textY = this.headerHeight / 2;
        if (cols.workX >= 0) {
            ctx.fillText('Work', cols.workX + this.workColWidth / 2, textY);
        }
        ctx.fillText('Bid', cols.bidQtyX + this.qtyColWidth / 2, textY);
        ctx.fillText('Price', cols.priceX + this.priceColWidth / 2, textY);
        ctx.fillText('Ask', cols.askQtyX + this.qtyColWidth / 2, textY);
        if (cols.ltqX >= 0) {
            ctx.fillText('LTQ', cols.ltqX + this.ltqColWidth / 2, textY);
        }
        if (cols.barX >= 0 && cols.barX < this.width) {
            ctx.fillText('Volume', cols.barX + (this.width - cols.barX) / 2, textY); // Centered on the part on screen
        }
    }

    private renderBackground(): void {
        this.drawFullBackground();
        this.copyToMainCanvas();
        this.needsFullRedraw = true;
    }

    /**
     * False when render would draw nothing new for an unchanged book: the view is where the last
     * render left it (mirrors SkiaRenderer.NeedsRender). The host can then skip building a
     * snapshot for an idle frame
     */
    public needsRender(): boolean {
        return this.needsFullRedraw || this.layoutChanged() || this.centerPrice !== this.lastCenterPrice ||
            this.autoCenter !== this.lastAutoCenter;
    }

    /**
     * Render a price ladder snapshot, redrawing only the rows that changed
     */
    public render(snapshot: OrderBookSnapshot): void {
        this.currentSnapshot = snapshot;
        scanLevels(snapshot.asks, this.askStats);
        scanLevels(snapshot.bids, this.bidStats);

        // Largest level sizes the quantity column and scales every volume bar
        const maxQuantity = Math.max(this.bidStats.maxQuantity, this.askStats.maxQuantity);
        this.maxQuantity = this.showVolumeBars ? maxQuantity : 0;

        // Recalculate base scale if order quantities changed
        this.calculateBaseScale();

        this.clearDirtyState();

        if (this.autoCenter) {
            this.applyCenter(snapshot);
        }

        const referencePrice = this.removalMode === 'showEmpty'
            ? this.resolveReferencePrice(snapshot)
            : 0;

        // Rows don't depend on column widths, so build them first and size the price column from them
        this.buildRows(snapshot, referencePrice);
        const newQtyColWidth = this.calculateQuantityColumnWidth(maxQuantity,
            Math.max(this.bidStats.maxOrders, this.askStats.maxOrders));
        const newPriceColWidth = this.calculatePriceColumnWidth();
        const [newWorkColWidth, newLtqColWidth] = this.calculateCellColumnWidths();
        if (newQtyColWidth !== this.qtyColWidth || newPriceColWidth !== this.priceColWidth ||
            newWorkColWidth !== this.workColWidth || newLtqColWidth !== this.ltqColWidth) {
            this.qtyColWidth = newQtyColWidth;
            this.priceColWidth = newPriceColWidth;
            this.workColWidth = newWorkColWidth;
            this.ltqColWidth = newLtqColWidth;
            // Resize canvas to accommodate new column widths
            const newCanvasWidth = this.calculateCanvasWidth();
            if (newCanvasWidth !== this.width) {
                this.resizeCanvas(newCanvasWidth, this.height);
            }
        }

        // Compare the rows this frame should show with what the canvas holds and redraw only the
        // ones that differ. A layout change (or a move that isn't whole rows) redraws everything
        if (this.needsFullRedraw || this.layoutChanged() || this.rows.length !== this.nextRows.length ||
            !this.tryShiftRows(referencePrice)) {
            this.renderFull();
        } else {
            this.renderChangedRows();
        }
        const rows = this.rows;
        this.rows = this.nextRows;
        this.nextRows = rows;

        this.updateLastState(referencePrice);
    }

    private renderFull(): void {
        this.drawFullBackground();
        for (let rowIndex = 0; rowIndex < this.nextRows.length; rowIndex++) {
            this.drawRowContent(this.nextRows[rowIndex], rowIndex, true);
        }
        this.copyToMainCanvas();
        this.markAllDirty();
    }

    private resolveReferencePrice(snapshot: OrderBookSnapshot): number {
        let referencePrice = this.centerPrice;
        const isEmpty = snapshot.bids.length === 0 && snapshot.asks.length === 0;

        if (isEmpty) {
            this.centerPrice = 0;
            referencePrice = 0;
        } else if (this.centerPrice === 0) {
            const midPrice = snapshot.midPrice ?? 100.0;
            this.centerPrice = this.snapToDisplayTick(midPrice);
            referencePrice = this.centerPrice;
        } else {
            referencePrice = this.centerPrice;
        }

        return referencePrice;
    }

    /**
     * Something every row's pixels depend on changed (mirrors LayoutState in SkiaRenderer.cs)
     */
    private layoutChanged(): boolean {
        return this.lastRemovalMode !== this.removalMode ||
            this.lastShowOrderCount !== this.showOrderCount ||
            this.lastShowVolumeBars !== this.showVolumeBars ||
            this.lastTickSize !== this.tickSize ||
            this.lastDisplayTickSize !== this.displayTickSize ||
            this.lastWidth !== this.width ||
            this.lastHeight !== this.height ||
            this.lastQtyColWidth !== this.qtyColWidth ||
            this.lastPriceColWidth !== this.priceColWidth ||
            this.lastWorkColWidth !== this.workColWidth ||
            this.lastLtqColWidth !== this.ltqColWidth ||
            this.lastBarColumnWidth !== this.barColumnWidth ||
            this.lastMboOrderSizeFilter !== this.mboOrderSizeFilter ||
            this.lastSegmentPixelsPerUnit !== this.segmentPixelsPerUnit() ||
            this.lastSegmentScrollOffset !== this.segmentState.horizontalScrollOffset ||
            (this.removalMode === 'removeRow' && this.lastDensePackingScrollOffset !== this.scrollOffset);
    }

    private updateLastState(referencePrice: number): void {
        this.needsFullRedraw = false;
        this.lastRemovalMode = this.removalMode;
        this.lastShowOrderCount = this.showOrderCount;
        this.lastShowVolumeBars = this.showVolumeBars;
        this.lastTickSize = this.tickSize;
        this.lastDisplayTickSize = this.displayTickSize;
        this.lastDensePackingScrollOffset = this.scrollOffset;
        this.lastReferencePrice = referencePrice;
        this.lastWidth = this.width;
        this.lastHeight = this.height;
        this.lastQtyColWidth = this.qtyColWidth;
        this.lastPriceColWidth = this.priceColWidth;
        this.lastWorkColWidth = this.workColWidth;
        this.lastLtqColWidth = this.ltqColWidth;
        this.lastBarColumnWidth = this.barColumnWidth;
        this.lastMboOrderSizeFilter = this.mboOrderSizeFilter;
        this.lastSegmentPixelsPerUnit = this.segmentPixelsPerUnit();
        this.lastSegmentScrollOffset = this.segmentState.horizontalScrollOffset;
        this.lastCenterPrice = this.centerPrice;
        this.lastAutoCenter = this.autoCenter;
    }

    private segmentPixelsPerUnit(): number {
        return this.segmentState.basePixelsPerUnit * this.segmentState.userScaleFactor;
    }

    // Rows below the header, counting one cut off at the bottom (mirrors SkiaRenderer's visibleRows)
    private rowCount(): number {
        return Math.max(0, Math.ceil((this.height - this.headerHeight) / this.rowHeight));
    }

    // Rows that fit whole below the header
    private fullRows(): number {
        return Math.max(0, Math.floor((this.height - this.headerHeight) / this.rowHeight));
    }

    // Y of a row's top edge (mirrors SkiaRenderer.RowTop)
    private rowTop(rowIndex: number): number {
        return this.headerHeight + rowIndex * this.rowHeight;
    }

    private markRowDirty(rowIndex: number): void {
        this.minDirtyRow = Math.min(this.minDirtyRow, rowIndex);
        this.maxDirtyRow = Math.max(this.maxDirtyRow, rowIndex);
    }

    /**
     * Fill nextRows with what each row shows this frame (mirrors SkiaRenderer.BuildRows)
     */
    private buildRows(snapshot: OrderBookSnapshot, referencePrice: number): void {
        const rowCount = this.rowCount();
        const rows = this.nextRows;
        while (rows.length < rowCount) {
            rows.push(createRow());
        }
        rows.length = rowCount;

        if (this.removalMode === 'removeRow') {
            // Dense packing: asks then bids on consecutive rows, empty rows elsewhere
            for (const row of rows) {
                resetRow(row, NaN);
            }
            const layout = this.buildDensePackingLayout(snapshot, this.askStats, this.bidStats);
            for (let i = 0; i < layout.askRowsToRender; i++) {
                const askIndex = layout.nonEmptyAsks.length - 1 - layout.startAskIndex - i;
                const rowIndex = layout.firstRowIndex + i;
                if (askIndex >= 0 && askIndex < layout.nonEmptyAsks.length && rowIndex < rowCount) {
                    this.placeLevel(rows[rowIndex].ask, layout.nonEmptyAsks[askIndex], snapshot.askOrders);
                }
            }

            const firstBidRow = layout.firstRowIndex + layout.askRowsToRender;
            for (let i = 0; i < layout.bidRowsToRender; i++) {
                const bidIndex = layout.nonEmptyBids.length - 1 - layout.startBidIndex - i;
                const rowIndex = firstBidRow + i;
                if (bidIndex >= 0 && bidIndex < layout.nonEmptyBids.length && rowIndex < rowCount) {
                    this.placeLevel(rows[rowIndex].bid, layout.nonEmptyBids[bidIndex], snapshot.bidOrders);
                }
            }
            this.placeCells(rows, snapshot, layout, 0, 0);
            return;
        }

        // Show Empty Rows: every row is a price step from the reference price
        const midRow = Math.floor(this.visibleRows / 2);
        for (let rowIndex = 0; rowIndex < rowCount; rowIndex++) {
            const price = referencePrice - ((rowIndex - midRow) * this.displayTickSize);
            // Round to display tick size to avoid floating-point precision issues
            resetRow(rows[rowIndex], Math.round(price / this.displayTickSize) * this.displayTickSize);
        }
        this.placeLevels(rows, snapshot.asks, this.askStats, snapshot.askOrders, Side.ASK, midRow, referencePrice);
        this.placeLevels(rows, snapshot.bids, this.bidStats, snapshot.bidOrders, Side.BID, midRow, referencePrice);
        this.placeCells(rows, snapshot, null, midRow, referencePrice);
    }

    /**
     * Put our working orders and the last trade on the rows of their prices (mirrors
     * SkiaRenderer.PlaceCells). layout is the Remove Rows layout, null in Show Empty Rows
     */
    private placeCells(rows: RowState[], snapshot: OrderBookSnapshot, layout: DensePackingLayout | null,
        midRow: number, referencePrice: number): void {
        if (this.showWorkColumn) {
            for (const [work, side, bit] of [[snapshot.bidWork, Side.BID, WORK_BUY], [snapshot.askWork, Side.ASK, WORK_SELL]] as const) {
                if (!work) {
                    continue;
                }
                for (const [price, orders] of work) {
                    const rowIndex = this.rowOfPrice(price, side === Side.BID, layout, midRow, referencePrice);
                    if (rowIndex >= 0) {
                        const row = rows[rowIndex];
                        row.workQuantity += orders.quantity;
                        row.workCount += orders.count;
                        row.workSides |= bit;
                    }
                }
            }
        }

        const trade = snapshot.lastTrade;
        if (this.showLtqColumn && trade && trade.quantity > 0) {
            // A seller aggressor hit a bid, so its price is looked up among the bids first
            const rowIndex = this.rowOfPrice(trade.price, trade.aggressor === Side.ASK, layout, midRow, referencePrice);
            if (rowIndex >= 0) {
                rows[rowIndex].ltqQuantity = trade.quantity;
                rows[rowIndex].ltqAggressor = trade.aggressor ?? -1;
            }
        }
    }

    /**
     * Row showing a price, -1 if none. Show Empty Rows maps it like placeLevels. Remove Rows finds
     * the level with that price, on the bid side first when bidFirst
     */
    private rowOfPrice(price: number, bidFirst: boolean, layout: DensePackingLayout | null, midRow: number,
        referencePrice: number): number {
        const rowCount = this.rowCount();
        if (layout === null) {
            const rowIndex = midRow - Math.round((price - referencePrice) / this.displayTickSize);
            return rowIndex >= 0 && rowIndex < rowCount ? rowIndex : -1;
        }
        const first = bidFirst ? this.denseBidRow(layout, price) : this.denseAskRow(layout, price);
        const rowIndex = first >= 0 ? first : bidFirst ? this.denseAskRow(layout, price) : this.denseBidRow(layout, price);
        return rowIndex < rowCount ? rowIndex : -1;
    }

    // Remove Rows: row of the ask level at price, -1 if it isn't drawn (inverse of buildRows' ask loop)
    private denseAskRow(layout: DensePackingLayout, price: number): number {
        const i = layout.nonEmptyAsks.length - 1 - layout.startAskIndex - this.levelIndex(layout.nonEmptyAsks, price);
        return i >= 0 && i < layout.askRowsToRender ? layout.firstRowIndex + i : -1;
    }

    private denseBidRow(layout: DensePackingLayout, price: number): number {
        const i = layout.nonEmptyBids.length - 1 - layout.startBidIndex - this.levelIndex(layout.nonEmptyBids, price);
        return i >= 0 && i < layout.bidRowsToRender ? layout.firstRowIndex + layout.askRowsToRender + i : -1;
    }

    // Index of the level at price (within half a display tick) in ascending levels, levels.length if none
    private levelIndex(levels: BookLevel[], price: number): number {
        const tolerance = this.displayTickSize / 2;
        const index = firstAtOrAbove(levels, price - tolerance);
        return index < levels.length && levels[index].price < price + tolerance ? index : levels.length;
    }

    /**
     * Show Empty Rows: put each level on the row its price maps to (mirrors SkiaRenderer.PlaceLevels).
     * Ascending levels are read only around the rows' prices
     */
    private placeLevels(rows: RowState[], levels: BookLevel[], stats: LevelStats,
        orderMap: Map<number, Order[]> | null | undefined, side: Side, midRow: number, referencePrice: number): void {
        let start = 0;
        let maxPrice = Infinity;
        if (stats.ascending) {
            // Rows span midRow - rows.length + 1 .. midRow ticks from the reference price. A tick
            // more on each side covers rounding, and the row check below has the final say
            start = firstAtOrAbove(levels, referencePrice + (midRow - rows.length - 0.5) * this.displayTickSize);
            maxPrice = referencePrice + (midRow + 1.5) * this.displayTickSize;
        }
        for (let i = start; i < levels.length; i++) {
            const level = levels[i];
            if (level.price > maxPrice) {
                break;
            }
            if (!(level.quantity > 0)) {
                continue; // An empty level draws nothing
            }
            const rowIndex = midRow - Math.round((level.price - referencePrice) / this.displayTickSize);
            if (rowIndex < 0 || rowIndex >= rows.length) {
                continue;
            }

            const row = rows[rowIndex];
            const slot = side === Side.ASK ? row.ask : row.bid;
            if (!slot.present) {
                this.placeLevel(slot, level, orderMap);
            } else {
                // Prices off the display grid can share a row: draw them all, in snapshot order
                const overlap = createLevel(side);
                this.placeLevel(overlap, level, orderMap);
                (row.overlaps ??= []).push(overlap);
            }
        }
    }

    /**
     * Fill a row's ask or bid slot with a level and, in MBO mode, its orders
     */
    private placeLevel(slot: RowLevel, level: BookLevel, orderMap: Map<number, Order[]> | null | undefined): void {
        const orders = orderMap?.get(level.price); // Keyed by the same prices as the levels
        slot.present = true;
        slot.price = level.price;
        slot.quantity = level.quantity;
        slot.numOrders = level.numOrders;
        slot.hasOwnOrders = level.hasOwnOrders;
        slot.orders = orders;
        // Same condition as the aggregated bar in drawRowBars
        slot.bar = this.showVolumeBars && !(orders && orders.length > 0) ? this.volumeBarWidth(level.quantity) : 0;
    }

    /**
     * Show Empty Rows: when the reference price moves by whole rows, move the canvas pixels and
     * row states with it, so only rows scrolling in (or changed) get redrawn. False when the move
     * can't be reused (off the display grid, or a screen or more). Mirrors SkiaRenderer.TryShiftRows
     */
    private tryShiftRows(referencePrice: number): boolean {
        if (this.removalMode !== 'showEmpty' || referencePrice === this.lastReferencePrice) {
            return true;
        }

        const steps = (referencePrice - this.lastReferencePrice) / this.displayTickSize; // Price up: content moves down
        const shift = Math.round(steps);
        if (Math.abs(steps - shift) > TICK_EPSILON || Math.abs(shift) >= this.rows.length) {
            return false;
        }

        // The main canvas still shows the last frame. Only the rows move, the header stays
        const shiftPixels = shift * this.rowHeight;
        const height = this.height - this.headerHeight - Math.abs(shiftPixels);
        if (height > 0) {
            this.offscreenCtx.drawImage(this.canvas, 0, this.headerHeight + Math.max(0, -shiftPixels), this.width, height,
                0, this.headerHeight + Math.max(0, shiftPixels), this.width, height);
        }

        // Rotate the states. A row keeps its state only if it came from a strip that was fully on screen
        const fullRows = this.fullRows();
        const before = this.rows.slice();
        for (let rowIndex = 0; rowIndex < before.length; rowIndex++) {
            const from = rowIndex - shift;
            const row = before[(from + before.length) % before.length];
            if (from < 0 || from >= fullRows) {
                row.drawn = false;
            }
            this.rows[rowIndex] = row;
        }
        this.markRowDirty(0);
        this.markRowDirty(this.rows.length - 1);
        return true;
    }

    /**
     * Redraw the rows whose state differs from the canvas. Each row strip comes out the same as
     * renderFull draws it (mirrors SkiaRenderer.RenderChangedRows)
     */
    private renderChangedRows(): void {
        if (this.moveSource.length !== this.nextRows.length) {
            this.moveSource = new Int32Array(this.nextRows.length);
        }
        if (this.findMovedRows(this.moveSource)) {
            this.moveRows(this.moveSource);
        }

        // First pixel column a bar can touch; the columns end at or before it
        const barX = this.getColumnPositions().barX;
        const barColumn = barX >= 0 ? Math.floor(barX) : -1;
        const ctx = this.offscreenCtx;

        for (let rowIndex = 0; rowIndex < this.nextRows.length; rowIndex++) {
            const cached = this.rows[rowIndex];
            const next = this.nextRows[rowIndex];
            if (sameRow(cached, next, true)) {
                continue;
            }

            if (barColumn < 0 || !sameRow(cached, next, false)) {
                this.drawRowBackground(rowIndex);
                this.drawRowContent(next, rowIndex, true);
            } else if (barColumn >= this.width) {
                // Only the bars changed, and a narrow ladder puts the whole bar column past the right edge
                continue;
            } else {
                // Only the bars changed (the volume scale moved): replay the row clipped to the bar
                // column. The clip is pixel aligned, so its edge pixel comes out as in a full redraw.
                // Text never reaches the bar column, so it's skipped
                const y = this.rowTop(rowIndex);
                ctx.save();
                ctx.beginPath();
                ctx.rect(barColumn, y, this.width - barColumn, this.rowHeight);
                ctx.clip();
                this.drawRowBackground(rowIndex);
                this.drawRowContent(next, rowIndex, false);
                ctx.restore();
            }
            this.markRowDirty(rowIndex);
        }

        // One copy for every changed row (rows in between are the same on both canvases)
        if (this.maxDirtyRow >= 0) {
            const y = this.rowTop(this.minDirtyRow);
            const height = Math.min(this.rowTop(this.maxDirtyRow + 1), this.height) - y;
            this.ctx.drawImage(this.offscreenCanvas as any, 0, y, this.width, height, 0, y, this.width, height);
        }
    }

    /**
     * For each changed row, a nearby row that already shows its content (-1: none). Levels added
     * or removed above a row shift it in Remove Rows mode
     */
    private findMovedRows(source: Int32Array): boolean {
        const fullRows = this.fullRows(); // A row cut off at the bottom can't be a source
        let anyMoved = false;
        for (let rowIndex = 0; rowIndex < source.length; rowIndex++) {
            source[rowIndex] = -1;
            const next = this.nextRows[rowIndex];
            if (sameRow(this.rows[rowIndex], next, false)) {
                continue;
            }

            for (let distance = 1; distance <= MAX_ROW_MOVE; distance++) {
                const above = rowIndex - distance;
                if (above >= 0 && above < fullRows && sameRow(this.rows[above], next, false)) {
                    source[rowIndex] = above;
                    break;
                }
                const below = rowIndex + distance;
                if (below < fullRows && below < this.rows.length && sameRow(this.rows[below], next, false)) {
                    source[rowIndex] = below;
                    break;
                }
            }
            anyMoved ||= source[rowIndex] >= 0;
        }
        return anyMoved;
    }

    /**
     * Copy row strips to their new rows from the main canvas, which still shows the last frame.
     * Neighbors moving by the same amount go in one copy
     */
    private moveRows(source: Int32Array): void {
        const before = this.rows.slice();
        for (let rowIndex = 0; rowIndex < source.length;) {
            const from = source[rowIndex];
            if (from < 0) {
                rowIndex++;
                continue;
            }

            let end = rowIndex + 1;
            while (end < source.length && source[end] >= 0 && source[end] - end === from - rowIndex) {
                end++;
            }
            const y = this.rowTop(rowIndex);
            const height = Math.min((end - rowIndex) * this.rowHeight, this.height - y); // May be cut off at the bottom
            this.offscreenCtx.drawImage(this.canvas, 0, this.rowTop(from), this.width, height,
                0, y, this.width, height);

            for (; rowIndex < end; rowIndex++) {
                this.rows[rowIndex] = copyRow(before[source[rowIndex]]); // One row can be the source of several
                this.markRowDirty(rowIndex);
            }
        }
    }

    private buildDensePackingLayout(snapshot: OrderBookSnapshot, askStats: LevelStats, bidStats: LevelStats): DensePackingLayout {
        // Levels with quantity only (never rendered in removeRow mode), one per price, ascending
        const nonEmptyAsks = nonEmptyLevels(snapshot.asks, askStats);
        const nonEmptyBids = nonEmptyLevels(snapshot.bids, bidStats);
        const midRow = Math.floor(this.visibleRows / 2);
        const rowCount = this.rowCount(); // A row cut off at the bottom shows the part that fits
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
            askRowsToRender = Math.min(nonEmptyAsks.length, Math.max(0, rowCount + virtualTopRow));
            startBidIndex = 0;
            const bidRowsAvailable = Math.max(0, rowCount + virtualTopRow - nonEmptyAsks.length);
            bidRowsToRender = Math.min(nonEmptyBids.length, bidRowsAvailable);
        } else if (virtualTopRow < nonEmptyAsks.length) {
            startAskIndex = virtualTopRow;
            askRowsToRender = Math.min(nonEmptyAsks.length - startAskIndex, rowCount);
            startBidIndex = 0;
            const bidRowsAvailable = rowCount - askRowsToRender;
            bidRowsToRender = Math.min(nonEmptyBids.length, bidRowsAvailable);
        } else if (virtualTopRow < totalLevels) {
            startAskIndex = nonEmptyAsks.length;
            askRowsToRender = 0;
            const bidStartRow = virtualTopRow - nonEmptyAsks.length;
            startBidIndex = bidStartRow;
            bidRowsToRender = Math.min(nonEmptyBids.length - startBidIndex, rowCount);
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

    /**
     * A row's content over its blank strip. textToo false draws only what can reach the bar
     * column (LTQ fill, bars, segments, own-order borders), in the same order
     */
    private drawRowContent(row: RowState, rowIndex: number, textToo: boolean): void {
        this.drawCells(row, rowIndex, textToo);
        if (Number.isNaN(row.label)) {
            // Remove Rows: one level per row
            const level = row.ask.present ? row.ask : row.bid;
            if (level.present) {
                this.renderRow(rowIndex, level, textToo);
            }
            return;
        }

        // Show Empty Rows: the price label, then asks before bids, each in snapshot order
        if (textToo) {
            this.renderPriceOnly(rowIndex, row.label);
        }
        if (row.ask.present) {
            this.renderDataOverlay(rowIndex, row.ask, textToo);
        }
        this.drawOverlaps(row, rowIndex, Side.ASK, textToo);
        if (row.bid.present) {
            this.renderDataOverlay(rowIndex, row.bid, textToo);
        }
        this.drawOverlaps(row, rowIndex, Side.BID, textToo);
    }

    /**
     * Work and LTQ cells (mirrors SkiaRenderer.DrawCells). The LTQ fill's antialiased right edge
     * can touch the bar column's first pixel, so it's drawn without text too
     */
    private drawCells(row: RowState, rowIndex: number, textToo: boolean): void {
        const ctx = this.offscreenCtx;
        const cols = this.getColumnPositions();
        const y = this.rowTop(rowIndex);
        const textY = y + this.rowHeight / 2;

        if (row.ltqQuantity > 0) {
            ctx.fillStyle = row.ltqAggressor === Side.BID ? this.colors.ltqBuyBackground
                : row.ltqAggressor === Side.ASK ? this.colors.ltqSellBackground : this.colors.ltqUnknownBackground;
            ctx.fillRect(cols.ltqX, y + 1, this.ltqColWidth, this.rowHeight - 1); // Below the row's grid line
            if (textToo) {
                ctx.fillStyle = this.colors.ltqText;
                ctx.textAlign = 'center';
                ctx.fillText(this.formatQuantity(row.ltqQuantity), cols.ltqX + this.ltqColWidth / 2, textY);
            }
        }

        if (textToo && row.workCount > 0) {
            // Buy orders green, sell orders red, both on one row in the normal text color
            ctx.fillStyle = row.workSides === WORK_BUY ? this.colors.bidBar
                : row.workSides === WORK_SELL ? this.colors.askBar : this.colors.text;
            ctx.textAlign = 'center';
            ctx.fillText(this.workText(row.workQuantity, row.workCount), cols.workX + this.workColWidth / 2, textY);
        }
    }

    private drawOverlaps(row: RowState, rowIndex: number, side: Side, textToo: boolean): void {
        if (row.overlaps === null) {
            return;
        }
        for (const level of row.overlaps) {
            if (level.side === side) {
                this.renderDataOverlay(rowIndex, level, textToo);
            }
        }
    }

    private renderRow(rowIndex: number, row: RowLevel, textToo: boolean): void {
        const y = this.rowTop(rowIndex);
        const textY = y + this.rowHeight / 2;

        // Get column positions
        const cols = this.getColumnPositions();

        // Set text style
        this.offscreenCtx.fillStyle = this.colors.text;
        this.offscreenCtx.textAlign = 'center';

        if (row.side === Side.BID) {
            // BID: bid_qty, price, bars
            if (textToo) {
                this.offscreenCtx.fillText(this.quantityText(row.quantity, row.numOrders, Side.BID), cols.bidQtyX + this.qtyColWidth / 2, textY);
            }

            // Own order highlight in PriceLevel mode
            if (row.hasOwnOrders) {
                this.offscreenCtx.strokeStyle = this.colors.ownOrderBorder;
                this.offscreenCtx.lineWidth = 2;
                this.offscreenCtx.strokeRect(cols.bidQtyX, y + 1, this.qtyColWidth, this.rowHeight - 2);
            }

            // price
            if (textToo) {
                this.offscreenCtx.fillText(this.formatPrice(row.price), cols.priceX + this.priceColWidth / 2, textY);
            }
        } else {
            // ASK: price, ask_qty, bars
            if (textToo) {
                // price
                this.offscreenCtx.fillText(this.formatPrice(row.price), cols.priceX + this.priceColWidth / 2, textY);

                // ask_qty
                this.offscreenCtx.fillText(this.quantityText(row.quantity, row.numOrders, Side.ASK), cols.askQtyX + this.qtyColWidth / 2, textY);
            }

            // Own order highlight in PriceLevel mode
            if (row.hasOwnOrders) {
                this.offscreenCtx.strokeStyle = this.colors.ownOrderBorder;
                this.offscreenCtx.lineWidth = 2;
                this.offscreenCtx.strokeRect(cols.askQtyX, y + 1, this.qtyColWidth, this.rowHeight - 2);
            }
        }

        this.drawRowBars(row, cols.barX, y);
    }

    /**
     * Aggregated volume bar, or the order segments in MBO mode
     */
    private drawRowBars(row: RowLevel, barX: number, y: number): void {
        if (!this.showVolumeBars) {
            return;
        }

        if (row.orders && row.orders.length > 0) {
            // MBO mode: Draw individual order bars
            this.drawIndividualOrders(row.orders, row.side, y);
        } else if (row.bar > 0) {
            // PriceLevel mode: Draw single aggregated bar
            this.offscreenCtx.fillStyle = row.side === Side.BID ? this.colors.bidBar : this.colors.askBar;
            this.offscreenCtx.fillRect(barX, y + 4, row.bar, this.rowHeight - 8);
        }
    }

    /**
     * A blank row strip: background, columns and the row's top grid line. The line below belongs
     * to the next row, so redrawing this row never touches that row's pixels
     */
    private drawRowBackground(rowIndex: number): void {
        const y = this.rowTop(rowIndex);

        // Base row background
        this.offscreenCtx.fillStyle = this.colors.background;
        this.offscreenCtx.fillRect(0, y, this.width, this.rowHeight);
        this.fillColumns(y, this.rowHeight);

        // Grid line (a 1px rect, as on desktop)
        this.offscreenCtx.fillStyle = this.colors.gridLine;
        this.offscreenCtx.fillRect(0, y, this.width, 1);
    }

    /**
     * Render only the price label for a row (used in Show Empty mode)
     */
    private renderPriceOnly(rowIndex: number, price: number): void {
        const y = this.rowTop(rowIndex);
        const priceText = this.formatPrice(price);

        // Get column positions
        const cols = this.getColumnPositions();

        // Set text style
        this.offscreenCtx.fillStyle = this.colors.text;
        this.offscreenCtx.textAlign = 'center';

        // Render price only
        this.offscreenCtx.fillText(priceText, cols.priceX + this.priceColWidth / 2, y + this.rowHeight / 2);
    }

    /**
     * Render data overlay (quantity, orders, bars) for a row that has a level
     */
    private renderDataOverlay(rowIndex: number, row: RowLevel, textToo: boolean): void {
        const y = this.rowTop(rowIndex);
        const textY = y + this.rowHeight / 2;

        // Get column positions
        const cols = this.getColumnPositions();

        if (textToo) {
            // Set text style
            this.offscreenCtx.fillStyle = this.colors.text;
            this.offscreenCtx.textAlign = 'center';

            const qtyX = row.side === Side.BID ? cols.bidQtyX : cols.askQtyX;
            this.offscreenCtx.fillText(this.quantityText(row.quantity, row.numOrders, row.side), qtyX + this.qtyColWidth / 2, textY);
        }

        this.drawRowBars(row, cols.barX, y);
    }

    /**
     * Draw individual order bars (MBO mode)
     * Draws bars as horizontally adjacent segments within the row
     */
    private drawIndividualOrders(
        orders: Order[],
        side: Side,
        y: number
    ): void {
        if (!orders || orders.length === 0 || !this.showVolumeBars) return;

        // Calculate segment widths without minimum constraint
        const pixelsPerUnit = this.segmentState.basePixelsPerUnit * this.segmentState.userScaleFactor;
        const scrollOffset = this.segmentState.horizontalScrollOffset;
        const cols = this.getColumnPositions();
        const barStartX = cols.barX;
        const minQty = this.mboOrderSizeFilter;

        // Same height as single bar (with padding)
        const barHeight = this.rowHeight - 8;
        const fillColor = side === Side.BID ? this.colors.bidBar : this.colors.askBar;

        // Clip to bar column so scrolled segments/text don't bleed into qty columns.
        const clipX = barStartX;
        const clipY = y + 4;
        const clipWidth = this.barColumnWidth;
        const clipHeight = barHeight;
        this.offscreenCtx.save();
        this.offscreenCtx.beginPath();
        this.offscreenCtx.rect(clipX, clipY, clipWidth, clipHeight);
        this.offscreenCtx.clip();

        let xOffset = 0; // Track position within virtual segment space
        const gap = SEGMENT_GAP_PX;

        for (let i = 0; i < orders.length; i++) {
            const order = orders[i];

            if (order.quantity <= minQty) {
                continue;
            }

            // Calculate proportional width (no min constraint)
            let segmentWidth = order.quantity * pixelsPerUnit;

            // Apply minimum rendering width
            const renderWidth = Math.max(MIN_SEGMENT_WIDTH_PX, segmentWidth);

            // Calculate screen position accounting for scroll
            const segmentStartX = barStartX + xOffset - scrollOffset;
            const segmentEndX = segmentStartX + renderWidth;

            // Cull segments outside visible area
            const visibleStartX = Math.max(segmentStartX, barStartX);
            const visibleEndX = Math.min(segmentEndX, barStartX + this.barColumnWidth);

            if (visibleEndX > visibleStartX && visibleEndX > barStartX && visibleStartX < barStartX + this.barColumnWidth) {
                const visibleWidth = visibleEndX - visibleStartX;

                // Draw segment background
                this.offscreenCtx.fillStyle = fillColor;
                this.offscreenCtx.fillRect(visibleStartX, y + 4, visibleWidth, barHeight);

                // Draw exact quantity text with appropriate decimal precision
                const qtyText = this.formatQuantity(order.quantity);
                this.offscreenCtx.font = '10px monospace';
                const textWidth = this.offscreenCtx.measureText(qtyText).width;

                // Only draw text if segment is wide enough and text is in visible area
                if (renderWidth > 40 && textWidth < renderWidth - 4) {
                    const textCenterX = segmentStartX + renderWidth / 2;

                    // Check if text center is in visible area
                    if (textCenterX >= barStartX && textCenterX <= barStartX + this.barColumnWidth) {
                        this.offscreenCtx.fillStyle = this.colors.text;
                        this.offscreenCtx.textAlign = 'center';
                        this.offscreenCtx.textBaseline = 'middle';
                        this.offscreenCtx.fillText(qtyText, textCenterX, y + 4 + barHeight / 2);
                    }
                }

                // Restore font
                this.offscreenCtx.font = '14px monospace';
                this.offscreenCtx.textAlign = 'left';
                this.offscreenCtx.textBaseline = 'middle';

                // Draw gold border for own orders
                if (order.isOwnOrder) {
                    this.offscreenCtx.strokeStyle = this.colors.ownOrderBorder;
                    this.offscreenCtx.lineWidth = 2;
                    this.offscreenCtx.strokeRect(visibleStartX, y + 4, visibleWidth, barHeight);
                }
            }

            // Move to next segment position
            xOffset += renderWidth + gap;
        }

        this.offscreenCtx.restore();
    }

    /**
     * Calculate dynamic segment scale step based on order size and zoom level.
     */
    private calculateDynamicSegmentScaleStep(): number {
        const baseStep = SEGMENT_SCALE_STEP;
        const basePixelsPerUnit = this.segmentState.basePixelsPerUnit;

        if (basePixelsPerUnit <= 0) {
            return baseStep;
        }

        const maxOrderQtyEstimate = TARGET_MAX_SEGMENT_WIDTH / basePixelsPerUnit;
        const qtyRatio = Math.max(1, maxOrderQtyEstimate / 100);
        const qtyBoost = Math.min(4, 1 + Math.log10(qtyRatio));
        const zoomBoost = Math.min(5, 1 + Math.log2(Math.max(1, this.segmentState.userScaleFactor)));

        const step = baseStep * qtyBoost * zoomBoost;
        return Math.max(baseStep, Math.min(3, step));
    }

    /**
     * Adjust segment scale factor (called by Shift+Scroll)
     */
    public adjustSegmentScale(delta: number): void {
        const step = this.calculateDynamicSegmentScaleStep();
        const newScale = this.segmentState.userScaleFactor + (delta * step);

        // Clamp to valid range
        this.segmentState.userScaleFactor = Math.max(
            SEGMENT_SCALE_MIN,
            Math.min(SEGMENT_SCALE_MAX, newScale)
        );

        // Recalculate max scroll offset with new scale
        this.recalculateMaxScroll();

        // Clamp current scroll to new max
        if (this.segmentState.horizontalScrollOffset > this.segmentState.maxScrollOffset) {
            this.segmentState.horizontalScrollOffset = this.segmentState.maxScrollOffset;
        }

        // Save to localStorage for persistence
        localStorage.setItem('segmentScaleFactor', this.segmentState.userScaleFactor.toString());

        // Force full redraw (structural change)
        this.needsFullRedraw = true;
    }

    /**
     * Adjust horizontal scroll offset (called by mouse drag)
     */
    public adjustHorizontalScroll(delta: number): void {
        // Only scrolling reads the limit, so it's found here: the widest level changes with the book
        this.recalculateMaxScroll();
        this.segmentState.horizontalScrollOffset = Math.max(
            0,
            Math.min(
                this.segmentState.maxScrollOffset,
                this.segmentState.horizontalScrollOffset + delta
            )
        );

        // Mark for redraw
        this.needsFullRedraw = true;
    }

    /**
     * Get bar column width (for interaction handler)
     */
    public getBarColumnWidth(): number {
        return this.barColumnWidth;
    }

    /**
     * Get showOrderCount setting
     */
    public getShowOrderCount(): boolean {
        return this.showOrderCount;
    }

    /**
     * X where the volume bar column starts, -1 when bars are hidden (for interaction handler)
     */
    public getBarColumnX(): number {
        return this.getColumnPositions().barX;
    }

    private volumeBarWidth(quantity: number): number {
        const BAR_MAX_WIDTH = (COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER) - 5;
        return this.maxQuantity > 0 ? (quantity / this.maxQuantity) * BAR_MAX_WIDTH : 0;
    }

    private copyToMainCanvas(): void {
        // Copy offscreen canvas to main canvas (hardware accelerated)
        this.ctx.drawImage(this.offscreenCanvas as any, 0, 0);
    }

    private markAllDirty(): void {
        this.minDirtyRow = 0;
        this.maxDirtyRow = this.visibleRows - 1;
    }

    private clearDirtyState(): void {
        this.minDirtyRow = Infinity;
        this.maxDirtyRow = -1;
    }

    /**
     * Count one display frame, rendered or skipped as idle, for getMetrics().fps
     */
    public recordFrame(): void {
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
     * Column id under screen X: 0 bid qty, 1 price, 2 ask qty, 3 bars and beyond, 4 Work, 5 LTQ,
     * -1 left of the ladder. Ids, not positions: Work sits left of bid qty. Found from the
     * columns' pixel positions, since their widths change
     */
    public screenXToColumn(x: number): number {
        const cols = this.getColumnPositions();
        if (x < 0) return -1;
        if (x < cols.bidQtyX) return this.getWorkColumn(); // Only with the Work column, otherwise bidQtyX is 0
        if (x < cols.priceX) return this.getBidQtyColumn();
        if (x < cols.askQtyX) return this.getPriceColumn();
        if (x < cols.askQtyX + this.qtyColWidth) return this.getAskQtyColumn();
        if (cols.ltqX >= 0 && x < cols.ltqX + this.ltqColWidth) return this.getLtqColumn();
        return 3;
    }

    /**
     * Get the Work column id
     */
    public getWorkColumn(): number {
        return 4;
    }

    /**
     * Get the LTQ column id
     */
    public getLtqColumn(): number {
        return 5;
    }

    /**
     * Get the price column index
     */
    public getPriceColumn(): number {
        return 1;
    }

    /**
     * Get the BID quantity column index
     */
    public getBidQtyColumn(): number {
        return 0;
    }

    /**
     * Get the ASK quantity column index
     */
    public getAskQtyColumn(): number {
        return 2;
    }

    /**
     * Convert screen Y coordinate to row index, negative on the header
     */
    public screenYToRow(y: number): number {
        return Math.floor((y - this.headerHeight) / this.rowHeight);
    }

    /**
     * Convert row index to price
     */
    /**
     * Price of a row as drawn (mirrors ViewportManager.RowPriceAt): its level's price, the ask
     * first, else its Show Empty Rows label, which holds working orders even without a level.
     * Null for the header or a Remove Rows row with no level
     */
    public rowPrice(rowIndex: number): number | null {
        const row = this.rows[rowIndex];
        if (row === undefined) return null;
        if (row.ask.present) return row.ask.price;
        if (row.bid.present) return row.bid.price;
        return Number.isNaN(row.label) ? null : parseFloat(this.formatPrice(row.label)); // Formatted: no float noise
    }

    public rowToPrice(rowIndex: number): number | null {
        const levelInfo = this.rowToLevelInfo(rowIndex);
        return levelInfo?.price ?? null;
    }

    /**
     * Convert row index to level info (price and quantity)
     */
    public rowToLevelInfo(rowIndex: number): { price: number; quantity: number; side: Side } | null {
        if (!this.currentSnapshot || rowIndex < 0) return null; // Negative: the header

        if (this.removalMode === 'removeRow') {
            // Dense packing mode: use the same layout logic as rendering. Levels may have changed
            // since the render, so they're scanned again
            const snapshot = this.currentSnapshot;
            const layout = this.buildDensePackingLayout(snapshot,
                scanLevels(snapshot.asks, createStats()), scanLevels(snapshot.bids, createStats()));

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
            const price = referencePrice - (rowOffset * this.displayTickSize);
            const roundedPrice = Math.round(price / this.displayTickSize) * this.displayTickSize;

            // Find level at this price
            const askLevel = this.currentSnapshot.asks.find(a => Math.abs(a.price - roundedPrice) < this.displayTickSize * 0.5);
            if (askLevel && askLevel.quantity > 0) {
                return { price: askLevel.price, quantity: askLevel.quantity, side: Side.ASK };
            }

            const bidLevel = this.currentSnapshot.bids.find(b => Math.abs(b.price - roundedPrice) < this.displayTickSize * 0.5);
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
        this.resizeCanvas(width, height);
        this.renderBackground();

        if (this.currentSnapshot) {
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Resize the canvases without drawing. Their pixels are cleared, so the next render redraws
     * everything
     */
    private resizeCanvas(width: number, height: number): void {
        this.width = width;
        this.height = height;
        this.visibleRows = this.fullRows();

        // Recalculate bar column width based on new width
        this.barColumnWidth = this.calculateBarColumnWidth();

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
        this.needsFullRedraw = true;
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
     * Set whether to show order count in the qty column
     */
    public setShowOrderCount(show: boolean): void {
        if (this.showOrderCount !== show) {
            this.showOrderCount = show;
            // Redraw everything: the render refits the qty column to the new text
            const newWidth = this.calculateCanvasWidth();
            this.resize(newWidth, this.height);
        }
    }

    /**
     * Set MBO order size filter (only show orders with qty > filter)
     */
    public setMboOrderSizeFilter(filter: number): void {
        const normalized = Math.max(0, filter);
        if (this.mboOrderSizeFilter === normalized) {
            return;
        }
        this.mboOrderSizeFilter = normalized;
        this.needsFullRedraw = true;
        if (this.currentSnapshot) {
            this.calculateBaseScale();
            this.recalculateMaxScroll();
            if (this.segmentState.horizontalScrollOffset > this.segmentState.maxScrollOffset) {
                this.segmentState.horizontalScrollOffset = this.segmentState.maxScrollOffset;
            }
            this.render(this.currentSnapshot);
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
                // Round to display tick size to ensure proper alignment
                this.centerPrice = this.snapToDisplayTick(midPrice);
            }
        }

        // Apply scroll delta and round to display tick size
        this.centerPrice = this.snapToDisplayTick(this.centerPrice + delta);

        if (this.currentSnapshot) {
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Enable/disable auto-centering. Enabling re-centers immediately.
     */
    public setAutoCenter(enabled: boolean): void {
        this.autoCenter = enabled;
        if (enabled) {
            this.recenter();
        }
    }

    public getAutoCenter(): boolean {
        return this.autoCenter;
    }

    /**
     * Move the bid/ask midpoint to the middle row (both removal modes)
     */
    public recenter(): void {
        this.scrollOffset = 0;
        if (this.currentSnapshot) {
            this.applyCenter(this.currentSnapshot);
            this.render(this.currentSnapshot);
        }
    }

    /**
     * Reset row scroll and snap centerPrice to the midpoint (or the only side present)
     */
    private applyCenter(snapshot: OrderBookSnapshot): void {
        this.scrollOffset = 0;
        const target = snapshot.midPrice ?? snapshot.bestBid ?? snapshot.bestAsk;
        if (target !== null) {
            this.centerPrice = this.snapToDisplayTick(target);
        }
    }

    /**
     * Snap a price to the display tick grid, rounding half away from zero like C#
     * MidpointRounding.AwayFromZero on decimal. TICK_EPSILON absorbs float error
     * (100.05 / 0.1 = 1000.4999...) so half ticks land on the same row as desktop.
     */
    private snapToDisplayTick(price: number): number {
        const ticks = price / this.displayTickSize;
        const snapped = Math.floor(Math.abs(ticks) + 0.5 + TICK_EPSILON);
        return (ticks < 0 ? -snapped : snapped) * this.displayTickSize;
    }

    /**
     * Set removal mode (affects how empty levels are handled)
     */
    public setDisplayTickSize(value: number): void {
        this.displayTickSize = Math.max(this.tickSize, value);
        this.updatePriceFormat();
        this.needsFullRedraw = true;
        this.centerPrice = 0; // Reset so it re-snaps to new tick grid on next render
    }

    public forceFullRedraw(): void {
        this.needsFullRedraw = true;
    }

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
        // Two quantity columns (bid and ask) with dynamic width
        const qtyColumnsWidth = 2 * this.qtyColWidth;

        // Price column with dynamic width
        const priceColWidth = this.priceColWidth;

        // Work and LTQ columns when shown
        const cellColumnsWidth = (this.showWorkColumn ? this.workColWidth : 0) + (this.showLtqColumn ? this.ltqColWidth : 0);

        // Volume bar column
        const barWidth = this.showVolumeBars ? COL_WIDTH * VOLUME_BAR_WIDTH_MULTIPLIER : 0;

        return Math.round(qtyColumnsWidth + priceColWidth + cellColumnsWidth + barWidth);
    }
}
