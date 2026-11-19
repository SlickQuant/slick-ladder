import { Side, ClickButton, COL_WIDTH } from './types';
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

    // Bound listeners, kept so destroy() can remove them
    private listeners: Array<[string, EventListener]> = [];

    // Callbacks
    public onPriceClick?: (price: number | null, side: Side, button: ClickButton) => boolean; // Returns handled
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
        // Focusable so the canvas receives keydown (Space = re-center). The browser's
        // focus ring (:focus-visible) shows for keyboard focus only, not mouse clicks
        this.canvas.tabIndex = 0;
        this.listen('keydown', this.handleKeyDown);
        this.listen('click', this.handleClick);
        this.listen('auxclick', this.handleClick); // Middle and right buttons
        this.listen('mousedown', this.handleMouseDown);
        this.listen('mouseup', this.handleMouseUp);
        this.listen('mousemove', this.handleMouseMove);
        this.listen('mouseleave', this.handleMouseLeave);
        this.listen('wheel', this.handleWheel);
        this.listen('contextmenu', this.handleContextMenu);
    }

    // passive: false so handlers (wheel) can preventDefault
    private listen<K extends keyof HTMLElementEventMap>(type: K, handler: (event: HTMLElementEventMap[K]) => void): void {
        const listener = handler.bind(this) as EventListener;
        this.canvas.addEventListener(type, listener, { passive: false });
        this.listeners.push([type, listener]);
    }

    // Side of the quantity column under x, null outside both
    private qtySideAt(x: number): Side | null {
        const column = this.renderer.screenXToColumn(x);
        if (column === this.renderer.getBidQtyColumn()) return Side.BID;
        if (column === this.renderer.getAskQtyColumn()) return Side.ASK;
        return null;
    }

    private handleClick(event: MouseEvent): void {
        if (this.suppressClick) {
            this.suppressClick = false;
            return;
        }
        if (this.readOnly || event.button > ClickButton.Right) return; // Back/forward buttons don't click levels

        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;

        // Only trigger trade if clicking on quantity columns.
        // BID qty column = BUY, ASK qty column = SELL at this price
        const side = this.qtySideAt(x);
        if (side === null) return;

        const levelInfo = this.renderer.rowToLevelInfo(this.renderer.screenYToRow(y));
        const price = levelInfo !== null ? levelInfo.price : null;
        const button = event.button as ClickButton;
        const handled = this.onPriceClick?.(price, side, button) === true;

        // Middle-click on a qty column re-centers like anywhere else unless the app took it
        if (button === ClickButton.Middle && !handled) {
            this.renderer.recenter();
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
        // Explicit focus: preventDefault below (drag, middle button) would otherwise block it
        this.canvas.focus({ preventScroll: true });

        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;

        // Middle button (wheel click): re-center, suppress browser autoscroll. On a qty column
        // the level click decides on auxclick, re-centering only if the app doesn't handle it
        if (event.button === ClickButton.Middle) {
            event.preventDefault();
            if (this.readOnly || this.qtySideAt(x) === null) {
                this.renderer.recenter();
            }
            return;
        }

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

        // Auto-center pins the view, so vertical scroll is disabled
        if (this.renderer.getAutoCenter()) {
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

    private handleKeyDown(event: KeyboardEvent): void {
        if (event.code === 'Space') {
            event.preventDefault(); // Keep the page from scrolling
            this.renderer.recenter();
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
        for (const [type, listener] of this.listeners) {
            this.canvas.removeEventListener(type, listener);
        }
        this.listeners = [];
    }
}
