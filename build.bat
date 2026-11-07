@echo off
REM Builds GearTray as one small exe in publish\GearTray.exe (needs the .NET 10
REM SDK; the exe itself needs only the .NET 10 Desktop Runtime).
REM
REM To install or update the copy Windows starts at sign-in, exit the running
REM GearTray first (tray icon > Exit), then run, as administrator:
REM
REM     publish\GearTray.exe --install-startup
REM
REM which copies it to C:\Program Files\GearTray and (re)creates the logon task.
setlocal
dotnet publish "%~dp0GearTray\GearTray.csproj" -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true -p:DebugType=none -o "%~dp0publish" -nologo
endlocal & exit /b %ERRORLEVEL%
