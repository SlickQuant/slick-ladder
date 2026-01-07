#!/bin/bash
# Build script for compiling C# core to WebAssembly

set -e  # Exit on error

echo "🔨 Building SlickLadder WASM Module..."
echo ""

# Check if .NET SDK is installed
if ! command -v dotnet &> /dev/null; then
    echo "❌ Error: .NET SDK not found. Please install .NET 8+ SDK."
    exit 1
fi

# Check .NET version
DOTNET_VERSION=$(dotnet --version)
echo "✅ .NET SDK version: $DOTNET_VERSION"
echo ""

# Build the WASM module
echo "📦 Compiling C# to WebAssembly..."
dotnet publish src/core/SlickLadder.Core.csproj \
    -c Release \
    -r browser-wasm \
    -p:Configuration=WASM \
    -p:InvariantGlobalization=true

if [ $? -eq 0 ]; then
    echo "✅ WASM compilation successful!"
    echo ""
    echo "📋 WASM files automatically copied to src/web/public/wasm/ by MSBuild post-build event"
    echo ""
    echo "🎉 Build complete! You can now:"
    echo "   1. Run 'cd src/web && npm run build' to build the web bundle"
    echo "   2. Run 'cd src/web && npm run serve' to start the dev server"
else
    echo "❌ WASM compilation failed!"
    exit 1
fi
