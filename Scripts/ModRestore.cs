using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WarudoImporter.Serialized;

namespace WarudoImporter
{
    /// <summary>
    /// Puts back the components uMod stripped out of a .warudo mod.
    ///
    /// uMod replaces every mod MonoBehaviour with a placeholder that carries the original
    /// type plus a graph of its member values, and rebuilds them at load with the uMod
    /// runtime. Warudo has that runtime; VNyan does not, which is why an imported mod used
    /// to arrive with no Magica Cloth, no spring bones and no PhysBones at all.
    ///
    /// This reads the same data directly out of the bundle and rebuilds the components
    /// here, so nothing from uMod has to be present on the machine.
    /// </summary>
    public static class ModRestore
    {
        public class Result
        {
            public bool ran;
            public int rebuilt;
            public int failed;
            public int magicaCloth;
            public int magicaColliders;
            public int springBones;
            public int physBones;
            public int otherPrefab;
            public int placeholdersRemoved;
            /// <summary>Bones now driven by restored native physics, so nothing else should claim them.</summary>
            public HashSet<Transform> nativelyDriven = new HashSet<Transform>();
            public List<string> notes = new List<string>();
            public string error;

            public bool HasNativeCloth { get { return magicaCloth > 0; } }
            public bool HasNativeSpringBones { get { return springBones > 0; } }
        }

        // Everything uMod stripped is worth restoring; anything whose runtime type is absent
        // is reported and skipped rather than guessed at.
        static readonly string[] Prefixes =
        {
            "MagicaCloth2.",
            "VRM.",
            "VRC.",
            "UniVRM10.",
            "UniGLTF.",
        };

        public static Result Restore(GameObject template, string bundlePath)
        {
            var r = new Result();
            if (template == null || string.IsNullOrEmpty(bundlePath)) return r;

            LinkPayload payload;
            try
            {
                payload = LinkPayload.Read(bundlePath);
            }
            catch (Exception e)
            {
                r.error = e.Message;
                r.notes.Add("Could not read the mod's component data: " + e.Message);
                return r;
            }

            r.ran = true;
            foreach (var n in payload.Notes) r.notes.Add(n);

            if (payload.Components.Count == 0)
            {
                r.notes.Add("This mod has no stripped components - nothing to restore.");
                return r;
            }
            r.notes.Add("Mod component data: " + payload.Summarize());

            var opt = new LinkRebuilder.Options();
            opt.TypePrefixes.AddRange(Prefixes);

            var rebuilder = new LinkRebuilder(payload, template, opt);
            rebuilder.Run();

            foreach (var n in rebuilder.Notes) r.notes.Add(n);
            r.rebuilt = rebuilder.Rebuilt;
            r.failed = rebuilder.Failed;
            r.otherPrefab = rebuilder.OtherPrefab;

            foreach (var kv in rebuilder.RebuiltByType)
            {
                if (kv.Key == "MagicaCloth2.MagicaCloth") r.magicaCloth += kv.Value;
                else if (kv.Key.StartsWith("MagicaCloth2.") && kv.Key.EndsWith("Collider")) r.magicaColliders += kv.Value;
                else if (kv.Key == "VRM.VRMSpringBone") r.springBones += kv.Value;
                else if (kv.Key.EndsWith("VRCPhysBone")) r.physBones += kv.Value;
            }

            if (rebuilder.Rebuilt > 0)
            {
                r.notes.Add("Restored the creator's own components: " + rebuilder.Summarize() + ".");
                // The placeholders have served their purpose. Left in place they try to
                // relink themselves the moment VNyan activates the avatar, which throws once
                // per component because the uMod loader that owns that data is not here.
                r.placeholdersRemoved = StripUModBookkeeping(template);
                if (r.placeholdersRemoved > 0)
                    r.notes.Add("Removed " + r.placeholdersRemoved + " leftover uMod placeholder(s).");
            }

            CollectDrivenBones(rebuilder.Created, r);
            return r;
        }

        /// <summary>Drops uMod's own bookkeeping components once their data has been read.</summary>
        static int StripUModBookkeeping(GameObject root)
        {
            var all = root.GetComponentsInChildren<Component>(true);
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                if (c == null) continue;
                Type t = c.GetType();
                if (t.FullName == null || !t.FullName.StartsWith("UMod.", StringComparison.Ordinal)) continue;
                UnityEngine.Object.DestroyImmediate(c);
                n++;
            }
            return n;
        }

        /// <summary>
        /// Reports whether the restored cloth simulations actually built and are running.
        /// Magica Cloth builds asynchronously on the first frames after the avatar appears,
        /// so this is worth calling a moment after the avatar is handed over.
        /// </summary>
        public static string DescribePhysicsState(GameObject root)
        {
            if (root == null) return "no avatar";

            int cloth = 0, valid = 0, running = 0;
            var problems = new List<string>();
            var all = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                if (c == null || c.GetType().FullName != "MagicaCloth2.MagicaCloth") continue;
                cloth++;
                bool ok = CallBool(c, "IsValid");
                if (ok) valid++;
                object proc = GetMember(c, "Process");
                bool live = proc != null && CallBool(proc, "IsRunning");
                if (live) running++;
                if (!ok || !live)
                {
                    object result = proc != null ? GetMember(proc, "Result") : null;
                    string why = Describe(result);
                    problems.Add(c.gameObject.name + (why.Length > 0 ? " (" + why + ")" : ""));
                }
            }

            int dyn = 0, spring = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i];
                if (c == null) continue;
                string n = c.GetType().FullName;
                if (n == "DynamicBone") dyn++;
                else if (n == "VRM.VRMSpringBone") spring++;
            }

            if (cloth == 0 && dyn == 0 && spring == 0) return "no physics components on the avatar";
            var sb = new System.Text.StringBuilder("Physics running: ");
            if (cloth > 0) sb.Append(valid).Append("/").Append(cloth).Append(" Magica Cloth built, ")
                             .Append(running).Append(" simulating");
            if (spring > 0) sb.Append(cloth > 0 ? "; " : "").Append(spring).Append(" VRM spring bone(s)");
            if (dyn > 0) sb.Append((cloth > 0 || spring > 0) ? "; " : "").Append(dyn).Append(" DynamicBone chain(s)");
            if (problems.Count > 0)
                sb.Append(". Not simulating: ").Append(string.Join(", ", problems.ToArray()));
            return sb.ToString();
        }

        /// <summary>Magica Cloth explains itself through GetResultString(); ToString() does not.</summary>
        static string Describe(object result)
        {
            if (result == null) return "";
            try
            {
                var m = result.GetType().GetMethod("GetResultString", BindingFlags.Public | BindingFlags.Instance,
                                                   null, Type.EmptyTypes, null);
                if (m != null && m.ReturnType == typeof(string))
                {
                    string s = (string)m.Invoke(result, null);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                object code = GetMember(result, "result");
                if (code != null) return code.ToString();
            }
            catch { }
            return "";
        }

        static bool CallBool(object target, string method)
        {
            if (target == null) return false;
            try
            {
                var m = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance,
                                                   null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(bool)) return false;
                return (bool)m.Invoke(target, null);
            }
            catch { return false; }
        }

        /// <summary>
        /// Works out which bones the restored simulations already move. Anything in this set
        /// must not also get a DynamicBone or a PhysBone chain, or it gets driven twice and
        /// the result jitters.
        /// </summary>
        static void CollectDrivenBones(List<Component> created, Result r)
        {
            foreach (var c in created)
            {
                if (c == null) continue;
                string type = c.GetType().FullName;

                if (type == "MagicaCloth2.MagicaCloth")
                {
                    object sd = GetMember(c, "serializeData");
                    if (sd != null) AddTransforms(GetMember(sd, "rootBones"), r);
                }
                else if (type == "VRM.VRMSpringBone")
                {
                    AddTransforms(GetMember(c, "RootBones"), r);
                    AddTransforms(GetMember(c, "m_rootBones"), r);
                }
            }
        }

        static void AddTransforms(object value, Result r)
        {
            if (value == null) return;

            var single = value as Transform;
            if (single != null) { ClaimSubtree(single, r); return; }

            var seq = value as IEnumerable;
            if (seq == null) return;
            foreach (var item in seq)
            {
                var t = item as Transform;
                if (t != null) ClaimSubtree(t, r);
            }
        }

        static void ClaimSubtree(Transform t, Result r)
        {
            if (t == null) return;
            var all = t.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) r.nativelyDriven.Add(all[i]);
        }

        static object GetMember(object target, string name)
        {
            if (target == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
                var p = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead)
                {
                    try { return p.GetValue(target, null); }
                    catch { return null; }
                }
            }
            return null;
        }
    }
}
