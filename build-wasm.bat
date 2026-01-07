@echo off
REM Build script for compiling C# core to WebAssembly

echo 🔨 Building SlickLadder WASM Module...
echo.

REM Check if .NET SDK is installed
where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ❌ Error: .NET SDK not found. Please install .NET 8+ SDK.
    exit /b 1
)

REM Check .NET version
for /f "tokens=*" %%i in ('dotnet --version') do set DOTNET_VERSION=%%i
echo ✅ .NET SDK version: %DOTNET_VERSION%
echo.

REM Build the WASM module
echo 📦 Compiling C# to WebAssembly...
dotnet publish src\core\SlickLadder.Core.csproj ^
    -c Release ^
    -r browser-wasm ^
    -p:Configuration=WASM ^
    -p:InvariantGlobalization=true

if %ERRORLEVEL% EQU 0 (
    echo ✅ WASM compilation successful!
    echo.
    echo 📋 WASM files automatically copied to src\web\public\wasm\ by MSBuild post-build event
    echo.
    echo 🎉 Build complete! You can now:
    echo    1. Run 'cd src\web ^&^& npm run build' to build the web bundle
    echo    2. Run 'cd src\web ^&^& npm run serve' to start the dev server
) else (
    echo ❌ WASM compilation failed!
    exit /b 1
)
