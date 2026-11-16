import { BookLevel } from './types';

/**
 * One side's levels ascending by price, kept in order as levels come and go, so snapshots need
 * no sort. A snapshot shares the array: the next add or remove copies it first
 */
export class SortedLevels {
    private sorted: BookLevel[] = [];
    private shared = false;

    get levels(): readonly BookLevel[] {
        return this.sorted;
    }

    // The array for a snapshot, which it keeps unchanged
    share(): BookLevel[] {
        this.shared = true;
        return this.sorted;
    }

    // Replaces the level already at this price
    add(level: BookLevel): void {
        const index = this.indexOf(level.price);
        const levels = this.writable();
        if (levels[index]?.price === level.price) {
            levels[index] = level;
        } else {
            levels.splice(index, 0, level);
        }
    }

    // A price not in the list changes nothing
    remove(price: number): void {
        const index = this.indexOf(price);
        if (this.sorted[index]?.price === price) {
            this.writable().splice(index, 1);
        }
    }

    // First level priced at or above price
    private indexOf(price: number): number {
        let low = 0;
        let high = this.sorted.length;
        while (low < high) {
            const mid = (low + high) >>> 1;
            if (this.sorted[mid].price < price) {
                low = mid + 1;
            } else {
                high = mid;
            }
        }
        return low;
    }

    private writable(): BookLevel[] {
        if (this.shared) {
            this.sorted = this.sorted.slice();
            this.shared = false;
        }
        return this.sorted;
    }
}
