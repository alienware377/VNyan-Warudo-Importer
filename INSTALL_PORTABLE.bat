@echo off
setlocal enableextensions
REM ===========================================================================
REM  Warudo Importer - portable installer.
REM  NO Unity, NO compiler, NO build required.
REM
REM  The files in dist\ are ALREADY built. The .vnobj was produced with Unity
REM  2022.3.22f1 and loads fine on VNyan's 2022.3.62 runtime, so nothing has to
REM  be compiled on this PC. This installer just lets you browse to your VNyan
REM  folder and copies these four files in:
REM      WarudoImporter.dll                -> VNyan\Items\Assemblies\WarudoImporter\
REM      WarudoImporter.vnobj              -> VNyan\Items\Assemblies\WarudoImporter\
REM      VRC.Dynamics.dll                  -> VNyan\Items\Assemblies\WarudoImporter\
REM      VRC.SDK3.Dynamics.PhysBone.dll    -> VNyan\Items\Assemblies\WarudoImporter\
REM
REM  WHY THE TWO VRC.* STUBS: a .warudo built with the VRChat SDK carries
REM  VRCPhysBone components, and in a host that has no VRChat SDK those load as
REM  dead "missing script" placeholders. These two stub assemblies re-declare
REM  those classes with the exact assembly names, version (1.0.0.0) and
REM  serialized fields, so VNyan's plugin loader picks them up at startup and the
REM  bundle's PhysBone data deserializes - which is what lets the importer turn
REM  the creator's real physics tuning into DynamicBone. Without them,
REM  VRChat-authored models fall back to generic auto-detected physics.
REM
REM  (The Unity Editor itself can't be bundled - it's several gigabytes and its
REM   license forbids redistribution - but it isn't needed here: the build
REM   output it would produce is already in dist\. Only use INSTALL_BUILD.bat if
REM   the prebuilt bundle ever refuses to load.)
REM ===========================================================================

REM  Everything that gets installed, in ONE place. Add a file here and the
REM  existence check, the unblock, the plain copy, the staging copy, the
REM  elevated copy, the hash-verify and the summary below all pick it up.
set "FILES=WarudoImporter.dll WarudoImporter.vnobj VRC.Dynamics.dll VRC.SDK3.Dynamics.PhysBone.dll"

set "SRC=%~dp0dist"
set "STAGE=C:\VNyanInstallTemp"

echo.
echo ============================================================
echo   Warudo Importer for VNyan
echo   Portable installer (no Unity needed)
echo ============================================================
echo.

REM --- prebuilt files present? ---------------------------------------------
for %%F in (%FILES%) do if not exist "%SRC%\%%F" goto :nodist

REM --- VNyan must be closed -------------------------------------------------
REM  A loaded plugin assembly is locked by Windows; copying over it while VNyan
REM  runs leaves a half-installed plugin. Refuse rather than half-install.
REM  Never launch VNyan.exe to "focus" it - that starts a second instance and
REM  the OSC / VMC ports collide.
tasklist /FI "IMAGENAME eq VNyan.exe" /NH 2>nul | find /I "VNyan.exe" >nul
if not errorlevel 1 goto :running

echo Select your VNyan install folder (the folder that contains VNyan.exe)...

REM --- folder picker via PowerShell; result written to a temp file ----------
set "SELFILE=%TEMP%\_vnyan_warudo_pick.txt"
del "%SELFILE%" >nul 2>&1
powershell -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; $f=New-Object System.Windows.Forms.FolderBrowserDialog; $f.Description='Select your VNyan install folder (the folder containing VNyan.exe)'; $f.ShowNewFolderButton=$false; if(Test-Path 'C:\Program Files\VNyan'){ $f.SelectedPath='C:\Program Files\VNyan' }; if($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){ Set-Content -LiteralPath '%SELFILE%' -Value $f.SelectedPath -Encoding Default -NoNewline }"

if not exist "%SELFILE%" goto :nopick
set "VNYAN="
set /p VNYAN=<"%SELFILE%"
del "%SELFILE%" >nul 2>&1
if not defined VNYAN goto :nopick

echo   VNyan: "%VNYAN%"

if not exist "%VNYAN%\VNyan_Data\Managed\VNyanInterface.dll" goto :novnyan

set "DST=%VNYAN%\Items\Assemblies\WarudoImporter"

REM --- strip "Mark of the Web" from the source files ------------------------
REM  When this folder is zipped / downloaded / copied from another PC, Windows
REM  tags these files as blocked. VNyan then silently refuses to load
REM  the assembly and the Plugins panel is simply empty, with nothing logged.
powershell -NoProfile -Command "foreach($f in '%FILES%'.Split(' ')){ Unblock-File -LiteralPath (Join-Path '%SRC%' $f) -ErrorAction SilentlyContinue }" >nul 2>&1

REM --- try a plain copy first (works for user-writable install folders) -----
echo.
echo Installing...
set "NEEDADMIN="
if not exist "%DST%" mkdir "%DST%" 2>nul
if not exist "%DST%" set "NEEDADMIN=1"
for %%F in (%FILES%) do if not defined NEEDADMIN copy /Y "%SRC%\%%F" "%DST%\" >nul 2>&1 || set "NEEDADMIN=1"

if not defined NEEDADMIN goto :unblockdst

REM --- the folder is protected: stage locally, then copy while elevated -----
REM  IMPORTANT: an elevated session does NOT see mapped / virtual cloud drives.
REM  If this repo lives on one, an elevated copy taken straight from here fails
REM  silently because the source path does not exist in that session. Staging
REM  onto a real local path first is what makes the elevated copy work.
echo   That folder needs Administrator. Staging files to %STAGE% ...
if exist "%STAGE%" rd /S /Q "%STAGE%" >nul 2>&1
mkdir "%STAGE%" 2>nul
if not exist "%STAGE%" goto :nostage
for %%F in (%FILES%) do copy /Y "%SRC%\%%F" "%STAGE%\" >nul || goto :fail
powershell -NoProfile -Command "foreach($f in '%FILES%'.Split(' ')){ Unblock-File -LiteralPath (Join-Path '%STAGE%' $f) -ErrorAction SilentlyContinue }" >nul 2>&1

set "CP=%STAGE%\_copy.cmd"
> "%CP%"  echo @echo off
>>"%CP%"  echo if not exist "%DST%" mkdir "%DST%"
for %%F in (%FILES%) do >>"%CP%" echo copy /Y "%STAGE%\%%F" "%DST%\"
>>"%CP%"  echo powershell -NoProfile -Command "foreach($f in '%FILES%'.Split(' ')){ Unblock-File -LiteralPath (Join-Path '%DST%' $f) -ErrorAction SilentlyContinue }"

echo   Requesting Administrator permission...
powershell -NoProfile -Command "Start-Process -FilePath '%CP%' -Verb RunAs -Wait -WindowStyle Hidden"

:unblockdst
REM --- unblock again at the destination, in case the copy carried the tag ---
powershell -NoProfile -Command "foreach($f in '%FILES%'.Split(' ')){ Unblock-File -LiteralPath (Join-Path '%DST%' $f) -ErrorAction SilentlyContinue }" >nul 2>&1

REM --- verify by comparing hashes ------------------------------------------
echo.
echo Verifying installed files against dist\ ...
powershell -NoProfile -Command "$ok=$true; foreach($f in '%FILES%'.Split(' ')){ $s=Join-Path '%SRC%' $f; $d=Join-Path '%DST%' $f; if(-not (Test-Path -LiteralPath $d)){ Write-Host ('  FAIL  ' + $f + ' - not installed'); $ok=$false; continue }; $a=(Get-FileHash -LiteralPath $s).Hash; $b=(Get-FileHash -LiteralPath $d).Hash; if($a -eq $b){ Write-Host ('  PASS  ' + $f) } else { Write-Host ('  FAIL  ' + $f + ' - installed copy does not match dist\'); $ok=$false } }; if($ok){ exit 0 } else { exit 1 }"
set "VERIFY=%ERRORLEVEL%"

rd /S /Q "%STAGE%" >nul 2>&1

if not "%VERIFY%"=="0" goto :verifyfail

echo.
echo Done. Installed into:
echo   %DST%
for %%F in (%FILES%) do echo     %%F
echo.
echo Now start VNyan, open the Plugins window and click "Warudo Importer".
echo.
echo If the Plugins panel is still empty after restarting:
echo   1. VNyan ^> Settings ^> Misc ^> "Allow Third Party Plugins" must be ON.
echo      VNyan logs nothing when this is off - the panel is silently empty.
echo      Check this first.
echo   2. Make sure you picked the folder that actually contains VNyan.exe.
echo      If you have several VNyan installs, launch the same one you installed
echo      into. Player.log's "Loading player data from" line names the running copy.
echo   3. The files were unblocked automatically, but if VNyan was open during
echo      the install, close it completely ^(check the tray^) and reopen it.
pause
exit /b 0

:nodist
echo *** A file is missing from dist\. All of these have to be there: ***
for %%F in (%FILES%) do echo         dist\%%F
echo     Keep INSTALL_PORTABLE.bat in the same folder as dist\.
pause
exit /b 1

:nopick
echo No folder selected. Aborting.
pause
exit /b 1

:novnyan
echo.
echo *** That folder is not a VNyan install ^(no VNyan_Data\Managed\VNyanInterface.dll^). ***
echo     Pick the folder that contains VNyan.exe.
pause
exit /b 1

:running
echo *** VNyan is currently running. ***
echo.
echo     The plugin DLL is locked while VNyan has it loaded, so installing now
echo     would leave you with a half-updated plugin.
echo.
echo     Close VNyan completely ^(check the system tray^), then run this again.
pause
exit /b 1

:nostage
echo *** Could not create the staging folder %STAGE%. ***
echo     Check that drive C: is writable and try again.
pause
exit /b 1

:verifyfail
echo.
echo *** Verification FAILED - the files in VNyan do not match dist\. ***
echo     Most likely you declined the Administrator prompt, or VNyan was
echo     reopened partway through. Close VNyan and run this again.
pause
exit /b 1

:fail
echo.
echo *** Copy failed. Check that you picked the right VNyan folder and try again. ***
rd /S /Q "%STAGE%" >nul 2>&1
pause
exit /b 1
