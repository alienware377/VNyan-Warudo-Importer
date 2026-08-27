# Warudo Importer for VNyan

Load `.warudo` character mods in VNyan and get the same feature set you would from a
`.vsfavatar`: face and body tracking, expressions, hand gestures, chains and colliders, and
the node graph. No repacking, no conversion step, no files patched on disk.

---

## Install — one click

> **The prebuilt files in `dist/` load on VNyan's Unity 2022.3 runtime. You don't need Unity
> or a compiler.**

1. Download or clone this repository.
2. **Close VNyan completely** (check the system tray). The plugin DLL is locked while VNyan
   has it loaded, and the installers will refuse to run rather than half-install.
3. Double-click **`INSTALL_PORTABLE.bat`**.
   It opens a folder browser so you can point it at your VNyan install, asks for
   Administrator permission if that folder needs it, then copies the files in and
   hash-checks them against `dist/` (you'll see `PASS` per file).
4. Start VNyan, open the **Plugins** window, and click **Warudo Importer**.

> If your VNyan is at the default location (`C:\Program Files\VNyan`), you can use
> **`INSTALL.bat`** instead — it skips the folder picker.

Both installers copy four files into `VNyan\Items\Assemblies\WarudoImporter\`:

| File | What it is |
|------|------------|
| `WarudoImporter.dll` | the plugin itself |
| `WarudoImporter.vnobj` | the plugin's window, as an asset bundle |
| `VRC.Dynamics.dll` | VRChat stub assembly (see below) |
| `VRC.SDK3.Dynamics.PhysBone.dll` | VRChat stub assembly (see below) |

**About the two VRC files.** A `.warudo` built with the VRChat SDK carries VRCPhysBone
components, and outside VRChat those load as dead "missing script" placeholders — the
creator's physics tuning is right there in the file but unreadable. These two small
assemblies re-declare those classes with the exact names and fields the SDK uses, so VNyan
binds the mod's components to them and the real values come back. That's what lets the
importer convert the creator's actual physics instead of guessing. They contain no VRChat
code and simulate nothing; they're data shells. Leave them out and VRChat-authored models
still work, just with generic auto-detected physics.

If you're only updating the code, redeploying `WarudoImporter.dll` is enough — the `.vnobj`
and the two VRC files can stay where they are.

**A note on the staging folder.** The installers copy the two files into
`C:\VNyanInstallTemp` before the elevated copy into VNyan, then delete it. That's not
busywork: an elevated session doesn't see mapped or virtual cloud drives, so an elevated
copy taken straight out of this repo can fail silently when the repo lives on one. Staging
onto a real local disk first is what makes it work.

---

## Usage

1. Open the **Plugins** panel and click **Warudo Importer**.
2. **Browse .warudo** — pick the mod file.
3. **Analyze** — the mod's cover art and info appear, and the log fills in. Read it. It
   tells you what rig the mod ships, what got mapped, and what didn't.
4. Check the **Humanoid bones** list. Anything shown in red couldn't be matched
   automatically — click **Pick** on that row and choose the right transform from the model.
5. **Import into VNyan** — the model loads as your avatar.

The **Sway chains** section (Hair / Skirt / Tail / Ears / Breast / Misc) controls which bone
chains get detected for the physics export described below.

---

## How it works

A `.warudo` file is a uMod container: a 12-byte `UMOD` header followed by a ZIP holding
`modinfo.dat`, `sharedassets.bin` and `sharedassets.meta`. The interesting part,
`sharedassets.bin`, is a stock Unity AssetBundle.

The plugin unpacks the container, loads that bundle, rebuilds a humanoid rig if the mod only
ships a Generic one, and synthesises the VRM components VNyan expects — `VRMMeta`,
`VRMHumanoidDescription`, `VRMBlendShapeProxy`, `VRMFirstPerson`, `VRMLookAtHead`. It then
seeds VNyan's own avatar cache and asks VNyan to load it, so VNyan runs its normal
`.vsfavatar` path on the model. That's why tracking, expressions, gestures and the node
graph behave exactly as they do for any other avatar.

Nothing on disk is modified. Your `.warudo` file is left untouched.

### Materials are never substituted

Poiyomi and other shaders travel compiled inside the bundle and are used as-is. MToon is
never swapped in, and no material is replaced with a stand-in. What the mod author shipped
is what renders.

---

## Physics

There are two ways to give the model working hair/skirt/breast physics. Pick one — running
both on the same bones makes them fight and jitter.

### Option A — Convert to DynamicBone (default, like Warudo)

The **Convert physics to DynamicBone** toggle (on by default) turns the mod's physics into
VNyan's own built-in DynamicBone components right as the avatar is imported — exactly what
Warudo does when it converts VRC PhysBones to Dynamic Bones at load. Nothing else to click,
no second plugin: the physics is live the moment the model appears. It converts, in order:

- **VRChat PhysBones** — revived from the mod using the two bundled VRC stub assemblies
  (`VRC.Dynamics.dll`, `VRC.SDK3.Dynamics.PhysBone.dll`, installed alongside the plugin), so
  the creator's actual pull/spring/stiffness/radius and colliders come through, then mapped
  to DynamicBone with VRChat's own conversion math (run in reverse).
- **VRM Spring Bones** — mapped directly (VNyan already understands these).
- Anything left over — a light auto-detected sway setup on hair/skirt/tail/ears/breast bones,
  so even a mod that shipped neither still moves.

### Option B — Export for the PhysBones plugin

If you'd rather drive the physics through the
[VNyan PhysBones plugin](https://github.com/alienware377/VNyan-PhysBones-BreastPhysics-PoseStudio),
turn the DynamicBone toggle **off**, then:

1. Click **Export physbones.json**.
2. Press **Reload** in the PhysBones plugin.

The file is written to `%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\physbones.json`; any
existing file is backed up with a timestamped `.bak` first, so your own tuning is never lost.

---

## Troubleshooting

**1. There's no "Warudo Importer" button in the Plugins panel.**
Go to VNyan → **Settings → Misc → Allow Third Party Plugins** and make sure it's **on**.
When it's off, VNyan loads no assembly plugins at all and logs nothing about it — the panel
is silently empty. This is the cause in most cases, so check it first.

**2. You have several VNyan installs and launched a different one.**
The tell-tale in `Player.log` is:

```
SocketException: Only one usage of each socket address (protocol/network address/port) is normally permitted
```

Confirm which copy is actually running from the log's header line,
`Loading player data from <path>`, then install into that copy.

**3. "Import failed: this VNyan build does not expose the avatar loader".**
A VNyan update moved the internal loader the plugin hooks. Use the offline converter below
to produce a `.vsfavatar` instead.

**4. The model appears untextured or pink.**
The mod's shader wasn't included in its bundle. There's nothing the importer can do about
that — the shader simply isn't in the file.

**5. Where the log is.**

```
C:\Users\<you>\AppData\LocalLow\Suvidriel\VNyan\Player.log
```

---

## Offline converter (`.warudo` → `.vsfavatar`)

`UnityToolset\Assets\WarudoConvert\` is a drop-in folder for a Unity project that already
has UniVRM **and the shaders the mod uses** installed. The Warudo SDK project is the ideal
host, since it has both by definition.

Copy the folder into that project's `Assets`, then use the menu
**Warudo → Convert .warudo to .vsfavatar**. Two steps:

1. **Stage into project** — writes the bundle's meshes, materials and textures out as real
   project assets. Textures are re-encoded as PNG because bundle textures are GPU-only; on
   large models this is slow.
2. **Build .vsfavatar** — produces the avatar file.

**Caveat, honestly:** if the project doesn't have the mod's shader installed, those materials
will render pink. Install the same shader the mod uses. Don't substitute a different one —
the result won't look like the model the author made.

---

## Known limits

- Mods that aren't character mods (no `sharedassets.bin`) are rejected.
- Models whose rig can't be mapped to a humanoid need manual bone assignment via **Pick**.
- Animations and AnimatorControllers shipped in the mod are not carried over.
- MagicaCloth and DynamicBone setups authored in the mod are not converted. Use the
  `physbones.json` export instead.

---

## Rebuild from source

The prebuilt `dist/` files should load on any VNyan 2022.3 install. If they don't (check
`Player.log` for a bundle-version error), rebuild against your own VNyan and Unity by
running **`INSTALL_BUILD.bat`**. It picks your VNyan folder, finds your Unity, recompiles
the DLL against your VNyan's assemblies, rebuilds the `.vnobj`, installs both, and
hash-verifies the result.

Requirements: the full source tree (`Scripts\`, `_unitybuild\`, `dist\`) and Unity 2022.3.x.
Unity also supplies the Roslyn compiler the sources need — the in-box .NET Framework
`csc.exe` is C# 5 and can't build them.

Manually, the same two steps are:

```
<Unity>\Editor\Data\Tools\Roslyn\csc.exe -noconfig -nostdlib+ @build\build.rsp
Unity.exe -batchmode -quit -nographics -projectPath "<repo>\_unitybuild" -executeMethod WarudoImporterBuild.Build
```

`build\build.rsp` pulls its references straight from
`C:\Program Files\VNyan\VNyan_Data\Managed`. `build_vnobj.bat` runs both steps and stages the
output into `dist\`.

> **Critical:** VNyan loads the autostart prefab by the addressable name `vnyanitem`, and the
> prefab file must be named `VNyanTemp.prefab` (the saved root is renamed to match the file
> name). The build script sets both. If you rebuild manually and the plugin button never
> appears, check those two things first.

After rebuilding by hand, copy the new `.dll` and `.vnobj` into `dist\` before running an
installer.
