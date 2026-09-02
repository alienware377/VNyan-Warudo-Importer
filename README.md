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

### Why a mod's own physics arrives empty (and the one way to get it back)

If you have ever wondered why an imported model's hair physics doesn't match what the creator
built, this is why — and it is worth understanding before blaming the importer.

A `.warudo` is built by the uMod pipeline, and uMod does **not** store mod components the way
Unity normally does. Every MonoBehaviour on the model is replaced at build time by uMod's own
`LinkBehaviourV2`, which records the original type name and its field values; the real
component is reconstructed at load time by uMod's relinker. Warudo runs that relinker, so it
sees the real thing. Any other host sees only dead placeholder scripts.

That single fact explains all of it: Magica Cloth, VRM spring bones and VRChat PhysBones all
arrive empty in VNyan, and no amount of having the right assemblies installed changes it,
because the values were never inside a component to begin with.

**Getting it back — solved.** The importer reads that placeholder data straight out of the
AssetBundle and rebuilds the real components itself. Nothing from uMod has to be installed.

Warudo bundles carry full type trees, so the stripped data is self-describing: the importer
walks it, resolves the link graph, and puts every member back on a freshly added component by
name. That means the model gets the creator's **actual** settings, not an approximation —
every curve, every stiffness value, every collider assignment, including fields this importer
has never heard of.

What comes back, when the runtime for it exists:

| Original component | In VNyan |
| --- | --- |
| **Magica Cloth 2** (cloth + capsule/sphere colliders) | Rebuilt and simulated **natively** — VNyan ships MagicaClothV2 |
| **VRM spring bones** and collider groups | Rebuilt and simulated natively — VNyan ships UniVRM |
| **VRChat PhysBones** | Rebuilt, then converted to DynamicBone with the creator's real numbers |

Anything driven by a restored simulation is excluded from the sway-chain detector, so nothing
ends up simulated twice.

The log tells you what actually happened, a few seconds after the avatar appears:

```
Restored the creator's own components: rebuilt 41 (10x MagicaCloth, 18x MagicaCapsuleCollider, 7x MagicaSphereCollider, ...)
Left 436 bone(s) to the mod's own physics.
Physics running: 9/10 Magica Cloth built, 9 simulating; 4 DynamicBone chain(s)
```

A cloth that reports as not simulating is usually an **empty component the author left on the
model** — one with no root bones and no renderer has nothing to simulate, and it fails in
Warudo too. The log names it so you can check.

Bundles sometimes contain two hierarchies (the Warudo character *and* a VRM export of it).
Components belonging to the other one are reported separately, not as failures.

### Expressions and Perfect Sync (ARKit)

Two separate things happen here, and both matter.

**VRM expressions.** Mouth shapes and emotions are mapped onto the standard VRM clips —
`A/I/U/E/O`, `Blink` (plus `Blink_L`/`Blink_R`), `Joy`, `Angry`, `Sorrow`, `Fun` and the four
look directions. The mapper recognises VRoid's `Fcl_*` names, VRChat viseme spellings
(`vrc.v_aa`), Japanese kana (`あいうえお`), and `_L`/`Left` suffix variants, so most models
land on the right clips without any manual work.

**The mod's own expression set wins.** Many mods ship a complete authored `BlendShapeAvatar`
inside the bundle. Unlike components, those load fine, so when one is present it is used as-is
and the importer only fills in what it does not already define. Authored clips carry binary
flags, material bindings and multi-mesh bindings that cannot be recovered by reading mesh
names, so replacing them with reconstructions would be a downgrade.

**Every blendshape is exposed.** Beyond the VRM presets and ARKit, every remaining shape on
the meshes gets its own named clip, so custom expressions and toggles are reachable from
VNyan's expression system and node graph. A clip binds **every** mesh carrying that shape —
outfits routinely duplicate the body's shapes across several meshes, and binding only the
first would move the body while the clothes stayed put.

**Perfect Sync.** If the model carries the 52 ARKit shapes, the importer creates a blendshape
clip for each one so face tracking can actually reach them. This is not optional and it is
easy to get wrong: VNyan applies tracking through the VRM blendshape proxy, looking clips up
**by name** — it never reads mesh blendshapes directly. A model can ship all 52 ARKit shapes
and still sit completely frozen until each one also exists as a clip.

Each shape is registered under both its authored spelling and a lower-case one
(`jawOpen` and `jawopen`), because the name comparison is case-sensitive and hosts differ on
whether they lower-case incoming tracking names. Both clips share a single binding, and only
the name the host actually asks for is ever applied, so nothing is driven twice.

The status log tells you what happened — for example
`Perfect Sync: 104 ARKit clips created`. If a model has no ARKit shapes authored into it,
that line is absent and only the VRM expressions above are available; nothing the importer
does can invent shapes the model doesn't have.

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

## Exporting a `.vsfavatar`

A `.vsfavatar` is a Unity AssetBundle, and **only the Unity editor can build one** — a running
player has no equivalent API. So the importer does not pretend to build one itself; it drives a
Unity editor instead.

### From inside VNyan — **Export .vsfavatar**

Analyze a model, then press **Export .vsfavatar**. The first time it asks for a Unity project
to build in; after that it just asks where to save. It copies the converter into that project,
runs Unity headlessly, and reports back in the log when the file is ready. The result is an
ordinary `.vsfavatar` that VNyan loads with its normal **Load Avatar** button — no plugin
needed on the machine that uses it.

Two things the project must satisfy:

- **UniVRM and the shaders the mod uses** have to be installed, or the avatar comes out
  without VRM components and with pink materials. The Warudo SDK project is the natural
  choice, since it has both by definition.
- **Its Unity version must be at least the mod's.** AssetBundles are forward compatible only,
  so a 2019.4 project cannot open a bundle built with 2021.3. The importer checks this before
  launching Unity and tells you rather than letting it fail ten minutes in.

The first run in a project imports the whole thing and can take several minutes.

### By hand, in the editor

`UnityToolset\Assets\WarudoConvert\` is the same converter as a drop-in folder. Copy it into
the project's `Assets`, then use the menu **Warudo → Convert .warudo to .vsfavatar**. Two steps:

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
- Bundles compressed with LZMA cannot be read. Warudo's default is LZ4, so this is rare.
- A restored component only comes back if a runtime for it exists. Magica Cloth 2, UniVRM
  spring bones and DynamicBone all ship with VNyan; anything else is reported and skipped.

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
