import { BookLevel, DirtyLevelChange, Side } from './types';

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
    const raw = (roundUp ? Math.ceil : Math.floor)(price / displayTickSize) * displayTickSize;
    return parseFloat(raw.toFixed(getDecimalPlaces(displayTickSize)));
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

/**
 * Remaps DirtyLevelChange prices to their display bucket prices and deduplicates.
 * Uses side-aware rounding: bids round down, asks round up.
 * No-op when displayTickSize <= tickSize.
 */
export function remapDirtyChanges(
    changes: DirtyLevelChange[],
    displayTickSize: number,
    tickSize: number
): DirtyLevelChange[] {
    if (displayTickSize <= tickSize) return changes;
    const seen = new Set<string>();
    const result: DirtyLevelChange[] = [];
    for (const c of changes) {
        const bp = bucketPrice(c.price, displayTickSize, c.side === Side.ASK);
        const key = `${c.side}_${bp}`;
        if (!seen.has(key)) {
            seen.add(key);
            result.push({ ...c, price: bp });
        }
    }
    return result;
}
