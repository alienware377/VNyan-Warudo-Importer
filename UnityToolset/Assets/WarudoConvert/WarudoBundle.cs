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

            /// <summary>
            /// The mod's own VRM data, when it ships any. These are ScriptableObjects, and unlike
            /// the prefab's MonoBehaviours they deserialize fine as long as the host has UniVRM -
            /// so a mod authored with a full expression set hands us the creator's actual clips,
            /// which are always better than anything reconstructed from mesh names.
            /// </summary>
            public UnityEngine.Object blendShapeAvatar;
            public UnityEngine.Object vrmMeta;
        }

        /// <summary>
        /// Picks the mod's authored VRM assets out of the bundle. Types are matched by NAME
        /// because this assembly never references UniVRM directly.
        /// </summary>
        public static void FindVrmAssets(Result r)
        {
            if (r == null || r.bundle == null) return;
            UnityEngine.Object[] all;
            try { all = r.bundle.LoadAllAssets(); }
            catch { return; }

            int bestClipCount = -1;
            for (int i = 0; i < all.Length; i++)
            {
                UnityEngine.Object o = all[i];
                if (o == null) continue;
                string tn = o.GetType().Name;
                if (tn == "BlendShapeAvatar")
                {
                    // A mod can ship more than one; keep whichever defines the most clips.
                    int n = CountClips(o);
                    if (n > bestClipCount) { bestClipCount = n; r.blendShapeAvatar = o; }
                }
                else if (tn == "VRMMetaObject" && r.vrmMeta == null)
                {
                    r.vrmMeta = o;
                }
            }
        }

        static int CountClips(UnityEngine.Object blendShapeAvatar)
        {
            try
            {
                System.Reflection.FieldInfo f = blendShapeAvatar.GetType().GetField("Clips");
                System.Collections.IList l = f != null ? f.GetValue(blendShapeAvatar) as System.Collections.IList : null;
                return l != null ? l.Count : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// A bundle whose internal name is already loaded makes LoadFromFile return null; the only
        /// cure is to drop the previously loaded bundles and retry. This is the same trap the
        /// .vsfavatar reader hits, so handle it here once.
        /// </summary>
        /// <summary>
        /// Names of assets the bundle exposes, for diagnostics. A mod built with a full VRM setup
        /// ships its own BlendShapeClip / AvatarDescription assets here; whether they actually
        /// LOAD is a separate question that depends on their script binding.
        /// </summary>
        public static string DescribeAssets(AssetBundle ab)
        {
            if (ab == null) return "(no bundle)";
            string[] names = ab.GetAllAssetNames();
            int clips = 0, other = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].IndexOf("blendshape", StringComparison.OrdinalIgnoreCase) >= 0) clips++;
                else other++;
            }
            UnityEngine.Object[] loaded = ab.LoadAllAssets();
            Dictionary<string, int> byType = new Dictionary<string, int>();
            for (int i = 0; i < loaded.Length; i++)
            {
                string t = loaded[i].GetType().Name;
                if (!byType.ContainsKey(t)) byType[t] = 0;
                byType[t]++;
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in byType) parts.Add(kv.Value + "x " + kv.Key);
            return "bundle assets: " + names.Length + " named (" + clips + " blendshape-ish), loaded as: " +
                   string.Join(", ", parts.ToArray());
        }

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
            FindVrmAssets(r);
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

        /// <summary>
        /// Which non-Unity components actually came back alive, and how many stayed dead.
        ///
        /// This is the difference between reading a creator's authored physics and having to
        /// invent it: a MonoBehaviour whose script cannot be rebound deserializes as null and
        /// its values are gone. Printing the live ones by type name says immediately whether a
        /// given source (Magica Cloth, VRM spring bones, VRChat PhysBones) is usable on this
        /// model or not, instead of leaving it to be inferred from a chain count.
        /// </summary>
        public static string DescribeComponents(GameObject root)
        {
            if (root == null) return "(null)";
            Dictionary<string, int> live = new Dictionary<string, int>();
            int dead = 0;
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) { dead++; continue; }
                Type t = c.GetType();
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("UnityEngine")) continue;      // not interesting here
                string n = t.FullName;
                if (!live.ContainsKey(n)) live[n] = 0;
                live[n]++;
            }
            if (live.Count == 0)
                return dead + " dead scripts, NO live third-party components (the mod's own " +
                       "physics/metadata components did not rebind, so their authored values " +
                       "cannot be read).";
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in live) parts.Add(kv.Value + "x " + kv.Key);
            return dead + " dead scripts; live third-party components: " + string.Join(", ", parts.ToArray());
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
