# SlickLadder

Ultra-low latency price ladder component for real-time trading UIs.

SlickLadder renders order book ladders across web, WASM, and desktop (WPF/Avalonia) with a shared core and consistent visuals. It supports both aggregated price levels and full Market-By-Order (MBO) visualization, optimized for high-frequency update streams.

## Highlights

- Multi-platform: Web (TypeScript + Canvas 2D), optional WASM core, WPF, Avalonia
- Dual data modes: PriceLevel (aggregated) and MBO (per order)
- High throughput pipeline: micro-batched updates and dirty region rendering
- Cross-platform renderers: Canvas 2D for web, SkiaSharp for desktop
- Built-in metrics: FPS, frame time, update counts

## Architecture (high level)

```
Market Data Feed
  -> UpdateBatcher
  -> OrderBook
  -> (PriceLevelManager | MBOManager)
  -> Renderer (Canvas 2D | SkiaSharp)
```

### Core components

- PriceLadderCore: orchestrates order book, batching, and processing
- OrderBook: sorted bid/ask levels and snapshots
- UpdateBatcher: micro-batching to keep latency low
- PriceLevelManager: aggregated price level updates
- MBOManager: per-order updates at each price level
- Renderers: Canvas 2D for web, SkiaSharp for WPF/Avalonia

## Data modes

### PriceLevel mode (default)
- Aggregated volume bars per price level
- One row per price
- Lowest memory footprint

### MBO mode (Market-By-Order)
- Individual orders rendered as stacked bars per price level
- Per-order visibility for detailed analysis
- Higher memory use than PriceLevel mode

## Getting started

### Prerequisites

- .NET 8 SDK (desktop demos and WASM build)
- Node.js 18+ (web build and demos)
- Visual Studio 2022 or VS Code (optional)

### Install dependencies

```bash
npm install
```

### Build web library and demos

```bash
npm run build:lib
npm run build:demo
```

### Build the WASM core

```bash
npm run build:wasm
```

This runs `dotnet publish` for `src/core/SlickLadder.Core.csproj` and copies the WASM output to `examples/web/public/wasm`.

### Run demos

```bash
# Web demo (webpack dev server)
npm run serve:demo

# WPF demo
dotnet run --project examples/wpf/SlickLadder.WPF.Demo.csproj

# Avalonia demo
dotnet run --project examples/avalonia/SlickLadder.Avalonia.Demo.csproj
```

See `examples/README.md` for demo details and feature lists.

## Usage (web)

```typescript
import { PriceLadder } from 'slick-ladder';
import { Side, OrderUpdateType } from 'slick-ladder/types';

const ladder = new PriceLadder({
    container: document.getElementById('ladder-container')!,
    width: 600,
    height: 400,
    mode: 'PriceLevel' // or 'MBO'
});

ladder.processUpdate({
    side: Side.BID,
    price: 100.25,
    quantity: 1200,
    numOrders: 4
});

// MBO updates (mode: 'MBO')
ladder.processOrderUpdate({
    orderId: 12345,
    side: Side.ASK,
    price: 100.30,
    quantity: 150,
    priority: 999
}, OrderUpdateType.Add);
```

### WASM backend (web)

```typescript
import { WasmPriceLadder } from 'slick-ladder/wasm-adapter';
import { Side } from 'slick-ladder/types';

const wasm = new WasmPriceLadder(200, 0.01);
await wasm.waitForReady();

wasm.processUpdate({
    side: Side.BID,
    price: 100.25,
    quantity: 1200,
    numOrders: 4
});
```

## Usage (desktop)

```csharp
var core = new PriceLadderCore();
var ladder = new PriceLadderControl(core);

core.SetDataMode(DataMode.MBO); // or DataMode.PriceLevel
core.ProcessPriceLevelUpdate(update);
core.ProcessOrderUpdate(update, OrderUpdateType.Add);
```

## Project structure

```
src/core/           .NET order book core (WASM-capable)
src/web/            TypeScript web component + WASM adapter
examples/web/       Web demo app
examples/wpf/       WPF demo app
examples/avalonia/  Avalonia demo app
scripts/build-wasm.js
```

## Performance notes

- Designed for sub-millisecond update processing in the core pipeline
- 60 FPS render loop with dirty row tracking
- MBO mode is heavier than PriceLevel mode due to per-order storage

## License

ISC License

## Credits

SlickQuant
