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

    # Copy from AppBundle to public directory
    echo "📋 Copying WASM files from AppBundle to public directory..."
    APPBUNDLE="src/core/bin/Release/net8.0/browser-wasm/AppBundle/_framework"
    mkdir -p src/web/public/wasm

    # Copy all framework files
    cp -f "$APPBUNDLE"/*.wasm src/web/public/wasm/ 2>/dev/null || true
    cp -f "$APPBUNDLE"/*.js src/web/public/wasm/ 2>/dev/null || true
    cp -f "$APPBUNDLE"/*.json src/web/public/wasm/ 2>/dev/null || true
    cp -f "$APPBUNDLE"/*.map src/web/public/wasm/ 2>/dev/null || true

    # Copy support files if they exist
    if [ -d "$APPBUNDLE/supportFiles" ]; then
        mkdir -p src/web/public/wasm/supportFiles
        cp -f "$APPBUNDLE"/supportFiles/* src/web/public/wasm/supportFiles/ 2>/dev/null || true
    fi

    # Copy and patch runtime config to enable JSON reflection for JSExport/JSImport
    APPBUNDLE_ROOT="src/core/bin/Release/net8.0/browser-wasm/AppBundle"
    if [ -f "$APPBUNDLE_ROOT/SlickLadder.Core.runtimeconfig.json" ]; then
        sed 's/"System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault": false/"System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault": true/' \
            "$APPBUNDLE_ROOT/SlickLadder.Core.runtimeconfig.json" > src/web/public/wasm/SlickLadder.Core.runtimeconfig.json
    fi

    echo "✅ WASM files copied to public/wasm/"
    echo ""
    echo "🎉 Build complete! You can now run 'npm run serve' in src/web/ to test."
else
    echo "❌ WASM compilation failed!"
    exit 1
fi
