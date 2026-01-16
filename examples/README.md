# SlickLadder Examples

This directory contains example applications demonstrating SlickLadder usage across different platforms.

## Web Demo

**Location:** `examples/web/`

Demonstrates the web component with TypeScript/WASM hybrid rendering.

### To run:
1. Install dependencies (from root): `npm install`
2. Build WASM module (optional): `npm run build:wasm`
3. Build TypeScript library: `npm run build:lib`
4. Start dev server: `npm run serve:demo`
5. Open browser to http://localhost:9000

### Features:
- TypeScript and WASM engine selection
- Configurable tick size (0.01, 0.05, 0.10, 0.25, 1.00)
- Market data simulation (10 to 10,000 updates/sec)
- Show/hide volume bars and order count
- Level removal modes (remove rows vs. show empty)
- Real-time performance metrics

## WPF Demo

**Location:** `examples/wpf/`

Demonstrates WPF desktop control with SkiaSharp rendering.

### To run:
```bash
dotnet build examples/wpf/SlickLadder.WPF.Demo.csproj
dotnet run --project examples/wpf/SlickLadder.WPF.Demo.csproj
```

### Features:
- High-performance SkiaSharp rendering
- Shared rendering core with Avalonia
- Market data simulation
- Interactive controls for all display options
- Performance metrics (FPS, frame time)

## Avalonia Demo

**Location:** `examples/avalonia/`

Demonstrates cross-platform Avalonia control with SkiaSharp rendering.

### To run:
```bash
dotnet build examples/avalonia/SlickLadder.Avalonia.Demo.csproj
dotnet run --project examples/avalonia/SlickLadder.Avalonia.Demo.csproj
```

### Features:
- Cross-platform (Windows, Linux, macOS)
- High-performance SkiaSharp rendering
- Shared rendering core with WPF
- Market data simulation
- Interactive controls for all display options
- Performance metrics (FPS, frame time)

## Architecture

All examples demonstrate the same core functionality but on different platforms:

- **Web**: TypeScript/Canvas 2D + optional WASM C# core
- **WPF**: C# with SkiaSharp GPU-accelerated rendering (Windows only)
- **Avalonia**: C# with SkiaSharp GPU-accelerated rendering (cross-platform)

The desktop examples share ~95% of their code through the `SlickLadder.Rendering` library.
