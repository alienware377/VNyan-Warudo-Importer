using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WarudoImporter.Serialized
{
    /// <summary>
    /// Rebuilds the components uMod stripped out of a .warudo bundle, using the payload
    /// read back by <see cref="LinkPayload"/>.
    ///
    /// Members are restored generically by reflection rather than by a hand-written field
    /// map, so whatever the author actually set is what the avatar gets back - including
    /// settings this importer has never heard of.
    /// </summary>
    internal sealed class LinkRebuilder
    {
        public sealed class Options
        {
            /// <summary>Type-name prefixes to rebuild. Anything else is reported and skipped.</summary>
            public List<string> TypePrefixes = new List<string>();
        }

        readonly LinkPayload payload;
        readonly GameObject root;
        readonly Options options;

        readonly Dictionary<long, Transform> liveTransforms = new Dictionary<long, Transform>();
        readonly Dictionary<long, Component> liveComponents = new Dictionary<long, Component>();

        public readonly List<string> Notes = new List<string>();
        /// <summary>Every component this rebuilt, in the order they were added.</summary>
        public readonly List<Component> Created = new List<Component>();
        public int Rebuilt;
        public int Skipped;
        public int Failed;
        /// <summary>
        /// Components belonging to a different prefab in the same bundle. Warudo mods often
        /// ship a VRM export alongside the character; those components are not ours to place.
        /// </summary>
        public int OtherPrefab;
        /// <summary>Components whose class has no implementation on this machine.</summary>
        public int NoRuntime;
        public readonly Dictionary<string, int> RebuiltByType = new Dictionary<string, int>();

        long chosenRoot;
        readonly Dictionary<string, int> missingTypes = new Dictionary<string, int>();

        public LinkRebuilder(LinkPayload payload, GameObject root, Options options)
        {
            this.payload = payload;
            this.root = root;
            this.options = options ?? new Options();
        }

        public void Run()
        {
            if (!MapHierarchy()) return;

            // Pass one creates every component, so that cross references between them
            // (a cloth pointing at its colliders) can be resolved in pass two.
            var created = new List<KeyValuePair<LinkedComponent, Component>>();
            foreach (var lc in payload.Components)
            {
                if (!Wanted(lc.TypeName)) { Skipped++; continue; }

                Type t = FindType(lc);
                if (t == null)
                {
                    NoRuntime++;
                    // One line per missing type, not one per component: a model can carry
                    // fifty spring bones and the answer is the same for all of them.
                    int n;
                    missingTypes.TryGetValue(lc.TypeName, out n);
                    missingTypes[lc.TypeName] = n + 1;
                    continue;
                }

                Transform host = ResolveTransformForGameObject(lc.GameObjectPathId);
                if (host == null)
                {
                    if (BelongsToAnotherPrefab(lc.GameObjectPathId)) OtherPrefab++;
                    else
                    {
                        Failed++;
                        Note("could not place " + lc.TypeName + ": its GameObject is not in this prefab");
                    }
                    continue;
                }

                Component c;
                try { c = host.gameObject.AddComponent(t); }
                catch (Exception e)
                {
                    Failed++;
                    Note("AddComponent " + t.Name + " on " + host.name + " failed: " + e.Message);
                    continue;
                }
                if (c == null) { Failed++; continue; }

                liveComponents[lc.PathId] = c;
                Created.Add(c);
                created.Add(new KeyValuePair<LinkedComponent, Component>(lc, c));
            }

            foreach (var kv in missingTypes)
                Note("skipped " + kv.Value + "x " + kv.Key + " - no runtime for it here");

            // Pass two fills in the data.
            foreach (var kv in created)
            {
                try
                {
                    ApplyMembers(kv.Value, kv.Value.GetType(), kv.Key.Members);
                    Rebuilt++;
                    int n;
                    RebuiltByType.TryGetValue(kv.Key.TypeName, out n);
                    RebuiltByType[kv.Key.TypeName] = n + 1;
                }
                catch (Exception e)
                {
                    Failed++;
                    Note("restoring " + kv.Key.TypeName + " on " + kv.Value.gameObject.name + " failed: " + e.Message);
                }
            }
        }

        bool Wanted(string typeName)
        {
            if (options.TypePrefixes.Count == 0) return true;
            foreach (var p in options.TypePrefixes)
                if (typeName.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        // ---- hierarchy mapping ---------------------------------------------------------

        /// <summary>
        /// A bundle can hold more than one prefab hierarchy, so the right root is chosen by
        /// walking each candidate and keeping whichever reproduces the live tree best.
        /// </summary>
        bool MapHierarchy()
        {
            var roots = new Dictionary<long, int>();
            foreach (var rec in payload.Transforms.Values)
            {
                int n;
                roots.TryGetValue(rec.RootPathId, out n);
                roots[rec.RootPathId] = n + 1;
            }
            if (roots.Count == 0) { Note("bundle has no transform hierarchy"); return false; }

            long bestRoot = 0;
            int bestScore = -1;
            Dictionary<long, Transform> bestMap = null;

            foreach (var candidate in roots.Keys)
            {
                var map = new Dictionary<long, Transform>();
                int score = 0;
                foreach (var rec in payload.Transforms.Values)
                {
                    if (rec.RootPathId != candidate) continue;
                    Transform t = Walk(root.transform, rec.IndexPath);
                    if (t == null) continue;
                    map[rec.PathId] = t;
                    if (t.name == rec.Name) score++;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoot = candidate;
                    bestMap = map;
                }
            }

            int expected = roots[bestRoot];
            if (bestMap == null || bestScore <= 0)
            {
                Note("could not match the bundle hierarchy to the loaded avatar");
                return false;
            }

            chosenRoot = bestRoot;
            foreach (var kv in bestMap) liveTransforms[kv.Key] = kv.Value;

            if (bestScore < expected)
                Note("matched " + bestScore + " of " + expected + " transforms by name; " +
                     "some components may land on the wrong bone");
            return true;
        }

        static Transform Walk(Transform from, int[] indices)
        {
            Transform t = from;
            for (int i = 0; i < indices.Length; i++)
            {
                int idx = indices[i];
                if (t == null || idx < 0 || idx >= t.childCount) return null;
                t = t.GetChild(idx);
            }
            return t;
        }

        bool BelongsToAnotherPrefab(long goPathId)
        {
            long transformPathId;
            if (!payload.GameObjectToTransform.TryGetValue(goPathId, out transformPathId)) return false;
            TransformRecord rec;
            if (!payload.Transforms.TryGetValue(transformPathId, out rec)) return false;
            return rec.RootPathId != chosenRoot;
        }

        Transform ResolveTransformForGameObject(long goPathId)
        {
            long transformPathId;
            if (!payload.GameObjectToTransform.TryGetValue(goPathId, out transformPathId)) return null;
            Transform t;
            return liveTransforms.TryGetValue(transformPathId, out t) ? t : null;
        }

        // ---- type resolution -----------------------------------------------------------

        static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        static Type FindType(LinkedComponent lc)
        {
            Type cached;
            if (typeCache.TryGetValue(lc.TypeName, out cached)) return cached;

            Type found = null;
            string wantAsm = lc.ShortAssemblyName;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(lc.TypeName, false); }
                catch { continue; }
                if (t == null) continue;
                // Prefer the assembly the mod actually referenced.
                if (found == null) found = t;
                if (asm.GetName().Name == wantAsm) { found = t; break; }
            }

            typeCache[lc.TypeName] = found;
            return found;
        }

        // ---- member restoration --------------------------------------------------------

        void ApplyMembers(object target, Type type, Dictionary<string, LinkValue> members)
        {
            foreach (var kv in members)
            {
                FieldInfo f = FindField(type, kv.Key);
                if (f == null) continue;   // field removed in this version of the package
                object value;
                if (!Coerce(kv.Value, f.FieldType, f.GetValue(target), out value)) continue;
                try { f.SetValue(target, value); }
                catch (Exception e) { Note("could not set " + type.Name + "." + kv.Key + ": " + e.Message); }
            }
        }

        static FieldInfo FindField(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        bool Coerce(LinkValue v, Type target, object existing, out object result)
        {
            result = null;
            if (v == null || v.Kind == LinkKind.Null)
            {
                // Leave value types and prebuilt containers alone rather than nulling them.
                if (target.IsValueType) return false;
                result = null;
                return true;
            }

            if (target.IsEnum)
            {
                long raw = v.Kind == LinkKind.Enum ? v.EnumValue : (long)v.Number;
                result = Enum.ToObject(target, raw);
                return true;
            }

            switch (v.Kind)
            {
                case LinkKind.Number:
                    if (!IsNumeric(target)) return false;
                    result = Convert.ChangeType(v.Number, target);
                    return true;

                case LinkKind.Bool:
                    if (target == typeof(bool)) { result = v.Boolean; return true; }
                    if (IsNumeric(target)) { result = Convert.ChangeType(v.Boolean ? 1 : 0, target); return true; }
                    return false;

                case LinkKind.String:
                    if (target != typeof(string)) return false;
                    result = v.Text;
                    return true;

                case LinkKind.Enum:
                    if (!IsNumeric(target)) return false;
                    result = Convert.ChangeType(v.EnumValue, target);
                    return true;

                case LinkKind.Curve:
                    if (target != typeof(AnimationCurve)) return false;
                    result = BuildCurve(v);
                    return true;

                case LinkKind.UnityObject:
                    return ResolveObject(v, target, out result);

                case LinkKind.Array:
                    return BuildSequence(v, target, out result);

                case LinkKind.Instance:
                    return BuildInstance(v, target, existing, out result);
            }
            return false;
        }

        static bool IsNumeric(Type t)
        {
            return t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(uint)
                || t == typeof(long) || t == typeof(ulong) || t == typeof(short) || t == typeof(ushort)
                || t == typeof(byte) || t == typeof(sbyte) || t == typeof(decimal);
        }

        static AnimationCurve BuildCurve(LinkValue v)
        {
            var keys = new Keyframe[v.CurveKeys.Count];
            for (int i = 0; i < v.CurveKeys.Count; i++)
            {
                var k = v.CurveKeys[i];
                keys[i] = new Keyframe(k[0], k[1], k[2], k[3]);
            }
            return new AnimationCurve(keys);
        }

        bool ResolveObject(LinkValue v, Type target, out object result)
        {
            result = null;

            // linkType 4 names another mod behaviour by its uMod link id.
            if (v.BehaviourId != -1)
            {
                long linkPathId;
                if (payload.LinkIdToComponent.TryGetValue(v.BehaviourId, out linkPathId))
                {
                    Component c;
                    if (liveComponents.TryGetValue(linkPathId, out c) && c != null && target.IsInstanceOfType(c))
                    {
                        result = c;
                        return true;
                    }
                }
            }

            // A transform, or the GameObject that owns one.
            Transform t;
            if (liveTransforms.TryGetValue(v.ObjectPathId, out t))
            {
                if (target.IsInstanceOfType(t)) { result = t; return true; }
                if (target == typeof(GameObject)) { result = t.gameObject; return true; }
                var comp = t.GetComponent(target);
                if (comp != null) { result = comp; return true; }
                return false;
            }

            // A GameObject named directly.
            long tid;
            if (payload.GameObjectToTransform.TryGetValue(v.ObjectPathId, out tid) &&
                liveTransforms.TryGetValue(tid, out t))
            {
                if (target == typeof(GameObject)) { result = t.gameObject; return true; }
                if (target.IsInstanceOfType(t)) { result = t; return true; }
                var comp = t.GetComponent(target);
                if (comp != null) { result = comp; return true; }
                return false;
            }

            // Some other component - find it through the GameObject that carries it.
            long ownerGo;
            if (payload.ComponentToGameObject.TryGetValue(v.ObjectPathId, out ownerGo))
            {
                Transform owner = ResolveTransformForGameObject(ownerGo);
                if (owner != null)
                {
                    if (target == typeof(GameObject)) { result = owner.gameObject; return true; }
                    var comp = owner.GetComponent(target);
                    if (comp != null) { result = comp; return true; }
                }
            }

            // Assets (meshes, materials, ScriptableObjects) are not remapped; leaving the
            // field untouched is safer than clearing whatever the loader already set.
            return false;
        }

        bool BuildSequence(LinkValue v, Type target, out object result)
        {
            result = null;
            Type element;

            if (target.IsArray) element = target.GetElementType();
            else if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>))
                element = target.GetGenericArguments()[0];
            else return false;

            var items = new List<object>(v.Items.Count);
            foreach (var item in v.Items)
            {
                object o;
                if (!Coerce(item, element, null, out o))
                {
                    // Keep positions stable: an unresolved entry becomes a null slot.
                    o = element.IsValueType ? Activator.CreateInstance(element) : null;
                }
                items.Add(o);
            }

            if (target.IsArray)
            {
                var arr = Array.CreateInstance(element, items.Count);
                for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                result = arr;
                return true;
            }

            var list = (IList)Activator.CreateInstance(target);
            foreach (var o in items) list.Add(o);
            result = list;
            return true;
        }

        bool BuildInstance(LinkValue v, Type target, object existing, out object result)
        {
            result = null;
            if (target == typeof(string)) return false;

            object box = existing;
            if (box == null || target.IsValueType)
            {
                try { box = Activator.CreateInstance(target); }
                catch { return false; }
            }

            ApplyMembers(box, target, v.Members);
            result = box;
            return true;
        }

        void Note(string s)
        {
            if (Notes.Count < 40) Notes.Add(s);
        }

        public string Summarize()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("rebuilt ").Append(Rebuilt);
            if (Failed > 0) sb.Append(", failed ").Append(Failed);
            if (Skipped > 0) sb.Append(", skipped ").Append(Skipped);
            if (OtherPrefab > 0) sb.Append(", ").Append(OtherPrefab).Append(" on the bundle's other prefab");
            if (NoRuntime > 0) sb.Append(", ").Append(NoRuntime).Append(" with no runtime here");
            if (RebuiltByType.Count > 0)
            {
                sb.Append(" (");
                bool first = true;
                foreach (var kv in RebuiltByType)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(kv.Value).Append("x ").Append(ShortName(kv.Key));
                    first = false;
                }
                sb.Append(")");
            }
            return sb.ToString();
        }

        static string ShortName(string full)
        {
            int i = full.LastIndexOf('.');
            return i < 0 ? full : full.Substring(i + 1);
        }
    }
}
