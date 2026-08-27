@echo off
setlocal enableextensions
REM ===========================================================================
REM  Warudo Importer - installer for the DEFAULT VNyan location
REM  (C:\Program Files\VNyan). If your VNyan lives somewhere else, use
REM  INSTALL_PORTABLE.bat instead - it opens a folder picker.
REM
REM  Copies the prebuilt files from dist\ :
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
REM  Nothing is compiled here. The .vnobj was built with Unity 2022.3.22f1 and
REM  loads on VNyan's 2022.3.62 runtime, so no Unity install is needed.
REM
REM  WHY THE STAGING FOLDER: writing into Program Files needs Administrator,
REM  and an elevated session does NOT see mapped/virtual cloud drives. If this
REM  repo sits on such a drive, an elevated copy straight from here fails
REM  silently because the source path simply does not exist over there. So the
REM  files are staged into C:\VNyanInstallTemp first (as you), the elevated
REM  step copies from that real local path, and the staging folder is deleted
REM  afterwards.
REM ===========================================================================

REM  Everything that gets installed, in ONE place. Add a file here and the
REM  existence check, the staging copy, the unblock, the elevated copy, the
REM  hash-verify and the summary below all pick it up.
set "FILES=WarudoImporter.dll WarudoImporter.vnobj VRC.Dynamics.dll VRC.SDK3.Dynamics.PhysBone.dll"

set "SRC=%~dp0dist"
set "VNYAN=C:\Program Files\VNyan"
set "DST=%VNYAN%\Items\Assemblies\WarudoImporter"
set "STAGE=C:\VNyanInstallTemp"

echo.
echo ============================================================
echo   Warudo Importer for VNyan
echo   Installer (default location)
echo ============================================================
echo.

REM --- 1) prebuilt files present? ------------------------------------------
for %%F in (%FILES%) do if not exist "%SRC%\%%F" goto :nodist

REM --- 2) is this actually a VNyan install? --------------------------------
if not exist "%VNYAN%\VNyan_Data\Managed\VNyanInterface.dll" goto :novnyan

REM --- 3) VNyan must be closed ---------------------------------------------
REM  Windows holds a lock on a loaded plugin assembly, so copying over a DLL
REM  while VNyan is running leaves you with a half-installed plugin (new
REM  .vnobj, old .dll). Refuse instead. Do NOT start VNyan.exe to bring it to
REM  the front - that launches a SECOND instance and the OSC / VMC ports end
REM  up in use.
tasklist /FI "IMAGENAME eq VNyan.exe" /NH 2>nul | find /I "VNyan.exe" >nul
if not errorlevel 1 goto :running

REM --- 4) stage the files onto a real local disk ---------------------------
echo Staging files to %STAGE% ...
if exist "%STAGE%" rd /S /Q "%STAGE%" >nul 2>&1
mkdir "%STAGE%" 2>nul
if not exist "%STAGE%" goto :nostage
for %%F in (%FILES%) do copy /Y "%SRC%\%%F" "%STAGE%\" >nul || goto :fail

REM  Strip "Mark of the Web" now, while the files are still ours. VNyan
REM  silently refuses to load a blocked assembly and the Plugins panel just
REM  stays empty, with nothing in the log.
powershell -NoProfile -Command "foreach($f in '%FILES%'.Split(' ')){ Unblock-File -LiteralPath (Join-Path '%STAGE%' $f) -ErrorAction SilentlyContinue }" >nul 2>&1

REM --- 5) build the little elevated copy script ----------------------------
set "CP=%STAGE%\_copy.cmd"
> "%CP%"  echo @echo off
>>"%CP%"  echo if not exist "%DST%" mkdir "%DST%"
for %%F in (%FILES%) do >>"%CP%" echo copy /Y "%STAGE%\%%F" "%DST%\"
>>"%CP%"  echo powershell -NoProfile -Command "foreach($f in '%FILES%'.Split(' ')){ Unblock-File -LiteralPath (Join-Path '%DST%' $f) -ErrorAction SilentlyContinue }"

echo Requesting Administrator permission to write into Program Files...
powershell -NoProfile -Command "Start-Process -FilePath '%CP%' -Verb RunAs -Wait -WindowStyle Hidden"

REM --- 6) verify by comparing hashes ---------------------------------------
echo.
echo Verifying installed files against dist\ ...
powershell -NoProfile -Command "$ok=$true; foreach($f in '%FILES%'.Split(' ')){ $s=Join-Path '%SRC%' $f; $d=Join-Path '%DST%' $f; if(-not (Test-Path -LiteralPath $d)){ Write-Host ('  FAIL  ' + $f + ' - not installed'); $ok=$false; continue }; $a=(Get-FileHash -LiteralPath $s).Hash; $b=(Get-FileHash -LiteralPath $d).Hash; if($a -eq $b){ Write-Host ('  PASS  ' + $f) } else { Write-Host ('  FAIL  ' + $f + ' - installed copy does not match dist\'); $ok=$false } }; if($ok){ exit 0 } else { exit 1 }"
set "VERIFY=%ERRORLEVEL%"

REM --- 7) clean up the staging folder --------------------------------------
rd /S /Q "%STAGE%" >nul 2>&1

if not "%VERIFY%"=="0" goto :verifyfail

echo.
echo Done. Installed to:
for %%F in (%FILES%) do echo   %DST%\%%F
echo.
echo Start VNyan, open the Plugins window, and click "Warudo Importer".
echo If no plugin buttons appear at all, check
echo   Settings ^> Misc ^> "Allow Third Party Plugins" is ON.
pause
exit /b 0

:nodist
echo *** A file is missing from dist\. All of these have to be there: ***
for %%F in (%FILES%) do echo         dist\%%F
echo     Keep INSTALL.bat in the same folder as dist\.
pause
exit /b 1

:novnyan
echo *** VNyan was not found at "%VNYAN%". ***
echo     ^(No VNyan_Data\Managed\VNyanInterface.dll there.^)
echo     If your VNyan is installed elsewhere, run INSTALL_PORTABLE.bat instead.
pause
exit /b 1

:running
echo *** VNyan is currently running. ***
echo.
echo     The plugin DLL is locked while VNyan has it loaded, so installing now
echo     would leave you with a half-updated plugin.
echo.
echo     Close VNyan completely ^(check the system tray^), then run this again.
echo     Do not just minimise it.
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
echo     The most likely causes are: you declined the Administrator prompt,
echo     or VNyan was reopened partway through. Close VNyan and try again.
pause
exit /b 1

:fail
echo.
echo *** Copy failed while staging the files. ***
rd /S /Q "%STAGE%" >nul 2>&1
pause
exit /b 1
