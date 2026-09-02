using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// Rebuilds a mod's real components from uMod's link data.
    ///
    /// This is the root fix for "115 dead scripts". A .warudo is built by the uMod pipeline,
    /// which does NOT serialise mod MonoBehaviours as ordinary Unity components: every one is
    /// replaced by uMod's own LinkBehaviourV2 carrying a TypeReference (assembly + class) plus
    /// the authored field values, and the real component is reconstructed at load time by
    /// uMod's relinker. A host that never runs that relinker sees only dead placeholders, which
    /// is why Magica Cloth, VRM spring bones and VRChat PhysBones all arrive empty no matter
    /// which assemblies are present - their data was never in a component to begin with.
    ///
    /// Verified in the bundles themselves: they contain
    /// "PPtr&lt;$LinkBehaviourV2&gt;", "PPtr&lt;$LinkScriptableObjectV2&gt;" and
    /// "UMod.Shared.Linker.TypeHandler" from assembly UMod-Shared.
    ///
    /// uMod is commercial middleware and is deliberately NOT redistributed with this plugin.
    /// It is used only if the machine already has a licensed copy - the Warudo Creator SDK
    /// ships one - and everything degrades to the reconstructed physics path without it.
    /// </summary>
    public static class UModRelink
    {
        const string ASSEMBLY_NAME = "UMod-Shared";

        static bool s_tried;
        static Assembly s_umod;
        static Type s_linker;
        static MethodInfo s_relink, s_preRelink, s_initHandlers;
        static string s_source;

        public static string LastError { get; private set; }
        public static string Source { get { return s_source; } }

        /// <summary>Where the uMod runtime might already exist on this machine.</summary>
        static IEnumerable<string> CandidatePaths()
        {
            // 1. Beside the plugin - the user can drop their own copy in.
            string pluginDir = null;
            try { pluginDir = Path.GetDirectoryName(typeof(UModRelink).Assembly.Location); }
            catch { }
            if (!string.IsNullOrEmpty(pluginDir))
                yield return Path.Combine(pluginDir, ASSEMBLY_NAME + ".dll");

            // 2. Next to VNyan itself.
            string root = null;
            try { root = Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
            catch { }
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, ASSEMBLY_NAME + ".dll");
                yield return Path.Combine(root, "VNyan_Data/Managed/" + ASSEMBLY_NAME + ".dll");
            }

            // 3. A path the user configured (a Warudo install or Creator SDK project).
            string configured = ConfiguredPath;
            if (!string.IsNullOrEmpty(configured))
            {
                if (configured.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    yield return configured;
                else
                {
                    yield return Path.Combine(configured, ASSEMBLY_NAME + ".dll");
                    yield return Path.Combine(configured, "Warudo_Data/Managed/" + ASSEMBLY_NAME + ".dll");
                    yield return Path.Combine(configured, "Assets/Packages/UMod/Plugin/" + ASSEMBLY_NAME + ".dll");
                }
            }

            // 4. A Steam install of Warudo, which ships the runtime.
            string[] steam =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Warudo",
                @"C:\Program Files\Steam\steamapps\common\Warudo",
                @"D:\Steam\steamapps\common\Warudo",
                @"E:\Steam\steamapps\common\Warudo"
            };
            for (int i = 0; i < steam.Length; i++)
                yield return Path.Combine(steam[i], "Warudo_Data/Managed/" + ASSEMBLY_NAME + ".dll");
        }

        /// <summary>Optional explicit location, set from the plugin's settings.</summary>
        public static string ConfiguredPath { get; set; }

        public static bool Available
        {
            get { Init(); return s_relink != null; }
        }

        static void Init()
        {
            if (s_tried) return;
            s_tried = true;

            // Already in the domain? (Another plugin may have loaded it.)
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
                if (loaded[i].GetName().Name == ASSEMBLY_NAME) { s_umod = loaded[i]; s_source = "already loaded"; break; }

            if (s_umod == null)
            {
                foreach (string p in CandidatePaths())
                {
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                    try
                    {
                        // Load the bytes rather than the file so the DLL is not locked, and so a
                        // Mark-of-the-Web block on a copied file cannot refuse the load.
                        s_umod = Assembly.Load(File.ReadAllBytes(p));
                        s_source = p;
                        break;
                    }
                    catch (Exception e) { LastError = "load failed (" + p + "): " + e.Message; }
                }
            }

            if (s_umod == null)
            {
                if (LastError == null)
                    LastError = "uMod runtime not found. Point the plugin at a Warudo install or " +
                                "Creator SDK to rebuild the mod's original physics components.";
                return;
            }

            try { s_linker = s_umod.GetType("UMod.Shared.Linker.ModLinkerV2"); }
            catch { }
            if (s_linker == null)
            {
                LastError = "uMod runtime loaded but ModLinkerV2 was not found in it.";
                return;
            }

            const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            s_initHandlers = s_linker.GetMethod("InitializeHandlers", ANY);
            s_preRelink = PickByGameObject(s_linker.GetMethods(ANY), "PreRelinkModObject");
            s_relink = PickByGameObject(s_linker.GetMethods(ANY), "RelinkModObject");
            if (s_relink == null)
                LastError = "ModLinkerV2.RelinkModObject(GameObject) was not found - this uMod " +
                            "version differs from the one this was written against.";
        }

        /// <summary>
        /// Finds the overload that takes the avatar object. Matched by parameter TYPE rather than
        /// a fixed signature so a different uMod build does not silently break the lookup.
        /// </summary>
        static MethodInfo PickByGameObject(MethodInfo[] methods, string name)
        {
            MethodInfo fallback = null;
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != name) continue;
                ParameterInfo[] ps = methods[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(GameObject)) return methods[i];
                if (fallback == null) fallback = methods[i];
            }
            return fallback;
        }

        /// <summary>Signatures of the relink entry points, for the log when something does not line up.</summary>
        public static string Describe()
        {
            Init();
            if (s_linker == null) return LastError ?? "uMod runtime unavailable";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("uMod from ").Append(s_source).Append("; ");
            const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo[] ms = s_linker.GetMethods(ANY);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name.IndexOf("Relink", StringComparison.Ordinal) < 0) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                sb.Append(ms[i].Name).Append('(');
                for (int k = 0; k < ps.Length; k++)
                {
                    if (k > 0) sb.Append(", ");
                    // Parameter NAMES are what tell us what the undocumented flags mean.
                    sb.Append(ps[k].ParameterType.Name).Append(' ').Append(ps[k].Name);
                }
                sb.Append(") ");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Rebuilds the real components on <paramref name="root"/> from its link data. Returns the
        /// number of live third-party components gained, so the caller can tell whether it worked
        /// rather than assuming it did.
        /// </summary>
        public static int Relink(GameObject root, List<string> notes)
        {
            Init();
            if (root == null) return 0;
            if (s_relink == null)
            {
                if (notes != null && LastError != null) notes.Add(LastError);
                return 0;
            }

            InstallHooks(notes);

            int before = CountLiveForeign(root);

            try { if (s_initHandlers != null) s_initHandlers.Invoke(null, null); }
            catch (Exception e) { if (notes != null) notes.Add("uMod InitializeHandlers: " + Unwrap(e).Message); }

            // The flags are RelinkModObject(go, isSceneLink, relinkDependencies) and
            // PreRelinkModObject(go, relinkDependencies) - read from the parameter names on the
            // loaded assembly. An imported avatar is a prefab instance, not a scene link, and its
            // dependencies do need relinking, so (false, true) is the correct call. The other
            // combinations stay as fallbacks in case a different uMod build reorders them; each
            // attempt is judged by whether live components actually appeared, so a wrong guess
            // can never look like success.
            bool[][] combos = { new[] { false, true }, new[] { true, true }, new[] { false, false }, new[] { true, false } };
            string lastErr = null;
            for (int i = 0; i < combos.Length; i++)
            {
                try
                {
                    if (s_preRelink != null) InvokeOn(s_preRelink, root, combos[i]);
                    InvokeOn(s_relink, root, combos[i]);
                }
                catch (Exception e) { lastErr = Unwrap(e).Message; continue; }

                int gained = CountLiveForeign(root) - before;
                if (gained > 0)
                {
                    if (notes != null && i > 0)
                        notes.Add("uMod relink used flag set #" + (i + 1) + " (" +
                                  string.Join(",", new[] { combos[i][0].ToString(), combos[i][1].ToString() }) + ").");
                    return gained;
                }
            }

            if (notes != null)
                notes.Add("uMod relink ran but produced no components" +
                          (lastErr != null ? " (last error: " + lastErr + ")" : "") +
                          ". Signatures: " + Describe());
            return 0;
        }

        static Exception Unwrap(Exception e)
        {
            return (e is TargetInvocationException && e.InnerException != null) ? e.InnerException : e;
        }

        /// <summary>
        /// Installs uMod's two STATIC resolver hooks, which the hosting application is expected to
        /// provide and which are null in any host that is not Warudo. Without them
        /// LinkBehaviourV2.PreDeserialize throws a NullReferenceException and every relink is
        /// silently swallowed into a warning:
        ///
        ///     Func&lt;Type, Assembly&gt;                        onLinkAssembly
        ///     Func&lt;ModIdentity, TypeReference, object&gt;     onLinkRequest
        ///
        /// onLinkAssembly answers "which assembly defines this type", and onLinkRequest resolves a
        /// TypeReference (assembly name + class name) to an instance. Both are answered from the
        /// assemblies already loaded in this process, which is exactly what we want: the mod's
        /// components rebind against the host's own Magica Cloth / UniVRM builds.
        /// </summary>
        static bool InstallHooks(List<string> notes)
        {
            if (s_hooksInstalled) return true;
            Type tRef = s_umod != null ? s_umod.GetType("UMod.Shared.Linker.TypeReference") : null;
            if (tRef == null) return false;

            const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo fAsm = tRef.GetField("onLinkAssembly", ANY);
            FieldInfo fReq = tRef.GetField("onLinkRequest", ANY);

            try
            {
                if (fAsm != null && fAsm.GetValue(null) == null)
                {
                    MethodInfo mi = typeof(UModRelink).GetMethod("ResolveAssembly",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    fAsm.SetValue(null, Delegate.CreateDelegate(fAsm.FieldType, mi));
                }
                if (fReq != null && fReq.GetValue(null) == null)
                {
                    // The first parameter type (ModIdentity) is only carried through, so the
                    // delegate is built against the field's own signature rather than a
                    // hard-referenced one.
                    MethodInfo mi = typeof(UModRelink).GetMethod("ResolveLinkRequest",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    fReq.SetValue(null, Delegate.CreateDelegate(fReq.FieldType, mi));
                }
                s_hooksInstalled = true;
                return true;
            }
            catch (Exception e)
            {
                if (notes != null) notes.Add("uMod hook install failed: " + Unwrap(e).Message);
                return false;
            }
        }

        static bool s_hooksInstalled;

        /// <summary>Which assembly defines this type - it is already loaded, so just report it.</summary>
        static Assembly ResolveAssembly(Type type)
        {
            return type != null ? type.Assembly : null;
        }

        /// <summary>
        /// Resolves a TypeReference to an instance. The reference names an assembly and a class;
        /// both are looked up among the loaded assemblies so the mod's components bind to the
        /// host's own copies of Magica Cloth, UniVRM and friends.
        /// </summary>
        static object ResolveLinkRequest(object modIdentity, object typeReference)
        {
            if (typeReference == null) return null;
            try
            {
                Type tr = typeReference.GetType();
                const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo fTarget = tr.GetField("targetObject", ANY);
                if (fTarget != null)
                {
                    object existing = fTarget.GetValue(typeReference);
                    if (existing != null) return existing;
                }

                string asmName = tr.GetField("assemblyName", ANY) != null
                    ? tr.GetField("assemblyName", ANY).GetValue(typeReference) as string : null;
                string clsName = tr.GetField("scriptName", ANY) != null
                    ? tr.GetField("scriptName", ANY).GetValue(typeReference) as string : null;
                if (string.IsNullOrEmpty(clsName)) return null;

                Type resolved = FindLoadedType(asmName, clsName);
                return resolved;
            }
            catch { return null; }
        }

        static Type FindLoadedType(string assemblyName, string className)
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            // Prefer the named assembly, so a class that exists in two of them is not confused.
            if (!string.IsNullOrEmpty(assemblyName))
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].GetName().Name != assemblyName) continue;
                    Type t = all[i].GetType(className);
                    if (t != null) return t;
                }
            for (int i = 0; i < all.Length; i++)
            {
                Type t = null;
                try { t = all[i].GetType(className); }
                catch { }
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// Binds arguments by parameter NAME, not position. PreRelinkModObject's single bool is
        /// relinkDependencies while RelinkModObject's first bool is isSceneLink, so filling
        /// positionally would hand the scene-link value to the dependency flag and quietly relink
        /// the wrong thing. flags[0] = isSceneLink, flags[1] = relinkDependencies.
        /// </summary>
        static void InvokeOn(MethodInfo m, GameObject root, bool[] flags)
        {
            ParameterInfo[] ps = m.GetParameters();
            object[] args = new object[ps.Length];
            int unnamedBool = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                string pn = (ps[i].Name ?? "").ToLowerInvariant();
                if (pt == typeof(GameObject)) args[i] = root;
                else if (pt == typeof(Transform)) args[i] = root.transform;
                else if (pt == typeof(bool))
                {
                    if (pn.Contains("scene")) args[i] = flags[0];
                    else if (pn.Contains("depend")) args[i] = flags[1];
                    else args[i] = flags[Mathf.Min(unnamedBool++, flags.Length - 1)];
                }
                else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            m.Invoke(null, args);
        }

        /// <summary>
        /// Counts REBUILT components only: neither Unity's own, nor uMod's link scaffolding.
        /// Counting the scaffolding would make the before/after comparison meaningless, since the
        /// LinkBehaviourV2 placeholders are themselves third-party components.
        /// </summary>
        static int CountLiveForeign(GameObject root)
        {
            int n = 0;
            Component[] all = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                string ns = all[i].GetType().Namespace ?? "";
                if (ns.StartsWith("UnityEngine")) continue;
                if (ns.StartsWith("UMod")) continue;
                n++;
            }
            return n;
        }
    }
}
