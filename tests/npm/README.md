# npm Package Test

This directory tests the published `@slickquant/slick-ladder` npm package.

## Setup

```bash
# Install dependencies (including http-server)
npm install
```

## Running Tests

### 1. Node.js Structure Test

Tests package structure, files, and exports:

```bash
npm test
```

This verifies:
- ✓ Package is installed correctly
- ✓ Main UMD bundle exists
- ✓ TypeScript definitions are present
- ✓ WASM files are included
- ✓ README and documentation exist
- ✓ Package size is reasonable

### 2. Browser Integration Test

Tests the package in a real browser environment:

```bash
# Start local server
npm run serve

# Open http://localhost:8080/test.html in your browser
```

The browser test automatically:
- Loads the UMD bundle from node_modules
- Creates a PriceLadder instance
- Tests the API
- Verifies WASM files are accessible
- Provides interactive controls for manual testing

### 3. Reinstall Package

To test with the latest local build:

```bash
# From repository root, build and pack
npm run build:lib:full
npm run release

# In this directory, reinstall from the tarball
npm run reinstall
# Or manually:
npm install ../../src/web/slickquant-slick-ladder-*.tgz
```

## Test Files

- **test.js** - Node.js test script for package structure
- **test.html** - Browser test for UMD bundle and API
- **package.json** - Test dependencies and scripts

## What Gets Tested

### Package Structure
- Main entry point (`dist/slick-ladder.js`)
- Type definitions (`dist/*.d.ts`)
- WASM runtime files (`dist/wasm/*`)
- Documentation (`README.md`)

### API Functionality (Browser)
- Global `SlickLadder` namespace
- `PriceLadder` constructor
- `updateOrderBook()` method
- `clearOrderBook()` method
- Canvas rendering
- WASM file accessibility

## Expected Output

### Node.js Test
```
🧪 Testing @slickquant/slick-ladder npm package

✓ Package is installed
✓ package.json exists
✓ Main UMD bundle exists
  Bundle size: 45.23 KB
✓ TypeScript definitions exist
✓ WASM files are included
  WASM files count: 17
✓ README.md exists
✓ All module type definitions exist
✓ Total package size
  Total package size: 3.24 MB

8 passed, 0 failed
```

### Browser Test
Opens an interactive test page showing:
- Package load status
- API test results
- Live price ladder rendering
- Interactive controls for manual testing
