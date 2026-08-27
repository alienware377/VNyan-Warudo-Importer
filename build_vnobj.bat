@echo off
setlocal EnableDelayedExpansion
REM Rebuilds the Warudo Importer VNyan plugin end to end:
REM   1) compile WarudoImporter.dll with Unity's Roslyn compiler (build\build.rsp)
REM   2) copy the DLL into the Unity build project's Assets\Plugins
REM   3) build WarudoImporter.vnobj via Unity 2022.3.22f1 batchmode
REM   4) stage both into dist\

set "PROJ=%~dp0"
set "MGD=C:\Program Files\VNyan\VNyan_Data\Managed"
set "VNI=%MGD%\VNyanInterface.dll"
set "CSC=C:\Program Files\Unity\Hub\Editor\2019.4.16f1\Editor\Data\Tools\Roslyn\csc.exe"
set "UNITY=C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"
set "UBUILD=%PROJ%_unitybuild"
set "PLUGINS=%UBUILD%\Assets\Plugins"
set "DLL=%PROJ%build\WarudoImporter.dll"
set "VNOBJ=%UBUILD%\AssetBundles\WarudoImporter.vnobj"

if not exist "%MGD%\UnityEngine.dll" ( echo [!] VNyan managed DLLs not found at "%MGD%". & goto :fail )
if not exist "%VNI%"   ( echo [!] VNyanInterface.dll not found at "%VNI%". & goto :fail )
if not exist "%CSC%"   ( echo [!] Roslyn compiler not found at "%CSC%". Install Unity 2019.4.16f1 via Unity Hub. & goto :fail )
if not exist "%UNITY%" ( echo [!] Unity 2022.3.22f1 not found at "%UNITY%". Install it via Unity Hub. & goto :fail )
if not exist "%PROJ%build\build.rsp" ( echo [!] build\build.rsp is missing. & goto :fail )

echo [1/4] Compiling WarudoImporter.dll ...
pushd "%PROJ%"
if exist "%DLL%" del /Q "%DLL%"
"%CSC%" -noconfig -nostdlib+ @build\build.rsp
set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" ( echo [!] csc.exe exited with %RC%. & goto :fail )
if not exist "%DLL%" ( echo [!] DLL compile failed - build\WarudoImporter.dll was not produced. & goto :fail )
echo     ok - build\WarudoImporter.dll

echo [2/4] Copying the DLL into the Unity project ...
if not exist "%PLUGINS%" mkdir "%PLUGINS%"
copy /Y "%DLL%" "%PLUGINS%\WarudoImporter.dll" >nul
if errorlevel 1 ( echo [!] Could not copy the DLL into "%PLUGINS%". & goto :fail )
REM The plugin's own dependencies must sit beside it or Unity cannot load the type.
if not exist "%PLUGINS%\VNyanInterface.dll"  copy /Y "%VNI%" "%PLUGINS%\" >nul
if not exist "%PLUGINS%\Newtonsoft.Json.dll" copy /Y "%MGD%\Newtonsoft.Json.dll" "%PLUGINS%\" >nul
echo     ok - Assets\Plugins updated

echo [3/4] Building .vnobj via Unity batchmode (this can take a few minutes) ...
if exist "%VNOBJ%" del /Q "%VNOBJ%"
"%UNITY%" -batchmode -quit -nographics -projectPath "%UBUILD%" -executeMethod WarudoImporterBuild.Build -logFile "%UBUILD%\build.log"
if not exist "%VNOBJ%" ( echo [!] .vnobj build failed. See "%UBUILD%\build.log". & goto :fail )
echo     ok - WarudoImporter.vnobj

echo [4/4] Staging dist\ ...
if not exist "%PROJ%dist" mkdir "%PROJ%dist"
copy /Y "%DLL%"   "%PROJ%dist\" >nul
copy /Y "%VNOBJ%" "%PROJ%dist\" >nul
if not exist "%PROJ%dist\WarudoImporter.vnobj" ( echo [!] Staging failed. & goto :fail )

echo.
echo *** Build complete. *** dist\ contains:
echo     WarudoImporter.dll    -^> VNyan\Items\Assemblies
echo     WarudoImporter.vnobj  -^> VNyan\Items
pause
exit /b 0

:fail
echo.
echo *** Build failed. ***
pause
exit /b 1
