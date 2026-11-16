import { Side, Order, OrderUpdate, OrderUpdateType, BookLevel } from './types';
import { SortedLevels } from './sorted-levels';

/**
 * OrderLevel: Represents all individual orders at a specific price level.
 * Maintains sorted orders by OrderId and caches aggregated quantity/count.
 */
class OrderLevel {
    public price: number;
    public side: Side;
    public orders: Map<number, Order>; // OrderId → Order
    public totalQuantity: number;
    public orderCount: number;
    public readonly book: BookLevel;   // This level in snapshots, kept current by markChanged
    private ordersArray: Order[] | null = null; // Snapshot copy of orders, kept until they change

    constructor(price: number, side: Side) {
        this.price = price;
        this.side = side;
        this.orders = new Map();
        this.totalQuantity = 0;
        this.orderCount = 0;
        this.book = { price, quantity: 0, numOrders: 0, side, isDirty: true, hasOwnOrders: false };
    }

    /**
     * Get all orders as array (for rendering individual orders). The same array until the orders
     * change, so the renderer can tell an unchanged level by reference
     */
    getOrdersArray(): Order[] {
        return this.ordersArray ??= Array.from(this.orders.values());
    }

    /**
     * Call after changing orders
     */
    markChanged(): void {
        this.book.quantity = this.totalQuantity;
        this.book.numOrders = this.orderCount;
        this.book.isDirty = true;
        this.ordersArray = null;
    }
}

/**
 * One side of the book: its levels by price, the same levels in price order, and the price →
 * orders map snapshots carry. The map is kept between snapshots and updated only for levels that
 * changed, so a snapshot costs nothing for a quiet level
 */
class BookSide {
    readonly levels = new Map<number, OrderLevel>();
    readonly sorted = new SortedLevels();
    private readonly ordersByPrice = new Map<number, Order[]>();
    private readonly changed = new Set<OrderLevel>();

    // The level at price, created if needed
    level(price: number, side: Side): OrderLevel {
        let level = this.levels.get(price);
        if (!level) {
            level = new OrderLevel(price, side);
            this.levels.set(price, level);
            this.sorted.add(level.book);
        }
        return level;
    }

    // Call after changing a level's orders
    levelChanged(level: OrderLevel): void {
        level.markChanged();
        this.changed.add(level);
    }

    removeLevel(level: OrderLevel): void {
        this.levels.delete(level.price);
        this.sorted.remove(level.price);
        this.ordersByPrice.delete(level.price);
        this.changed.delete(level);
    }

    orders(): Map<number, Order[]> {
        for (const level of this.changed) {
            this.ordersByPrice.set(level.price, level.getOrdersArray());
        }
        this.changed.clear();
        return this.ordersByPrice;
    }
}

/**
 * Market-By-Order (MBO) Manager
 *
 * Tracks individual orders at each price level and aggregates to BookLevel for rendering.
 * Provides O(1) OrderId lookup for fast Modify/Delete operations.
 *
 * Architecture:
 * - OrderLevel per price: Maintains individual orders with cached aggregates
 * - OrderIndex: Fast OrderId → (Price, Side) lookup
 * - Aggregation: Automatically updates BookLevel on each order change
 */
export class MBOManager {
    private bids = new BookSide();
    private asks = new BookSide();
    private orderIndex: Map<number, { price: number; side: Side }> = new Map(); // OrderId → location

    private bookSide(side: Side): BookSide {
        return side === Side.BID ? this.bids : this.asks;
    }

    /**
     * Process an order add operation.
     * Creates new OrderLevel if price doesn't exist, adds order to level, updates aggregate.
     */
    processOrderAdd(update: OrderUpdate): BookLevel {
        const price = update.price;
        const side = update.side;
        const bookSide = this.bookSide(side);
        const level = bookSide.level(price, side);

        // Add order to level
        const order: Order = {
            orderId: update.orderId,
            quantity: update.quantity,
            priority: update.priority,
            isOwnOrder: update.isOwnOrder ?? false
        };
        level.orders.set(update.orderId, order);

        // Update cached aggregates
        level.totalQuantity += update.quantity;
        level.orderCount++;
        bookSide.levelChanged(level);

        // Index for fast lookup
        this.orderIndex.set(update.orderId, { price, side });

        return level.book;
    }

    /**
     * Process an order modify operation.
     * Updates quantity of existing order, recalculates aggregate.
     */
    processOrderModify(update: OrderUpdate): BookLevel | null {
        // Lookup existing order location
        const location = this.orderIndex.get(update.orderId);
        if (!location) {
            // Unknown order - ignore
            return null;
        }

        const bookSide = this.bookSide(location.side);
        const level = bookSide.levels.get(location.price);
        if (!level) {
            // Level disappeared - should not happen, but handle gracefully
            this.orderIndex.delete(update.orderId);
            return null;
        }

        // Get existing order
        const existingOrder = level.orders.get(update.orderId);
        if (!existingOrder) {
            // Order disappeared - should not happen
            this.orderIndex.delete(update.orderId);
            return null;
        }

        // Update quantity delta
        level.totalQuantity += update.quantity - existingOrder.quantity;

        // Update order (preserve isOwnOrder from existing order)
        level.orders.set(update.orderId, {
            orderId: update.orderId,
            quantity: update.quantity,
            priority: existingOrder.priority,
            isOwnOrder: existingOrder.isOwnOrder
        });
        bookSide.levelChanged(level);

        return level.book;
    }

    /**
     * Process an order delete operation.
     * Removes order from level, removes level if empty.
     */
    processOrderDelete(update: OrderUpdate): BookLevel | null {
        // Lookup and remove from index
        const location = this.orderIndex.get(update.orderId);
        if (!location) {
            // Unknown order - ignore
            return null;
        }
        this.orderIndex.delete(update.orderId);

        const bookSide = this.bookSide(location.side);
        const level = bookSide.levels.get(location.price);
        if (!level) {
            // Level disappeared - should not happen
            return null;
        }

        // Get existing order
        const existingOrder = level.orders.get(update.orderId);
        if (!existingOrder) {
            // Order disappeared
            return null;
        }

        // Update cached aggregates
        level.totalQuantity -= existingOrder.quantity;
        level.orderCount--;
        level.orders.delete(update.orderId);
        bookSide.levelChanged(level);

        // If level empty, remove from book (its BookLevel now has qty=0)
        if (level.orderCount === 0) {
            bookSide.removeLevel(level);
        }
        return level.book;
    }

    /**
     * Process OrderUpdate with specified type. Null when it changed nothing
     */
    processOrderUpdate(update: OrderUpdate, type: OrderUpdateType): BookLevel | null {
        switch (type) {
            case OrderUpdateType.Add:
                return this.processOrderAdd(update);
            case OrderUpdateType.Modify:
                return this.processOrderModify(update);
            case OrderUpdateType.Delete:
                return this.processOrderDelete(update);
            default:
                return null;
        }
    }

    /**
     * Individual orders by price for bid levels (for rendering). The same map every time,
     * brought up to date
     */
    getBidOrders(): Map<number, Order[]> {
        return this.bids.orders();
    }

    /**
     * Individual orders by price for ask levels (for rendering). The same map every time,
     * brought up to date
     */
    getAskOrders(): Map<number, Order[]> {
        return this.asks.orders();
    }

    /**
     * Bid levels ascending by price, for a snapshot (the array stays unchanged)
     */
    getBidLevels(): BookLevel[] {
        return this.bids.sorted.share();
    }

    /**
     * Ask levels ascending by price, for a snapshot (the array stays unchanged)
     */
    getAskLevels(): BookLevel[] {
        return this.asks.sorted.share();
    }

    /**
     * One side's current levels ascending by price, to read now (it changes with the book)
     */
    levels(side: Side): readonly BookLevel[] {
        return this.bookSide(side).sorted.levels;
    }

    /**
     * Reset all state
     */
    reset(): void {
        this.bids = new BookSide();
        this.asks = new BookSide();
        this.orderIndex.clear();
    }

    /**
     * Get number of tracked orders
     */
    getOrderCount(): number {
        return this.orderIndex.size;
    }

    /**
     * Get number of price levels
     */
    getLevelCount(): number {
        return this.bids.levels.size + this.asks.levels.size;
    }
}
