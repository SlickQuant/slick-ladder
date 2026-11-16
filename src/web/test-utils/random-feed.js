// Seeded random book feeds shared by the multi-ladder perf test and the incremental render test
const { Side, OrderUpdateType } = require('./ladder-harness');

const TICK_SIZE = 0.01;
const DEPTH = 40; // Levels per side the feed quotes around its mid

// mulberry32: seeded PRNG so every run replays the same feeds
function seededRandom(seed) {
    return () => {
        seed = (seed + 0x6D2B79F5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

// One ladder's data source: a book quoted around a random-walking mid. Ticks arrive with
// exponential gaps at a per-feed rate (1-200/s), each 1-10 level updates or, 2% of the time,
// a 50-200 update burst. Prices are kept in ticks so the reference book compares exactly
class Feed {
    constructor(seed, depth = DEPTH) {
        this.random = seededRandom(seed);
        this.ticksPerMs = (1 + this.random() * 199) / 1000;
        this.nextTickMs = this.gap();
        this.mid = 1000 + this.int(0, 5_000_000);
        this.depth = depth;
        this.bids = new Map(); // tick -> quantity
        this.asks = new Map();
        this.updateCount = 0;
    }

    int(min, max) { return min + Math.floor(this.random() * (max - min + 1)); }
    gap() { return -Math.log(1 - this.random()) / this.ticksPerMs; }

    // Full book on both sides
    seed() {
        const batch = [];
        for (let distance = 1; distance <= this.depth; distance++) {
            batch.push(this.level(Side.BID, this.mid - distance, this.int(1, 10000)));
            batch.push(this.level(Side.ASK, this.mid + distance, this.int(1, 10000)));
        }
        return batch;
    }

    // One batch per tick due by untilMs
    advance(untilMs) {
        const ticks = [];
        for (; this.nextTickMs <= untilMs; this.nextTickMs += this.gap()) {
            ticks.push(this.tick());
        }
        return ticks;
    }

    tick() {
        const batch = [];
        if (this.random() < 0.1) {
            // Keeps bids < mid < asks: clear the level the mid moves onto
            this.mid += this.random() < 0.5 ? -1 : 1;
            batch.push(this.level(Side.BID, this.mid, 0), this.level(Side.ASK, this.mid, 0));
        }
        const count = this.random() < 0.02 ? this.int(50, 200) : this.int(1, 10);
        for (let i = 0; i < count; i++) {
            const side = this.random() < 0.5 ? Side.BID : Side.ASK;
            const distance = this.int(1, this.depth);
            const quantity = this.random() < 0.1 ? 0 : this.int(1, 10000);
            batch.push(this.level(side, side === Side.BID ? this.mid - distance : this.mid + distance, quantity));
        }
        return batch;
    }

    level(side, tick, quantity) {
        const book = side === Side.BID ? this.bids : this.asks;
        if (quantity > 0) book.set(tick, quantity); else book.delete(tick);
        this.updateCount++;
        return { side, price: tick * TICK_SIZE, quantity, numOrders: quantity > 0 ? this.int(1, 30) : 0 };
    }

    bestBid() { return this.bids.size > 0 ? Math.max(...this.bids.keys()) : null; }
    bestAsk() { return this.asks.size > 0 ? Math.min(...this.asks.keys()) : null; }
}

// MBO version of Feed (same rates and mid walk): each tick adds, modifies and deletes orders, 1-5
// per level to start. Batches are { update, type } arrays for processOrderBatch. bids and asks
// hold each level's total quantity
class OrderFeed extends Feed {
    constructor(seed, depth = DEPTH) {
        super(seed, depth);
        this.orders = new Map();                         // orderId -> order
        this.ids = [];                                   // Live order ids, to pick one at random
        this.levelIds = { [Side.BID]: new Map(), [Side.ASK]: new Map() }; // tick -> Set of orderIds
        this.nextId = 1;
    }

    seed() {
        const batch = [];
        for (let distance = 1; distance <= this.depth; distance++) {
            for (let n = this.int(1, 5); n > 0; n--) {
                batch.push(this.add(Side.BID, this.mid - distance), this.add(Side.ASK, this.mid + distance));
            }
        }
        return batch;
    }

    tick() {
        const batch = [];
        if (this.random() < 0.1) {
            // Keeps bids < mid < asks: delete the orders on the level the mid moves onto
            this.mid += this.random() < 0.5 ? -1 : 1;
            for (const side of [Side.BID, Side.ASK]) {
                for (const id of [...(this.levelIds[side].get(this.mid) ?? [])]) batch.push(this.remove(id));
            }
        }
        const count = this.random() < 0.02 ? this.int(50, 200) : this.int(1, 10);
        for (let i = 0; i < count; i++) {
            const op = this.random();
            if (this.ids.length < this.depth * 2 || op < 0.35) {
                const side = this.random() < 0.5 ? Side.BID : Side.ASK;
                const distance = this.int(1, this.depth);
                batch.push(this.add(side, side === Side.BID ? this.mid - distance : this.mid + distance));
            } else if (op < 0.65) {
                batch.push(this.modify(this.ids[this.int(0, this.ids.length - 1)]));
            } else {
                batch.push(this.remove(this.ids[this.int(0, this.ids.length - 1)]));
            }
        }
        return batch;
    }

    add(side, tick) {
        const orderId = this.nextId++;
        const order = { orderId, side, tick, price: tick * TICK_SIZE, quantity: this.int(1, 500), priority: orderId,
            isOwnOrder: this.random() < 0.05 };
        this.orders.set(orderId, order);
        this.ids.push(orderId);
        const ids = this.levelIds[side];
        if (!ids.has(tick)) ids.set(tick, new Set());
        ids.get(tick).add(orderId);
        this.addTotal(order, order.quantity);
        return this.update(order, OrderUpdateType.Add);
    }

    modify(orderId) {
        const order = this.orders.get(orderId);
        const quantity = this.int(1, 500);
        this.addTotal(order, quantity - order.quantity);
        order.quantity = quantity;
        return this.update(order, OrderUpdateType.Modify);
    }

    remove(orderId) {
        const order = this.orders.get(orderId);
        this.orders.delete(orderId);
        const index = this.ids.indexOf(orderId);
        this.ids[index] = this.ids[this.ids.length - 1];
        this.ids.pop();
        const ids = this.levelIds[order.side];
        ids.get(order.tick).delete(orderId);
        if (ids.get(order.tick).size === 0) ids.delete(order.tick);
        this.addTotal(order, -order.quantity);
        return this.update(order, OrderUpdateType.Delete);
    }

    addTotal(order, delta) {
        const book = order.side === Side.BID ? this.bids : this.asks;
        const total = (book.get(order.tick) ?? 0) + delta;
        if (this.levelIds[order.side].has(order.tick)) book.set(order.tick, total); else book.delete(order.tick);
    }

    // A copy: later changes to the order mustn't reach a queued update
    update({ orderId, side, price, quantity, priority, isOwnOrder }, type) {
        this.updateCount++;
        return { update: { orderId, side, price, quantity, priority, isOwnOrder }, type };
    }
}

module.exports = { Feed, OrderFeed, seededRandom, TICK_SIZE };
