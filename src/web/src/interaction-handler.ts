import { Side, COL_WIDTH } from './types';
import { CanvasRenderer } from './canvas-renderer';

/**
 * Handles user interactions with the price ladder (clicks, hover, scroll)
 */
export class InteractionHandler {
    private canvas: HTMLCanvasElement;
    private renderer: CanvasRenderer;
    private readOnly: boolean;

    private hoveredRow: number | null = null;
    private hoveredPrice: number | null = null;

    // Mouse drag state for horizontal scroll
    private isDraggingBar: boolean = false;
    private dragStartX: number = 0;
    private dragDistance: number = 0;
    private suppressClick: boolean = false;
    private static readonly dragSuppressThresholdPx = 3;

    // Callbacks
    public onPriceClick?: (price: number, side: Side) => void;
    public onPriceHover?: (price: number | null) => void;
    public onScroll?: (delta: number) => void;
    public onRenderNeeded?: () => void;

    constructor(canvas: HTMLCanvasElement, renderer: CanvasRenderer, readOnly: boolean = false) {
        this.canvas = canvas;
        this.renderer = renderer;
        this.readOnly = readOnly;

        this.setupEventListeners();
    }

    private setupEventListeners(): void {
        this.canvas.addEventListener('click', this.handleClick.bind(this));
        this.canvas.addEventListener('mousedown', this.handleMouseDown.bind(this));
        this.canvas.addEventListener('mouseup', this.handleMouseUp.bind(this));
        this.canvas.addEventListener('mousemove', this.handleMouseMove.bind(this));
        this.canvas.addEventListener('mouseleave', this.handleMouseLeave.bind(this));
        this.canvas.addEventListener('wheel', this.handleWheel.bind(this), { passive: false });
        this.canvas.addEventListener('contextmenu', this.handleContextMenu.bind(this));
    }

    private handleClick(event: MouseEvent): void {
        if (this.suppressClick) {
            this.suppressClick = false;
            return;
        }
        if (this.readOnly) return;

        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;

        // Convert to price and get level info
        const rowIndex = this.renderer.screenYToRow(y);
        const levelInfo = this.renderer.rowToLevelInfo(rowIndex);

        if (levelInfo !== null) {
            // Get column indices
            const clickedColumn = this.renderer.screenXToColumn(x);
            const bidQtyColumn = this.renderer.getBidQtyColumn();
            const askQtyColumn = this.renderer.getAskQtyColumn();

            // console.log(`Click: column ${clickedColumn}, bidQty=${bidQtyColumn}, askQty=${askQtyColumn}, price ${levelInfo.price}, qty ${levelInfo.quantity}`);

            // Only trigger trade if clicking on quantity columns
            if (clickedColumn === bidQtyColumn) {
                // Click on BID qty column = BUY (you want to buy at this ASK price)
                console.log('Action: BUY');
                this.onPriceClick?.(levelInfo.price, Side.ASK);
            } else if (clickedColumn === askQtyColumn) {
                // Click on ASK qty column = SELL (you want to sell at this BID price)
                console.log('Action: SELL');
                this.onPriceClick?.(levelInfo.price, Side.BID);
            } else {
                console.log('Action: none (clicked outside quantity columns)');
            }
        }
    }

    private handleMouseMove(event: MouseEvent): void {
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;

        // Handle horizontal scroll dragging
        if (this.isDraggingBar) {
            const dragDelta = this.dragStartX - x; // Invert for natural scroll direction
            this.dragDistance += Math.abs(dragDelta);
            if (this.dragDistance >= InteractionHandler.dragSuppressThresholdPx) {
                this.suppressClick = true;
            }
            this.renderer.adjustHorizontalScroll(dragDelta);
            this.dragStartX = x;
            this.onRenderNeeded?.();
            event.preventDefault();
            return;
        }

        const rowIndex = this.renderer.screenYToRow(y);

        if (rowIndex !== this.hoveredRow) {
            this.hoveredRow = rowIndex;
            const price = this.renderer.rowToPrice(rowIndex);

            if (price !== this.hoveredPrice) {
                this.hoveredPrice = price;
                this.onPriceHover?.(price);
            }
        }

        // Update cursor style
        if (!this.readOnly && this.hoveredPrice !== null) {
            this.canvas.style.cursor = 'pointer';
        } else {
            this.canvas.style.cursor = 'default';
        }
    }

    private handleMouseLeave(): void {
        this.hoveredRow = null;
        this.hoveredPrice = null;
        this.onPriceHover?.(null);
        this.canvas.style.cursor = 'default';

        // Stop dragging if mouse leaves canvas
        if (this.isDraggingBar) {
            this.isDraggingBar = false;
            this.dragDistance = 0;
            this.suppressClick = false;
        }
    }

    private handleMouseDown(event: MouseEvent): void {
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;

        // Check if click is in bar column
        const showOrderCount = this.renderer.getShowOrderCount();
        const barStartX = showOrderCount ? COL_WIDTH * 5 : COL_WIDTH * 3;
        const barColumnWidth = this.renderer.getBarColumnWidth();

        if (x >= barStartX && x < barStartX + barColumnWidth) {
            this.isDraggingBar = true;
            this.dragStartX = x;
            this.dragDistance = 0;
            this.suppressClick = false;
            event.preventDefault();
        }
    }

    private handleMouseUp(event: MouseEvent): void {
        if (this.isDraggingBar) {
            this.isDraggingBar = false;
            this.dragDistance = 0;
            if (this.suppressClick) {
                setTimeout(() => {
                    this.suppressClick = false;
                }, 0);
            }
            event.preventDefault();
        }
    }

    private handleWheel(event: WheelEvent): void {
        event.preventDefault();

        // Shift+Scroll: Adjust segment scale globally
        if (event.shiftKey) {
            const delta = event.deltaY > 0 ? -1 : 1; // Inverted for intuitive zoom
            this.renderer.adjustSegmentScale(delta);
            this.onRenderNeeded?.();
            return;
        }

        // Regular scroll: Vertical navigation (existing behavior)
        const delta = Math.sign(event.deltaY);

        // Check current mode to determine scroll behavior
        const mode = this.renderer.getRemovalMode();

        if (mode === 'removeRow') {
            // Dense packing mode: row-based scrolling
            const scrollTicks = delta * 5; // Scroll 5 rows per wheel tick
            this.onScroll?.(scrollTicks);
        } else {
            // Show empty mode: price-based scrolling
            const scrollTicks = delta > 0 ? -5 : 5; // Inverted for scroll up = higher prices
            const tickSize = this.renderer.getTickSize();
            const scrollAmount = scrollTicks * tickSize; // 5 ticks * tickSize
            this.renderer.scrollByPrice(scrollAmount);
        }
    }

    private handleContextMenu(event: MouseEvent): void {
        event.preventDefault();
        // Could show custom context menu here
    }

    /**
     * Update renderer reference
     */
    public setRenderer(renderer: CanvasRenderer): void {
        this.renderer = renderer;
    }

    /**
     * Update read-only state
     */
    public setReadOnly(readOnly: boolean): void {
        this.readOnly = readOnly;
    }

    /**
     * Clean up event listeners
     */
    public destroy(): void {
        this.canvas.removeEventListener('click', this.handleClick);
        this.canvas.removeEventListener('mousedown', this.handleMouseDown);
        this.canvas.removeEventListener('mouseup', this.handleMouseUp);
        this.canvas.removeEventListener('mousemove', this.handleMouseMove);
        this.canvas.removeEventListener('mouseleave', this.handleMouseLeave);
        this.canvas.removeEventListener('wheel', this.handleWheel);
        this.canvas.removeEventListener('contextmenu', this.handleContextMenu);
    }
}
