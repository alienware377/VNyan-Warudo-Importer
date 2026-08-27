using System;
using System.Collections.Generic;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// Loads the AssetBundle that lives inside a .warudo container and picks out the character
    /// prefab. Kept separate from <see cref="WarudoContainer"/> so the Unity Editor converter can
    /// reuse it unchanged.
    /// </summary>
    public static class WarudoBundle
    {
        public class Result
        {
            public AssetBundle bundle;
            public GameObject prefab;      // asset inside the bundle - do NOT destroy
            public string assetName;
            public string error;
            public bool Ok { get { return prefab != null; } }
        }

        /// <summary>
        /// A bundle whose internal name is already loaded makes LoadFromFile return null; the only
        /// cure is to drop the previously loaded bundles and retry. This is the same trap the
        /// .vsfavatar reader hits, so handle it here once.
        /// </summary>
        public static Result Load(string bundlePath)
        {
            Result r = new Result();
            AssetBundle ab = null;
            try { ab = AssetBundle.LoadFromFile(bundlePath); }
            catch (Exception e) { r.error = "LoadFromFile threw: " + e.Message; return r; }

            if (ab == null)
            {
                AssetBundle.UnloadAllAssetBundles(false);
                try { ab = AssetBundle.LoadFromFile(bundlePath); }
                catch (Exception e) { r.error = "LoadFromFile threw on retry: " + e.Message; return r; }
            }
            if (ab == null)
            {
                r.error = "AssetBundle.LoadFromFile returned null. The bundle is either already " +
                          "loaded or was built for another platform/Unity generation.";
                return r;
            }

            r.bundle = ab;
            string[] names = ab.GetAllAssetNames();
            string pick = PickPrefabName(names);
            if (pick != null)
            {
                r.prefab = ab.LoadAsset<GameObject>(pick);
                r.assetName = pick;
            }
            if (r.prefab == null)
            {
                GameObject[] all = ab.LoadAllAssets<GameObject>();
                GameObject best = PickRichest(all);
                if (best != null) { r.prefab = best; r.assetName = best.name; }
            }
            if (r.prefab == null)
                r.error = "Bundle contains no GameObject. Assets found: " + string.Join(", ", names);
            return r;
        }

        /// <summary>Warudo's SDK emits "Character.prefab"; uMod lower-cases the addressable path.</summary>
        static string PickPrefabName(string[] names)
        {
            string firstPrefab = null;
            foreach (string n in names)
            {
                if (!n.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (firstPrefab == null) firstPrefab = n;
                string leaf = n;
                int slash = leaf.LastIndexOf('/');
                if (slash >= 0) leaf = leaf.Substring(slash + 1);
                if (leaf.StartsWith("character.", StringComparison.OrdinalIgnoreCase)) return n;
            }
            return firstPrefab;
        }

        /// <summary>Fallback when nothing is named Character: the object with the most skinned meshes.</summary>
        static GameObject PickRichest(GameObject[] candidates)
        {
            GameObject best = null;
            int bestScore = -1;
            foreach (GameObject g in candidates)
            {
                if (g == null) continue;
                int score = g.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length * 10
                          + g.GetComponentsInChildren<Transform>(true).Length;
                if (score > bestScore) { bestScore = score; best = g; }
            }
            return best;
        }

        /// <summary>
        /// Frees the container but keeps every loaded asset (mesh, material, compiled shader)
        /// alive - exactly what we need, since the instantiated avatar references them.
        /// </summary>
        public static void Release(Result r)
        {
            if (r != null && r.bundle != null)
            {
                r.bundle.Unload(false);
                r.bundle = null;
            }
        }

        /// <summary>Diagnostics for the log: what the mod actually shipped.</summary>
        public static string Describe(GameObject root)
        {
            if (root == null) return "(null)";
            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int verts = 0, shapes = 0, maxInfluence = 0, missing = 0;
            HashSet<string> shaders = new HashSet<string>();
            foreach (SkinnedMeshRenderer s in smrs)
            {
                if (s.sharedMesh == null) continue;
                verts += s.sharedMesh.vertexCount;
                shapes += s.sharedMesh.blendShapeCount;
                try
                {
                    Unity.Collections.NativeArray<byte> bpv = s.sharedMesh.GetBonesPerVertex();
                    for (int i = 0; i < bpv.Length; i++) if (bpv[i] > maxInfluence) maxInfluence = bpv[i];
                }
                catch { }
                foreach (Material m in s.sharedMaterials)
                    if (m != null && m.shader != null) shaders.Add(m.shader.name);
            }
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (c == null) missing++;

            string[] sh = new string[shaders.Count];
            shaders.CopyTo(sh);
            return smrs.Length + " skinned meshes, " + verts + " verts, " + shapes + " blendshapes, " +
                   "max " + maxInfluence + " influences/vertex, " + missing + " dead scripts, shaders: " +
                   string.Join(" | ", sh);
        }
    }
}
