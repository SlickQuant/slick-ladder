import { BookLevel, Order, Side, Trade, WorkingOrders } from './types';

/**
 * Returns the number of decimal places needed to represent a value.
 */
export function getDecimalPlaces(value: number): number {
    if (value >= 1) return 0;
    const str = value.toString();
    const dotIndex = str.indexOf('.');
    if (dotIndex === -1) return 0;
    return str.length - dotIndex - 1;
}

/**
 * Maps a raw price to its display bucket price.
 * Bids use Math.floor (round down); asks use Math.ceil (round up).
 * This prevents bid and ask levels from collapsing into the same bucket
 * when a spread straddles a bucket boundary (e.g. bid=50000.00, ask=50000.05
 * with displayTickSize=0.10 → bid stays at 50000.00, ask moves to 50000.10).
 */
export function bucketPrice(price: number, displayTickSize: number, roundUp: boolean = false): number {
    return snapToBucket(price, displayTickSize, roundUp ? Math.ceil : Math.floor);
}

// A tick count this close to a whole number is that number. Float division puts 100.3 / 0.1 at
// 1002.9999999999999, which Math.floor would round a bucket low
const WHOLE_TICK_EPSILON = 1e-6;

function snapToBucket(price: number, displayTickSize: number, round: (ticks: number) => number): number {
    const ticks = price / displayTickSize;
    const whole = Math.round(ticks);
    const raw = round(Math.abs(ticks - whole) < WHOLE_TICK_EPSILON ? whole : ticks) * displayTickSize;
    return parseFloat(raw.toFixed(getDecimalPlaces(displayTickSize)));
}

// Nearest whole tick, half away from zero like C# MidpointRounding.AwayFromZero. The epsilon
// absorbs float error (100.15 / 0.1 = 1001.4999...)
const roundHalfAway = (ticks: number): number => Math.sign(ticks) * Math.floor(Math.abs(ticks) + 0.5 + 1e-6);

/**
 * Display bucket of a trade's price: a buyer aggressor lifted an ask, so it rounds up like asks,
 * a seller hit a bid and rounds down like bids. Unknown rounds to the nearest bucket.
 * Mirrors PriceLadderViewModel.TradeBucketPrice
 */
export function tradeBucketPrice(trade: Trade, displayTickSize: number): number {
    const round = trade.aggressor === Side.BID ? Math.ceil : trade.aggressor === Side.ASK ? Math.floor : roundHalfAway;
    return snapToBucket(trade.price, displayTickSize, round);
}

/**
 * Working orders summed into display buckets, rounding like the levels of their side
 */
export function aggregateWork(work: Map<number, WorkingOrders>, displayTickSize: number, roundUp: boolean): Map<number, WorkingOrders> {
    const buckets = new Map<number, WorkingOrders>();
    for (const [price, orders] of work) {
        const bp = bucketPrice(price, displayTickSize, roundUp);
        const existing = buckets.get(bp);
        buckets.set(bp, existing
            ? { quantity: existing.quantity + orders.quantity, count: existing.count + orders.count }
            : orders);
    }
    return buckets;
}

/**
 * Aggregates a sorted BookLevel array into coarser display buckets.
 * Quantities and order counts are summed; hasOwnOrders and isDirty are OR'd.
 *
 * Input must be sorted ascending by price (as produced by getSnapshot).
 * Output preserves ascending sort order.
 *
 * No-op when displayTickSize <= tickSize.
 */
export function aggregateLevels(levels: BookLevel[], displayTickSize: number, tickSize: number, roundUp: boolean = false): BookLevel[] {
    if (displayTickSize <= tickSize) return levels;
    const map = new Map<number, BookLevel>();
    for (const level of levels) {
        const bp = bucketPrice(level.price, displayTickSize, roundUp);
        const existing = map.get(bp);
        if (existing) {
            existing.quantity += level.quantity;
            existing.numOrders += level.numOrders;
            existing.hasOwnOrders = existing.hasOwnOrders || level.hasOwnOrders;
            existing.isDirty = existing.isDirty || level.isDirty;
        } else {
            map.set(bp, { ...level, price: bp });
        }
    }
    return Array.from(map.values());
}

// One display bucket's orders: the order arrays of its price levels, nearest the spread first,
// merged into one
type OrderBucket = { sources: Order[][]; orders: Order[]; breaks: number[] };

const NO_BREAKS: number[] = [];

function mergeBucket(sources: Order[][]): OrderBucket {
    if (sources.length === 1) {
        return { sources, orders: sources[0], breaks: NO_BREAKS }; // One level: its own array
    }
    const orders: Order[] = [];
    const breaks: number[] = [];
    for (const levelOrders of sources) {
        if (orders.length > 0) breaks.push(orders.length);
        for (const order of levelOrders) orders.push(order);
    }
    return { sources, orders, breaks };
}

function sameSources(a: Order[][], b: Order[][]): boolean {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
        if (a[i] !== b[i]) return false;
    }
    return true;
}

/**
 * MBO orders merged into display buckets: each bucket shows the orders of every price level in
 * it, the level nearest the spread first (bids from the highest price, asks from the lowest),
 * each in its usual order. breaks holds where each level after the first starts in a bucket's
 * orders (buckets of one level have none). A bucket whose levels' order arrays haven't changed
 * keeps its merged array, and both maps stay the same while no bucket changed, so renderers can
 * tell by reference. Mirrors OrderBuckets.cs
 */
export class OrderBuckets {
    private buckets = new Map<number, OrderBucket>();
    private displayTickSize = 0;
    private orders = new Map<number, Order[]>();
    private breaks = new Map<number, number[]>();

    // roundUp: asks, which bucket up like their levels
    constructor(private readonly roundUp: boolean) {}

    merge(orderMap: Map<number, Order[]>, displayTickSize: number): { orders: Map<number, Order[]>; breaks: Map<number, number[]> } {
        let changed = displayTickSize !== this.displayTickSize;
        if (changed) {
            this.buckets = new Map();
            this.displayTickSize = displayTickSize;
        }

        const pricesByBucket = new Map<number, number[]>();
        for (const price of orderMap.keys()) {
            const bucket = bucketPrice(price, displayTickSize, this.roundUp);
            const prices = pricesByBucket.get(bucket);
            if (prices) prices.push(price); else pricesByBucket.set(bucket, [price]);
        }

        const buckets = new Map<number, OrderBucket>();
        changed ||= pricesByBucket.size !== this.buckets.size;
        for (const [bucketKey, prices] of pricesByBucket) {
            prices.sort(this.roundUp ? (a, b) => a - b : (a, b) => b - a); // Nearest the spread first
            const sources = prices.map((price) => orderMap.get(price)!);
            const previous = this.buckets.get(bucketKey);
            const bucket = previous && sameSources(previous.sources, sources) ? previous : mergeBucket(sources);
            changed ||= bucket !== previous;
            buckets.set(bucketKey, bucket);
        }
        this.buckets = buckets;

        if (changed) {
            this.orders = new Map();
            this.breaks = new Map();
            for (const [bucketKey, bucket] of buckets) {
                this.orders.set(bucketKey, bucket.orders);
                if (bucket.breaks.length > 0) this.breaks.set(bucketKey, bucket.breaks);
            }
        }
        return { orders: this.orders, breaks: this.breaks };
    }
}
