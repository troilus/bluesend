@echo off
echo Building BlueSend...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
echo.
echo Done! Executable: publish\BlueSend.exe
pause
