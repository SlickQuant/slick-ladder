# SlickLadder

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![npm version](https://img.shields.io/npm/v/@slickquant/slick-ladder.svg)](https://www.npmjs.com/package/@slickquant/slick-ladder)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.0-blue.svg)](https://www.typescriptlang.org/)

Ultra-low latency price ladder component for real-time trading UIs.

SlickLadder renders order book ladders across web, WASM, and desktop (WPF/Avalonia) with a shared core and consistent visuals. It supports both aggregated price levels and full Market-By-Order (MBO) visualization, optimized for high-frequency update streams.

## Table of Contents

- [Features](#features)
- [Platform Support](#platform-support)
- [Quick Start](#quick-start)
- [Installation](#installation)
- [Usage](#usage)
  - [Web (TypeScript)](#web-typescript)
  - [Web (WASM)](#web-wasm)
  - [Desktop (WPF)](#desktop-wpf)
  - [Desktop (Avalonia)](#desktop-avalonia)
- [API Reference](#api-reference)
  - [Configuration Options](#configuration-options)
  - [Methods](#methods)
  - [Types](#types)
  - [Events](#events)
- [Architecture](#architecture)
- [Data Modes](#data-modes)
- [Performance](#performance)
- [Project Structure](#project-structure)
- [Development](#development)
- [Contributing](#contributing)
- [License](#license)

## Features

### Multi-Platform Rendering
- **Web**: TypeScript + Canvas 2D API for native browser performance
- **Web (WASM)**: Optional .NET 8 WebAssembly backend for complex order book logic
- **Desktop**: SkiaSharp GPU-accelerated rendering for WPF and Avalonia

### Dual Data Modes
- **PriceLevel Mode**: Aggregated volume bars per price level (lower memory footprint)
- **MBO Mode**: Individual orders rendered as stacked bars per price level

### High-Performance Pipeline
- Sub-millisecond update processing
- Micro-batched updates to minimize latency
- Dirty region rendering (only redraws changed rows)
- 60 FPS render loop

### Display Features
- Configurable tick sizes (0.01, 0.05, 0.10, 0.25, 1.00)
- **Display tick size aggregation**: group multiple raw price levels into coarser display buckets (e.g. show 0.10 rows when tick size is 0.01)
- Toggle volume bars and order count columns
- Own order highlighting with gold border
- Price level scrolling with mouse wheel (when auto-center is off)
- Auto-center on the bid/ask midpoint, or re-center on demand with Space / middle-click
- Hover highlighting and click interactions

### Built-in Metrics
- Real-time FPS monitoring
- Frame time tracking
- Update count statistics
- Dirty row count per frame

## Platform Support

| Platform | Renderer | Framework | Status |
|----------|----------|-----------|--------|
| Web | Canvas 2D | TypeScript | Stable |
| Web (WASM) | Canvas 2D | .NET 8 WASM | Stable |
| Windows | SkiaSharp | WPF | Stable |
| Windows/Linux/macOS | SkiaSharp | Avalonia | Stable |

## Quick Start

### Prerequisites

- **Node.js 18+** (web builds)
- **.NET 8 SDK** (desktop and WASM builds)

### Web

```bash
# Clone the repository
git clone https://github.com/SlickQuant/slick-ladder.git
cd slick-ladder

# Install dependencies
npm install

# Build and run the web demo
npm run build:lib:full
npm run serve:demo

# Open http://localhost:9000 in your browser
```

### Desktop

```bash
# WPF (Windows only)
dotnet run --project examples/wpf/SlickLadder.WPF.Demo.csproj

# Avalonia (cross-platform)
dotnet run --project examples/avalonia/SlickLadder.Avalonia.Demo.csproj
```

## Installation

### Web (npm)

```bash
npm install @slickquant/slick-ladder
```

### Desktop (NuGet)

```bash
dotnet add package SlickLadder.Avalonia   # Avalonia (Windows, Linux, macOS)
dotnet add package SlickLadder.WPF        # WPF (Windows)
```

Both bring in `SlickLadder.Rendering` and `SlickLadder.Core`. `SlickLadder.Core` alone has the order books and update batching with no UI.

### From Source

```bash
git clone https://github.com/SlickQuant/slick-ladder.git
cd slick-ladder
npm install
npm run build:all
```

## Usage

### Web (TypeScript)

#### Basic Setup

```typescript
import { PriceLadder, Side } from 'slick-ladder';

// Create the ladder
const ladder = new PriceLadder({
    container: document.getElementById('ladder-container')!,
    width: 400,
    height: 600
});

// Process price level updates
ladder.processUpdate({
    side: Side.BID,
    price: 100.25,
    quantity: 1200,
    numOrders: 4
});

ladder.processUpdate({
    side: Side.ASK,
    price: 100.30,
    quantity: 800,
    numOrders: 2
});
```

#### Full Configuration

```typescript
import { PriceLadder, Side, ClickButton, CanvasColors } from 'slick-ladder';

const customColors: Partial<CanvasColors> = {
    background: '#1e1e1e',
    bidBar: '#4caf50',
    askBar: '#f44336'
};

const ladder = new PriceLadder({
    container: document.getElementById('ladder-container')!,
    width: 500,
    height: 800,
    rowHeight: 24,
    visibleLevels: 50,
    tickSize: 0.01,
    mode: 'PriceLevel',
    readOnly: false,
    showVolumeBars: true,
    showOrderCount: true,
    colors: customColors,
    onLevelClick: (price, side, button) => {
        const action = side === Side.ASK ? 'BUY' : 'SELL';
        console.log(`${action} @ ${price} (${ClickButton[button]} click)`);
    },
    onPriceHover: (price) => {
        if (price !== null) {
            console.log(`Hovering: ${price}`);
        }
    }
});

// Get current metrics
const metrics = ladder.getMetrics();
console.log(`FPS: ${metrics.fps}, Updates: ${metrics.updateCount}`);

// Resize dynamically
ladder.resize(600, 900);

// Toggle features
ladder.setShowVolumeBars(false);
ladder.setShowOrderCount(true);

// Clean up when done
ladder.destroy();
```

#### Display Tick Size (Aggregation)

Group raw price levels into coarser display buckets without changing the underlying data:

```typescript
// Create ladder with 0.01 tick size but display at 0.10 granularity
const ladder = new PriceLadder({
    container: document.getElementById('ladder-container')!,
    tickSize: 0.01,
    displayTickSize: 0.10   // Show rows at 0.10 intervals; quantities are summed
});

// Change aggregation at runtime — no need to recreate the ladder
ladder.updateConfig({ displayTickSize: 0.50 });

// Revert to no aggregation
ladder.updateConfig({ displayTickSize: 0.01 });
```

For desktop (WPF / Avalonia), set the property on the ViewModel and viewport:

```csharp
viewModel.DisplayTickSize = 0.10m;           // aggregates data
priceLadder.GetViewport().DisplayTickSize = 0.10m; // updates price grid
```

#### MBO Mode (Market-By-Order)

```typescript
import { PriceLadder, Side, OrderUpdateType } from 'slick-ladder';

const ladder = new PriceLadder({
    container: document.getElementById('ladder-container')!,
    width: 400,
    height: 600,
    mode: 'MBO'
});

// Add individual orders
ladder.processOrderUpdate({
    orderId: 12345,
    side: Side.BID,
    price: 100.25,
    quantity: 500,
    priority: 1
}, OrderUpdateType.Add);

ladder.processOrderUpdate({
    orderId: 12346,
    side: Side.BID,
    price: 100.25,
    quantity: 300,
    priority: 2
}, OrderUpdateType.Add);

// Modify an order
ladder.processOrderUpdate({
    orderId: 12345,
    side: Side.BID,
    price: 100.25,
    quantity: 400,  // Updated quantity
    priority: 1
}, OrderUpdateType.Modify);

// Delete an order
ladder.processOrderUpdate({
    orderId: 12346,
    side: Side.BID,
    price: 100.25,
    quantity: 0,
    priority: 2
}, OrderUpdateType.Delete);

// Switch between modes at runtime
ladder.setDataMode('PriceLevel');
ladder.setDataMode('MBO');
```

#### Batch Processing

```typescript
// PriceLevel mode batch
ladder.processBatch([
    { side: Side.BID, price: 100.25, quantity: 1200, numOrders: 4 },
    { side: Side.BID, price: 100.20, quantity: 800, numOrders: 2 },
    { side: Side.ASK, price: 100.30, quantity: 600, numOrders: 3 }
]);

// MBO mode batch
ladder.processOrderBatch([
    { update: { orderId: 1, side: Side.BID, price: 100.25, quantity: 500, priority: 1 }, type: OrderUpdateType.Add },
    { update: { orderId: 2, side: Side.BID, price: 100.25, quantity: 300, priority: 2 }, type: OrderUpdateType.Add }
]);
```

### Web (WASM)

```typescript
import { WasmPriceLadder } from 'slick-ladder/wasm-adapter';
import { Side } from 'slick-ladder/types';

// Initialize WASM backend
const wasm = new WasmPriceLadder(200, 0.01);
await wasm.waitForReady();

// Process updates (same API as TypeScript)
wasm.processUpdate({
    side: Side.BID,
    price: 100.25,
    quantity: 1200,
    numOrders: 4
});
```

### Desktop (WPF)

```csharp
using SlickLadder.Core;
using SlickLadder.Rendering;
using SlickLadder.WPF;

// In your WPF window
var core = new PriceLadderCore();
var ladderControl = new PriceLadderControl(core);

// Add to your XAML container
myGrid.Children.Add(ladderControl);

// Configure
core.SetDataMode(DataMode.PriceLevel);

// Process updates
core.ProcessPriceLevelUpdate(new PriceLevelUpdate {
    Side = Side.Bid,
    Price = 100.25m,
    Quantity = 1200,
    NumOrders = 4
});

// MBO mode
core.SetDataMode(DataMode.MBO);
core.ProcessOrderUpdate(new OrderUpdate {
    OrderId = 12345,
    Side = Side.Bid,
    Price = 100.25m,
    Quantity = 500,
    Priority = 1
}, OrderUpdateType.Add);

// Auto-center is on by default. Turn it off to scroll freely,
// then Space, middle-click or Recenter() brings the spread back to the middle
ladderControl.GetViewport()!.AutoCenter = false;
ladderControl.Recenter();
```

### Desktop (Avalonia)

```csharp
using SlickLadder.Core;
using SlickLadder.Rendering;
using SlickLadder.Avalonia;

// In your Avalonia window
var core = new PriceLadderCore();
var ladderControl = new PriceLadderControl(core);

// Add to your panel
myPanel.Children.Add(ladderControl);

// Same API as WPF
core.ProcessPriceLevelUpdate(update);
```

## API Reference

### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `container` | `HTMLElement` | **required** | DOM element to render into |
| `width` | `number` | auto | Canvas width in pixels |
| `height` | `number` | `600` | Canvas height in pixels |
| `rowHeight` | `number` | `24` | Height of each price row in pixels |
| `visibleLevels` | `number` | `50` | Number of price levels to track |
| `tickSize` | `number` | `0.01` | Minimum price increment |
| `displayTickSize` | `number` | same as `tickSize` | Display bucket size — must be ≥ `tickSize`. When larger, raw levels are aggregated into coarser rows (quantities summed, `hasOwnOrders` OR'd). |
| `mode` | `'PriceLevel' \| 'MBO'` | `'PriceLevel'` | Data visualization mode |
| `readOnly` | `boolean` | `false` | Disable click interactions |
| `showVolumeBars` | `boolean` | `true` | Show volume bar visualization |
| `showOrderCount` | `boolean` | `true` | Show order count columns |
| `mboOrderSizeFilter` | `number` | `0` | Minimum order size to display in MBO mode |
| `removalMode` | `'showEmpty' \| 'removeRow'` | `'removeRow'` | How to handle empty price levels |
| `autoCenter` | `boolean` | `true` | Keep the bid/ask midpoint on the middle row. Wheel scroll is off while enabled. When disabled, Space or middle-click re-centers (on a qty column, only when `onLevelClick` doesn't handle it) |
| `colors` | `CanvasColors` | (see below) | Custom color scheme |
| `onLevelClick` | `function` | - | Called with the price, side and mouse button when a qty column is clicked. Return `true` if handled |
| `onPriceHover` | `function` | - | Callback when price is hovered |

### Default Colors

```typescript
const DEFAULT_COLORS = {
    background: '#1e1e1e',      // Main background
    bidQtyBackground: '#1a2f3a', // BID quantity column (dark blue)
    askQtyBackground: '#3a1a1f', // ASK quantity column (dark red)
    priceBackground: '#3a3a3a',  // Price column (gray)
    bidBar: '#4caf50',           // BID volume bar (green)
    askBar: '#f44336',           // ASK volume bar (red)
    text: '#e0e0e0',             // Text color
    gridLine: '#444444',         // Grid lines
    ownOrderBorder: '#ffd700',   // Own order highlight (gold)
    hoverBackground: 'rgba(255, 255, 255, 0.1)'
};
```

### Methods

| Method | Parameters | Returns | Description |
|--------|------------|---------|-------------|
| `processUpdate` | `PriceLevel` | `void` | Process a single price level update |
| `processBatch` | `PriceLevel[]` | `void` | Process multiple price level updates |
| `processOrderUpdate` | `OrderUpdate, OrderUpdateType` | `void` | Process a single MBO update |
| `processOrderBatch` | `Array<{update, type}>` | `void` | Process multiple MBO updates |
| `setDataMode` | `'PriceLevel' \| 'MBO'` | `void` | Switch data mode |
| `setShowVolumeBars` | `boolean` | `void` | Toggle volume bars |
| `setShowOrderCount` | `boolean` | `void` | Toggle order count columns |
| `setRemovalMode` | `'showEmpty' \| 'removeRow'` | `void` | Set how empty price levels are displayed |
| `setAutoCenter` | `boolean` | `void` | Toggle auto-center (enabling re-centers immediately) |
| `getAutoCenter` | - | `boolean` | Whether auto-center is on |
| `recenter` | - | `void` | Move the bid/ask midpoint to the middle row once |
| `updateConfig` | `{ tickSize?, displayTickSize? }` | `void` | Live-update tick size or display tick size without recreating the ladder |
| `setReadOnly` | `boolean` | `void` | Enable/disable interactions |
| `resize` | `width?, height?` | `void` | Resize the canvas |
| `getMetrics` | - | `object` | Get performance metrics |
| `getBestBid` | - | `number \| null` | Get current best bid |
| `getBestAsk` | - | `number \| null` | Get current best ask |
| `getMidPrice` | - | `number \| null` | Get current mid price |
| `getSpread` | - | `number \| null` | Get current spread |
| `clear` | - | `void` | Clear all data |
| `destroy` | - | `void` | Clean up resources |

### Types

```typescript
enum Side {
    BID = 0,
    ASK = 1
}

enum ClickButton { // Same values as MouseEvent.button
    Left = 0,
    Middle = 1,
    Right = 2
}

enum OrderUpdateType {
    Add = 0,
    Modify = 1,
    Delete = 2
}

interface PriceLevel {
    side: Side;
    price: number;
    quantity: number;
    numOrders: number;
}

interface OrderUpdate {
    orderId: number;
    side: Side;
    price: number;
    quantity: number;
    priority: number;
}

interface RenderMetrics {
    fps: number;
    frameTime: number;
    dirtyRowCount: number;
    totalRows: number;
}
```

### Events (Web/TypeScript)

#### onLevelClick
Fired when a quantity column is clicked with the left, middle or right button (unless `readOnly` is `true`).

- Clicking on **BID qty column** triggers a BUY
- Clicking on **ASK qty column** triggers a SELL
- Return `true` when you handle the click. A middle-click you don't handle re-centers the ladder, like a middle-click anywhere else.

```typescript
onLevelClick: (price: number | null, side: Side, button: ClickButton) => boolean | void
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `price` | `number \| null` | The price level that was clicked, `null` for an empty row |
| `side` | `Side` | `Side.ASK` for BUY, `Side.BID` for SELL |
| `button` | `ClickButton` | `Left`, `Middle` or `Right` |

#### onPriceHover
Fired when the mouse hovers over a price level.

```typescript
onPriceHover: (price: number | null) => void
```

### Events (Desktop/C#)

#### OnLevelClick
Subscribe to trade click events on the `PriceLadderViewModel`:

```csharp
viewModel.OnLevelClick += (LevelClickEventArgs trade) =>
{
    var action = trade.Side == Side.ASK ? "BUY" : "SELL";
    Console.WriteLine($"{action} @ {trade.Price} ({trade.Button} click)");
};
```

**LevelClickEventArgs Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Price` | `decimal?` | The price level clicked, `null` for an empty row |
| `Side` | `Side` | `Side.ASK` for BUY, `Side.BID` for SELL |
| `Button` | `ClickButton` | `Left`, `Middle` or `Right` |
| `Handled` | `bool` | Set it to `true` when you handle the click. A middle-click left unhandled re-centers the ladder |

## Architecture

```
Market Data Feed
       │
       ▼
┌──────────────┐
│ UpdateBatcher│  Micro-batching for <1ms latency
└──────┬───────┘
       │
       ▼
┌──────────────┐
│  OrderBook   │  Sorted bid/ask levels
└──────┬───────┘
       │
       ▼
┌──────────────────────────────────┐
│ PriceLevelManager │ MBOManager   │  Mode-specific processing
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│   Canvas 2D  │  SkiaSharp        │  Dirty region rendering
└──────────────────────────────────┘
```

### Core Components

- **PriceLadderCore**: Orchestrates order book, batching, and processing
- **OrderBook**: Maintains sorted bid/ask levels with O(log n) operations
- **UpdateBatcher**: Groups updates into micro-batches to minimize latency
- **PriceLevelManager**: Aggregates volume at each price level
- **MBOManager**: Tracks individual orders with priority ordering
- **CanvasRenderer** / **SkiaRenderer**: Platform-specific dirty region rendering

## Data Modes

### PriceLevel Mode (Default)

Aggregates all orders at each price level into a single quantity.

**Characteristics:**
- One row per distinct price (or per display bucket when `displayTickSize > tickSize`)
- Single volume bar per level
- Lower memory footprint
- Suitable for most trading applications

**Visual:**
```
[Orders] [Qty]  [Price]  [Qty] [Orders]  [Volume Bars]
   4     1200   100.25                    ████████
   2      800   100.20                    █████
                100.30    600     3       ████
                100.35    400     2       ███
```

### MBO Mode (Market-By-Order)

Displays individual orders as stacked segments within each price level.

**Characteristics:**
- Per-order visibility
- Stacked bar visualization
- Higher memory usage
- Detailed analysis for market makers

**Visual:**
```
[Orders] [Qty]  [Price]  [Qty] [Orders]  [Volume Bars]
   4     1200   100.25                    ██|███|██|█
   2      800   100.20                    ████|███
                100.30    600     3       ██|███|█
```

## Performance

SlickLadder is designed for high-frequency trading environments:

- **Update Processing**: Sub-millisecond latency
- **Render Loop**: 60 FPS with dirty region optimization
- **Throughput**: 10,000+ updates per second
- **Memory**: Efficient data structures with configurable level limits

### Dirty Region Rendering

Only changed rows are redrawn each frame. Both renderers (web `CanvasRenderer`, desktop `SkiaRenderer`) compare what each row should show with what they last drew, so changes from several flushes between frames all land. Rows that only moved are copied, a volume scale change redraws just the bar column, and a Show Empty Rows center move shifts the pixels. The result is pixel-identical to a full redraw.

Idle ladders skip their frame: the web render loop builds no snapshot while the book and view are unchanged, and desktop hosts check `NeedsRender()`. A host whose surface keeps its pixels between paints (WPF `SKElement`) can call `RenderRetained()` to update it in place without copying a cached frame. `Invalidate()` forces a full redraw.

On a GPU target (Avalonia with GPU rendering) the renderer keeps its frame as a texture on that GPU. Changed rows are drawn into it there, and the frame reaches the window as a GPU copy, so nothing is uploaded. The `GpuTargets_KeepUpAt60Fps` benchmark measures it.

Snapshots are cheap too. Each side's levels stay sorted as levels come and go, so no snapshot sorts, and MBO order maps change only for the levels whose orders changed. Repeating text (prices, quantities, column widths) is formatted and measured once. Each render makes one pass over every level, then touches only the levels it draws.

### Benchmarks

From the multi-ladder perf tests on a Ryzen 9 5900HX laptop. Each ladder has its own feed averaging 1-200 ticks per second with bursts. Frame time covers ingest and render for every ladder, average / p95.

| Scenario | Updates/sec | Web (Node) | Desktop (raster) |
|----------|-------------|------------|------------------|
| 50 ladders, price levels | ~40,000 | 0.7 / 1.4ms | 5.8 / 7.9ms |
| 100 ladders, price levels | ~82,000 | 1.5 / 1.9ms | 11.6 / 16.9ms |
| 50 ladders, MBO | ~39,000 | 2.1 / 2.9ms | 8.3 / 18.9ms |
| 100 ladders, MBO | ~80,000 | 5.9 / 7.8ms | 16.9 / 36.7ms |
| 100 ladders, 500-level books | ~82,000 | 3.3 / 4.4ms | 5.7 / 8.2ms |

The web test draws to a stubbed canvas, so the browser's own drawing isn't in it: in Edge, 100 price-level ladders take ~30ms per frame. On a GPU (Avalonia, each ladder in its own OpenGL surface), 50 and 100 ladders take ~14ms and ~26ms. Run the tests to get numbers for your machine.

## Project Structure

```
slick-ladder/
├── src/
│   ├── core/                          # .NET order book core (WASM-capable)
│   │   ├── SlickLadder.Core.csproj
│   │   ├── PriceLadderCore.cs         # Main orchestrator
│   │   ├── OrderBook.cs               # Sorted price levels
│   │   ├── UpdateBatcher.cs           # Micro-batching
│   │   └── Managers/
│   │       ├── PriceLevelManager.cs
│   │       └── MBOManager.cs
│   │
│   ├── web/                           # TypeScript web component
│   │   ├── package.json
│   │   └── src/
│   │       ├── main.ts                # PriceLadder class
│   │       ├── canvas-renderer.ts     # Canvas 2D rendering
│   │       ├── types.ts               # TypeScript interfaces
│   │       ├── mbo-manager.ts         # MBO state management
│   │       └── wasm-adapter.ts        # WASM integration
│   │
│   └── desktop/
│       ├── SlickLadder.Rendering/     # Shared desktop rendering
│       │   ├── Core/
│       │   │   ├── SkiaRenderer.cs    # SkiaSharp rendering
│       │   │   └── RenderConfig.cs    # Configuration constants
│       │   └── ViewModels/
│       ├── SlickLadder.WPF/           # WPF wrapper
│       └── SlickLadder.Avalonia/      # Avalonia wrapper
│
├── examples/
│   ├── web/                           # Web demo application
│   ├── wpf/                           # WPF demo (Windows)
│   └── avalonia/                      # Avalonia demo (cross-platform)
│
├── scripts/
│   └── build-wasm.js                  # Cross-platform WASM build
│
├── package.json                       # npm workspace config
└── slick-ladder.sln                   # Visual Studio solution
```

## Development

### Building from Source

```bash
# Clone repository
git clone https://github.com/SlickQuant/slick-ladder.git
cd slick-ladder

# Install npm dependencies
npm install

# Build TypeScript library
npm run build:lib

# Build WASM core (requires .NET 8 SDK)
npm run build:wasm

# Build everything
npm run build:all

# Build desktop projects
dotnet build slick-ladder.sln
```

### Running Examples

```bash
# Web demo (http://localhost:9000)
npm run serve:demo

# Web demo with hot reload
npm run dev:demo

# WPF demo (Windows only)
dotnet run --project examples/wpf/SlickLadder.WPF.Demo.csproj

# Avalonia demo (cross-platform)
dotnet run --project examples/avalonia/SlickLadder.Avalonia.Demo.csproj
```

### npm Scripts

| Script | Description |
|--------|-------------|
| `build:wasm` | Build .NET WASM module |
| `build:lib` | Build TypeScript library |
| `build:lib:full` | Build WASM + TypeScript library |
| `build:demo` | Build web demo |
| `build:all` | Build everything |
| `dev:demo` | Run web demo with hot reload |
| `serve:demo` | Run web demo server |
| `test:web` | Run web library unit tests and the multi-ladder perf test (Node test runner, stubbed DOM) |

The multi-ladder perf tests (`src/web/test/multi-ladder.perf.test.js`, `tests/SlickUI.Core.Tests/MultiLadderPerformanceTests.cs`) open 50 and 100 ladders, each fed by its own random-rate source: price levels, MBO orders, and books 500 levels deep. They fail when the average frame is over 16.7ms or p95 is over 1.5 times that. Set `SLICK_PERF_FRAME_BUDGET_MS` to change the budget, or `SLICK_SKIP_PERF=1` to skip the web one. On desktop, 100 MBO ladders and the GPU runs are measured but not held to the budget.

On every push and PR to `main`, CI (`.github/workflows/ci.yml`) runs the web tests on Node 20 and 24 on Linux and Windows, without the perf test. It also builds the .NET solution and runs its unit tests on Windows, skipping the timing-based `*PerformanceTests`.

### Demo Features

The demo applications include:
- Engine selection (TypeScript / WASM)
- Data mode toggle (PriceLevel / MBO)
- Tick size configuration
- Update rate slider (10 - 10,000 updates/sec)
- Volume bars and order count toggles
- Auto-center toggle
- Real-time performance metrics

## Contributing

Contributions are welcome! Please follow these guidelines:

1. **Fork** the repository
2. **Create** a feature branch (`git checkout -b feature/amazing-feature`)
3. **Commit** your changes (`git commit -m 'Add amazing feature'`)
4. **Push** to the branch (`git push origin feature/amazing-feature`)
5. **Open** a Pull Request

### Code Style

- TypeScript: Follow existing patterns, use strict mode
- C#: Follow .NET conventions
- Keep renderers synchronized (see `CLAUDE.md` for details)

### Renderer Synchronization

When modifying rendering code, ensure both implementations stay in sync:
- **Web**: `src/web/src/canvas-renderer.ts`
- **Desktop**: `src/desktop/SlickLadder.Rendering/Core/SkiaRenderer.cs`
- **Config**: `src/desktop/SlickLadder.Rendering/Core/RenderConfig.cs` (source of truth)

## License

MIT License - see [LICENSE](LICENSE) for details.

Copyright (c) 2026 Slick Quant

## Credits

Developed by [SlickQuant](https://github.com/SlickQuant)
