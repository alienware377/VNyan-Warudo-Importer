using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace WarudoImporter.EditorTools
{
    /// <summary>
    /// Offline converter: .warudo -> .vsfavatar.
    ///
    /// This is the fallback for when the in-VNyan plugin cannot be used (a VNyan update moved the
    /// loader, or you want a file you can share). The output is a genuine .vsfavatar, so VNyan
    /// loads it through its normal Load Avatar button with no plugin involved at all.
    ///
    /// Drop the WarudoConvert folder into a Unity project that already has UniVRM and the shaders
    /// the mod uses - the Warudo SDK project is the ideal host, since it has both. Menu:
    /// Warudo > Convert .warudo to .vsfavatar.
    ///
    /// Why two steps rather than one button: assets that come out of an AssetBundle live only in
    /// memory. To build a NEW bundle they have to exist in the AssetDatabase first, so step 1
    /// writes real .asset/.mat/.png files into the project and step 2 bundles them.
    /// </summary>
    public class WarudoConvertWindow : EditorWindow
    {
        string warudoPath = "";
        string outputDir = "";
        Vector2 scroll;
        string log = "";
        GameObject staged;

        bool stripAnimators = true;
        bool disableConstraints = true;
        bool writePhysBonesJson = true;
        bool reencodeTextures = true;

        [MenuItem("Warudo/Convert .warudo to .vsfavatar")]
        public static void Open()
        {
            GetWindow<WarudoConvertWindow>(false, "Warudo -> VSFAvatar", true).minSize = new Vector2(460, 420);
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("1. Stage the model into this project", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            warudoPath = EditorGUILayout.TextField("Warudo file", warudoPath);
            if (GUILayout.Button("...", GUILayout.Width(28)))
            {
                string p = EditorUtility.OpenFilePanel("Choose a .warudo mod", "", "warudo");
                if (!string.IsNullOrEmpty(p)) warudoPath = p;
            }
            EditorGUILayout.EndHorizontal();

            stripAnimators = EditorGUILayout.Toggle(
                new GUIContent("Strip nested Animators", "Child Animators were authored for another host and fight VNyan's tracking."),
                stripAnimators);
            disableConstraints = EditorGUILayout.Toggle(
                new GUIContent("Disable baked constraints", "Constraints inside the mod pull bones off the tracked pose."),
                disableConstraints);
            reencodeTextures = EditorGUILayout.Toggle(
                new GUIContent("Re-encode textures",
                    "Textures inside an AssetBundle are GPU-only. Re-encoding writes them back as PNGs so " +
                    "they can be rebuilt into a new bundle. Slow on large models; turn it off if the shaders " +
                    "in this project already own the textures."),
                reencodeTextures);

            GUI.enabled = File.Exists(warudoPath);
            if (GUILayout.Button("Stage into project", GUILayout.Height(26))) Stage();
            GUI.enabled = true;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. Export", EditorStyles.boldLabel);
            staged = (GameObject)EditorGUILayout.ObjectField("Prepared avatar", staged, typeof(GameObject), true);
            EditorGUILayout.BeginHorizontal();
            outputDir = EditorGUILayout.TextField("Output folder", outputDir);
            if (GUILayout.Button("...", GUILayout.Width(28)))
            {
                string p = EditorUtility.SaveFolderPanel("Where to write the .vsfavatar", "", "");
                if (!string.IsNullOrEmpty(p)) outputDir = p;
            }
            EditorGUILayout.EndHorizontal();
            writePhysBonesJson = EditorGUILayout.Toggle(
                new GUIContent("Also write physbones.json",
                    "Warudo models carry VRChat PhysBones, which do nothing in VNyan. This emits a config " +
                    "for the VNyan PhysBones plugin instead."),
                writePhysBonesJson);

            GUI.enabled = staged != null && !string.IsNullOrEmpty(outputDir);
            if (GUILayout.Button("Build .vsfavatar", GUILayout.Height(26))) Export();
            GUI.enabled = true;

            EditorGUILayout.Space();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.TextArea(log, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void L(string s) { log += s + "\n"; Debug.Log("[WarudoConvert] " + s); Repaint(); }

        // ------------------------------------------------------------------ stage

        void Stage()
        {
            log = "";
            try
            {
                string cache = Path.Combine(Path.GetTempPath(), "WarudoConvert");
                WarudoContainer c = WarudoContainer.Open(warudoPath, cache);
                L("Mod: " + c.DisplayName + " v" + c.modVersion + " (Unity " + c.unityVersion + ")");

                WarudoBundle.Result res = WarudoBundle.Load(c.bundlePath);
                if (!res.Ok) { L("FAILED: " + res.error); return; }

                GameObject inst = (GameObject)UnityEngine.Object.Instantiate(res.prefab);
                inst.name = c.DisplayName;
                WarudoBundle.Release(res);
                L(WarudoBundle.Describe(inst));

                string projDir = "Assets/WarudoImported/" + Sanitize(c.DisplayName);
                EnsureFolder(projDir);
                Persist(inst, projDir);

                AvatarPrep.Options opt = new AvatarPrep.Options();
                opt.title = c.DisplayName;
                opt.author = c.author;
                opt.version = c.modVersion;
                opt.stripNestedAnimators = stripAnimators;
                opt.disableConstraints = disableConstraints;

                AvatarPrep.Result prep = AvatarPrep.Prepare(inst, opt);
                L(prep.Summary());

                // The VRM ScriptableObjects are created in memory; they must become assets too or
                // the bundle build silently drops the expression data.
                PersistVrmScriptableObjects(inst, projDir);

                staged = inst;
                if (outputDir == "") outputDir = Path.GetDirectoryName(warudoPath);
                Selection.activeGameObject = inst;
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                L(prep.Ok
                    ? "Staged. Check the avatar in the scene, then Build .vsfavatar."
                    : "Staged WITH ERRORS - fix them on the scene object before exporting.");
            }
            catch (Exception e) { L("EXCEPTION: " + e); }
        }

        /// <summary>
        /// Copies every bundle-owned mesh / material / texture into the project. Bundle assets are
        /// memory-only: without this the exported bundle would reference nothing.
        /// </summary>
        void Persist(GameObject root, string dir)
        {
            EnsureFolder(dir + "/Meshes");
            EnsureFolder(dir + "/Materials");
            EnsureFolder(dir + "/Textures");

            Dictionary<Mesh, Mesh> meshMap = new Dictionary<Mesh, Mesh>();
            Dictionary<Material, Material> matMap = new Dictionary<Material, Material>();
            Dictionary<Texture, Texture> texMap = new Dictionary<Texture, Texture>();
            int shaderMisses = 0;

            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                SkinnedMeshRenderer smr = smrs[i];
                if (smr.sharedMesh != null)
                    smr.sharedMesh = CopyMesh(smr.sharedMesh, dir + "/Meshes", meshMap);
                smr.sharedMaterials = CopyMaterials(smr.sharedMaterials, dir, matMap, texMap, ref shaderMisses);
            }
            MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < mfs.Length; i++)
                if (mfs[i].sharedMesh != null)
                    mfs[i].sharedMesh = CopyMesh(mfs[i].sharedMesh, dir + "/Meshes", meshMap);
            MeshRenderer[] mrs = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
                mrs[i].sharedMaterials = CopyMaterials(mrs[i].sharedMaterials, dir, matMap, texMap, ref shaderMisses);

            L("Persisted " + meshMap.Count + " meshes, " + matMap.Count + " materials, " + texMap.Count + " textures.");
            if (shaderMisses > 0)
                L("WARNING: " + shaderMisses + " material(s) use a shader that is not installed in this project. " +
                  "Install the same shader (e.g. Poiyomi) and re-stage, or those materials will render pink. " +
                  "Do NOT substitute MToon - it cannot reproduce Poiyomi's features.");
        }

        Mesh CopyMesh(Mesh src, string dir, Dictionary<Mesh, Mesh> map)
        {
            Mesh existing;
            if (map.TryGetValue(src, out existing)) return existing;
            // Instantiate is a deep copy and preserves the BoneWeight1 data, so >4 influences per
            // vertex survive; the legacy boneWeights API would silently truncate to 4.
            Mesh copy = UnityEngine.Object.Instantiate(src);
            copy.name = src.name;
            string path = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + Sanitize(src.name) + ".asset");
            AssetDatabase.CreateAsset(copy, path);
            map[src] = copy;
            return copy;
        }

        Material[] CopyMaterials(Material[] src, string dir, Dictionary<Material, Material> map,
                                 Dictionary<Texture, Texture> texMap, ref int shaderMisses)
        {
            if (src == null) return null;
            Material[] outMats = new Material[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                Material m = src[i];
                if (m == null) continue;
                Material existing;
                if (map.TryGetValue(m, out existing)) { outMats[i] = existing; continue; }

                Material copy = UnityEngine.Object.Instantiate(m);
                copy.name = m.name;

                // The bundle carries a compiled shader that is not in this project's AssetDatabase.
                // Rebinding to the *same named* shader installed here keeps every property; it is
                // not a substitution.
                if (m.shader != null)
                {
                    Shader local = Shader.Find(m.shader.name);
                    if (local != null) copy.shader = local;
                    else shaderMisses++;
                }

                if (reencodeTextures) RebindTextures(copy, dir + "/Textures", texMap);

                string path = AssetDatabase.GenerateUniqueAssetPath(dir + "/Materials/" + Sanitize(m.name) + ".mat");
                AssetDatabase.CreateAsset(copy, path);
                map[m] = copy;
                outMats[i] = copy;
            }
            return outMats;
        }

        /// <summary>
        /// Textures inside a bundle are GPU-side and not readable, so they are pulled back through
        /// a RenderTexture blit and written as PNGs. Lossless relative to what is on the GPU, but
        /// it does bake in whatever compression the mod shipped.
        /// </summary>
        void RebindTextures(Material mat, string dir, Dictionary<Texture, Texture> map)
        {
            Shader sh = mat.shader;
            if (sh == null) return;
            int count = ShaderUtil.GetPropertyCount(sh);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(sh, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string prop = ShaderUtil.GetPropertyName(sh, i);
                Texture t = mat.GetTexture(prop);
                if (t == null) continue;
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(t))) continue; // already a project asset

                Texture saved;
                if (!map.TryGetValue(t, out saved))
                {
                    saved = SaveTexture(t as Texture2D, dir);
                    map[t] = saved;
                }
                if (saved != null) mat.SetTexture(prop, saved);
            }
        }

        Texture2D SaveTexture(Texture2D src, string dir)
        {
            if (src == null) return null;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                Texture2D flat = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                flat.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                flat.Apply();
                byte[] png = flat.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(flat);

                string path = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + Sanitize(src.name) + ".png");
                File.WriteAllBytes(path, png);
                AssetDatabase.ImportAsset(path);
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            catch (Exception e) { L("Texture " + src.name + " could not be re-encoded: " + e.Message); return null; }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }

        void PersistVrmScriptableObjects(GameObject root, string dir)
        {
            EnsureFolder(dir + "/VRM");
            Component[] comps = root.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                SerializedObject so = new SerializedObject(c);
                SerializedProperty p = so.GetIterator();
                bool dirty = false;
                while (p.NextVisible(true))
                {
                    if (p.propertyType != SerializedPropertyType.ObjectReference) continue;
                    ScriptableObject sobj = p.objectReferenceValue as ScriptableObject;
                    if (sobj == null) continue;
                    if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(sobj))) continue;
                    string path = AssetDatabase.GenerateUniqueAssetPath(
                        dir + "/VRM/" + Sanitize(string.IsNullOrEmpty(sobj.name) ? c.GetType().Name : sobj.name) + ".asset");
                    AssetDatabase.CreateAsset(sobj, path);
                    // Sub-assets (the individual BlendShapeClips) have to be reachable too.
                    dirty = true;
                }
                if (dirty) so.ApplyModifiedPropertiesWithoutUndo();
            }
            // BlendShapeClips are referenced from the BlendShapeAvatar's Clips list; walking the
            // proxy again catches them now that the avatar itself is an asset.
            Component proxy = null;
            for (int i = 0; i < comps.Length; i++)
                if (comps[i] != null && comps[i].GetType().Name == "VRMBlendShapeProxy") proxy = comps[i];
            if (proxy == null) return;

            SerializedObject pso = new SerializedObject(proxy);
            SerializedProperty avatarProp = pso.FindProperty("BlendShapeAvatar");
            if (avatarProp == null || avatarProp.objectReferenceValue == null) return;
            ScriptableObject avatarSo = avatarProp.objectReferenceValue as ScriptableObject;
            string avatarPath = AssetDatabase.GetAssetPath(avatarSo);
            if (string.IsNullOrEmpty(avatarPath)) return;

            SerializedObject aso = new SerializedObject(avatarSo);
            SerializedProperty clips = aso.FindProperty("Clips");
            if (clips == null) return;
            for (int i = 0; i < clips.arraySize; i++)
            {
                ScriptableObject clip = clips.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableObject;
                if (clip == null) continue;
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(clip))) continue;
                AssetDatabase.AddObjectToAsset(clip, avatarPath);
            }
            aso.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ export

        void Export()
        {
            try
            {
                string projDir = "Assets/WarudoImported/" + Sanitize(staged.name);
                EnsureFolder(projDir);
                string prefabPath = projDir + "/" + Sanitize(staged.name) + ".prefab";
#if UNITY_2018_3_OR_NEWER
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(staged, prefabPath);
#else
                GameObject prefab = PrefabUtility.CreatePrefab(prefabPath, staged);
#endif
                if (prefab == null) { L("Could not save the prefab."); return; }

                // VNyan/VSeeFace look the avatar up by the addressable name "VSFAvatar"; any other
                // name loads as null and the host logs "Failed loading vsfavatar".
                AssetBundleBuild build = new AssetBundleBuild();
                build.assetBundleName = "vsfavatar";
                build.assetNames = new string[] { prefabPath };
                build.addressableNames = new string[] { "VSFAvatar" };

                string tmp = Path.Combine(Path.GetTempPath(), "WarudoConvertBuild");
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);

                BuildPipeline.BuildAssetBundles(tmp, new AssetBundleBuild[] { build },
                    BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);

                string src = Path.Combine(tmp, "vsfavatar");
                if (!File.Exists(src)) { L("Bundle build produced nothing - check the console."); return; }

                Directory.CreateDirectory(outputDir);
                string dst = Path.Combine(outputDir, Sanitize(staged.name) + ".vsfavatar");
                File.Copy(src, dst, true);
                L("Wrote " + dst + " (" + new FileInfo(dst).Length / (1024 * 1024) + " MB)");

                if (writePhysBonesJson)
                {
                    Animator anim = staged.GetComponent<Animator>();
                    GenOptions go = new GenOptions();
                    List<GenChain> chains = PhysBonesGen.Detect(staged, anim, go);
                    string json = PhysBonesGen.BuildJson(staged, anim, chains, go);
                    string err;
                    string p = PhysBonesGen.Write(json, Path.Combine(outputDir, "physbones.json"), out err);
                    L(p != null
                        ? "Wrote " + chains.Count + " sway chains to " + p + " (copy it next to VNyan's other configs)"
                        : "physbones.json failed: " + err);
                }
                L("Done. Load the .vsfavatar in VNyan with its normal Load Avatar button.");
            }
            catch (Exception e) { L("EXCEPTION: " + e); }
        }

        // ------------------------------------------------------------------ util

        static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string leaf = Path.GetFileName(assetPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unnamed";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }
    }
}
