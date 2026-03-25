# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
