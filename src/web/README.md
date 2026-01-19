# @slickquant/slick-ladder

Ultra-low latency price ladder component for web applications.

## Installation

```bash
npm install @slickquant/slick-ladder
```

## Quick Start

### HTML (UMD Bundle)

```html
<!DOCTYPE html>
<html>
<head>
  <title>Price Ladder Example</title>
</head>
<body>
  <div id="ladder-container" style="width: 400px; height: 600px;"></div>

  <!-- Load from CDN or local node_modules -->
  <script src="node_modules/@slickquant/slick-ladder/dist/slick-ladder.js"></script>

  <script>
    // Access via global SlickLadder namespace
    const container = document.getElementById('ladder-container');
    const ladder = new SlickLadder.PriceLadder({ container });

    // Update market data (Side.BID = 0, Side.ASK = 1)
    ladder.processUpdate({ price: 100.5, quantity: 1000, numOrders: 1, side: 0 });
    ladder.processUpdate({ price: 100.4, quantity: 2000, numOrders: 1, side: 0 });
    ladder.processUpdate({ price: 100.6, quantity: 1500, numOrders: 1, side: 1 });
    ladder.processUpdate({ price: 100.7, quantity: 1800, numOrders: 1, side: 1 });
  </script>
</body>
</html>
```

### TypeScript/ES Modules

```typescript
import { PriceLadder, Side } from '@slickquant/slick-ladder';

// Get container element
const container = document.getElementById('ladder-container') as HTMLElement;

// Initialize the ladder
const ladder = new PriceLadder({
  container,
  width: 400,
  height: 600
});

// Update market data using price level updates
ladder.processUpdate({ price: 100.5, quantity: 1000, numOrders: 1, side: Side.BID });
ladder.processUpdate({ price: 100.4, quantity: 2000, numOrders: 1, side: Side.BID });
ladder.processUpdate({ price: 100.6, quantity: 1500, numOrders: 1, side: Side.ASK });
ladder.processUpdate({ price: 100.7, quantity: 1800, numOrders: 1, side: Side.ASK });
```

## Features

- **Ultra-low latency** rendering using HTML Canvas
- **WebAssembly powered** order book processing (optional)
- **Real-time updates** with minimal CPU overhead
- **Customizable appearance** and behavior
- **Interactive** - click to trade

## WebAssembly Mode

The package includes pre-built WASM files for enhanced performance:

```typescript
import { initWasm } from '@slickquant/slick-ladder/wasm-adapter';

// Initialize WASM runtime
await initWasm('/path/to/wasm/files/');

// Now create ladder with WASM support
const ladder = new PriceLadder(canvas, {
  useWasm: true
});
```

## API Documentation

See the [main repository](https://github.com/SlickQuant/slick-ladder) for detailed documentation.

## License

MIT
