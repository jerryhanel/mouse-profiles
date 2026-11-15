@echo off
setlocal

set "ROOT=%~dp0.."
set "PUBLISHDIR=%~dp0publish"

echo Publishing self-contained win-x64...
dotnet publish "%ROOT%\MouseProfiles\MouseProfiles.csproj" -c Release -r win-x64 --self-contained -o "%PUBLISHDIR%" /p:PublishSingleFile=false
if errorlevel 1 (echo dotnet publish failed & exit /b 1)

echo Compiling installer...
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (echo ISCC.exe not found - install Inno Setup 6 & exit /b 1)

"%ISCC%" "%~dp0MouseProfiles.iss"
if errorlevel 1 (echo ISCC failed & exit /b 1)

echo Done! Installer: %~dp0output\MouseProfilesSetup.exe
