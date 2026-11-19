# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **NuGet packages**: `SlickLadder.Core`, `SlickLadder.Rendering`, `SlickLadder.Avalonia` and `SlickLadder.WPF`, released with the npm package at the same version and published to nuget.org with trusted publishing. They come with Source Link and `.snupkg` symbols, and the GitHub release attaches both the packages and the symbols.
- **Level click button**: `onLevelClick` gets the mouse button as a third argument, and desktop's `LevelClickEventArgs` has a `Button` property. Both use the new `ClickButton` enum (`Left`, `Middle`, `Right`). Web now reports middle and right clicks too.
- Level clicks can be marked handled: return `true` from `onLevelClick`, or set `LevelClickEventArgs.Handled` on desktop. A middle-click on a qty column still re-centers unless something handles it.

### Changed
- Desktop: `PriceLadderViewModel.HandlePriceClick` takes an optional button and returns whether a handler set `Handled`. Back/forward mouse buttons no longer fire `OnLevelClick`.
- Web: a middle-click on a qty column that nothing handles re-centers on release instead of press.

### Fixed
- **Avalonia on Linux**: Avalonia.Skia pulled in the 2.88 Linux native SkiaSharp library, which doesn't match SkiaSharp 3.119.1. `SlickLadder.Avalonia` now references `SkiaSharp.NativeAssets.Linux` 3.119.1, so the Linux demo and apps using the package get a matching library.

## [0.4.0] - 2026-11-17

### Added
- Multi-ladder performance tests (web and .NET): 50 and 100 ladders, each with its own random-rate feed, on a simulated 60 FPS clock, for price levels, MBO orders and books 500 levels deep. They check every ladder's book against its feed and fail when the average frame goes over 16.7ms or p95 over 1.5 times that (`SLICK_PERF_FRAME_BUDGET_MS` overrides). CI skips them like the other timing tests.
- Desktop: `SkiaRenderer.NeedsRender()` (false when the snapshot and view haven't changed), `RenderRetained()` (update a surface that keeps its pixels between paints, without the cache copy) and `Invalidate()`
- Web: `CanvasRenderer.needsRender()` and `recordFrame()` (counts a frame for `getMetrics().fps`, rendered or skipped)
- Desktop GPU benchmark (`GpuTargets_KeepUpAt60Fps`): the multi-ladder scenario with each ladder drawn into its own OpenGL surface, like one Avalonia window per ladder on a GPU, with GPU time reported separately. It checks the books but not the frame budget.
- Desktop: `PriceLadderViewModel` is `IDisposable`. Disposing it stops snapshots and completes `Spread`, `BidLevelCount` and `AskLevelCount`, and it's safe while a feed thread is flushing.
- Desktop: `RenderMetrics.RecordFrame(long timestamp)` for a caller that has its own frame time (a `Stopwatch` timestamp)

### Changed
- **Desktop: much faster with many ladders**: 100 ladders went from ~53ms to ~10ms per frame in the multi-ladder test.
  - `SkiaRenderer` keeps what each row shows and redraws only rows that differ. Rows that only moved are copied, a volume scale change redraws just the bar column, and a Show Empty Rows center move shifts the pixels. Text blobs are cached and grid lines are filled rects. Output is pixel-identical to a full redraw.
  - The WPF and Avalonia controls repaint only when `NeedsRender()` says so, and the WPF control renders straight into its `SKElement` bitmap with `RenderRetained()`
  - `PriceLadderViewModel` pushes `Spread`, `BidLevelCount` and `AskLevelCount` only when they change. `WhenAnyValue` ran ReactiveUI's change pipeline three times per flush, most of the ingest cost.
  - Demo FPS counts UI frames, so it stays at the display rate while idle ladders skip their repaint
  - One pass over each side's levels per render finds the largest quantity and whether the levels are sorted. Show Empty Rows then reads only the levels around the visible prices.
  - The segment scroll limit is found when dragging instead of every frame, which walked every order. 100 MBO ladders render in ~10ms instead of ~11ms.
  - Text is drawn with `SKFont`, replacing `SKPaint`'s obsolete text properties. The pixels are identical.
  - Prices, quantities and order counts are formatted once and reused, and an MBO side whose orders didn't change isn't scanned again for its largest order
  - `RenderMetrics.RecordFrame()` keeps a running sum instead of averaging its whole window every frame
- **Desktop: GPU frames stay on the GPU**: on a GPU canvas (Avalonia) `SkiaRenderer` keeps its frame as a texture there and draws changed rows into it, so no frame is uploaded. 100 ladders, each in its own window, went from ~47ms to ~30ms per frame in the GPU benchmark. A renderer disposed off its render thread, or never disposed, leaves the texture for the next render on that GPU to free.
- **Web: rows compared like desktop**: `CanvasRenderer` keeps what each row shows and redraws only rows that differ, with the same row moves, bar-column redraws and Show Empty Rows shifts as `SkiaRenderer`. A volume scale change no longer redraws the whole ladder. Grid lines are 1px rects, as on desktop. Output matches a full redraw pixel for pixel (checked in Edge). 100 active ladders went from ~64ms to ~42ms per frame in Edge.
- **Web: idle frames skipped**: the render loop builds a snapshot only when the book or view changed, so 100 idle ladders cost ~0.03ms per frame instead of ~2ms. `MBOManager` keeps a level's order array until its orders change.
- **Web: cheaper snapshots and text**: each side's levels stay sorted as levels come and go, so snapshots don't sort, and levels are updated in place. MBO order maps change only for levels whose orders changed. Show Empty Rows places each level by its price instead of looking rows up in a map of formatted prices. Integer quantities are grouped by hand rather than through `Intl`, and price labels, column widths and column positions are cached. A render reads every level once (largest quantity, price column width, sort check), and Show Empty Rows binary-searches the levels around the visible prices. In the Node perf test, 100 ladders went from ~7ms to ~1.5ms per frame, 100 MBO ladders from ~17ms to ~6ms, and 100 books 500 levels deep from ~42ms to ~3.3ms. In Edge, 100 ladders went from ~40ms to ~30ms, and the rest is canvas drawing.
  - `needsRender()` compares the view with the last render, like desktop `NeedsRender()`
  - WASM snapshots keep a level's previous order array when its orders didn't change, so the renderer can tell by reference. The check reads the worker's orders in place instead of building each one.
  - A queue of 8 or more orders keeps its largest order until the queue changes, so deep queues aren't scanned every frame for the segment scale

### Removed
- **Dirty-change hints on snapshots**: web `OrderBookSnapshot.dirtyChanges`, `structuralChange` and `DirtyLevelChange`, and on .NET `OrderBookSnapshot.DirtyChanges`, `StructuralChange`, `DirtyLevelChange`, `OrderBook.GetDirtyChanges()` and `HasStructuralChange`. Both renderers compare rows themselves, so nothing read them, and the WASM worker no longer writes them into every snapshot. Code that read them on web now gets `undefined`.

### Fixed
- **Web: slow rendering with many ladders**: `formatQuantity()` called `toLocaleString()` with options, which builds a new `Intl.NumberFormat` for every quantity cell. The formatters are now built once and give the same text. Rendering 100 ladders went from ~104ms to ~7ms per frame (Node, no-op canvas).
- **Desktop: a row change lost between frames**: each flush's snapshot lists only its own changed rows, so with two flushes before a frame the first one's row kept its old value
- **Stale volume bars** (web and desktop) when the largest quantity changed, since only the updated row was redrawn. On web, MBO order segments had the same problem when the largest order changed.
- **Web: MBO order modify not shown**: `MBOManager` recorded no dirty change for a modify, so the order's row kept its old segments until something else repainted it
- **Desktop: Show Empty Rows price off-center** on a row redrawn by itself (centered on the default column width, not the price column)
- **Desktop: own-order border cut** when the row above it was redrawn
- **Web: `setHasOwnOrders()` not shown** until something else redrew the row, since it changed the level in place without marking it
- **Web: blank ladder after a container resize**: setting the canvas width cleared it, then only rows with changes were drawn
- **Web: Show Empty Rows dropped levels sharing a row**: a bid and an ask on one price showed only the bid, and a second price rounding to the same row was lost. All of them are drawn now, as on desktop.
- **Web: segment drag stopped short**: the drag limit was updated only when the segment scale changed by 20% or more, so a level that grew wider couldn't be dragged to its end. Both platforms now find the limit when dragging.
- **Desktop: Remove Rows prices** always had two decimals and were centered on the default column width. They now use the display tick's decimals and the price column, like Show Empty Rows and web.
- **Price column too wide on deep books** (web and desktop): it fit the highest price anywhere in the book, so a full exchange book (Coinbase BTC-USD quotes asks out to 138,991,023.41) got room for 12 characters when the screen needed 8. It now fits the highest and lowest prices on the rows drawn, and follows scrolling and re-centering. A book entirely below zero no longer gets the default width, which clipped its prices.

## [0.3.0] - 2026-11-14

### Added
- **Auto-center (default on)**: keeps the bid/ask midpoint on the ladder's middle row. In Remove Rows mode the best bid sits on the middle row with the best ask right above. Falls back to the best bid or ask when only one side exists. A half-tick midpoint rounds away from zero on both platforms.
  - Web: `autoCenter` config option, `setAutoCenter()`, `getAutoCenter()`, `recenter()`
  - Desktop: `ViewportManager.AutoCenter`, `ViewportManager.Recenter(snapshot)`, `PriceLadderControl.Recenter()`
- **Manual re-center**: with auto-center off, press Space or click the middle mouse button on the ladder. A mouse press on the ladder gives it keyboard focus.
- "Auto Center" checkbox in the Web, WPF and Avalonia demos
- Web unit tests: `npm run test:web` (Node's built-in test runner against a stubbed DOM, no new dependencies)
- CI workflow (`ci.yml`): web tests and .NET build + unit tests on every push and PR to `main`

### Changed
- **Behavior change: vertical wheel scroll is off by default**. Auto-center is on by default and pins the view, so the wheel no longer scrolls the ladder. The ladder still consumes the wheel event, so a surrounding scroll container doesn't scroll either. Set `autoCenter: false` (web) or `ViewportManager.AutoCenter = false` (desktop) to get the old behavior back.

### Fixed
- **Web: removal mode and scroll position lost on column toggle**: `setShowVolumeBars()` and `setShowOrderCount()` recreate the renderer, which dropped its removal mode (Show Empty Rows fell back to Remove Rows) and its scroll position. `setRemovalMode()` now records the mode in config, and `createRenderer()` re-applies it and copies the view position from the old renderer (WASM ladder included).
- **Web: `destroy()` left canvas listeners attached**: it removed the unbound handler methods instead of the bound listeners it had added. It now keeps a list of the bound listeners and removes those.

## [0.2.0] - 2026-04-09

### Added
- **`displayTickSize` config option (price level aggregation)**: When `displayTickSize > tickSize`, the ladder aggregates raw price levels into coarser display buckets before rendering. Quantities and order counts are summed; `hasOwnOrders` and `isDirty` are OR'd across merged levels.
  - Web (`aggregation.ts`): Pure `bucketPrice`, `aggregateLevels`, `remapDirtyChanges` utilities applied in `main.ts` and `wasm-adapter.ts` at snapshot time — raw data stores are unchanged
  - Desktop (`PriceLadderViewModel.cs`): `AggregateLevels` applied in `OnSnapshotReady` before the snapshot reaches the renderer; `ViewportManager.DisplayTickSize` drives the price grid step size
  - `PriceLadder.updateConfig({ displayTickSize })` live-updates aggregation without recreating the ladder (web)
  - Bids bucket with `Math.floor`, asks with `Math.ceil` — prevents bid and ask from collapsing into the same row when the spread straddles a bucket boundary
  - Demo controls added to Web, WPF, and Avalonia demos (Display Tick Size dropdown with options: same as tick size, 0.05, 0.10, 0.25, 0.50, 1.00, 5.00)

### Fixed
- **Show Empty Rows + aggregation (desktop)**: The show-empty-rows price grid was generated at raw `tickSize` intervals even when `displayTickSize` was set coarser. Empty rows now step at `displayTickSize` intervals, matching web behaviour.

## [0.1.8] - 2026-04-06

### Added
- **`hasOwnOrders` highlighting in PriceLevel mode**: When a `BookLevel` has `hasOwnOrders = true`, the quantity cell is highlighted with a 2px gold border (`#ffd700`) — consistent with the existing MBO own-order highlight style
  - Web (`canvas-renderer.ts`): `strokeRect` on bid/ask quantity cell when `level.hasOwnOrders` is true
  - Desktop (`SkiaRenderer.cs`): `DrawRect` with `_ownOrderBorderPaint` on bid/ask quantity cell when `level.HasOwnOrders` is true
- **Own-order flag preservation**: `hasOwnOrders`/`HasOwnOrders` is now preserved across market data updates (previously reset to `false` on every update)
  - Web: `processUpdate()` in `main.ts` carries the flag forward from the existing level
  - Desktop: `OrderBook.UpdateLevel()` preserves the flag from the existing level in the sorted array
- **`setHasOwnOrders` / `SetHasOwnOrders` API**:
  - Web (`PriceLadder`): `setHasOwnOrders(price, side, value)` mutates the level in the local bid/ask map
  - Web (`WasmPriceLadder`): overrides with an overlay map applied to every WASM snapshot, so the flag survives worker-driven updates
  - Desktop (`PriceLadderCore`): `SetHasOwnOrders(price, side, value)` updates the `OrderBook` and immediately emits a fresh snapshot via `UpdateBatcher.EmitCurrentSnapshot()`
- **Demo: click-to-mark own order** (Web, WPF, Avalonia): clicking the bid/ask quantity cell of a price level, after closing the confirmation pop-up, marks that level with `hasOwnOrders = true`; a random timer (2–8 s) then clears the flag to simulate order removal

### Changed
- Renamed `onTrade` to `OnLevelClick`. Renamed `TradeRequest` to `LevelClickEventArgs`

## [0.1.7] - 2026-03-30

### Fixed
- **Inverted trade side on click**: Clicking the BID quantity column now correctly fires `Side.BID` (BUY) and ASK column fires `Side.ASK` (SELL) — was reversed across all platforms (Web, WPF, Avalonia)
- **Empty row clicks now invoke `onTrade`**: Clicking a quantity column on an empty row previously silently returned; now fires the callback with `price = null` so consumers can handle it explicitly
  - `TradeRequest.Price` is now `decimal?` in C#; `onTrade` signature is `(price: number | null, side: Side)` in TypeScript
- **`screenXToColumn` ignores dynamic column widths**: Fixed hit detection to use actual pixel positions of bid/ask quantity columns instead of a fixed `Math.floor(x / COL_WIDTH)` calculation

## [0.1.6] - 2026-03-25

### Fixed
- **RemoveRow mode structural changes**: Simplified rendering logic for add/remove operations to ensure correctness
  - Structural changes (add/remove levels) now trigger full redraw instead of complex incremental dirty tracking
  - Fixes stale rendering when levels are added (e.g., new limit order) or removed (e.g., market order fills)
  - Fixes duplicate price levels appearing after partial fills until scroll
  - Removed complex `estimateRowForRemovedLevel` logic in favor of reliable full redraw approach
  - Full redraws are still very fast (< 1ms) and only triggered on structural changes, not quantity updates
  - Added dirty change deduplication to prevent duplicate processing within same frame
  - Added explicit sorting after deduplication in `buildDensePackingLayout` to ensure correct price ordering
  - Fixed off-by-one error in `markRowDirty` bounds check (`rowIndex >= visibleRows` instead of `>`)
  - Synchronized across TypeScript (`canvas-renderer.ts`) and C# (`SkiaRenderer.cs`) renderers

## [0.1.5] - 2026-03-25

### Fixed
- **RemoveRow mode stale rendering**: Fixed critical bug where rows below a removed level weren't redrawn, causing stale data to display until scroll
  - When a level is removed (e.g., fully consumed by market order), all rows below now properly shift up and redraw immediately
  - Added `estimateRowForRemovedLevel` helper function to calculate affected row range after level removals
  - Structural changes (add/remove) now correctly mark all impacted rows as dirty for incremental redraw
  - Synchronized fix across both TypeScript (`canvas-renderer.ts`) and C# (`SkiaRenderer.cs`) renderers

## [0.1.4] - 2026-03-24

### Fixed
- **RemoveRow mode duplicate levels**: Fixed rendering bug where duplicate price levels could appear after partial fills in MBO mode (e.g., same price showing both old and new quantities)
- Added deduplication logic in both TypeScript and C# renderers to ensure only one row per price level
- MBO manager now includes defensive duplicate detection with console warnings

## [0.1.3] - 2026-03-22

### Added
- **Dynamic column auto-sizing**: Price and quantity columns now automatically resize based on content across all platforms (Web, WPF, Avalonia)
- **Min Quantity Threshold**: Configurable threshold with visual indicator - quantities below threshold display as `<0.0001`
- **Decimal quantity support**: Full decimal precision support with smart formatting based on threshold value
- Min Quantity Threshold and MBO Order Filter controls in all demo applications

### Fixed
- Avalonia demo input controls: Improved hover/focus visibility with light background colors
- Cross-platform consistency: Synchronized decimal formatting between TypeScript and C# implementations

## [0.1.2] - 2026-03-21

### Added
- Shift+Wheel zoom and Mouse Drag horizontal scroll interaction features
- Dynamic zoom scale functionality
- MBO order size filter capability
- `removalMode` configuration option in `PriceLadderConfig` for web implementation

### Changed
- Exposed MBO order filter in demo applications
- `removalMode` can now be set during PriceLadder initialization via config
- Updated README.md documentation with `removalMode` and `mboOrderSizeFilter` options

## [0.1.1] - 2026-01-17

### Added
- GitHub Action release workflow
- Added CHANGELOG.md

### Changed
- Updated NPM release command for public access publishing

### Fixed
- Preparation for npm publishing

## [0.1.0] - 2026-01-17

### Added

#### Core Features
- Ultra-low latency price ladder component with dual rendering modes (TypeScript Canvas & WASM)
- Market-by-Order (MBO) support across all platforms (Web, WPF, Avalonia)
- Configurable tick size for both Web and Desktop implementations
- Click-to-trade functionality
- Own order marking/highlighting feature
- Volume bar visualization for bid/ask quantities
- Dirty row optimization for efficient partial redraws
- Debug overlay for dirty row visualization

#### Platform Support
- **Web**: TypeScript Canvas 2D renderer with WASM acceleration option
- **Desktop WPF**: C# SkiaSharp-based renderer for Windows
- **Desktop Avalonia**: Cross-platform C# implementation (Windows, macOS, Linux)

#### Developer Experience
- Cross-platform WASM build automation via `npm run build:wasm` (replaces platform-specific scripts)
- Synchronized rendering between TypeScript and C# implementations
- Workspace-based monorepo structure
- NPM test suite for integration testing
- Demo applications for Web, WPF, and Avalonia

### Changed
- Reorganized demo apps into `examples/` folder for better project structure
- Improved ladder font size consistency across platforms
- Enhanced performance with dirty region drawing strategy
- Tightened dirty-row detection for structural changes (add/remove operations)

### Technical Details
- Dual rendering engine: TypeScript (Canvas 2D API) and C# (SkiaSharp)
- Shared configuration source of truth in `RenderConfig.cs`
- Optimized rendering with row-level dirty tracking
- Custom scrolling implementation with removal mode support
- Performance tracking and metrics

---

## Release Types

- **Added** for new features
- **Changed** for changes in existing functionality
- **Deprecated** for soon-to-be removed features
- **Removed** for now removed features
- **Fixed** for any bug fixes
- **Security** in case of vulnerabilities

[Unreleased]: https://github.com/SlickQuant/slick-ladder/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/SlickQuant/slick-ladder/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/SlickQuant/slick-ladder/releases/tag/v0.1.0
