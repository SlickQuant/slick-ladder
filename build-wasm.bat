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

    REM Copy from AppBundle to public directory
    echo 📋 Copying WASM files from AppBundle to public directory...
    set APPBUNDLE=src\core\bin\Release\net8.0\browser-wasm\AppBundle\_framework
    if not exist src\web\public\wasm mkdir src\web\public\wasm

    REM Copy all framework files (force overwrite with /Y)
    copy /Y %APPBUNDLE%\*.wasm src\web\public\wasm\ >nul 2>&1
    copy /Y %APPBUNDLE%\*.js src\web\public\wasm\ >nul 2>&1
    copy /Y %APPBUNDLE%\*.json src\web\public\wasm\ >nul 2>&1
    copy /Y %APPBUNDLE%\*.map src\web\public\wasm\ >nul 2>&1

    REM Copy support files if they exist
    if exist %APPBUNDLE%\supportFiles (
        if not exist src\web\public\wasm\supportFiles mkdir src\web\public\wasm\supportFiles
        copy %APPBUNDLE%\supportFiles\* src\web\public\wasm\supportFiles\ >nul 2>&1
    )

    REM Copy and patch runtime config to enable JSON reflection for JSExport/JSImport
    set APPBUNDLE_ROOT=src\core\bin\Release\net8.0\browser-wasm\AppBundle
    if exist %APPBUNDLE_ROOT%\SlickLadder.Core.runtimeconfig.json (
        powershell -Command "(Get-Content '%APPBUNDLE_ROOT%\SlickLadder.Core.runtimeconfig.json' -Raw) -replace '\"System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault\": false', '\"System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault\": true' | Set-Content 'src\web\public\wasm\SlickLadder.Core.runtimeconfig.json'"
    )

    echo ✅ WASM files copied to public\wasm\
    echo.
    echo 🎉 Build complete! You can now run 'npm run serve' in src\web\ to test.
) else (
    echo ❌ WASM compilation failed!
    exit /b 1
)
