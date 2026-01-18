# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.1] - 2026-01-17

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
