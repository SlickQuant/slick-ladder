/**
 * TypeScript type definitions for SlickUI Price Ladder
 */

export enum Side {
    BID = 0,
    ASK = 1
}

// Mouse button of a level click. Values match MouseEvent.button
export enum ClickButton {
    Left = 0,
    Middle = 1,
    Right = 2
}

export interface BookLevel {
    price: number;
    quantity: number;
    numOrders: number;
    side: Side;
    isDirty: boolean;
    hasOwnOrders: boolean;
}

export interface PriceLevel {
    side: Side;
    price: number;
    quantity: number;
    numOrders: number;
}

export interface Order {
    orderId: number;
    quantity: number;
    priority: number;
    isOwnOrder?: boolean;
}

// Our own working orders at one price: their total quantity and how many there are
export interface WorkingOrders {
    quantity: number;
    count: number;
}

// A trade from the feed. aggressor is the side that crossed the spread (BID = buyer lifted the
// ask, ASK = seller hit the bid), null or missing when unknown
export interface Trade {
    price: number;
    quantity: number;
    aggressor?: Side | null;
}

export interface SegmentRenderState {
    basePixelsPerUnit: number;      // Auto-calculated from max order quantity
    userScaleFactor: number;        // 1.0 default (0.1 to 1000.0 range)
    horizontalScrollOffset: number; // Global scroll in pixels
    maxScrollOffset: number;        // Calculated from widest level
}

export enum OrderUpdateType {
    Add = 0,
    Modify = 1,
    Delete = 2
}

export interface OrderUpdate {
    orderId: number;
    side: Side;
    price: number;
    quantity: number;
    priority: number;
    isOwnOrder?: boolean;
}

export interface OrderBookSnapshot {
    bestBid: number | null;
    bestAsk: number | null;
    midPrice: number | null;
    bids: BookLevel[];
    asks: BookLevel[];
    timestamp: number;

    // MBO mode: Individual orders per price level (null in PriceLevel mode)
    bidOrders?: Map<number, Order[]> | null;
    askOrders?: Map<number, Order[]> | null;

    // Our own working orders by price (Work column) and the last trade (LTQ column)
    bidWork?: Map<number, WorkingOrders> | null;
    askWork?: Map<number, WorkingOrders> | null;
    lastTrade?: Trade | null;
}

export interface PriceLadderConfig {
    container: HTMLElement;
    width?: number;
    height?: number;
    rowHeight?: number;
    visibleLevels?: number;
    tickSize?: number;
    displayTickSize?: number; // Display bucket size >= tickSize. Default: same as tickSize (no aggregation)
    mode?: 'PriceLevel' | 'MBO';
    readOnly?: boolean;
    showVolumeBars?: boolean;
    showOrderCount?: boolean; // Order count in the qty column: "(5) 102" on bids, "102 (5)" on asks. Default: true
    showWorkColumn?: boolean; // Work column left of Bid: our working orders as "10 (2)". Default: true
    showLtqColumn?: boolean; // LTQ column right of Ask: the last trade's quantity on its price. Default: true
    showHeader?: boolean; // Column titles above the rows. Default: true
    mboOrderSizeFilter?: number;
    removalMode?: 'showEmpty' | 'removeRow';
    autoCenter?: boolean; // Keep the bid/ask midpoint on the middle row, default: true. When off, Space or middle-click re-centers
    minQuantityThreshold?: number; // Minimum quantity to display (filters out 0 and near-zero quantities), default: 0.0001
    colors?: Partial<CanvasColors>; // Merged over DEFAULT_COLORS
    onLevelClick?: (price: number | null, side: Side, button: ClickButton) => boolean | void; // Return true when handled. An unhandled middle-click re-centers
    onWorkClick?: (price: number | null, button: ClickButton) => boolean | void; // Work cell clicked, with its row's price. Return true when handled. An unhandled middle-click re-centers
    onPriceHover?: (price: number | null) => void;
}

export interface RenderMetrics {
    fps: number;
    frameTime: number;
    dirtyRowCount: number;
    totalRows: number;
}

export interface WorkerMessage {
    type: 'init' | 'update' | 'snapshot' | 'flush';
    buffer?: SharedArrayBuffer;
    offset?: number;
    length?: number;
    data?: Uint8Array;
}

export interface WorkerResponse {
    type: 'ready' | 'snapshot' | 'error';
    snapshot?: OrderBookSnapshot;
    error?: string;
}

export interface CanvasColors {
    background: string;         // Main background color for all rows
    bidQtyBackground: string;   // BID quantity column background (spans both BID and ASK sections)
    askQtyBackground: string;   // ASK quantity column background (spans both BID and ASK sections)
    priceBackground: string;    // Price column background
    bidBar: string;             // BID (green) volume bar color
    askBar: string;             // ASK (red) volume bar color
    text: string;               // Text color
    gridLine: string;           // Grid line color
    ownOrderBorder: string;     // Border color for own orders
    hoverBackground: string;    // Hover overlay color
    headerBackground: string;   // Header row
    workBackground: string;     // Work column
    ltqBackground: string;      // LTQ column
    ltqBuyBackground: string;   // LTQ cell, buyer was the aggressor
    ltqSellBackground: string;  // LTQ cell, seller was the aggressor
    ltqUnknownBackground: string; // LTQ cell, aggressor unknown
    ltqText: string;            // LTQ cell text
}

export const COL_WIDTH = 66.7;
export const VOLUME_BAR_WIDTH_MULTIPLIER = 2.5;
export const ORDER_SEGMENT_GAP = 2;
export const MIN_ORDER_SEGMENT_WIDTH = 30;  // Increased from 2 for better visibility and quantity text

// New segment rendering configuration
export const DEFAULT_SEGMENT_CONFIG: SegmentRenderState = {
    basePixelsPerUnit: 1.0, // Will be recalculated dynamically
    userScaleFactor: 1.0,
    horizontalScrollOffset: 0,
    maxScrollOffset: 0
};

export const DEFAULT_MBO_ORDER_SIZE_FILTER = 0;
export const DEFAULT_MIN_QUANTITY_THRESHOLD = 0.0001;
export const SEGMENT_SCALE_MIN = 0.1;
export const SEGMENT_SCALE_MAX = 100.0;
export const SEGMENT_SCALE_STEP = 0.2; // Base step for dynamic zoom
export const MIN_SEGMENT_WIDTH_PX = 1; // Minimum rendered width
export const SEGMENT_GAP_PX = 2;
export const MIN_BAR_COLUMN_WIDTH = 100; // Prevent collapse on resize
export const TARGET_MAX_SEGMENT_WIDTH = 200; // Target width for largest order

export const DEFAULT_COLORS: CanvasColors = {
    background: '#1e1e1e',
    bidQtyBackground: '#1a2f3a',  // Dark blue for BID qty column
    askQtyBackground: '#3a1a1f',  // Dark red for ASK qty column
    priceBackground: '#3a3a3a',   // Lighter gray for price column
    bidBar: '#4caf50',
    askBar: '#f44336',
    text: '#e0e0e0',
    gridLine: '#444444',          // More visible grid lines
    ownOrderBorder: '#ffd700',
    hoverBackground: 'rgba(255, 255, 255, 0.1)',
    headerBackground: '#2b2b2b',
    workBackground: '#252525',
    ltqBackground: '#252525',
    ltqBuyBackground: '#4caf50',
    ltqSellBackground: '#f44336',
    ltqUnknownBackground: '#ffffff',
    ltqText: '#000000'
};
