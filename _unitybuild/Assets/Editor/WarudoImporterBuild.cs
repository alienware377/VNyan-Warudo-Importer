using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Builds the Warudo Importer window prefab + starter prefab and bundles them into a
// .vnobj AssetBundle. Run headlessly via:
//   Unity.exe -batchmode -quit -nographics -projectPath <proj> -executeMethod WarudoImporterBuild.Build
//
// The window prefab honours the control-name contract expected by
// WarudoImporter.WarudoImporterPlugin, which resolves every control by GameObject name
// through a deep transform walk (FindDeep). Renaming anything here silently breaks the
// plugin at runtime.
public static class WarudoImporterBuild
{
    public static void Build()
    {
        const string windowPrefabPath = "Assets/WarudoImporterWindow.prefab";
        // SaveAsPrefabAsset renames the saved root to match the FILE name, so the file itself has
        // to be VNyanTemp.prefab - naming the in-scene GameObject is not enough. A different root
        // name makes VNyan log "The Object you want to instantiate is null" and the plugin button
        // never appears.
        const string starterPrefabPath = "Assets/VNyanTemp.prefab";
        const string bundleName = "warudoimporter";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);

        // Starter prefab carries the plugin component, pointed at the window prefab so the
        // window is pulled into the bundle as a dependency.
        // VNyan loads the bundle's addressable "vnyanitem"; the root name "VNyanTemp"
        // matches the official SDK output. Both are load-bearing.
        GameObject go = new GameObject("VNyanTemp");
        WarudoImporter.WarudoImporterPlugin plugin = go.AddComponent<WarudoImporter.WarudoImporterPlugin>();
        plugin.windowPrefab = windowAsset;
        GameObject starterAsset = PrefabUtility.SaveAsPrefabAsset(go, starterPrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (starterAsset == null)
        {
            Debug.LogError("[WarudoImporterBuild] failed to save the starter prefab");
            EditorApplication.Exit(2);
            return;
        }

        AssetBundleBuild abb = new AssetBundleBuild
        {
            assetBundleName = bundleName,
            assetNames = new[] { starterPrefabPath },
            addressableNames = new[] { "vnyanitem" }
        };

        Directory.CreateDirectory(outDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outDir, new[] { abb }, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("[WarudoImporterBuild] BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "WarudoImporter.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[WarudoImporterBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

    // ------------------------------------------------------------------ window prefab

    static DefaultControls.Resources _res;

    const float W = 560f;
    const float H = 686f;   // +26 for the DynamicBone toggle row
    const float P = 12f;
    const float CX = P;                 // content left
    const float CW = W - 2f * P;        // content width (536)

    static GameObject BuildWindowPrefab(string prefabPath)
    {
        _res = new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd")
        };

        GameObject root = new GameObject("WarudoImporterWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(W, H);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.13f, 0.13f, 0.15f, 0.97f);

        // Drag anywhere on the panel background moves the window.
        root.AddComponent<WarudoImporter.WindowDrag>();

        Transform R = root.transform;

        // ---- header ------------------------------------------------------
        MakeText(R, "Title", "Warudo Importer", CX, 8f, CW - 34f, 24f,
                 16, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeButton(R, "Button_Close", "X", W - P - 26f, 8f, 26f, 24f, 13);

        // ---- file row ----------------------------------------------------
        GameObject browse = MakeButton(R, "Button_Browse", "Browse .warudo…", CX, 38f, 160f, 26f, 12);
        MakeHelp(browse, "Browse");
        Text file = MakeText(R, "Label_File", "(no .warudo chosen)", CX + 168f, 38f, CW - 168f, 26f,
                             12, TextAnchor.MiddleLeft, FontStyle.Normal);
        file.color = new Color(0.80f, 0.84f, 0.92f);

        // ---- cover art + mod info + actions ------------------------------
        const float coverY = 70f;
        const float coverS = 110f;

        // A dark frame behind the cover so the box reads as a slot when empty.
        GameObject frame = new GameObject("CoverFrame", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        Image fimg = frame.GetComponent<Image>();
        fimg.sprite = _res.background;
        fimg.type = Image.Type.Sliced;
        fimg.color = new Color(0.07f, 0.07f, 0.09f, 1f);
        Place(frame.transform, R, CX, coverY, coverS, coverS);

        GameObject cover = new GameObject("Image_Cover", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        RawImage rawi = cover.GetComponent<RawImage>();
        rawi.color = Color.white;
        rawi.raycastTarget = false;
        Place(cover.transform, frame.transform, 3f, 3f, coverS - 6f, coverS - 6f);

        float rx = CX + coverS + 10f;              // right column x
        float rw = CX + CW - rx;                   // right column width

        Text info = MakeText(R, "Label_ModInfo", "no mod loaded", rx, coverY, rw, 56f,
                             12, TextAnchor.UpperLeft, FontStyle.Normal);
        info.horizontalOverflow = HorizontalWrapMode.Wrap;
        info.color = new Color(0.86f, 0.89f, 0.95f);

        // Action buttons live in the right column beside the cover art.
        const float gap = 8f;
        float abw = (rw - 2f * gap) / 3f;
        float aby = coverY + 60f;
        GameObject bAn = MakeButton(R, "Button_Analyze", "Analyze", rx + 0f * (abw + gap), aby, abw, 30f, 12);
        GameObject bIm = MakeButton(R, "Button_Import", "Import into VNyan", rx + 1f * (abw + gap), aby, abw, 30f, 11);
        GameObject bEx = MakeButton(R, "Button_ExportPhys", "Export physbones.json", rx + 2f * (abw + gap), aby, abw, 30f, 10);
        MakeHelp(bAn, "Analyze");
        MakeHelp(bIm, "Import");
        MakeHelp(bEx, "ExportPhys");

        // ---- options -----------------------------------------------------
        float y = coverY + coverS + 8f;            // 188

        MakeHeader(R, "Hdr_Options", "— Options —", y);
        y += 22f;

        GameObject t1 = MakeToggle(R, "Toggle_StripAnimators", "Strip nested Animators", CX, y, 320f, 22f, true);
        MakeHelp(t1, "StripAnimators");
        y += 24f;

        GameObject t2 = MakeToggle(R, "Toggle_DisableConstraints", "Disable baked constraints", CX, y, 320f, 22f, true);
        MakeHelp(t2, "DisableConstraints");
        y += 24f;

        GameObject t3 = MakeToggle(R, "Toggle_GenColliders", "Generate body colliders", CX, y, 320f, 22f, true);
        MakeHelp(t3, "GenColliders");
        y += 24f;

        GameObject t4 = MakeToggle(R, "Toggle_ConvertDynBone", "Convert physics to DynamicBone (Warudo-style)", CX, y, 360f, 22f, true);
        MakeHelp(t4, "ConvertDynBone");
        y += 26f;

        SliderRow("PhysScale", "Physics scale", 0.25f, 4f, 1f, R, y);
        y += 36f;                                   // row pitch (32) + a little air

        // ---- sway chains -------------------------------------------------
        GameObject hdrChains = MakeHeader(R, "Hdr_Chains", "— Sway chains —", y);
        MakeHelp(hdrChains, "Chains");
        y += 22f;

        string[] cats = { "Hair", "Skirt", "Tail", "Ears", "Breast", "Misc" };
        float cw3 = (CW - 2f * gap) / 3f;
        for (int i = 0; i < cats.Length; i++)
        {
            int col = i % 3;
            int row = i / 3;
            MakeToggle(R, "Toggle_" + cats[i], cats[i],
                       CX + col * (cw3 + gap), y + row * 24f, cw3, 22f, true);
        }
        y += 2f * 24f + 4f;

        MakeButton(R, "Button_ChainsAll", "All", CX, y, 64f, 22f, 12);
        MakeButton(R, "Button_ChainsNone", "None", CX + 72f, y, 64f, 22f, 12);
        y += 26f;

        MakeScroll(R, "ChainScrollView", "ChainContent", CX, y, CW, 64f, true);
        y += 64f + 6f;

        // ---- humanoid bones ----------------------------------------------
        GameObject hdrBones = MakeHeader(R, "Hdr_Bones", "— Humanoid bones —", y);
        MakeHelp(hdrBones, "Bones");
        y += 22f;

        MakeScroll(R, "BoneScrollView", "BoneContent", CX, y, CW, 78f, true);
        y += 78f + 6f;

        // ---- status log ---------------------------------------------------
        // The log Text *is* the scroll content, grown by a ContentSizeFitter so long
        // logs scroll instead of clipping.
        float statusH = H - y - P;
        RectTransform statusRt = MakeScroll(R, "StatusScrollView", "Label_Status", CX, y, CW, statusH, true);
        Text status = statusRt.gameObject.AddComponent<Text>();
        status.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        status.fontSize = 11;
        status.alignment = TextAnchor.UpperLeft;
        status.color = new Color(0.84f, 0.87f, 0.93f);
        status.horizontalOverflow = HorizontalWrapMode.Wrap;
        status.verticalOverflow = VerticalWrapMode.Overflow;
        status.text = "ready";
        ContentSizeFitter fitter = statusRt.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // ---- bone picker overlay (starts inactive) -------------------------
        BuildPicker(root);

        rrt.sizeDelta = new Vector2(W, H);

        GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return asset;
    }

    // A modal-ish overlay listing every transform in the model, so a humanoid bone can be
    // reassigned by hand. Rows are generated at runtime into "PickerContent".
    static void BuildPicker(GameObject window)
    {
        const float PW = 320f;
        const float PH = 430f;

        GameObject panel = new GameObject("Panel_Picker",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        Image pbg = panel.GetComponent<Image>();
        pbg.sprite = _res.background;
        pbg.type = Image.Type.Sliced;
        pbg.color = new Color(0.10f, 0.11f, 0.14f, 0.99f);
        Place(panel.transform, window.transform, (W - PW) * 0.5f, (H - PH) * 0.5f, PW, PH);

        MakeText(panel.transform, "Label_PickerTitle", "Assign bone", 10f, 8f, PW - 96f, 22f,
                 13, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeButton(panel.transform, "Button_PickerClose", "Close", PW - 82f, 8f, 72f, 22f, 12);

        MakeScroll(panel.transform, "PickerScrollView", "PickerContent",
                   10f, 36f, PW - 20f, PH - 36f - 10f, true);

        panel.SetActive(false);
    }

    // ------------------------------------------------------------------ helpers

    // Position a control under parent using top-left anchoring (y grows downward).
    static RectTransform Place(Transform t, Transform parent, float x, float y, float w, float h)
    {
        RectTransform rt = t as RectTransform ?? t.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, -y);
        return rt;
    }

    static Text MakeText(Transform parent, string name, string text,
        float x, float y, float w, float h, int size, TextAnchor anchor, FontStyle style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        Text t = go.GetComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = anchor;
        t.color = Color.white;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        Place(go.transform, parent, x, y, w, h);
        return t;
    }

    static GameObject MakeHeader(Transform parent, string name, string label, float y)
    {
        Text t = MakeText(parent, name, label, CX, y, CW, 18f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        t.color = new Color(0.62f, 0.72f, 0.95f);
        return t.gameObject;
    }

    static GameObject MakeButton(Transform parent, string name, string label,
        float x, float y, float w, float h, int size)
    {
        GameObject go = DefaultControls.CreateButton(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        // The label child is "Text" or "Text (Legacy)" depending on the Unity version, so
        // grab the component rather than the child name.
        Text bt = go.GetComponentInChildren<Text>(true);
        if (bt != null)
        {
            bt.text = label;
            bt.color = new Color(0.05f, 0.05f, 0.07f);
            bt.fontSize = size;
            bt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            bt.alignment = TextAnchor.MiddleCenter;
            bt.horizontalOverflow = HorizontalWrapMode.Overflow;
            bt.verticalOverflow = VerticalWrapMode.Overflow;
        }
        return go;
    }

    // DefaultControls.CreateToggle hands back isOn = true and leaves the graphics
    // references implicit; both are set here so the prefab state is unambiguous.
    static GameObject MakeToggle(Transform parent, string name, string label,
        float x, float y, float w, float h, bool on)
    {
        GameObject go = DefaultControls.CreateToggle(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);

        Toggle tg = go.GetComponent<Toggle>();
        Transform bgT = go.transform.Find("Background");
        if (bgT != null)
        {
            Image bgi = bgT.GetComponent<Image>();
            if (bgi != null) { tg.targetGraphic = bgi; bgi.color = new Color(0.86f, 0.88f, 0.94f, 1f); }
            Transform ck = bgT.Find("Checkmark");
            if (ck != null)
            {
                Image cki = ck.GetComponent<Image>();
                if (cki != null) { tg.graphic = cki; cki.color = new Color(0.16f, 0.34f, 0.72f, 1f); }
            }
        }
        tg.interactable = true;
        tg.toggleTransition = Toggle.ToggleTransition.Fade;
        tg.isOn = on;

        Transform lbl = go.transform.Find("Label");
        if (lbl != null)
        {
            Text lt = lbl.GetComponent<Text>();
            if (lt != null)
            {
                lt.text = label;
                lt.color = Color.white;
                lt.fontSize = 12;
                lt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                lt.alignment = TextAnchor.MiddleLeft;
                lt.horizontalOverflow = HorizontalWrapMode.Overflow;
                lt.verticalOverflow = VerticalWrapMode.Overflow;
                // Keep the label clear of the help chip in the top-right corner.
                RectTransform lrt = lt.rectTransform;
                lrt.offsetMax = new Vector2(-16f, lrt.offsetMax.y);
            }
        }
        return go;
    }

    // Vertical scroll view named <name> with a "Viewport" (RectMask2D) and a content child
    // named <contentName>, top-stretched with a top pivot. The plugin finds <contentName>
    // by name and fills it with generated rows.
    static RectTransform MakeScroll(Transform parent, string name, string contentName,
        float x, float y, float w, float h, bool withBar)
    {
        GameObject sv = new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        Image svBg = sv.GetComponent<Image>();
        svBg.sprite = _res.background;
        svBg.type = Image.Type.Sliced;
        svBg.color = new Color(0.08f, 0.08f, 0.10f, 0.92f);
        Place(sv.transform, parent, x, y, w, h);

        float barW = withBar ? 12f : 0f;

        GameObject vp = new GameObject("Viewport",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        RectTransform vprt = vp.GetComponent<RectTransform>();
        vprt.SetParent(sv.transform, false);
        vprt.anchorMin = new Vector2(0f, 0f);
        vprt.anchorMax = new Vector2(1f, 1f);
        vprt.pivot = new Vector2(0f, 1f);
        vprt.offsetMin = new Vector2(2f, 2f);
        vprt.offsetMax = new Vector2(-2f - barW, -2f);
        vp.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f);

        GameObject content = new GameObject(contentName, typeof(RectTransform));
        RectTransform crt = content.GetComponent<RectTransform>();
        crt.SetParent(vp.transform, false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0f, 1f);
        crt.sizeDelta = new Vector2(0f, 0f);
        crt.anchoredPosition = Vector2.zero;

        ScrollRect sr = sv.GetComponent<ScrollRect>();
        sr.viewport = vprt;
        sr.content = crt;
        sr.horizontal = false;
        sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 18f;

        if (withBar)
        {
            GameObject sb = DefaultControls.CreateScrollbar(_res);
            sb.name = name + "_Bar";
            Scrollbar sc = sb.GetComponent<Scrollbar>();
            sc.direction = Scrollbar.Direction.BottomToTop;
            RectTransform sbrt = sb.GetComponent<RectTransform>();
            sbrt.SetParent(sv.transform, false);
            sbrt.anchorMin = new Vector2(1f, 0f);
            sbrt.anchorMax = new Vector2(1f, 1f);
            sbrt.pivot = new Vector2(1f, 0.5f);
            sbrt.sizeDelta = new Vector2(barW, -4f);
            sbrt.anchoredPosition = new Vector2(-2f, 0f);
            sr.verticalScrollbar = sc;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }
        return crt;
    }

    // Label + slider + manual-entry box. The plugin binds Slider_<key> / Value_<key>.
    // The label column is deliberately narrow-but-wrapping: long labels used to spill over
    // the "?" chip and under the slider handle.
    static void SliderRow(string key, string label, float min, float max, float init,
        Transform parent, float y)
    {
        Text lt = MakeText(parent, "Lbl_" + key, label, CX, y, 104f, 30f,
                           12, TextAnchor.MiddleLeft, FontStyle.Normal);
        lt.horizontalOverflow = HorizontalWrapMode.Wrap;

        float fx = CX + CW - 64f;                  // value box
        float sx = CX + 104f + 18f;                // slider track (18 = chip + gap)
        float sw = fx - 8f - sx;

        GameObject sl = DefaultControls.CreateSlider(_res);
        sl.name = "Slider_" + key;
        Slider sc = sl.GetComponent<Slider>();
        sc.minValue = min;
        sc.maxValue = max;
        sc.wholeNumbers = false;
        sc.value = Mathf.Clamp(init, min, max);
        Place(sl.transform, parent, sx, y + 6f, sw, 18f);

        // The top-right corner of a slider is under the handle, so this chip sits just to
        // the LEFT of the track instead.
        MakeHelp(sl, key, true);

        GameObject fld = DefaultControls.CreateInputField(_res);
        fld.name = "Value_" + key;
        Place(fld.transform, parent, fx, y + 4f, 64f, 22f);
        StyleInputField(fld, init.ToString("0.##"));
    }

    // DefaultControls.CreateInputField is a minefield in 2022.3:
    //  * the text child is named "Text (Legacy)", not "Text";
    //  * it ships fontSize 14 + verticalOverflow Truncate inside an ~11px text area, which
    //    renders literally nothing;
    //  * neither Text nor Placeholder gets a font assigned in batchmode, and the default
    //    colours are light-on-light.
    static void StyleInputField(GameObject fld, string value)
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Color ink = new Color(0.05f, 0.05f, 0.07f);

        Image bgi = fld.GetComponent<Image>();
        if (bgi != null) bgi.color = new Color(0.92f, 0.93f, 0.96f, 1f);

        Transform txtT = fld.transform.Find("Text (Legacy)") ?? fld.transform.Find("Text");
        Transform phT = fld.transform.Find("Placeholder");

        Text txt = txtT != null ? txtT.GetComponent<Text>() : null;
        Text ph = phT != null ? phT.GetComponent<Text>() : null;

        if (txt != null)
        {
            txt.font = font;
            txt.fontSize = 11;
            txt.color = ink;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.supportRichText = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.text = value;
        }
        if (ph != null)
        {
            ph.font = font;
            ph.fontSize = 11;
            ph.fontStyle = FontStyle.Italic;
            ph.color = new Color(0.05f, 0.05f, 0.07f, 0.45f);
            ph.alignment = TextAnchor.MiddleLeft;
            ph.horizontalOverflow = HorizontalWrapMode.Overflow;
            ph.verticalOverflow = VerticalWrapMode.Overflow;
            ph.text = "";
        }

        InputField inp = fld.GetComponent<InputField>();
        if (inp != null)
        {
            inp.textComponent = txt;
            inp.placeholder = ph;
            inp.contentType = InputField.ContentType.DecimalNumber;
            inp.lineType = InputField.LineType.SingleLine;
            inp.characterLimit = 8;
            inp.text = value;
        }
    }

    // 13x13 "?" chip named Help_<id>, parented INTO the control it annotates and pushed to
    // the end of the sibling list so it draws (and receives clicks) on top. The plugin
    // wires the click handlers itself.
    static void MakeHelp(GameObject host, string id, bool leftOfControl)
    {
        GameObject go = new GameObject("Help_" + id,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        Image img = go.GetComponent<Image>();
        img.color = new Color(0.22f, 0.30f, 0.55f, 0.95f);
        Button b = go.GetComponent<Button>();
        b.targetGraphic = img;

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(host.transform, false);
        rt.sizeDelta = new Vector2(13f, 13f);
        if (leftOfControl)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-5f, -1f);
        }
        else
        {
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-1f, -1f);
        }

        GameObject t = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        RectTransform trt = t.GetComponent<RectTransform>();
        trt.SetParent(rt, false);
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        Text tt = t.GetComponent<Text>();
        tt.text = "?";
        tt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        tt.fontSize = 10;
        tt.fontStyle = FontStyle.Bold;
        tt.color = Color.white;
        tt.alignment = TextAnchor.MiddleCenter;
        tt.raycastTarget = false;
        tt.horizontalOverflow = HorizontalWrapMode.Overflow;
        tt.verticalOverflow = VerticalWrapMode.Overflow;

        rt.SetAsLastSibling();
    }

    static void MakeHelp(GameObject host, string id)
    {
        MakeHelp(host, id, false);
    }
}
