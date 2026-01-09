/**
 * TypeScript type definitions for SlickUI Price Ladder
 */

export enum Side {
    BID = 0,
    ASK = 1
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

export interface OrderBookSnapshot {
    bestBid: number | null;
    bestAsk: number | null;
    midPrice: number | null;
    bids: BookLevel[];
    asks: BookLevel[];
    timestamp: number;
}

export interface PriceLadderConfig {
    container: HTMLElement;
    width?: number;
    height?: number;
    rowHeight?: number;
    visibleLevels?: number;
    tickSize?: number;
    mode?: 'PriceLevel' | 'MBO';
    readOnly?: boolean;
    showVolumeBars?: boolean;
    showOrderCount?: boolean;
    colors?: CanvasColors;
    onTrade?: (price: number, side: Side) => void;
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
}

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
    hoverBackground: 'rgba(255, 255, 255, 0.1)'
};
