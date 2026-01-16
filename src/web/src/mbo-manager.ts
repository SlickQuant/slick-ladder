import { Side, Order, OrderUpdate, OrderUpdateType, BookLevel, OrderBookSnapshot, DirtyLevelChange } from './types';

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
    public isDirty: boolean;

    constructor(price: number, side: Side) {
        this.price = price;
        this.side = side;
        this.orders = new Map();
        this.totalQuantity = 0;
        this.orderCount = 0;
        this.isDirty = true;
    }

    /**
     * Get all orders as array (for rendering individual orders)
     */
    getOrdersArray(): Order[] {
        return Array.from(this.orders.values());
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
    private bidLevels: Map<number, OrderLevel>;
    private askLevels: Map<number, OrderLevel>;
    private orderIndex: Map<number, { price: number; side: Side }>; // OrderId → location

    private dirtyChanges: DirtyLevelChange[] = [];
    private hasStructuralChange: boolean = false;

    constructor() {
        this.bidLevels = new Map();
        this.askLevels = new Map();
        this.orderIndex = new Map();
    }

    /**
     * Process an order add operation.
     * Creates new OrderLevel if price doesn't exist, adds order to level, updates aggregate.
     */
    processOrderAdd(update: OrderUpdate): BookLevel {
        const price = update.price;
        const side = update.side;
        const levels = side === Side.BID ? this.bidLevels : this.askLevels;

        // Find or create OrderLevel at price
        let level = levels.get(price);
        const existed = !!level;
        if (!level) {
            level = new OrderLevel(price, side);
            levels.set(price, level);
        }

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
        level.isDirty = true;

        // Index for fast lookup
        this.orderIndex.set(update.orderId, { price, side });

        this.recordDirtyChange(price, side, false, !existed);

        // Return aggregated BookLevel
        return {
            price,
            quantity: level.totalQuantity,
            numOrders: level.orderCount,
            side,
            isDirty: true,
            hasOwnOrders: false
        };
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

        const levels = location.side === Side.BID ? this.bidLevels : this.askLevels;
        const level = levels.get(location.price);
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
        const quantityDelta = update.quantity - existingOrder.quantity;
        level.totalQuantity += quantityDelta;
        level.isDirty = true;

        // Update order (preserve isOwnOrder from existing order)
        const modifiedOrder: Order = {
            orderId: update.orderId,
            quantity: update.quantity,
            priority: existingOrder.priority,
            isOwnOrder: existingOrder.isOwnOrder
        };
        level.orders.set(update.orderId, modifiedOrder);

        // Return aggregated BookLevel
        return {
            price: location.price,
            quantity: level.totalQuantity,
            numOrders: level.orderCount,
            side: location.side,
            isDirty: true,
            hasOwnOrders: false
        };
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

        const levels = location.side === Side.BID ? this.bidLevels : this.askLevels;
        const level = levels.get(location.price);
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
        level.isDirty = true;

        const isRemoval = level.orderCount === 0;

        // If level empty, remove from book
        if (isRemoval) {
            levels.delete(location.price);
            this.recordDirtyChange(location.price, location.side, true, false);
            // Return BookLevel with qty=0 to signal removal
            return {
                price: location.price,
                quantity: 0,
                numOrders: 0,
                side: location.side,
                isDirty: true,
                hasOwnOrders: false
            };
        } else {
            this.recordDirtyChange(location.price, location.side, false, false);
            // Return updated BookLevel
            return {
                price: location.price,
                quantity: level.totalQuantity,
                numOrders: level.orderCount,
                side: location.side,
                isDirty: true,
                hasOwnOrders: false
            };
        }
    }

    /**
     * Process OrderUpdate with specified type.
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
     * Get individual orders for bid levels (for rendering).
     * Returns a map of price → Order[]
     */
    getBidOrders(): Map<number, Order[]> {
        const result = new Map<number, Order[]>();
        for (const [price, level] of this.bidLevels) {
            result.set(price, level.getOrdersArray());
        }
        return result;
    }

    /**
     * Get individual orders for ask levels (for rendering).
     * Returns a map of price → Order[]
     */
    getAskOrders(): Map<number, Order[]> {
        const result = new Map<number, Order[]>();
        for (const [price, level] of this.askLevels) {
            result.set(price, level.getOrdersArray());
        }
        return result;
    }

    /**
     * Get aggregated BookLevels for bids (highest to lowest)
     */
    getBidLevels(): BookLevel[] {
        const levels: BookLevel[] = [];
        for (const [price, level] of this.bidLevels) {
            levels.push({
                price: price,
                quantity: level.totalQuantity,
                numOrders: level.orderCount,
                side: Side.BID,
                isDirty: level.isDirty,
                hasOwnOrders: false
            });
        }
        // Sort by price ascending to match OrderBook snapshot ordering
        return levels.sort((a, b) => a.price - b.price);
    }

    /**
     * Get aggregated BookLevels for asks (lowest to highest)
     */
    getAskLevels(): BookLevel[] {
        const levels: BookLevel[] = [];
        for (const [price, level] of this.askLevels) {
            levels.push({
                price: price,
                quantity: level.totalQuantity,
                numOrders: level.orderCount,
                side: Side.ASK,
                isDirty: level.isDirty,
                hasOwnOrders: false
            });
        }
        // Sort by price ascending (lowest first)
        return levels.sort((a, b) => a.price - b.price);
    }

    /**
     * Get current order book snapshot
     */
    getSnapshot(): OrderBookSnapshot {
        const bids = this.getBidLevels();
        const asks = this.getAskLevels();

        const bestBid = bids.length > 0 ? bids[0].price : null;
        const bestAsk = asks.length > 0 ? asks[0].price : null;
        const midPrice = (bestBid !== null && bestAsk !== null) ? (bestBid + bestAsk) / 2 : null;

        const dirtyState = this.consumeDirtyState();

        return {
            bestBid,
            bestAsk,
            midPrice,
            bids,
            asks,
            timestamp: Date.now(),
            bidOrders: this.getBidOrders(),
            askOrders: this.getAskOrders(),
            dirtyChanges: dirtyState.dirtyChanges,
            structuralChange: dirtyState.structuralChange
        };
    }

    /**
     * Reset all state
     */
    reset(): void {
        this.bidLevels.clear();
        this.askLevels.clear();
        this.orderIndex.clear();
        this.dirtyChanges = [];
        this.hasStructuralChange = false;
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
        return this.bidLevels.size + this.askLevels.size;
    }

    consumeDirtyState(): { dirtyChanges: DirtyLevelChange[]; structuralChange: boolean } {
        const dirtyChanges = this.dirtyChanges;
        const structuralChange = this.hasStructuralChange;
        this.dirtyChanges = [];
        this.hasStructuralChange = false;
        return { dirtyChanges, structuralChange };
    }

    private recordDirtyChange(price: number, side: Side, isRemoval: boolean, isAddition: boolean): void {
        if (isRemoval || isAddition) {
            this.hasStructuralChange = true;
        }

        this.dirtyChanges.push({
            price,
            side,
            isRemoval,
            isAddition
        });
    }
}
