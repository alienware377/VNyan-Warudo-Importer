@echo off
setlocal enableextensions
REM ===========================================================================
REM  Build-from-source installer for the Warudo Importer.
REM
REM  Unlike INSTALL.bat / INSTALL_PORTABLE.bat (which copy the prebuilt files
REM  from dist\), this one:
REM    * lets YOU pick your own VNyan install folder with a file browser,
REM    * recompiles WarudoImporter.dll against YOUR VNyan's assemblies,
REM    * rebuilds WarudoImporter.vnobj with YOUR own Unity installation,
REM    * then installs both into the VNyan folder you chose and verifies it.
REM
REM  Requirements on this PC:
REM    * The full source tree (this folder, with Scripts\, _unitybuild\, dist\).
REM    * Unity 2022.3.x installed (via Unity Hub is fine). Unity also supplies
REM      the Roslyn C# compiler the sources need - the in-box .NET Framework
REM      csc.exe is C# 5 and cannot build them.
REM
REM  This .bat deliberately does NOT elevate itself. An elevated session cannot
REM  see mapped / virtual cloud drives, so relaunching this file from such a
REM  drive as Administrator would fail before it started. The build runs as you,
REM  and only the final copy into VNyan is elevated (from a local staging
REM  folder). The real work is in Install-FromSource.ps1.
REM ===========================================================================

set "PS=%~dp0Install-FromSource.ps1"
if not exist "%PS%" (
    echo.
    echo *** Could not find Install-FromSource.ps1 next to this file. ***
    echo     Make sure you kept the whole folder together.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -STA -Command "Unblock-File -LiteralPath '%PS%' -ErrorAction SilentlyContinue; Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force; & '%PS%'"
echo.
pause
exit /b
