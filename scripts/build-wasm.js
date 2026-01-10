#!/usr/bin/env node
/**
 * Cross-platform WASM build script
 * Compiles C# core to WebAssembly
 */

const { execSync } = require('child_process');
const path = require('path');

console.log('🔨 Building SlickLadder WASM Module...\n');

// Check if .NET SDK is installed
try {
  const dotnetVersion = execSync('dotnet --version', { encoding: 'utf8' }).trim();
  console.log(`✅ .NET SDK version: ${dotnetVersion}\n`);
} catch (error) {
  console.error('❌ Error: .NET SDK not found. Please install .NET 8+ SDK.');
  process.exit(1);
}

// Build the WASM module
console.log('📦 Compiling C# to WebAssembly...');

const csprojPath = path.join(__dirname, '..', 'src', 'core', 'SlickLadder.Core.csproj');

try {
  execSync(
    `dotnet publish "${csprojPath}" -c Release -r browser-wasm -p:Configuration=WASM -p:InvariantGlobalization=true`,
    {
      stdio: 'inherit',
      cwd: path.join(__dirname, '..')
    }
  );

  console.log('\n✅ WASM compilation successful!');
  console.log('\n📋 WASM files automatically copied to examples/web/public/wasm/ by MSBuild post-build event');
  console.log('\n🎉 WASM build complete!');
} catch (error) {
  console.error('\n❌ WASM compilation failed!');
  process.exit(1);
}
