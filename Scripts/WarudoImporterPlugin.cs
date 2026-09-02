using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace WarudoImporter
{
    /// <summary>
    /// VNyan plugin entry point: imports a .warudo (uMod) character mod as if it were a
    /// .vsfavatar.
    ///
    /// Pipeline: unpack the container -> load the AssetBundle inside it -> rebuild the humanoid
    /// rig and synthesise the VRM components VNyan expects -> seed VNyan's own avatar cache and
    /// let VNyan load it. Because the last step is VNyan's normal .vsfavatar path, everything
    /// downstream (face tracking, expressions, hand gestures, chains, colliders, node graph) works
    /// exactly as it does for a native avatar.
    ///
    /// Physics is deliberately not simulated here - VRChat PhysBone components inside a .warudo
    /// are dead scripts outside VRChat, so instead we generate a physbones.json for the PhysBones
    /// plugin, which already implements that simulation.
    /// </summary>
    public class WarudoImporterPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        const string BUTTON_NAME = "Warudo Importer";
        const string SETTINGS_ID = "WarudoImporter";
        const string LOG = "[WarudoImporter] ";

        // Assigned by the AssetBundle build (WarudoImporterBuild.cs).
        public GameObject windowPrefab;

        // ----- state -----
        string selectedPath;
        WarudoContainer container;
        WarudoBundle.Result loaded;
        GameObject template;              // what VNyan instantiates from
        bool templateHandedOff;           // true once VNyan's cache owns it - never destroy it then
        Transform stagingHolder;          // inactive, persistent parent that hides templates pre-import
        AvatarPrep.Result prep;
        ModRestore.Result restore;
        string unityProjectPath;
        VsfAvatarExport.Job exportJob;
        Texture2D coverTex;
        readonly Dictionary<HumanBodyBones, string> boneOverrides = new Dictionary<HumanBodyBones, string>();
        List<GenChain> detectedChains = new List<GenChain>();
        readonly GenOptions genOptions = new GenOptions();
        AvatarPrep.Options prepOptions = new AvatarPrep.Options();

        // Physics target: convert the mod's bone physics into VNyan's native DynamicBone during
        // import (Warudo's approach, self-contained), or leave it to the physbones.json path.
        bool convertDynBone = true;

        // ----- UI -----
        GameObject window;
        Text fileLabel, modInfoLabel, statusLabel;
        RawImage coverImage;
        RectTransform boneContent, chainContent;
        Font uiFont;
        readonly StringBuilder log = new StringBuilder();
        readonly Dictionary<string, Slider> sliders = new Dictionary<string, Slider>();
        readonly Dictionary<string, InputField> valueFields = new Dictionary<string, InputField>();
        GameObject tipBubble;
        string tipOwner;
        float tipLeaveAt = -1f;

        // Picker overlay (bone assignment).
        GameObject pickerWindow;
        RectTransform pickerContent;
        Text pickerTitle;
        HumanBodyBones pickerBone;

        static readonly string[] CATEGORY_TOGGLES = { "Hair", "Skirt", "Tail", "Ears", "Breast", "Misc" };

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            try { VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton(BUTTON_NAME, this); }
            catch (Exception e) { Debug.LogWarning(LOG + "registerPluginButton failed: " + e.Message); }

            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Parking a template under an INACTIVE PARENT hides it (activeInHierarchy false, so no
            // Awake/Update/rendering) while leaving the template's own activeSelf flag untouched.
            // That distinction matters: see the comment in VNyanBridge.Load.
            GameObject holder = new GameObject("WarudoImporterStaging");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);
            stagingHolder = holder.transform;

            // Must happen before any avatar bundle is loaded: components whose assembly is not
            // yet in the domain deserialize as dead scripts and lose the creator's tuning for
            // good. Doing it here, at startup, covers every later import.
            DynamicBoneConvert.PreloadPhysicsAssemblies();

            LoadSettings();
            SetupWindow();
            Log("Ready. VNyan loader hook: " + (VNyanBridge.Available ? "available" : "NOT AVAILABLE"));
            Log("Physics assemblies present: " + DynamicBoneConvert.DescribePhysicsAssemblies());
            Log("uMod relink: " + (UModRelink.Available
                ? "available - " + UModRelink.Describe()
                : "unavailable (" + (UModRelink.LastError ?? "?") + ")"));
            if (!VNyanBridge.Available) Log(VNyanBridge.Diagnose());
        }

        public void pluginButtonClicked()
        {
            if (window == null) { Log("Window prefab missing from the .vnobj."); return; }
            bool show = !window.activeSelf;
            window.SetActive(show);
            if (show) window.transform.SetAsLastSibling();
        }

        void Update()
        {
            // Our cache key is not a real file, so VNyan's avatar cache has to stay enabled or the
            // next avatar reload fails. It gets reset whenever VNyan reloads its app settings.
            VNyanBridge.HoldCacheOpen();

            // Tooltip bubbles stay while hovered and close 5s after the cursor leaves.
            if (tipBubble != null && tipBubble.activeSelf)
            {
                RectTransform rt = tipBubble.GetComponent<RectTransform>();
                bool over = rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, null);
                if (over) tipLeaveAt = -1f;
                else if (tipLeaveAt < 0f) tipLeaveAt = Time.unscaledTime + 5f;
                else if (Time.unscaledTime >= tipLeaveAt) HideTip();
            }
        }

        void OnDestroy()
        {
            if (coverTex != null) Destroy(coverTex);
        }

        // ------------------------------------------------------------------ actions

        void OnBrowse()
        {
            string start = string.IsNullOrEmpty(selectedPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Path.GetDirectoryName(selectedPath);

            string pick = null;
            try { pick = NativeFileDialog.OpenFile("Choose a Warudo model", start, "Warudo mods (*.warudo)", "*.warudo"); }
            catch (Exception e) { Log("File dialog failed: " + e.Message); }

            if (string.IsNullOrEmpty(pick))
            {
                // VNyan's own dialog as a fallback (some hosts block comdlg32).
                try { pick = VNyanInterface.VNyanInterface.VNyanUI.openLoadFileDialog(start, new string[] { "warudo" }); }
                catch { }
            }
            if (string.IsNullOrEmpty(pick)) return;

            selectedPath = pick;
            if (fileLabel != null) fileLabel.text = Path.GetFileName(pick);
            SaveSettings();
            Analyze();
        }

        /// <summary>Unpack + load + prepare, without handing anything to VNyan yet.</summary>
        void Analyze()
        {
            log.Length = 0;
            ReleasePrevious();

            if (string.IsNullOrEmpty(selectedPath) || !File.Exists(selectedPath))
            {
                Log("Pick a .warudo file first."); Flush(); return;
            }

            try
            {
                // NOT "WarudoImporter": VNyanSettings.saveSettings(SETTINGS_ID, ...) writes a
                // FILE with exactly that name into persistentDataPath, and the directory create
                // then fails with "a file or directory with the same name already exists".
                string cacheRoot = Path.Combine(Application.persistentDataPath, "WarudoImporterCache");
                container = WarudoContainer.Open(selectedPath, cacheRoot);
            }
            catch (Exception e) { Log("Could not open the container: " + e.Message); Flush(); return; }

            Log("Mod: " + container.DisplayName + "  v" + container.modVersion);
            if (!string.IsNullOrEmpty(container.author)) Log("Author: " + container.author);
            if (!string.IsNullOrEmpty(container.unityVersion)) Log("Built with Unity " + container.unityVersion +
                                                                  " (running on " + Application.unityVersion + ")");
            ShowCover();

            loaded = WarudoBundle.Load(container.bundlePath);
            if (!loaded.Ok) { Log("AssetBundle: " + loaded.error); Flush(); return; }
            Log("Loaded asset: " + loaded.assetName);
            Log(WarudoBundle.DescribeAssets(loaded.bundle));

            // Instantiate straight into the inactive staging parent. Creating it at the scene
            // root first would activate it for one frame, and every script on the mod would
            // run its Awake before we have had a chance to fix anything up.
            template = Instantiate(loaded.prefab, stagingHolder, false);
            template.name = container.DisplayName;
            // Hidden via the parent, not via SetActive(false) on the object itself - see the
            // comment in VNyanBridge.Load for why that distinction is load-bearing.
            template.SetActive(true);
            templateHandedOff = false;
            // The bundle container can go now; the assets it produced stay alive because the
            // instantiated copy references them.
            WarudoBundle.Release(loaded);

            // Rebuild the mod's real components from uMod's link data before anything inspects
            // the avatar. Everything downstream - physics conversion, native Magica Cloth, VRM
            // spring bones - depends on those components existing, and they do not until this
            // runs. No-ops when the uMod runtime is not on this machine.
            restore = ModRestore.Restore(template, container.bundlePath);
            for (int i = 0; i < restore.notes.Count; i++) Log(restore.notes[i]);

            // Fall back to driving uMod's own relinker, for the rare bundle this cannot read.
            if (restore.rebuilt == 0)
            {
                List<string> relinkNotes = new List<string>();
                int revived = UModRelink.Relink(template, relinkNotes);
                for (int i = 0; i < relinkNotes.Count; i++) Log(relinkNotes[i]);
                if (revived > 0)
                    Log("uMod relink: rebuilt " + revived + " original component(s) with the creator's " +
                        "authored values (" + UModRelink.Source + ").");
            }

            Log(WarudoBundle.Describe(template));
            Log(WarudoBundle.DescribeComponents(template));

            prepOptions.title = container.DisplayName;
            prepOptions.author = container.author;
            prepOptions.version = container.modVersion;
            prepOptions.thumbnail = coverTex;
            prepOptions.boneOverrides = boneOverrides;
            prepOptions.modBlendShapeAvatar = loaded.blendShapeAvatar;
            if (loaded.blendShapeAvatar != null)
                Log("Mod ships its own expression set: " +
                    VrmReflect.ClipCount(loaded.blendShapeAvatar) + " authored clips.");

            prep = AvatarPrep.Prepare(template, prepOptions);
            Log(prep.Summary());

            Animator anim = template.GetComponent<Animator>();

            // Collider radii are metres, so a chibi or a giant needs them rescaled. Measure once
            // per model and push it into the slider so the user starts from a sane value.
            float measured = PhysBonesGen.MeasureScale(anim);
            if (Mathf.Abs(measured - genOptions.scale) > 0.05f)
            {
                genOptions.scale = measured;
                Slider s;
                if (sliders.TryGetValue("PhysScale", out s) && s != null) s.value = measured;
                Log("Physics scale set to " + measured.ToString("0.##", CultureInfo.InvariantCulture) +
                    " from the model's leg length.");
            }

            detectedChains = PhysBonesGen.Detect(template, anim, genOptions);
            Log("Physics candidates found: " + detectedChains.Count);

            RebuildBoneList();
            RebuildChainList();
            Flush();
        }

        void OnImport()
        {
            if (template == null) { Log("Analyze a .warudo file first."); Flush(); return; }
            if (prep == null || !prep.Ok)
            {
                Log("Not importable yet - fix the errors above first.");
                Flush();
                return;
            }

            // Convert the mod's bone physics to live DynamicBone components BEFORE handing the
            // avatar over, so they self-simulate the moment VNyan instantiates it - the same way
            // Warudo converts VRC PhysBones to Dynamic Bones at load.
            if (convertDynBone)
            {
                if (DynamicBoneConvert.DynamicBoneAvailable)
                {
                    Animator anim = template.GetComponent<Animator>();
                    DynamicBoneConvert.Options dopt = new DynamicBoneConvert.Options();
                    dopt.gen = genOptions;
                    if (restore != null)
                    {
                        // The mod's own Magica Cloth and spring bones are alive now, so those
                        // chains are already simulated - converting them as well would fight.
                        dopt.preClaimed = restore.nativelyDriven;
                        if (restore.HasNativeCloth) dopt.fromMagicaCloth = false;
                        if (restore.HasNativeSpringBones) dopt.fromSpringBone = false;
                    }
                    DynamicBoneConvert.Result dres = DynamicBoneConvert.Convert(template, anim, dopt);
                    for (int i = 0; i < dres.notes.Count; i++) Log(dres.notes[i]);
                    Log("DynamicBone: " + dres.Total + " chain(s), " + dres.colliders + " collider(s).");
                }
                else Log("DynamicBone type not found in this VNyan build - physics not converted.");
            }

            string key = VNyanBridge.CacheKeyFor(selectedPath);
            if (VNyanBridge.Load(key, template))
            {
                templateHandedOff = true;
                Log("Handed to VNyan as \"" + Path.GetFileName(key) + "\".");
                Log("VNyan's avatar cache is now held open for this session - it has to be, because " +
                    "that key is not a real file on disk.");
                StartCoroutine(ReportPhysicsWhenReady());
            }
            else Log("Import failed: " + VNyanBridge.LastError);
            Flush();
        }

        /// <summary>
        /// Magica Cloth builds its simulation over the first frames after the avatar appears,
        /// so the honest answer to "is the physics actually running?" is only available a
        /// moment later. Reporting it beats asking the user to eyeball the hair.
        /// </summary>
        System.Collections.IEnumerator ReportPhysicsWhenReady()
        {
            yield return new WaitForSeconds(3f);

            GameObject avatar = null;
            try { avatar = VNyanInterface.VNyanInterface.VNyanAvatar.getAvatarObject() as GameObject; }
            catch (Exception e) { Log("Could not read back the avatar: " + e.Message); }

            if (avatar == null) { Log("Avatar not reachable yet - physics status unknown."); Flush(); yield break; }
            Log(ModRestore.DescribePhysicsState(avatar));
            Flush();
        }

        /// <summary>
        /// Adds the "Export .vsfavatar" button by cloning one of the existing ones and
        /// re-spacing the row, so the button matches the panel's styling without the .vnobj
        /// having to be rebuilt.
        /// </summary>
        void AddVsfAvatarButton()
        {
            if (Find<Button>("Button_ExportVsf") != null) return;
            Button phys = Find<Button>("Button_ExportPhys");
            if (phys == null) return;

            GameObject clone = Instantiate(phys.gameObject, phys.transform.parent);
            clone.name = "Button_ExportVsf";
            clone.transform.SetSiblingIndex(phys.transform.GetSiblingIndex() + 1);

            Text label = clone.GetComponentInChildren<Text>();
            if (label != null) label.text = "Export .vsfavatar";

            Button b = clone.GetComponent<Button>();
            if (b != null)
            {
                b.onClick.RemoveAllListeners();
                b.onClick.AddListener(OnExportVsfAvatar);
            }

            // A "?" tip tags along with the clone; it would open the physbones help.
            Transform stray = clone.transform.Find("Tip_ExportPhys");
            if (stray != null) Destroy(stray.gameObject);

            LayoutButtonRow(new string[] { "Button_Analyze", "Button_Import", "Button_ExportPhys", "Button_ExportVsf" });
        }

        /// <summary>Spreads the named buttons evenly across the span they already occupy.</summary>
        void LayoutButtonRow(string[] names)
        {
            var rects = new List<RectTransform>();
            for (int i = 0; i < names.Length; i++)
            {
                Button b = Find<Button>(names[i]);
                if (b == null) return;
                rects.Add(b.GetComponent<RectTransform>());
            }

            float left = float.MaxValue, right = float.MinValue;
            for (int i = 0; i < rects.Count; i++)
            {
                RectTransform rt = rects[i];
                float l = rt.anchoredPosition.x - rt.pivot.x * rt.sizeDelta.x;
                if (l < left) left = l;
                if (l + rt.sizeDelta.x > right) right = l + rt.sizeDelta.x;
            }

            const float gap = 6f;
            float width = (right - left - gap * (rects.Count - 1)) / rects.Count;
            if (width < 40f) return;   // too cramped to be worth rearranging

            for (int i = 0; i < rects.Count; i++)
            {
                RectTransform rt = rects[i];
                float l = left + i * (width + gap);
                rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
                rt.anchoredPosition = new Vector2(l + rt.pivot.x * width, rt.anchoredPosition.y);

                // Four labels in the space of three: let the text shrink rather than clip.
                Text t = rt.GetComponentInChildren<Text>();
                if (t != null)
                {
                    t.resizeTextForBestFit = true;
                    t.resizeTextMinSize = 9;
                    t.resizeTextMaxSize = t.fontSize;
                    t.horizontalOverflow = HorizontalWrapMode.Wrap;
                }
            }
        }

        /// <summary>
        /// Builds a real .vsfavatar. VNyan is a Unity player and players cannot build
        /// AssetBundles, so this hands the job to a Unity editor running headlessly.
        /// </summary>
        void OnExportVsfAvatar()
        {
            if (exportJob != null && !exportJob.Done)
            { Log("An export is already running - give it a moment."); Flush(); return; }

            if (string.IsNullOrEmpty(selectedPath) || !File.Exists(selectedPath))
            { Log("Choose a .warudo file first."); Flush(); return; }

            if (!VsfAvatarExport.ToolsetAvailable)
            {
                Log("The offline converter sources are not installed next to the plugin. Point me at " +
                    "the repo's UnityToolset\\Assets\\WarudoConvert folder.");
                Flush();
                string tp = NativeFileDialog.PickFolder("Locate UnityToolset\\Assets\\WarudoConvert",
                                                        VsfAvatarExport.PluginDir);
                if (string.IsNullOrEmpty(tp) || !VsfAvatarExport.LooksLikeToolset(tp))
                { Log("That folder does not contain the converter (Editor\\WarudoBatchConvert.cs)."); Flush(); return; }
                VsfAvatarExport.ConfiguredToolsetDir = tp;
                SaveSettings();
            }

            if (!VsfAvatarExport.IsUnityProject(unityProjectPath))
            {
                Log("Pick the Unity project to build in. It needs UniVRM and the shaders this mod " +
                    "uses already installed - the Warudo SDK project is the natural choice.");
                Flush();
                string picked = NativeFileDialog.PickFolder("Unity project to build the .vsfavatar in", unityProjectPath);
                if (string.IsNullOrEmpty(picked)) { Log("Cancelled."); Flush(); return; }
                if (!VsfAvatarExport.IsUnityProject(picked))
                { Log("That folder has no Assets\\ and ProjectSettings\\, so it is not a Unity project."); Flush(); return; }
                unityProjectPath = picked;
                SaveSettings();
            }

            string outDir = NativeFileDialog.PickFolder("Where to save the .vsfavatar",
                                                        Path.GetDirectoryName(selectedPath));
            if (string.IsNullOrEmpty(outDir)) { Log("Cancelled."); Flush(); return; }

            var opt = new VsfAvatarExport.Options();
            opt.warudoPath = selectedPath;
            opt.projectPath = unityProjectPath;
            opt.outputDir = outDir;
            opt.modUnityVersion = container != null ? container.unityVersion : null;
            opt.stripAnimators = prepOptions.stripNestedAnimators;
            opt.disableConstraints = prepOptions.disableConstraints;
            opt.writePhysBonesJson = !convertDynBone;

            var notes = new List<string>();
            exportJob = VsfAvatarExport.Start(opt, notes);
            for (int i = 0; i < notes.Count; i++) Log(notes[i]);
            Flush();

            if (exportJob != null) StartCoroutine(WatchExport(exportJob));
        }

        System.Collections.IEnumerator WatchExport(VsfAvatarExport.Job job)
        {
            int ticks = 0;
            while (!job.Done)
            {
                yield return new WaitForSeconds(5f);
                if (++ticks % 6 == 0)
                {
                    Log("...still building (" + (int)job.Elapsed.TotalSeconds + "s)");
                    Flush();
                }
            }

            string produced = job.ProducedFile();
            if (job.ExitCode == 0 && !string.IsNullOrEmpty(produced) && File.Exists(produced))
            {
                Log("Wrote " + produced + " (" + (new FileInfo(produced).Length / (1024 * 1024)) + " MB). " +
                    "Load it with VNyan's normal Load Avatar button - no plugin needed.");
            }
            else
            {
                Log("The .vsfavatar build failed (Unity exit code " + job.ExitCode + "). Last of its log:");
                Log(job.Tail(25));
                Log("Full log: " + job.logPath);
            }
            Flush();
        }

        void OnExportPhysBones()
        {
            if (template == null) { Log("Analyze a .warudo file first."); Flush(); return; }

            Animator anim = template.GetComponent<Animator>();
            List<GenChain> enabled = new List<GenChain>();
            for (int i = 0; i < detectedChains.Count; i++)
                if (detectedChains[i].enabled) enabled.Add(detectedChains[i]);

            if (convertDynBone)
                Log("NOTE: 'Convert to DynamicBone' is ON, so this avatar already has live physics. " +
                    "Running the PhysBones plugin on physbones.json too would double-drive the same " +
                    "bones (jitter). Turn DynamicBone off, or don't load this JSON in PhysBones.");

            string json = PhysBonesGen.BuildJson(template, anim, enabled, genOptions);
            string target = Path.Combine(Application.persistentDataPath, "physbones.json");
            string err;
            string written = PhysBonesGen.Write(json, target, out err);
            if (written == null) Log("Could not write physbones.json: " + err);
            else
            {
                Log("Wrote " + enabled.Count + " chains to " + written);
                Log("Open the PhysBones plugin and press Reload to pick it up.");
            }
            Flush();
        }

        void ReleasePrevious()
        {
            // A handed-off template is now VNyan's cache entry - AvatarCache.GetAvatarCache will
            // return it again on every future reload of this avatar, so it must never be
            // destroyed. An analyzed-but-never-imported template is only ours, so drop it here
            // rather than leaking one every time the user re-analyzes.
            if (template != null && !templateHandedOff) Destroy(template);
            template = null;

            if (loaded != null) WarudoBundle.Release(loaded);
            loaded = null;
            container = null;
            prep = null;
            restore = null;
            if (coverTex != null) { Destroy(coverTex); coverTex = null; }
            if (coverImage != null) coverImage.texture = null;
        }

        // ------------------------------------------------------------------ UI construction

        void SetupWindow()
        {
            if (windowPrefab == null) { Debug.LogWarning(LOG + "no window prefab in the bundle."); return; }
            try { window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab); }
            catch (Exception e) { Debug.LogWarning(LOG + "instantiateUIPrefab failed: " + e.Message); return; }
            if (window == null) return;

            RectTransform wrt = window.GetComponent<RectTransform>();
            if (wrt != null) wrt.anchoredPosition = Vector2.zero;

            fileLabel = Find<Text>("Label_File");
            modInfoLabel = Find<Text>("Label_ModInfo");
            statusLabel = Find<Text>("Label_Status");
            coverImage = Find<RawImage>("Image_Cover");
            boneContent = FindRect("BoneContent");
            chainContent = FindRect("ChainContent");
            pickerWindow = FindGO("Panel_Picker");
            pickerContent = FindRect("PickerContent");
            pickerTitle = Find<Text>("Label_PickerTitle");
            if (pickerWindow != null) pickerWindow.SetActive(false);

            WireButton("Button_Browse", OnBrowse);
            WireButton("Button_Analyze", Analyze);
            WireButton("Button_Import", OnImport);
            WireButton("Button_ExportPhys", OnExportPhysBones);
            WireButton("Button_Close", OnClose);
            WireButton("Button_PickerClose", ClosePicker);
            WireButton("Button_ChainsAll", delegate { SetAllChains(true); });
            WireButton("Button_ChainsNone", delegate { SetAllChains(false); });
            AddVsfAvatarButton();

            BindToggle("Toggle_StripAnimators", prepOptions.stripNestedAnimators,
                       delegate (bool v) { prepOptions.stripNestedAnimators = v; SaveSettings(); });
            BindToggle("Toggle_DisableConstraints", prepOptions.disableConstraints,
                       delegate (bool v) { prepOptions.disableConstraints = v; SaveSettings(); });
            BindToggle("Toggle_GenColliders", genOptions.generateColliders,
                       delegate (bool v) { genOptions.generateColliders = v; SaveSettings(); });
            BindToggle("Toggle_ConvertDynBone", convertDynBone,
                       delegate (bool v) { convertDynBone = v; SaveSettings(); });

            for (int i = 0; i < CATEGORY_TOGGLES.Length; i++)
            {
                string cat = CATEGORY_TOGGLES[i];
                BindToggle("Toggle_" + cat, CategoryEnabled(cat),
                           delegate (bool v) { SetCategory(cat, v); SaveSettings(); });
            }

            BindSlider("PhysScale", genOptions.scale, 0.25f, 4f,
                       delegate (float v) { genOptions.scale = v; SaveSettings(); });

            BuildTooltips();

            window.SetActive(false);
        }

        void OnClose()
        {
            if (window != null) window.SetActive(false);
        }

        // ----- bone mapping list -----

        /// <summary>
        /// Only the 15 bones Unity actually requires get a row - listing all 55 turns the panel
        /// into a wall, and fingers are optional anyway.
        /// </summary>
        void RebuildBoneList()
        {
            if (boneContent == null) return;
            ClearChildren(boneContent);
            if (prep == null || prep.boneMap == null)
            {
                AddRowLabel(boneContent, 0, prep != null && prep.animator != null && prep.animator.avatar != null &&
                                            prep.animator.avatar.isHuman
                    ? "Model already has a humanoid rig - nothing to map."
                    : "Analyze a model to see its bone mapping.");
                return;
            }

            HumanBodyBones[] req = HumanoidMapper.Required;
            float y = 0f;
            for (int i = 0; i < req.Length; i++)
            {
                HumanBodyBones b = req[i];
                Transform t;
                bool ok = prep.boneMap.map.TryGetValue(b, out t) && t != null;
                string label = b.ToString() + ":  " + (ok ? t.name : "<not found>");
                AddPickRow(boneContent, y, label, ok, b);
                y += 24f;
            }
            SetContentHeight(boneContent, y);
        }

        void AddPickRow(RectTransform parent, float y, string label, bool ok, HumanBodyBones bone)
        {
            GameObject row = new GameObject("BoneRow_" + bone, typeof(RectTransform));
            RectTransform rt = row.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(4f, -y);
            rt.sizeDelta = new Vector2(parent.rect.width - 12f, 22f);

            Text txt = MakeLabel(rt, "Label", label, 0f, 0f, rt.sizeDelta.x - 62f, 22f);
            txt.color = ok ? new Color(0.88f, 0.9f, 0.95f) : new Color(1f, 0.55f, 0.5f);

            Button btn = MakeButton(rt, "Pick", "Pick", rt.sizeDelta.x - 58f, 0f, 56f, 20f);
            HumanBodyBones captured = bone;
            btn.onClick.AddListener(delegate { OpenPicker(captured); });
        }

        void OpenPicker(HumanBodyBones bone)
        {
            if (pickerWindow == null || pickerContent == null || template == null) return;
            pickerBone = bone;
            if (pickerTitle != null) pickerTitle.text = "Assign " + bone;
            pickerWindow.SetActive(true);
            pickerWindow.transform.SetAsLastSibling();

            ClearChildren(pickerContent);
            Transform[] all = template.GetComponentsInChildren<Transform>(true);
            float y = 0f;
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                // Skinned-mesh nodes are never bones; hiding them keeps the list navigable.
                if (t.GetComponent<Renderer>() != null) continue;
                Button b = MakeButton(pickerContent, "Pick_" + i, t.name, 4f, y, pickerContent.rect.width - 16f, 20f);
                string name = t.name;
                b.onClick.AddListener(delegate { AssignBone(pickerBone, name); });
                y += 22f;
            }
            SetContentHeight(pickerContent, y);
        }

        void AssignBone(HumanBodyBones bone, string transformName)
        {
            boneOverrides[bone] = transformName;
            Log("Assigned " + bone + " -> " + transformName + ". Re-analyzing.");
            ClosePicker();
            Analyze();
        }

        void ClosePicker()
        {
            if (pickerWindow != null) pickerWindow.SetActive(false);
        }

        // ----- physics chain list -----

        void RebuildChainList()
        {
            if (chainContent == null) return;
            ClearChildren(chainContent);
            if (detectedChains == null || detectedChains.Count == 0)
            {
                AddRowLabel(chainContent, 0f, "No sway chains detected.");
                SetContentHeight(chainContent, 24f);
                return;
            }

            float y = 0f;
            for (int i = 0; i < detectedChains.Count; i++)
            {
                GenChain c = detectedChains[i];
                Toggle tg = MakeToggle(chainContent, "Chain_" + i,
                                       c.name + "   (" + c.category + ")", 4f, y,
                                       chainContent.rect.width - 16f, 20f);
                tg.isOn = c.enabled;
                GenChain captured = c;
                tg.onValueChanged.AddListener(delegate (bool v) { captured.enabled = v; });
                y += 22f;
            }
            SetContentHeight(chainContent, y);
        }

        void SetAllChains(bool on)
        {
            if (detectedChains == null) return;
            for (int i = 0; i < detectedChains.Count; i++) detectedChains[i].enabled = on;
            RebuildChainList();
        }

        // ------------------------------------------------------------------ settings

        void LoadSettings()
        {
            try
            {
                Dictionary<string, string> s = VNyanInterface.VNyanInterface.VNyanSettings.loadSettings(SETTINGS_ID);
                if (s == null) return;
                selectedPath = Get(s, "path", null);
                unityProjectPath = Get(s, "unityProject", null);
                VsfAvatarExport.ConfiguredToolsetDir = Get(s, "toolsetDir", null);
                prepOptions.stripNestedAnimators = GetBool(s, "stripAnimators", true);
                prepOptions.disableConstraints = GetBool(s, "disableConstraints", true);
                genOptions.generateColliders = GetBool(s, "genColliders", true);
                genOptions.includeHair = GetBool(s, "catHair", true);
                genOptions.includeSkirt = GetBool(s, "catSkirt", true);
                genOptions.includeTail = GetBool(s, "catTail", true);
                genOptions.includeEars = GetBool(s, "catEars", true);
                genOptions.includeBreast = GetBool(s, "catBreast", true);
                genOptions.includeMisc = GetBool(s, "catMisc", true);
                genOptions.scale = GetFloat(s, "physScale", 1f);
                convertDynBone = GetBool(s, "convertDynBone", true);
                // Optional: a Warudo install or Creator SDK folder to borrow the uMod runtime
                // from. Not shipped with the plugin - it is licensed middleware.
                UModRelink.ConfiguredPath = Get(s, "umodPath", null);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "loadSettings: " + e.Message); }
        }

        void SaveSettings()
        {
            try
            {
                Dictionary<string, string> s = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(selectedPath)) s["path"] = selectedPath;
                s["stripAnimators"] = prepOptions.stripNestedAnimators ? "1" : "0";
                s["disableConstraints"] = prepOptions.disableConstraints ? "1" : "0";
                s["genColliders"] = genOptions.generateColliders ? "1" : "0";
                s["catHair"] = genOptions.includeHair ? "1" : "0";
                s["catSkirt"] = genOptions.includeSkirt ? "1" : "0";
                s["catTail"] = genOptions.includeTail ? "1" : "0";
                s["catEars"] = genOptions.includeEars ? "1" : "0";
                s["catBreast"] = genOptions.includeBreast ? "1" : "0";
                s["catMisc"] = genOptions.includeMisc ? "1" : "0";
                s["physScale"] = genOptions.scale.ToString("0.###", CultureInfo.InvariantCulture);
                s["convertDynBone"] = convertDynBone ? "1" : "0";
                if (!string.IsNullOrEmpty(UModRelink.ConfiguredPath)) s["umodPath"] = UModRelink.ConfiguredPath;
                if (!string.IsNullOrEmpty(unityProjectPath)) s["unityProject"] = unityProjectPath;
                if (!string.IsNullOrEmpty(VsfAvatarExport.ConfiguredToolsetDir)) s["toolsetDir"] = VsfAvatarExport.ConfiguredToolsetDir;
                VNyanInterface.VNyanInterface.VNyanSettings.saveSettings(SETTINGS_ID, s);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "saveSettings: " + e.Message); }
        }

        static string Get(Dictionary<string, string> s, string k, string d)
        {
            string v; return s.TryGetValue(k, out v) ? v : d;
        }
        static bool GetBool(Dictionary<string, string> s, string k, bool d)
        {
            string v; return s.TryGetValue(k, out v) ? v == "1" : d;
        }
        static float GetFloat(Dictionary<string, string> s, string k, float d)
        {
            string v; float f;
            if (s.TryGetValue(k, out v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            return d;
        }

        bool CategoryEnabled(string cat)
        {
            switch (cat)
            {
                case "Hair": return genOptions.includeHair;
                case "Skirt": return genOptions.includeSkirt;
                case "Tail": return genOptions.includeTail;
                case "Ears": return genOptions.includeEars;
                case "Breast": return genOptions.includeBreast;
                default: return genOptions.includeMisc;
            }
        }

        void SetCategory(string cat, bool v)
        {
            switch (cat)
            {
                case "Hair": genOptions.includeHair = v; break;
                case "Skirt": genOptions.includeSkirt = v; break;
                case "Tail": genOptions.includeTail = v; break;
                case "Ears": genOptions.includeEars = v; break;
                case "Breast": genOptions.includeBreast = v; break;
                default: genOptions.includeMisc = v; break;
            }
        }

        // ------------------------------------------------------------------ misc UI helpers

        void ShowCover()
        {
            if (coverTex != null) { Destroy(coverTex); coverTex = null; }
            coverTex = container != null ? container.LoadCover() : null;
            if (coverImage != null)
            {
                coverImage.texture = coverTex;
                coverImage.enabled = coverTex != null;
            }
            if (modInfoLabel != null && container != null)
            {
                modInfoLabel.text = container.DisplayName + "\n" +
                                    (string.IsNullOrEmpty(container.author) ? "" : "by " + container.author + "\n") +
                                    "v" + container.modVersion + "  |  Unity " + container.unityVersion;
            }
        }

        void Log(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            log.AppendLine(s);
            Debug.Log(LOG + s);
        }

        void Flush()
        {
            if (statusLabel != null) statusLabel.text = log.ToString();
        }

        T Find<T>(string name) where T : Component
        {
            Transform t = FindDeep(window != null ? window.transform : null, name);
            return t != null ? t.GetComponent<T>() : null;
        }

        GameObject FindGO(string name)
        {
            Transform t = FindDeep(window != null ? window.transform : null, name);
            return t != null ? t.gameObject : null;
        }

        RectTransform FindRect(string name)
        {
            Transform t = FindDeep(window != null ? window.transform : null, name);
            return t as RectTransform;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        void WireButton(string name, UnityEngine.Events.UnityAction action)
        {
            Button b = Find<Button>(name);
            if (b != null) b.onClick.AddListener(action);
        }

        void BindToggle(string name, bool initial, UnityEngine.Events.UnityAction<bool> action)
        {
            Toggle t = Find<Toggle>(name);
            if (t == null) return;
            t.isOn = initial;
            t.onValueChanged.AddListener(action);
        }

        /// <summary>
        /// Sliders always ship with a manual-entry box; the box only writes back on submit so
        /// typing does not fight the slider, and the slider skips updating a focused box.
        /// </summary>
        void BindSlider(string key, float initial, float min, float max, UnityEngine.Events.UnityAction<float> action)
        {
            Slider s = Find<Slider>("Slider_" + key);
            InputField f = Find<InputField>("Value_" + key);
            if (s == null) return;
            s.minValue = min; s.maxValue = max; s.value = initial;
            sliders[key] = s;
            if (f != null)
            {
                valueFields[key] = f;
                f.text = initial.ToString("0.##", CultureInfo.InvariantCulture);
                f.onEndEdit.AddListener(delegate (string txt)
                {
                    float v;
                    if (!float.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return;
                    v = Mathf.Clamp(v, s.minValue, s.maxValue);
                    s.value = v;
                });
            }
            s.onValueChanged.AddListener(delegate (float v)
            {
                InputField fld;
                if (valueFields.TryGetValue(key, out fld) && fld != null && !fld.isFocused)
                    fld.text = v.ToString("0.##", CultureInfo.InvariantCulture);
                action(v);
            });
        }

        static void ClearChildren(RectTransform rt)
        {
            if (rt == null) return;
            for (int i = rt.childCount - 1; i >= 0; i--) Destroy(rt.GetChild(i).gameObject);
        }

        static void SetContentHeight(RectTransform rt, float h)
        {
            if (rt == null) return;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, Mathf.Max(h + 8f, 40f));
        }

        void AddRowLabel(RectTransform parent, float y, string text)
        {
            MakeLabel(parent, "Row", text, 4f, y, parent.rect.width - 16f, 22f);
        }

        Text MakeLabel(Transform parent, string name, string text, float x, float y, float w, float h)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            Text t = go.AddComponent<Text>();
            t.font = uiFont; t.fontSize = 12; t.text = text;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.color = new Color(0.88f, 0.9f, 0.95f);
            return t;
        }

        Button MakeButton(Transform parent, string name, string label, float x, float y, float w, float h)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            Image img = go.AddComponent<Image>();
            img.color = new Color(0.24f, 0.26f, 0.34f, 0.95f);
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            Text t = MakeLabel(rt, "Text", label, 0f, 0f, w, h);
            t.alignment = TextAnchor.MiddleCenter;
            return b;
        }

        Toggle MakeToggle(Transform parent, string name, string label, float x, float y, float w, float h)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);

            GameObject boxGo = new GameObject("Box", typeof(RectTransform));
            RectTransform box = boxGo.GetComponent<RectTransform>();
            box.SetParent(rt, false);
            box.anchorMin = new Vector2(0f, 1f); box.anchorMax = new Vector2(0f, 1f); box.pivot = new Vector2(0f, 1f);
            box.anchoredPosition = new Vector2(0f, -2f);
            box.sizeDelta = new Vector2(16f, 16f);
            Image bg = boxGo.AddComponent<Image>();
            bg.color = new Color(0.18f, 0.2f, 0.26f, 1f);

            GameObject markGo = new GameObject("Check", typeof(RectTransform));
            RectTransform mark = markGo.GetComponent<RectTransform>();
            mark.SetParent(box, false);
            mark.anchorMin = Vector2.zero; mark.anchorMax = Vector2.one;
            mark.offsetMin = new Vector2(3f, 3f); mark.offsetMax = new Vector2(-3f, -3f);
            Image check = markGo.AddComponent<Image>();
            check.color = new Color(0.45f, 0.72f, 1f, 1f);

            MakeLabel(rt, "Text", label, 20f, 0f, w - 22f, h);

            Toggle tg = go.AddComponent<Toggle>();
            tg.targetGraphic = bg;
            tg.graphic = check;
            return tg;
        }

        // ----- tooltips -----

        void BuildTooltips()
        {
            Dictionary<string, string> tips = new Dictionary<string, string>();
            tips["Browse"] = "Pick a .warudo file. It is a uMod container holding a Unity AssetBundle - " +
                             "the model, materials and skeleton are read straight out of it.";
            tips["Analyze"] = "Unpacks and inspects the mod without touching your current avatar: rig type, " +
                              "meshes, blendshapes, shaders and which humanoid bones were recognised.";
            tips["Import"] = "Hands the prepared model to VNyan's own avatar loader, so it behaves exactly " +
                             "like a .vsfavatar: tracking, expressions, gestures, chains and node graph all apply.";
            tips["ExportPhys"] = "Warudo models carry VRChat PhysBone components that do nothing outside " +
                                 "VRChat. This writes a physbones.json for the PhysBones plugin instead - " +
                                 "open it and press Reload.";
            tips["ConvertDynBone"] = "Convert the mod's physics into VNyan's built-in DynamicBone at import, " +
                                     "the way Warudo does. Revives VRChat PhysBones (via the bundled VRC " +
                                     "stubs) and VRM SpringBones and turns them into live Dynamic Bones - " +
                                     "no separate plugin needed. Leave the PhysBones-JSON export for when " +
                                     "you'd rather drive it through the PhysBones plugin instead.";
            tips["StripAnimators"] = "Removes Animators on child objects. They were authored for another host " +
                                     "and would fight VNyan's tracking on the root Animator.";
            tips["DisableConstraints"] = "Turns off rotation/position constraints baked into the mod, which " +
                                         "otherwise yank bones away from the tracked pose.";
            tips["GenColliders"] = "Also emit body colliders (head, chest, hips, arms, legs) so hair and skirts " +
                                   "do not sink through the model.";
            tips["PhysScale"] = "Multiplies every generated collider and bone radius. Raise it for tall models, " +
                                "lower it for chibi proportions.";
            tips["Chains"] = "Bones detected as swaying (hair, skirt, tail, ears, breast, misc). Untick anything " +
                             "you would rather keep rigid.";
            tips["Bones"] = "The 15 bones Unity requires for a humanoid rig. Red means auto-detection failed - " +
                            "use Pick to assign it, then Analyze again.";
            AddTooltips(tips);
        }

        void AddTooltips(Dictionary<string, string> tips)
        {
            foreach (KeyValuePair<string, string> kv in tips)
            {
                Button chip = Find<Button>("Help_" + kv.Key);
                if (chip == null) continue;
                string id = kv.Key;
                string body = kv.Value;
                RectTransform anchor = chip.GetComponent<RectTransform>();
                chip.onClick.AddListener(delegate { ToggleTip(id, body, anchor); });
            }
        }

        void ToggleTip(string id, string body, RectTransform anchor)
        {
            if (tipBubble != null && tipBubble.activeSelf && tipOwner == id) { HideTip(); return; }
            EnsureTipBubble();
            tipOwner = id;
            tipLeaveAt = -1f;

            Text t = tipBubble.transform.Find("Body").GetComponent<Text>();
            t.text = body;
            tipBubble.SetActive(true);
            tipBubble.transform.SetAsLastSibling();

            RectTransform brt = tipBubble.GetComponent<RectTransform>();
            RectTransform parent = window.GetComponent<RectTransform>();
            Vector3 local = parent.InverseTransformPoint(anchor.position);
            float halfW = parent.rect.width * 0.5f;
            float x = Mathf.Clamp(local.x, -halfW + 150f, halfW - 150f);
            brt.localPosition = new Vector3(x, local.y + 70f, 0f);
        }

        void EnsureTipBubble()
        {
            if (tipBubble != null) return;
            tipBubble = new GameObject("Tooltip", typeof(RectTransform));
            RectTransform rt = tipBubble.GetComponent<RectTransform>();
            rt.SetParent(window.transform, false);
            rt.sizeDelta = new Vector2(280f, 96f);
            Image bg = tipBubble.AddComponent<Image>();
            bg.color = new Color(0.09f, 0.10f, 0.14f, 0.98f);

            Text body = MakeLabel(rt, "Body", "", 8f, 8f, 244f, 80f);
            body.fontSize = 11;
            body.alignment = TextAnchor.UpperLeft;

            Button close = MakeButton(rt, "Close", "x", 258f, 4f, 16f, 16f);
            close.onClick.AddListener(HideTip);
            tipBubble.SetActive(false);
        }

        void HideTip()
        {
            if (tipBubble != null) tipBubble.SetActive(false);
            tipOwner = null;
            tipLeaveAt = -1f;
        }
    }
}
