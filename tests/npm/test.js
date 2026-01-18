#!/usr/bin/env node
/**
 * Node.js test script for @slickquant/slick-ladder npm package
 * Tests package structure and exports
 */

const fs = require('fs');
const path = require('path');

const packageRoot = path.join(__dirname, 'node_modules', '@slickquant', 'slick-ladder');
const tests = [];
let passed = 0;
let failed = 0;

function test(name, fn) {
  tests.push({ name, fn });
}

function log(message, isError = false) {
  const prefix = isError ? '✗' : '✓';
  const color = isError ? '\x1b[31m' : '\x1b[32m';
  console.log(`${color}${prefix}\x1b[0m ${message}`);
}

// Test 1: Package installed
test('Package is installed', () => {
  if (!fs.existsSync(packageRoot)) {
    throw new Error(`Package not found at ${packageRoot}`);
  }
});

// Test 2: package.json exists
test('package.json exists', () => {
  const pkgPath = path.join(packageRoot, 'package.json');
  if (!fs.existsSync(pkgPath)) {
    throw new Error('package.json not found');
  }
  const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
  if (pkg.name !== '@slickquant/slick-ladder') {
    throw new Error(`Unexpected package name: ${pkg.name}`);
  }
});

// Test 3: Main bundle exists
test('Main UMD bundle exists', () => {
  const mainPath = path.join(packageRoot, 'dist', 'slick-ladder.js');
  if (!fs.existsSync(mainPath)) {
    throw new Error('dist/slick-ladder.js not found');
  }
  const size = fs.statSync(mainPath).size;
  console.log(`  Bundle size: ${(size / 1024).toFixed(2)} KB`);
});

// Test 4: Type definitions exist
test('TypeScript definitions exist', () => {
  const typesPath = path.join(packageRoot, 'dist', 'main.d.ts');
  if (!fs.existsSync(typesPath)) {
    throw new Error('dist/main.d.ts not found');
  }
});

// Test 5: WASM files included
test('WASM files are included', () => {
  const wasmDir = path.join(packageRoot, 'dist', 'wasm');
  if (!fs.existsSync(wasmDir)) {
    throw new Error('dist/wasm directory not found');
  }

  const requiredFiles = [
    'dotnet.js',
    'dotnet.native.wasm',
    'dotnet.runtime.js',
    'SlickLadder.Core.wasm'
  ];

  const missing = requiredFiles.filter(file =>
    !fs.existsSync(path.join(wasmDir, file))
  );

  if (missing.length > 0) {
    throw new Error(`Missing WASM files: ${missing.join(', ')}`);
  }

  const wasmFiles = fs.readdirSync(wasmDir);
  console.log(`  WASM files count: ${wasmFiles.length}`);
});

// Test 6: README exists
test('README.md exists', () => {
  const readmePath = path.join(packageRoot, 'README.md');
  if (!fs.existsSync(readmePath)) {
    throw new Error('README.md not found');
  }
});

// Test 7: All type definition files
test('All module type definitions exist', () => {
  const requiredDefs = [
    'canvas-renderer.d.ts',
    'interaction-handler.d.ts',
    'mbo-manager.d.ts',
    'types.d.ts',
    'wasm-adapter.d.ts',
    'wasm-types.d.ts'
  ];

  const missing = requiredDefs.filter(file =>
    !fs.existsSync(path.join(packageRoot, 'dist', file))
  );

  if (missing.length > 0) {
    throw new Error(`Missing type definitions: ${missing.join(', ')}`);
  }
});

// Test 8: Package size check
test('Total package size', () => {
  function getDirectorySize(dir) {
    let size = 0;
    const files = fs.readdirSync(dir);
    for (const file of files) {
      const filePath = path.join(dir, file);
      const stats = fs.statSync(filePath);
      if (stats.isDirectory()) {
        size += getDirectorySize(filePath);
      } else {
        size += stats.size;
      }
    }
    return size;
  }

  const totalSize = getDirectorySize(packageRoot);
  console.log(`  Total package size: ${(totalSize / 1024 / 1024).toFixed(2)} MB`);

  if (totalSize > 10 * 1024 * 1024) {
    console.log('  ⚠️  Package is larger than 10MB');
  }
});

// Run all tests
console.log('\n🧪 Testing @slickquant/slick-ladder npm package\n');

for (const { name, fn } of tests) {
  try {
    fn();
    log(name);
    passed++;
  } catch (error) {
    log(`${name}: ${error.message}`, true);
    failed++;
  }
}

console.log(`\n${passed} passed, ${failed} failed\n`);

if (failed > 0) {
  process.exit(1);
}
