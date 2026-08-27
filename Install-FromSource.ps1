# =============================================================================
#  Install-FromSource.ps1
#
#  Portable build + install for the Warudo Importer VNyan plugin. Run via
#  INSTALL_BUILD.bat (it launches this with -STA so the folder/file dialogs
#  work).
#
#  Steps:
#    1. Ask the user to pick their VNyan install folder.
#    2. Find their Unity editor (prefer 2022.3.x) and its Roslyn C# compiler.
#    3. Recompile WarudoImporter.dll against THEIR VNyan assemblies.
#    4. Build the two VRChat stub assemblies from vrcstubs\.
#    5. Rebuild WarudoImporter.vnobj with THEIR Unity.
#    6. Stage all four onto a local disk, copy them into VNyan while elevated,
#       then hash-verify the result and clean up.
#
#  All four files land in VNyan\Items\Assemblies\WarudoImporter\ :
#      WarudoImporter.dll                (the plugin)
#      WarudoImporter.vnobj              (its UI bundle)
#      VRC.Dynamics.dll                  (VRChat stub assembly)
#      VRC.SDK3.Dynamics.PhysBone.dll    (VRChat stub assembly)
#
#  WHY THE TWO VRC.* STUBS: a .warudo built with the VRChat SDK carries
#  VRCPhysBone components, and in a host that has no VRChat SDK those load as
#  dead "missing script" placeholders. These two stub assemblies re-declare
#  those classes with the exact assembly names, version (1.0.0.0) and serialized
#  fields, so VNyan's plugin loader picks them up at startup and the bundle's
#  PhysBone data deserializes - which is what lets the importer turn the
#  creator's real physics tuning into DynamicBone. Without them, VRChat-authored
#  models fall back to generic auto-detected physics.
#
#  Two things this script is careful about, because both have bitten before:
#
#    * Elevation and cloud drives. Writing into Program Files needs
#      Administrator, and an elevated session does NOT see mapped / virtual
#      cloud drives - an elevated copy taken straight from such a drive fails
#      silently because the source path does not exist over there. So the build
#      output is staged into C:\VNyanInstallTemp first and the elevated step
#      copies from that real local path.
#
#    * VNyan locks the DLL. A loaded plugin assembly cannot be overwritten, so
#      this script refuses to run while VNyan is open rather than leaving a
#      half-updated plugin behind. It never starts VNyan.exe itself - launching
#      it again would create a second instance and the OSC / VMC ports would
#      collide.
# =============================================================================

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$Root = $PSScriptRoot
if ([string]::IsNullOrEmpty($Root)) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }

function Info ($m)  { Write-Host $m -ForegroundColor Cyan }
function Good ($m)  { Write-Host $m -ForegroundColor Green }
function Warn ($m)  { Write-Host $m -ForegroundColor Yellow }
function Err  ($m)  { Write-Host $m -ForegroundColor Red }

$Stage = 'C:\VNyanInstallTemp'

function Cleanup {
    if (Test-Path -LiteralPath $Stage) {
        Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Fail ($m) {
    Cleanup
    Err ""
    Err "*** $m"
    Err "*** Installation aborted. Nothing was changed in VNyan."
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor White
Write-Host "  Warudo Importer for VNyan" -ForegroundColor White
Write-Host "  Build-from-source installer (your Unity, your VNyan)" -ForegroundColor White
Write-Host "============================================================" -ForegroundColor White
Write-Host ""

# ----- plugin definition ------------------------------------------------------

$DllName   = 'WarudoImporter.dll'
$VnobjName = 'WarudoImporter.vnobj'
$DestRel   = 'Items\Assemblies\WarudoImporter'
$BuildFunc = 'WarudoImporterBuild.Build'

# The two VRChat stub assemblies. The .rsp files in vrcstubs\ hardcode the
# default C:\Program Files\VNyan path, which is exactly what this script must not
# assume - so the compile line is rebuilt here against the VNyan the user picked.
# Order matters: the PhysBone stub references VRC.Dynamics.dll, so it is second.
# AssemblyVersion 1.0.0.0 comes from vrcstubs\AssemblyInfo.cs and is load-bearing:
# avatar bundles reference these assemblies at exactly that version.
$StubBuilds = @(
    @{
        Out     = 'vrcstubs\VRC.Dynamics.dll'
        Sources = @('vrcstubs\VRCDynamicsStubs.cs', 'vrcstubs\AssemblyInfo.cs')
        Local   = @()
    },
    @{
        Out     = 'vrcstubs\VRC.SDK3.Dynamics.PhysBone.dll'
        Sources = @('vrcstubs\VRCPhysBoneComponents.cs', 'vrcstubs\AssemblyInfo.cs')
        Local   = @('vrcstubs\VRC.Dynamics.dll')
    }
)

# The stubs are data-only shells: they need nothing beyond the core framework and
# UnityEngine, so they deliberately do not use the full $managedRefs list.
$stubRefs = @(
    'mscorlib.dll',
    'netstandard.dll',
    'System.dll',
    'System.Core.dll',
    'UnityEngine.CoreModule.dll'
)

# Mirrors build\build.rsp. Everything is referenced explicitly out of the user's
# own VNyan_Data\Managed, and the compile runs -nostdlib+ so csc does not also
# drag in its own framework copies (that collides as CS1703).
$managedRefs = @(
    'mscorlib.dll',
    'netstandard.dll',
    'System.dll',
    'System.Core.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.AnimationModule.dll',
    'UnityEngine.AssetBundleModule.dll',
    'UnityEngine.ImageConversionModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'VNyanInterface.dll',
    'Newtonsoft.Json.dll'
)

# Compile order matters as little as it usually does, but this is the same list
# build\build.rsp uses, so the two stay comparable.
$sources = @(
    'Scripts\WarudoContainer.cs',
    'Scripts\WarudoBundle.cs',
    'Scripts\HumanoidMapper.cs',
    'Scripts\BlendShapeMapper.cs',
    'Scripts\PhysBonesGen.cs',
    'Scripts\VrmReflect.cs',
    'Scripts\AvatarPrep.cs',
    'Scripts\VNyanBridge.cs',
    'Scripts\WindowDrag.cs',
    'Scripts\NativeFileDialog.cs',
    'Scripts\WarudoImporterPlugin.cs'
)

# ----- 0) sanity checks -------------------------------------------------------

$unityProj = Join-Path $Root '_unitybuild'
if (-not (Test-Path (Join-Path $unityProj 'Assets\Editor\WarudoImporterBuild.cs'))) {
    Fail "This doesn't look like the full source folder (missing _unitybuild\Assets\Editor\WarudoImporterBuild.cs). Keep the whole repo together."
}
$pluginsDir = Join-Path $unityProj 'Assets\Plugins'

foreach ($s in $sources) {
    if (-not (Test-Path (Join-Path $Root $s))) { Fail "Missing source file: $s" }
}

# VNyan holds a lock on a loaded plugin DLL; refuse rather than half-install.
if (Get-Process -Name 'VNyan' -ErrorAction SilentlyContinue) {
    Fail "VNyan is running. Close it completely (check the system tray) and run this again - the plugin DLL is locked while VNyan has it loaded."
}

# ----- 1) choose VNyan folder ------------------------------------------------

Info "Step 1: choose your VNyan install folder (the folder that contains VNyan.exe)..."

$fb = New-Object System.Windows.Forms.FolderBrowserDialog
$fb.Description = 'Select your VNyan install folder (the folder containing VNyan.exe)'
$fb.ShowNewFolderButton = $false
if (Test-Path 'C:\Program Files\VNyan') { $fb.SelectedPath = 'C:\Program Files\VNyan' }

if ($fb.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
    Fail "No VNyan folder was selected."
}
$vnyan = $fb.SelectedPath
Good "  VNyan: $vnyan"

$managed = Join-Path $vnyan 'VNyan_Data\Managed'
if (-not (Test-Path (Join-Path $managed 'VNyanInterface.dll'))) {
    Fail "That folder is not a VNyan install (no VNyan_Data\Managed\VNyanInterface.dll). Pick the folder that contains VNyan.exe."
}

foreach ($r in $managedRefs) {
    if (-not (Test-Path (Join-Path $managed $r))) {
        Fail "Your VNyan is missing $r in VNyan_Data\Managed. Cannot build against this install."
    }
}

# ----- 2) find the user's Unity editor ---------------------------------------

Info ""
Info "Step 2: locating your Unity editor..."

function Get-UnityCandidates {
    $roots = @(
        'C:\Program Files\Unity\Hub\Editor',
        'C:\Program Files\Unity Hub\Editor',
        "${env:ProgramFiles}\Unity\Hub\Editor",
        "${env:LOCALAPPDATA}\Programs\Unity\Hub\Editor"
    ) | Select-Object -Unique

    $list = New-Object System.Collections.Generic.List[object]
    foreach ($r in $roots) {
        if (Test-Path $r) {
            Get-ChildItem -Path $r -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $exe = Join-Path $_.FullName 'Editor\Unity.exe'
                if (Test-Path $exe) {
                    $list.Add([pscustomobject]@{ Version = $_.Name; Exe = $exe })
                }
            }
        }
    }
    $single = 'C:\Program Files\Unity\Editor\Unity.exe'
    if (Test-Path $single) { $list.Add([pscustomobject]@{ Version = 'unknown'; Exe = $single }) }
    return $list
}

$cands = Get-UnityCandidates
$unity = $null
$unityVer = ''

if ($cands.Count -gt 0) {
    # Prefer 2022.3.*, then any 2022.*, then the highest version string.
    $pref = $cands | Where-Object { $_.Version -like '2022.3.*' } | Sort-Object Version -Descending
    if (-not $pref) { $pref = $cands | Where-Object { $_.Version -like '2022.*' } | Sort-Object Version -Descending }
    if (-not $pref) { $pref = $cands | Sort-Object Version -Descending }
    $chosen = $pref | Select-Object -First 1
    $unity = $chosen.Exe
    $unityVer = $chosen.Version
    Good "  Found Unity $unityVer"
    Info "    $unity"
}

if (-not $unity) {
    Warn "  No Unity editor was found automatically."
    Info "  Please browse to your Unity.exe (e.g. ...\Unity\Hub\Editor\2022.3.x\Editor\Unity.exe)"
    $of = New-Object System.Windows.Forms.OpenFileDialog
    $of.Title = 'Select your Unity.exe'
    $of.Filter = 'Unity editor (Unity.exe)|Unity.exe|All executables (*.exe)|*.exe'
    if ($of.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $unity = $of.FileName
        if ($unity -match '\\Editor\\([^\\]+)\\Editor\\Unity\.exe$') { $unityVer = $Matches[1] }
    }
}

if (-not $unity -or -not (Test-Path $unity)) {
    Fail "No Unity editor selected. The .vnobj bundle needs Unity to rebuild."
}

if ($unityVer -notlike '2022.3.*') {
    Warn ""
    Warn "  The selected Unity ($unityVer) is not 2022.3.x. VNyan runs on Unity 2022.3,"
    Warn "  and asset bundles built with a very different major version may FAIL to load."
    $ans = Read-Host "  Continue anyway? (y/N)"
    if ($ans -notmatch '^(y|yes)$') { Fail "Cancelled by user (Unity version mismatch)." }
}

# ----- 3) find a Roslyn C# compiler ------------------------------------------
# The sources use C# 6 syntax, so the in-box .NET Framework csc.exe
# (Microsoft.NET\Framework64\v4.0.30319) will NOT do - it only speaks C# 5.
# Every Unity install ships Roslyn under Editor\Data\Tools\Roslyn\csc.exe, so
# look there first, then fall back to a Visual Studio / MSBuild copy.

Info ""
Info "Step 3: locating a Roslyn C# compiler..."

$csc = $null

$cscCandidates = New-Object System.Collections.Generic.List[string]
$cscCandidates.Add((Join-Path (Split-Path -Parent $unity) 'Data\Tools\Roslyn\csc.exe'))
foreach ($c in $cands) {
    $cscCandidates.Add((Join-Path (Split-Path -Parent $c.Exe) 'Data\Tools\Roslyn\csc.exe'))
}
foreach ($p in @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
)) { $cscCandidates.Add($p) }

foreach ($p in $cscCandidates) {
    if ($p -and (Test-Path -LiteralPath $p)) { $csc = $p; break }
}

if (-not $csc) {
    Fail "Could not find a Roslyn csc.exe. Any Unity install has one at Editor\Data\Tools\Roslyn\csc.exe - install Unity 2022.3.x via Unity Hub, or install the Visual Studio Build Tools."
}
Good "  Using $csc"

# ----- 4) build the DLL against THIS VNyan's assemblies ----------------------

Info ""
Info "Step 4: compiling $DllName against your VNyan assemblies..."

$buildDir = Join-Path $Root 'build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$outDll = Join-Path $buildDir $DllName
if (Test-Path -LiteralPath $outDll) { Remove-Item -LiteralPath $outDll -Force }

$cscArgs = @('-noconfig', '-nostdlib+', '-target:library', '-nologo', '-optimize+', "-out:$outDll")
foreach ($r in $managedRefs) { $cscArgs += "-reference:$(Join-Path $managed $r)" }
foreach ($s in $sources)     { $cscArgs += (Join-Path $Root $s) }

$cscOut = & $csc @cscArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    $cscOut | ForEach-Object { Err "      $_" }
    Fail "Compilation of $DllName failed."
}
# The only expected warning is a benign netstandard version note (CS1701) plus
# its follow-on "(Location of symbol ...)" line; ignore both.
$realWarnings = $cscOut | Where-Object {
    $_ -match 'warning' -and $_ -notmatch 'CS1701' -and $_ -notmatch 'Location of symbol'
}
if ($realWarnings) { $realWarnings | ForEach-Object { Warn "      $_" } }

if (-not (Test-Path -LiteralPath $outDll)) { Fail "csc reported success but $DllName was not produced." }
Good "  Compiled $DllName"

# Stage the fresh DLL into the Unity project so the bundle build picks it up.
# Its dependencies must sit beside it or Unity cannot resolve the plugin type.
New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
Copy-Item -Force -LiteralPath $outDll -Destination (Join-Path $pluginsDir $DllName)
foreach ($dep in @('VNyanInterface.dll', 'Newtonsoft.Json.dll')) {
    $target = Join-Path $pluginsDir $dep
    if (-not (Test-Path -LiteralPath $target)) {
        Copy-Item -Force -LiteralPath (Join-Path $managed $dep) -Destination $target
    }
}

# ----- 5) build the two VRChat stub assemblies -------------------------------
# A .warudo built with the VRChat SDK carries VRCPhysBone components, and in a
# host that has no VRChat SDK those load as dead "missing script" placeholders.
# These two stubs re-declare those classes with the exact assembly names,
# version (1.0.0.0) and serialized fields, so VNyan's plugin loader picks them up
# at startup and the bundle's PhysBone data deserializes - which is what lets the
# importer turn the creator's real physics tuning into DynamicBone. Without them,
# VRChat-authored models fall back to generic auto-detected physics.
#
# The .rsp files use paths relative to the project root, so csc is run from
# there. Same Roslyn compiler and the same -noconfig -nostdlib+ as the plugin
# build above.

Info ""
Info "Step 5: building the VRChat stub assemblies..."

$distDir  = Join-Path $Root 'dist'
$stubOuts = @()

Push-Location -LiteralPath $Root
try {
    foreach ($sb in $StubBuilds) {
        $leaf = Split-Path -Leaf $sb.Out
        foreach ($src in $sb.Sources) {
            if (-not (Test-Path -LiteralPath (Join-Path $Root $src))) {
                Fail "Missing $src - keep the whole repo together."
            }
        }

        $so = Join-Path $Root $sb.Out
        if (Test-Path -LiteralPath $so) { Remove-Item -LiteralPath $so -Force }

        $stubArgs = @('-noconfig', '-nostdlib+', '-target:library', '-nologo', '-optimize+', "-out:$so")
        foreach ($r in $stubRefs)  { $stubArgs += "-reference:$(Join-Path $managed $r)" }
        foreach ($l in $sb.Local)  { $stubArgs += "-reference:$(Join-Path $Root $l)" }
        foreach ($s in $sb.Sources) { $stubArgs += (Join-Path $Root $s) }

        $stubOut = & $csc @stubArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            $stubOut | ForEach-Object { Err "      $_" }
            Fail "Compilation of $leaf failed."
        }
        $stubWarnings = $stubOut | Where-Object {
            $_ -match 'warning' -and $_ -notmatch 'CS1701' -and $_ -notmatch 'Location of symbol'
        }
        if ($stubWarnings) { $stubWarnings | ForEach-Object { Warn "      $_" } }

        if (-not (Test-Path -LiteralPath $so)) { Fail "csc reported success but $leaf was not produced." }

        # Keep dist\ (alongside the plugin outputs) up to date and install from
        # there, so what ships and what gets installed are the same bytes.
        New-Item -ItemType Directory -Force -Path $distDir | Out-Null
        Copy-Item -Force -LiteralPath $so -Destination (Join-Path $distDir $leaf)

        Good "  Compiled $leaf"
        $stubOuts += (Join-Path $distDir $leaf)
    }
} finally {
    Pop-Location
}

# ----- 6) rebuild the .vnobj bundle with the user's Unity --------------------

Info ""
Info "Step 6: rebuilding $VnobjName with Unity $unityVer (this can take a few minutes)..."

$abDir = Join-Path $unityProj 'AssetBundles'
$built = Join-Path $abDir $VnobjName
if (Test-Path -LiteralPath $built) { Remove-Item -LiteralPath $built -Force }

$log = Join-Path $unityProj 'build.log'
& $unity -batchmode -quit -nographics -projectPath $unityProj -executeMethod $BuildFunc -logFile $log
$code = $LASTEXITCODE

if ($code -ne 0 -or -not (Test-Path -LiteralPath $built)) {
    Err "      Unity build failed (exit $code). Last lines of the log:"
    if (Test-Path -LiteralPath $log) { Get-Content $log -Tail 25 | ForEach-Object { Err "      $_" } }
    Fail "Could not build $VnobjName."
}
Good "  Built $VnobjName"

# ----- 7) stage locally, then install while elevated -------------------------

Info ""
Info "Step 7: installing into $vnyan ..."

if (Get-Process -Name 'VNyan' -ErrorAction SilentlyContinue) {
    Fail "VNyan was opened during the build. Close it completely and run this again."
}

Cleanup
New-Item -ItemType Directory -Force -Path $Stage | Out-Null

# Everything that gets installed, in ONE place. Add a file here and the staging
# copy, the elevated copy, the unblock, the hash-verify and the summary below
# all pick it up.
$install = @(
    @{ Name = $DllName;   Src = $outDll },
    @{ Name = $VnobjName; Src = $built  }
)
foreach ($s in $stubOuts) { $install += @{ Name = (Split-Path -Leaf $s); Src = $s } }

foreach ($i in $install) {
    Copy-Item -Force -LiteralPath $i.Src -Destination (Join-Path $Stage $i.Name)
}

$dest = Join-Path $vnyan $DestRel

# A tiny .cmd is what gets elevated, and it reads only from C:\VNyanInstallTemp -
# a path the elevated session is guaranteed to see.
$copyCmd = Join-Path $Stage '_copy.cmd'
$copyLines = @(
    '@echo off',
    ('if not exist "' + $dest + '" mkdir "' + $dest + '"')
)
foreach ($i in $install) {
    $copyLines += ('copy /Y "' + (Join-Path $Stage $i.Name) + '" "' + $dest + '\"')
}
$copyLines | Set-Content -LiteralPath $copyCmd -Encoding ASCII

Info "  Requesting Administrator permission to write into the VNyan folder..."
try {
    Start-Process -FilePath $copyCmd -Verb RunAs -Wait -WindowStyle Hidden
} catch {
    Fail "The Administrator prompt was declined or failed, so nothing was copied into VNyan."
}

# Strip Mark-of-the-Web at the destination; VNyan silently refuses to load a
# blocked assembly and the Plugins panel is then simply empty, with no log line.
foreach ($i in $install) {
    Unblock-File -LiteralPath (Join-Path $dest $i.Name) -ErrorAction SilentlyContinue
}

# ----- 8) verify by comparing hashes -----------------------------------------

Info ""
Info "Step 8: verifying the installed files..."

$allOk = $true
foreach ($p in $install) {
    $d = Join-Path $dest $p.Name
    if (-not (Test-Path -LiteralPath $d)) {
        Err "  FAIL  $($p.Name) - not installed"
        $allOk = $false
        continue
    }
    $a = (Get-FileHash -LiteralPath $p.Src).Hash
    $b = (Get-FileHash -LiteralPath $d).Hash
    if ($a -eq $b) { Good "  PASS  $($p.Name)" }
    else {
        Err "  FAIL  $($p.Name) - installed copy does not match what was just built"
        $allOk = $false
    }
}

Cleanup

if (-not $allOk) {
    Err ""
    Err "*** Verification FAILED. The files in VNyan do not match the build output."
    Err "*** Most likely the Administrator prompt was declined, or VNyan was"
    Err "*** reopened partway through. Close VNyan and run this again."
    exit 1
}

Write-Host ""
Good "============================================================"
Good "  Done! Installed:"
foreach ($i in $install) { Good "    $DestRel\$($i.Name)" }
Good "============================================================"
Write-Host ""
Info "Now start VNyan, open the Plugins window, and click 'Warudo Importer'."
Info "If no plugin buttons appear at all, check that"
Info "  Settings > Misc > 'Allow Third Party Plugins' is ON."
Write-Host ""
exit 0
