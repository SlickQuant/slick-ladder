import { BookLevel, OrderBookSnapshot, OrderUpdate } from './types';

// Worker snapshots are C# objects as JSON: PascalCase, or camelCase from some serializers
const field = (raw: any, pascal: string, camel: string): any => raw[pascal] ?? raw[camel];

const toLevel = (raw: any): BookLevel => ({
    price: field(raw, 'Price', 'price'),
    quantity: field(raw, 'Quantity', 'quantity'),
    numOrders: field(raw, 'NumOrders', 'numOrders') ?? 0,
    side: field(raw, 'Side', 'side'),
    isDirty: field(raw, 'IsDirty', 'isDirty') ?? false,
    hasOwnOrders: field(raw, 'HasOwnOrders', 'hasOwnOrders') ?? false
});

const toOrder = (raw: any): OrderUpdate => ({
    orderId: field(raw, 'OrderId', 'orderId'),
    side: field(raw, 'Side', 'side'),
    price: field(raw, 'Price', 'price'),
    quantity: field(raw, 'Quantity', 'quantity'),
    priority: field(raw, 'Priority', 'priority'),
    isOwnOrder: field(raw, 'IsOwnOrder', 'isOwnOrder') ?? false
});

// Raw orders that toOrder would turn into exactly these, read in place rather than converted
function sameOrders(orders: OrderUpdate[], raw: any[]): boolean {
    if (orders.length !== raw.length) {
        return false;
    }
    for (let i = 0; i < raw.length; i++) {
        const order = orders[i];
        const next = raw[i];
        if (order.orderId !== field(next, 'OrderId', 'orderId') || order.quantity !== field(next, 'Quantity', 'quantity') ||
            order.priority !== field(next, 'Priority', 'priority') ||
            order.isOwnOrder !== (field(next, 'IsOwnOrder', 'isOwnOrder') ?? false) ||
            order.side !== field(next, 'Side', 'side') || order.price !== field(next, 'Price', 'price')) {
            return false;
        }
    }
    return true;
}

function toOrderMap(data: any, previous: Map<number, OrderUpdate[]> | null | undefined): Map<number, OrderUpdate[]> | null {
    if (!data) {
        return null;
    }
    const orderMap = new Map<number, OrderUpdate[]>();
    for (const [priceText, raw] of Object.entries(data) as Array<[string, any[]]>) {
        const price = parseFloat(priceText);
        const last = previous?.get(price);
        orderMap.set(price, last && sameOrders(last, raw) ? last : raw.map(toOrder));
    }
    return orderMap;
}

/**
 * A worker snapshot as an OrderBookSnapshot. A level whose orders match the previous snapshot's
 * keeps the previous array, so the renderer tells it's unchanged by reference instead of
 * comparing every order
 */
export function parseWasmSnapshot(data: any, previous: OrderBookSnapshot | null): OrderBookSnapshot {
    const parsed = typeof data === 'string' ? JSON.parse(data) : data;
    return {
        bestBid: field(parsed, 'BestBid', 'bestBid') ?? null,
        bestAsk: field(parsed, 'BestAsk', 'bestAsk') ?? null,
        midPrice: field(parsed, 'MidPrice', 'midPrice') ?? null,
        bids: (field(parsed, 'Bids', 'bids') ?? []).map(toLevel),
        asks: (field(parsed, 'Asks', 'asks') ?? []).map(toLevel),
        timestamp: field(parsed, 'Timestamp', 'timestamp') ?? Date.now(),
        bidOrders: toOrderMap(field(parsed, 'BidOrders', 'bidOrders'), previous?.bidOrders as Map<number, OrderUpdate[]>),
        askOrders: toOrderMap(field(parsed, 'AskOrders', 'askOrders'), previous?.askOrders as Map<number, OrderUpdate[]>)
    };
}
