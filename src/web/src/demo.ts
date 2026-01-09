import { PriceLadder } from './main';
import { WasmPriceLadder } from './wasm-adapter';
import { CanvasRenderer } from './canvas-renderer';
import { Side, PriceLevel } from './types';

/**
 * Demo application showing SlickUI price ladder in action
 */

let ladder: PriceLadder | WasmPriceLadder | null = null;
let renderer: CanvasRenderer | null = null;
let updateInterval: number | null = null;
let statsInterval: number | null = null;

// Performance tracking
let processingTimes: number[] = [];
let actualUpdateRate = 0;
let lastUpdateCount = 0;
let lastUpdateTime = Date.now();

// Initialize on DOM ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {
    init();
}

async function init() {
    const container = document.getElementById('ladder-container');
    if (!container) {
        console.error('Ladder container not found');
        return;
    }

    // Initialize with TypeScript engine by default
    await initializeBackend('typescript', container);

    // Setup controls
    setupControls();

    // Start stats update
    startStatsUpdate();
}

async function initializeBackend(backend: 'typescript' | 'wasm', container: HTMLElement) {
    // Stop any running updates
    stopMarketUpdates();

    // Destroy existing ladder
    if (ladder) {
        if (ladder instanceof WasmPriceLadder) {
            ladder.destroy();
        } else if (ladder instanceof PriceLadder) {
            ladder.destroy();
        }
        ladder = null;
    }

    // Clear container
    container.innerHTML = '';

    if (backend === 'wasm') {
        try {
            updateElement('backend', 'Initializing...');

            // Get current display settings from checkboxes
            const toggleBarsCheckbox = document.getElementById('toggle-bars') as HTMLInputElement;
            const toggleOrdersCheckbox = document.getElementById('toggle-orders') as HTMLInputElement;
            const showVolumeBars = toggleBarsCheckbox?.checked ?? true;
            const showOrderCount = toggleOrdersCheckbox?.checked ?? false;

            // Get current removal mode from radio buttons
            const removalModeRadio = document.querySelector('input[name="removal-mode"]:checked') as HTMLInputElement;
            const currentRemovalMode = (removalModeRadio?.value as 'showEmpty' | 'removeRow') ?? 'removeRow';

            // Calculate initial canvas width based on display settings
            const COL_WIDTH = 66.7;
            let columnCount = 6;
            if (!showOrderCount) columnCount -= 2;
            if (!showVolumeBars) columnCount -= 1;
            const canvasWidth = Math.round(COL_WIDTH * columnCount);

            // Create canvas for rendering
            const canvas = document.createElement('canvas');
            canvas.width = canvasWidth;
            canvas.height = 600;
            container.appendChild(canvas);

            // Create WASM-backed ladder
            const wasmLadder = new WasmPriceLadder(200);

            // Wait for WASM to be ready
            await wasmLadder.waitForReady();

            // Create renderer with current display settings
            renderer = new CanvasRenderer(canvas, canvasWidth, 600, 24, undefined, showVolumeBars, showOrderCount);

            // Apply current removal mode
            renderer.setRemovalMode(currentRemovalMode);

            // Set up snapshot callback for rendering
            wasmLadder.onSnapshot((snapshot) => {
                renderer?.render(snapshot);
            });

            // Add click handler for trading
            canvas.addEventListener('click', (e) => {
                if (!renderer) return;

                const rect = canvas.getBoundingClientRect();
                const x = e.clientX - rect.left;
                const y = e.clientY - rect.top;

                const rowIndex = renderer.screenYToRow(y);
                const price = renderer.rowToPrice(rowIndex);

                if (price !== null) {
                    const column = renderer.screenXToColumn(x);
                    const bidColumn = renderer.getBidQtyColumn();
                    const askColumn = renderer.getAskQtyColumn();

                    let action: string;

                    if (column === bidColumn) {
                        action = 'SELL';
                    } else if (column === askColumn) {
                        action = 'BUY';
                    } else {
                        return; // Clicked on price column, ignore
                    }

                    console.log(`${action} @ $${price.toFixed(2)}`);
                    alert(`${action} @ $${price.toFixed(2)}`);
                }
            });

            // Add hover handler for tooltip
            canvas.addEventListener('mousemove', (e) => {
                if (!renderer) return;

                const rect = canvas.getBoundingClientRect();
                const y = e.clientY - rect.top;

                const rowIndex = renderer.screenYToRow(y);
                const price = renderer.rowToPrice(rowIndex);

                const tooltip = document.getElementById('tooltip');
                if (tooltip && price !== null) {
                    tooltip.textContent = `Price: $${price.toFixed(2)}`;
                    tooltip.style.display = 'block';
                } else if (tooltip) {
                    tooltip.style.display = 'none';
                }
            });

            canvas.addEventListener('mouseleave', () => {
                const tooltip = document.getElementById('tooltip');
                if (tooltip) {
                    tooltip.style.display = 'none';
                }
            });

            // Add wheel handler for scrolling
            canvas.addEventListener('wheel', (e) => {
                e.preventDefault();
                if (!renderer) return;

                const delta = Math.sign(e.deltaY);
                const mode = renderer.getRemovalMode();

                if (mode === 'removeRow') {
                    // Dense packing mode: row-based scrolling
                    const scrollTicks = delta * 5; // Scroll 5 rows per wheel tick
                    const currentOffset = renderer.getScrollOffset();
                    renderer.setScrollOffset(currentOffset + scrollTicks);
                } else {
                    // Show empty mode: price-based scrolling
                    const scrollTicks = delta > 0 ? -5 : 5; // Inverted for scroll up = higher prices
                    const scrollAmount = scrollTicks * 0.01; // 5 ticks * 0.01 = 0.05 price change
                    renderer.scrollByPrice(scrollAmount);
                }
            }, { passive: false });

            ladder = wasmLadder;
            updateElement('backend', 'WASM Engine');
            console.log('Slick Ladder initialized with WASM engine');
        } catch (error) {
            console.error('Failed to initialize WASM engine:', error);
            alert(`WASM engine initialization failed: ${error instanceof Error ? error.message : String(error)}\n\nFalling back to TypeScript engine.`);

            // Fallback to TypeScript
            await initializeBackend('typescript', container);
            const backendSelect = document.getElementById('backend-select') as HTMLSelectElement;
            if (backendSelect) backendSelect.value = 'typescript';
            return;
        }
    } else {
        // Get current display settings from checkboxes
        const toggleBarsCheckbox = document.getElementById('toggle-bars') as HTMLInputElement;
        const toggleOrdersCheckbox = document.getElementById('toggle-orders') as HTMLInputElement;
        const showVolumeBars = toggleBarsCheckbox?.checked ?? true;
        const showOrderCount = toggleOrdersCheckbox?.checked ?? false;

        // Get current removal mode from radio buttons
        const removalModeRadio = document.querySelector('input[name="removal-mode"]:checked') as HTMLInputElement;
        const currentRemovalMode = (removalModeRadio?.value as 'showEmpty' | 'removeRow') ?? 'removeRow';

        // Calculate initial width based on display settings
        const COL_WIDTH = 66.7;
        let columnCount = 6;
        if (!showOrderCount) columnCount -= 2;
        if (!showVolumeBars) columnCount -= 1;
        const initialWidth = Math.round(COL_WIDTH * columnCount);

        // Create TypeScript ladder
        ladder = new PriceLadder({
            container,
            width: initialWidth,
            height: 600,
            rowHeight: 24,
            readOnly: false,
            showVolumeBars,
            showOrderCount,
            onTrade: (price, side) => {
                const action = side === Side.ASK ? 'BUY' : 'SELL';
                console.log(`${action} @ $${price.toFixed(2)}`);
                alert(`${action} @ $${price.toFixed(2)}`);
            },
            onPriceHover: (price) => {
                const tooltip = document.getElementById('tooltip');
                if (tooltip && price !== null) {
                    tooltip.textContent = `Price: $${price.toFixed(2)}`;
                    tooltip.style.display = 'block';
                } else if (tooltip) {
                    tooltip.style.display = 'none';
                }
            }
        });

        // Apply current removal mode to the internal renderer
        const internalRenderer = (ladder as any).renderer;
        if (internalRenderer && typeof internalRenderer.setRemovalMode === 'function') {
            internalRenderer.setRemovalMode(currentRemovalMode);
        }

        updateElement('backend', 'TypeScript Engine');
        console.log('Slick Ladder initialized with TypeScript engine');
    }

    // Initialize with some data
    initializeOrderBook();
}

function initializeOrderBook() {
    if (!ladder) return;

    const basePrice = 100;
    const levels = 50;

    // Add bid levels
    for (let i = 0; i < levels; i++) {
        const price = basePrice - i * 0.01;
        const qty = Math.floor(1000 + Math.random() * 5000);
        const numOrders = Math.floor(1 + Math.random() * 20);

        ladder.processUpdate({
            side: Side.BID,
            price,
            quantity: qty,
            numOrders
        });
    }

    // Add ask levels
    for (let i = 0; i < levels; i++) {
        const price = basePrice + 0.01 + i * 0.01;
        const qty = Math.floor(1000 + Math.random() * 5000);
        const numOrders = Math.floor(1 + Math.random() * 20);

        ladder.processUpdate({
            side: Side.ASK,
            price,
            quantity: qty,
            numOrders
        });
    }

    console.log('Order book initialized with sample data');
}

function setupControls() {
    const startButton = document.getElementById('start-updates');
    const stopButton = document.getElementById('stop-updates');
    const clearButton = document.getElementById('clear-book');
    const updateRateSelect = document.getElementById('update-rate') as HTMLSelectElement;
    const toggleBarsCheckbox = document.getElementById('toggle-bars') as HTMLInputElement;
    const toggleOrdersCheckbox = document.getElementById('toggle-orders') as HTMLInputElement;
    const backendSelect = document.getElementById('backend-select') as HTMLSelectElement;

    // Backend selection
    backendSelect?.addEventListener('change', async (e) => {
        const backend = (e.target as HTMLSelectElement).value as 'typescript' | 'wasm';
        const container = document.getElementById('ladder-container');
        if (container) {
            await initializeBackend(backend, container);
        }
    });

    startButton?.addEventListener('click', () => {
        const rate = parseInt(updateRateSelect.value);
        startMarketUpdates(rate);
    });

    stopButton?.addEventListener('click', stopMarketUpdates);
    clearButton?.addEventListener('click', clearOrderBook);

    // Toggle volume bars
    toggleBarsCheckbox?.addEventListener('change', (e) => {
        const checked = (e.target as HTMLInputElement).checked;
        if (ladder instanceof PriceLadder) {
            ladder.setShowVolumeBars(checked);
        } else if (renderer) {
            // WASM engine uses CanvasRenderer
            renderer.setShowVolumeBars(checked);
        }
    });

    // Toggle order count
    toggleOrdersCheckbox?.addEventListener('change', (e) => {
        const checked = (e.target as HTMLInputElement).checked;
        if (ladder instanceof PriceLadder) {
            ladder.setShowOrderCount(checked);
        } else if (renderer) {
            // WASM engine uses CanvasRenderer
            renderer.setShowOrderCount(checked);
        }
    });

    // Removal mode switching
    document.querySelectorAll('input[name="removal-mode"]').forEach(radio => {
        radio.addEventListener('change', (e) => {
            const mode = (e.target as HTMLInputElement).value as 'showEmpty' | 'removeRow';
            if (ladder instanceof PriceLadder) {
                // TypeScript engine: ladder has internal renderer
                const internalRenderer = (ladder as any).renderer;
                if (internalRenderer && typeof internalRenderer.setRemovalMode === 'function') {
                    internalRenderer.setRemovalMode(mode);
                }
            } else if (renderer) {
                // WASM engine uses CanvasRenderer directly
                renderer.setRemovalMode(mode);
            }
        });
    });
}

function startMarketUpdates(rate: number) {
    if (updateInterval) {
        clearInterval(updateInterval);
    }

    // Reset performance tracking
    processingTimes = [];
    lastUpdateCount = 0;
    lastUpdateTime = Date.now();

    // Calculate batch size and interval for smooth updates
    // For high rates (>1000/sec), use batching to overcome setInterval limitations
    let batchSize: number;
    let intervalMs: number;

    if (rate <= 100) {
        // Low rates: one update per interval
        batchSize = 1;
        intervalMs = 1000 / rate;
    } else if (rate <= 1000) {
        // Medium rates: small batches at reasonable intervals
        batchSize = Math.ceil(rate / 100);
        intervalMs = 10; // 100 Hz
    } else {
        // High rates: large batches to overcome browser timer limitations
        batchSize = Math.ceil(rate / 60); // Target 60 batches/sec
        intervalMs = 16; // ~60 Hz (one batch per frame)
    }

    console.log(`Starting market updates at ${rate} updates/sec`);
    console.log(`  Batch size: ${batchSize} updates`);
    console.log(`  Interval: ${intervalMs}ms (${(1000 / intervalMs).toFixed(1)} batches/sec)`);
    console.log(`  Expected rate: ${(batchSize * 1000 / intervalMs).toFixed(0)} updates/sec`);

    updateInterval = window.setInterval(() => {
        if (!ladder) return;

        const startTime = performance.now();

        // For WASM, collect updates into a batch array
        const isWasm = ladder instanceof WasmPriceLadder;
        const batchUpdates: PriceLevel[] = [];

        // Generate a batch of updates
        for (let i = 0; i < batchSize; i++) {
            // Generate random update
            const side = Math.random() < 0.5 ? Side.BID : Side.ASK;

            // Get best bid/ask (TypeScript engine only has synchronous access)
            let bestBid: number | null = null;
            let bestAsk: number | null = null;

            if (ladder instanceof PriceLadder) {
                bestBid = ladder.getBestBid();
                bestAsk = ladder.getBestAsk();
            }

            // Generate price based on side to maintain order book integrity
            let price: number;
            if (side === Side.BID) {
                // Bids must be <= best bid (or below best ask if no bids)
                const maxBid = bestBid || (bestAsk ? bestAsk - 0.01 : 100);
                price = Math.round((maxBid - Math.random() * 0.50) * 100) / 100;
            } else {
                // Asks must be >= best ask (or above best bid if no asks)
                const minAsk = bestAsk || (bestBid ? bestBid + 0.01 : 100.01);
                price = Math.round((minAsk + Math.random() * 0.50) * 100) / 100;
            }

            // 10% chance to remove a level (quantity = 0)
            const shouldRemove = Math.random() < 0.1;
            const qty = shouldRemove ? 0 : Math.floor(100 + Math.random() * 10000);
            const numOrders = shouldRemove ? 0 : Math.floor(1 + Math.random() * 30);

            const update: PriceLevel = {
                side,
                price,
                quantity: qty,
                numOrders
            };

            if (isWasm) {
                // Collect for batch sending
                batchUpdates.push(update);
            } else {
                // TypeScript engine: process immediately
                ladder.processUpdate(update);
            }
        }

        // Send batch to WASM worker in one message
        if (isWasm && batchUpdates.length > 0) {
            (ladder as WasmPriceLadder).processBatch(batchUpdates);
        }

        const endTime = performance.now();
        const processingTime = endTime - startTime;

        // Track processing times (keep last 100 samples)
        processingTimes.push(processingTime);
        if (processingTimes.length > 100) {
            processingTimes.shift();
        }
    }, intervalMs);
}

function stopMarketUpdates() {
    if (updateInterval) {
        clearInterval(updateInterval);
        updateInterval = null;
        console.log('Market updates stopped');
    }
}

function clearOrderBook() {
    if (ladder) {
        ladder.clear();
        console.log('Order book cleared');
    }
}

function startStatsUpdate() {
    statsInterval = window.setInterval(updateStats, 100);
}

async function updateStats() {
    if (!ladder) return;

    // Calculate actual update rate
    const now = Date.now();
    const timeDelta = (now - lastUpdateTime) / 1000; // seconds

    if (ladder instanceof WasmPriceLadder) {
        // WASM engine - get metrics asynchronously
        const metrics = await ladder.getMetrics();
        const currentCount = metrics.updateCount || 0;

        if (timeDelta >= 1.0) {
            actualUpdateRate = (currentCount - lastUpdateCount) / timeDelta;
            lastUpdateCount = currentCount;
            lastUpdateTime = now;
        }

        updateElement('fps', '60'); // WASM runs in worker, always 60 FPS for rendering
        updateElement('frame-time', '-'); // Not applicable for WASM
        updateElement('update-count', currentCount.toLocaleString());
        updateElement('bid-levels', metrics.bidLevels?.toString() || '0');
        updateElement('ask-levels', metrics.askLevels?.toString() || '0');

        updateElement('best-bid', metrics.bestBid ? `$${metrics.bestBid.toFixed(2)}` : '-');
        updateElement('best-ask', metrics.bestAsk ? `$${metrics.bestAsk.toFixed(2)}` : '-');

        const spread = (metrics.bestAsk && metrics.bestBid) ? metrics.bestAsk - metrics.bestBid : null;
        updateElement('spread', spread !== null ? `$${spread.toFixed(2)}` : '-');
    } else {
        // TypeScript engine - synchronous
        const metrics = ladder.getMetrics();
        const currentCount = metrics.updateCount;

        if (timeDelta >= 1.0) {
            actualUpdateRate = (currentCount - lastUpdateCount) / timeDelta;
            lastUpdateCount = currentCount;
            lastUpdateTime = now;
        }

        updateElement('fps', metrics.fps.toFixed(0));
        updateElement('frame-time', metrics.frameTime.toFixed(2) + 'ms');
        updateElement('update-count', currentCount.toLocaleString());
        updateElement('bid-levels', metrics.bidLevels.toString());
        updateElement('ask-levels', metrics.askLevels.toString());

        const bestBid = ladder.getBestBid();
        const bestAsk = ladder.getBestAsk();
        const spread = ladder.getSpread();

        updateElement('best-bid', bestBid !== null ? `$${bestBid.toFixed(2)}` : '-');
        updateElement('best-ask', bestAsk !== null ? `$${bestAsk.toFixed(2)}` : '-');
        updateElement('spread', spread !== null ? `$${spread.toFixed(2)}` : '-');
    }

    // Update batch processing metrics
    if (processingTimes.length > 0) {
        const avgProcessingTime = processingTimes.reduce((a, b) => a + b, 0) / processingTimes.length;
        const maxProcessingTime = Math.max(...processingTimes);
        updateElement('avg-processing-time', avgProcessingTime.toFixed(3) + 'ms');
        updateElement('max-processing-time', maxProcessingTime.toFixed(3) + 'ms');
    } else {
        updateElement('avg-processing-time', '-');
        updateElement('max-processing-time', '-');
    }

    // Update actual update rate
    if (actualUpdateRate > 0) {
        updateElement('actual-update-rate', actualUpdateRate.toFixed(0) + '/sec');
    } else {
        updateElement('actual-update-rate', '-');
    }
}

function updateElement(id: string, value: string) {
    const element = document.getElementById(id);
    if (element) {
        element.textContent = value;
    }
}

// Track mouse for tooltip
document.addEventListener('mousemove', (e) => {
    const tooltip = document.getElementById('tooltip');
    if (tooltip && tooltip.style.display === 'block') {
        tooltip.style.left = `${e.pageX + 15}px`;
        tooltip.style.top = `${e.pageY + 15}px`;
    }
});

// Cleanup on unload
window.addEventListener('beforeunload', () => {
    if (updateInterval) clearInterval(updateInterval);
    if (statsInterval) clearInterval(statsInterval);
    if (ladder) ladder.destroy();
});
