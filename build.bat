@echo off
set RUNTIME=win-x64

echo ========== 1. Full (Self-contained, ~100MB) ==========
dotnet publish -c Release -r %RUNTIME% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\full
if %errorlevel% neq 0 ( echo Full build failed! & pause & exit /b 1 )

echo.
echo ========== 2. Framework-dependent (~5MB) ==========
dotnet publish -c Release -r %RUNTIME% --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\framework-dependent
if %errorlevel% neq 0 ( echo Framework-dependent build failed! & pause & exit /b 1 )

echo.
echo ========== All builds complete! ==========
echo 1. Full:           publish\full\BlueSend.exe
echo 2. Framework-dep:  publish\framework-dependent\BlueSend.exe
pause
