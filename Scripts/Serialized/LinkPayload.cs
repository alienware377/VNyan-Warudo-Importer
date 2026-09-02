using System;
using System.Collections.Generic;
using System.Text;

namespace WarudoImporter.Serialized
{
    internal enum LinkKind
    {
        Null,
        Number,
        Bool,
        String,
        Enum,
        UnityObject,
        Array,
        Instance,
        Curve,
    }

    /// <summary>One decoded value out of a uMod link graph.</summary>
    internal sealed class LinkValue
    {
        public LinkKind Kind = LinkKind.Null;

        public double Number;
        public bool Boolean;
        public string Text;
        public long EnumValue;

        // UnityObject
        public long ObjectPathId;
        public int BehaviourId = -1;
        public int ObjectLinkType;

        public List<LinkValue> Items;                       // Array
        public Dictionary<string, LinkValue> Members;       // Instance
        public string InstanceTypeName;
        public List<float[]> CurveKeys;                     // Curve: time,value,inSlope,outSlope

        public static readonly LinkValue Null = new LinkValue();
    }

    /// <summary>A single mod component that uMod replaced with a link placeholder.</summary>
    internal sealed class LinkedComponent
    {
        public long PathId;                 // of the LinkBehaviourV2 itself
        public long GameObjectPathId;
        public string TypeName;             // e.g. MagicaCloth2.MagicaCloth
        public string AssemblyName;         // e.g. MagicaClothV2, Version=...
        public Dictionary<string, LinkValue> Members = new Dictionary<string, LinkValue>();

        public string ShortAssemblyName
        {
            get
            {
                if (string.IsNullOrEmpty(AssemblyName)) return "";
                int c = AssemblyName.IndexOf(',');
                return c < 0 ? AssemblyName : AssemblyName.Substring(0, c);
            }
        }
    }

    /// <summary>Where a transform sits in the prefab hierarchy, so it can be found again at runtime.</summary>
    internal sealed class TransformRecord
    {
        public long PathId;
        public long GameObjectPathId;
        public string Name;
        public int[] IndexPath;   // child indices from the root
        public string NamePath;   // slash-joined names from the root
        public long RootPathId;
    }

    /// <summary>
    /// Reads the original component data that uMod stripped out of a .warudo bundle.
    ///
    /// uMod replaces every mod MonoBehaviour with a LinkBehaviourV2 placeholder that stores
    /// the original type plus a graph of member values. Warudo rebuilds those with its
    /// licensed copy of the uMod runtime; this reads the same data straight out of the
    /// bundle instead, so no uMod assembly is needed.
    /// </summary>
    internal sealed class LinkPayload
    {
        public readonly List<LinkedComponent> Components = new List<LinkedComponent>();

        /// <summary>Transform + GameObject records keyed by their serialized path id.</summary>
        public readonly Dictionary<long, TransformRecord> Transforms = new Dictionary<long, TransformRecord>();
        public readonly Dictionary<long, long> GameObjectToTransform = new Dictionary<long, long>();
        public readonly Dictionary<long, string> GameObjectNames = new Dictionary<long, string>();

        /// <summary>uMod link id -> the LinkBehaviourV2 path id it names.</summary>
        public readonly Dictionary<int, long> LinkIdToComponent = new Dictionary<int, long>();

        /// <summary>Renderer / other component path id -> the GameObject that carries it.</summary>
        public readonly Dictionary<long, long> ComponentToGameObject = new Dictionary<long, long>();

        public readonly List<string> Notes = new List<string>();

        public static LinkPayload Read(string bundlePath)
        {
            var p = new LinkPayload();
            using (var bundle = new UnityBundle(bundlePath))
            {
                var file = OpenSerializedFile(bundle);
                p.Scan(file);
            }
            return p;
        }

        /// <summary>
        /// Finds the serialized asset file inside the bundle. Picking the largest node is not
        /// good enough: models with big textures also carry a .resS blob that dwarfs it, so
        /// each candidate is tried until one parses as a serialized file.
        /// </summary>
        static SerializedAssetFile OpenSerializedFile(UnityBundle bundle)
        {
            var candidates = new List<UnityBundle.Node>(bundle.Nodes);
            candidates.RemoveAll(delegate (UnityBundle.Node n)
            {
                string path = (n.Path ?? "").ToLowerInvariant();
                return path.EndsWith(".ress") || path.EndsWith(".resource");
            });
            candidates.Sort(delegate (UnityBundle.Node a, UnityBundle.Node b)
            {
                return b.Size.CompareTo(a.Size);
            });

            Exception last = null;
            foreach (var node in candidates)
            {
                try { return new SerializedAssetFile(bundle, node); }
                catch (Exception e) { last = e; }
            }

            if (last != null) throw last;
            throw new InvalidOperationException("Bundle contains no serialized asset file");
        }

        // Unity class ids we care about.
        const int ClassGameObject = 1;
        const int ClassTransform = 4;
        const int ClassRectTransform = 224;
        const int ClassMonoBehaviour = 114;
        const int ClassMonoScript = 115;

        void Scan(SerializedAssetFile file)
        {
            // Sweep forward through the file so the bundle's block cache stays warm.
            var ordered = new List<SerializedAssetFile.ObjectInfo>(file.Objects);
            ordered.Sort((a, b) => a.ByteStart.CompareTo(b.ByteStart));

            var monoScripts = new Dictionary<long, string[]>(); // pathId -> {asm, ns, class}
            var rawTransforms = new Dictionary<long, Dictionary<string, object>>();
            var linkBehaviours = new List<KeyValuePair<long, Dictionary<string, object>>>();
            var linkCaches = new List<Dictionary<string, object>>();

            foreach (var o in ordered)
            {
                if (o.ClassId != ClassGameObject && o.ClassId != ClassTransform &&
                    o.ClassId != ClassRectTransform && o.ClassId != ClassMonoBehaviour &&
                    o.ClassId != ClassMonoScript)
                    continue;

                Dictionary<string, object> d;
                try { d = file.ReadObjectDict(o); }
                catch (Exception e)
                {
                    Notes.Add("could not read object " + o.PathId + " (class " + o.ClassId + "): " + e.Message);
                    continue;
                }
                if (d == null) continue;

                switch (o.ClassId)
                {
                    case ClassMonoScript:
                        monoScripts[o.PathId] = new[]
                        {
                            GetString(d, "m_AssemblyName"),
                            GetString(d, "m_Namespace"),
                            GetString(d, "m_ClassName"),
                        };
                        break;

                    case ClassGameObject:
                    {
                        GameObjectNames[o.PathId] = GetString(d, "m_Name");
                        // Remember which GameObject each component belongs to, so that
                        // renderer/collider references can be resolved at runtime.
                        object comps;
                        if (d.TryGetValue("m_Component", out comps))
                        {
                            var list = comps as List<object>;
                            if (list != null)
                                foreach (var item in list)
                                {
                                    long cid = ComponentPathId(item);
                                    if (cid != 0) ComponentToGameObject[cid] = o.PathId;
                                }
                        }
                        break;
                    }

                    case ClassTransform:
                    case ClassRectTransform:
                        rawTransforms[o.PathId] = d;
                        break;

                    case ClassMonoBehaviour:
                        if (d.ContainsKey("typeReference") && d.ContainsKey("instance"))
                            linkBehaviours.Add(new KeyValuePair<long, Dictionary<string, object>>(o.PathId, d));
                        else if (d.ContainsKey("links"))
                            linkCaches.Add(d);
                        break;
                }
            }

            BuildHierarchy(rawTransforms);
            BuildLinkIds(linkCaches);

            foreach (var kv in linkBehaviours)
            {
                var lc = ParseLink(kv.Key, kv.Value);
                if (lc != null) Components.Add(lc);
            }
        }

        static long ComponentPathId(object entry)
        {
            // m_Component entries are either {component: PPtr} or a bare PPtr depending on version.
            var d = entry as Dictionary<string, object>;
            if (d == null) return 0;
            object inner;
            if (d.TryGetValue("component", out inner))
            {
                var pd = inner as Dictionary<string, object>;
                if (pd != null) return GetLong(pd, "m_PathID");
            }
            return GetLong(d, "m_PathID");
        }

        void BuildHierarchy(Dictionary<long, Dictionary<string, object>> rawTransforms)
        {
            // Link children to parents, then walk down from each root recording the path taken.
            foreach (var kv in rawTransforms)
            {
                long father = GetPPtr(kv.Value, "m_Father");
                if (father != 0 && rawTransforms.ContainsKey(father)) continue;
                WalkHierarchy(rawTransforms, kv.Key, kv.Key, new List<int>(), "");
            }
        }

        void WalkHierarchy(Dictionary<long, Dictionary<string, object>> raw, long rootId, long id,
                           List<int> indexPath, string namePath)
        {
            Dictionary<string, object> t;
            if (!raw.TryGetValue(id, out t)) return;

            long goId = GetPPtr(t, "m_GameObject");
            string name;
            if (!GameObjectNames.TryGetValue(goId, out name)) name = "";

            string myPath = namePath.Length == 0 ? name : namePath + "/" + name;

            var rec = new TransformRecord
            {
                PathId = id,
                GameObjectPathId = goId,
                Name = name,
                IndexPath = indexPath.ToArray(),
                NamePath = myPath,
                RootPathId = rootId,
            };
            Transforms[id] = rec;
            if (goId != 0) GameObjectToTransform[goId] = id;

            object childrenObj;
            if (!t.TryGetValue("m_Children", out childrenObj)) return;
            var children = childrenObj as List<object>;
            if (children == null) return;

            for (int i = 0; i < children.Count; i++)
            {
                var cd = children[i] as Dictionary<string, object>;
                if (cd == null) continue;
                long cid = GetLong(cd, "m_PathID");
                if (cid == 0) continue;
                indexPath.Add(i);
                WalkHierarchy(raw, rootId, cid, indexPath, myPath);
                indexPath.RemoveAt(indexPath.Count - 1);
            }
        }

        void BuildLinkIds(List<Dictionary<string, object>> caches)
        {
            foreach (var cache in caches)
            {
                object linksObj;
                if (!cache.TryGetValue("links", out linksObj)) continue;
                var links = linksObj as List<object>;
                if (links == null) continue;
                foreach (var item in links)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    int linkId = (int)GetLong(d, "linkID");
                    long target = GetPPtr(d, "link");
                    if (target != 0) LinkIdToComponent[linkId] = target;
                }
            }
        }

        LinkedComponent ParseLink(long pathId, Dictionary<string, object> d)
        {
            var typeRef = Get(d, "typeReference") as Dictionary<string, object>;
            var instance = Get(d, "instance") as Dictionary<string, object>;
            if (typeRef == null || instance == null) return null;

            var lc = new LinkedComponent
            {
                PathId = pathId,
                GameObjectPathId = GetPPtr(d, "m_GameObject"),
                TypeName = GetString(typeRef, "scriptName"),
                AssemblyName = GetString(typeRef, "assemblyName"),
            };
            if (string.IsNullOrEmpty(lc.TypeName)) return null;

            // Build the rid -> reference-entry index for this behaviour.
            var refMap = new Dictionary<long, Dictionary<string, object>>();
            var refs = Get(d, "references") as Dictionary<string, object>;
            if (refs != null)
            {
                var ids = Get(refs, "RefIds") as List<object>;
                if (ids != null)
                    foreach (var item in ids)
                    {
                        var e = item as Dictionary<string, object>;
                        if (e == null) continue;
                        refMap[GetLong(e, "rid")] = e;
                    }
            }

            var members = Get(instance, "instanceMembers") as List<object>;
            if (members != null)
                foreach (var item in members)
                {
                    var m = item as Dictionary<string, object>;
                    if (m == null) continue;
                    string name = GetString(m, "memberName");
                    if (string.IsNullOrEmpty(name)) continue;
                    lc.Members[name] = Decode(Get(m, "memberLink"), refMap, 0);
                }

            return lc;
        }

        LinkValue Decode(object link, Dictionary<long, Dictionary<string, object>> refMap, int depth)
        {
            if (depth > 24) return LinkValue.Null;
            var ld = link as Dictionary<string, object>;
            if (ld == null) return LinkValue.Null;

            object ridObj;
            if (!ld.TryGetValue("rid", out ridObj)) return LinkValue.Null;
            long rid = ToLong(ridObj);
            if (rid < 0) return LinkValue.Null;   // -1/-2 mean "null reference"

            Dictionary<string, object> entry;
            if (!refMap.TryGetValue(rid, out entry)) return LinkValue.Null;

            var type = Get(entry, "type") as Dictionary<string, object>;
            string cls = type != null ? GetString(type, "class") : "";
            var data = Get(entry, "data") as Dictionary<string, object>;
            if (data == null) return LinkValue.Null;

            switch (cls)
            {
                case "TypeHandler_PrimitiveDecimalV2":
                    return new LinkValue { Kind = LinkKind.Number, Number = ToDouble(Get(data, "value")) };

                case "TypeHandler_PrimitiveIntV2":
                    return new LinkValue { Kind = LinkKind.Number, Number = ToDouble(Get(data, "value")) };

                case "TypeHandler_PrimitiveBoolV2":
                    return new LinkValue { Kind = LinkKind.Bool, Boolean = ToLong(Get(data, "value")) != 0 };

                case "TypeHandler_PrimitiveStringV2":
                    return new LinkValue { Kind = LinkKind.String, Text = GetString(data, "value") };

                case "TypeHandler_PrimitiveEnumV2":
                    return new LinkValue { Kind = LinkKind.Enum, EnumValue = ToLong(Get(data, "enumValue")) };

                case "TypeHandler_UnityObjectV2":
                {
                    var v = new LinkValue { Kind = LinkKind.UnityObject };
                    var pptr = Get(data, "value") as Dictionary<string, object>;
                    v.ObjectPathId = pptr != null ? GetLong(pptr, "m_PathID") : 0;
                    v.ObjectLinkType = (int)ToLong(Get(data, "linkType"));
                    v.BehaviourId = (int)ToLong(Get(data, "behaviourID"));
                    if (v.ObjectPathId == 0) return LinkValue.Null;
                    return v;
                }

                case "TypeHandler_AnimationCurveV2":
                    return DecodeCurve(data);

                case "LinkArrayV2":
                {
                    var v = new LinkValue { Kind = LinkKind.Array, Items = new List<LinkValue>() };
                    var els = Get(data, "elementLinks") as List<object>;
                    if (els != null)
                        foreach (var e in els) v.Items.Add(Decode(e, refMap, depth + 1));
                    return v;
                }

                case "LinkInstanceV2":
                {
                    var v = new LinkValue
                    {
                        Kind = LinkKind.Instance,
                        Members = new Dictionary<string, LinkValue>(),
                    };
                    var reference = Get(data, "reference") as Dictionary<string, object>;
                    if (reference != null) v.InstanceTypeName = GetString(reference, "scriptName");

                    var ms = Get(data, "instanceMembers") as List<object>;
                    if (ms != null)
                        foreach (var item in ms)
                        {
                            var m = item as Dictionary<string, object>;
                            if (m == null) continue;
                            string name = GetString(m, "memberName");
                            if (string.IsNullOrEmpty(name)) continue;
                            v.Members[name] = Decode(Get(m, "memberLink"), refMap, depth + 1);
                        }
                    return v;
                }

                default:
                    return LinkValue.Null;
            }
        }

        static LinkValue DecodeCurve(Dictionary<string, object> data)
        {
            var v = new LinkValue { Kind = LinkKind.Curve, CurveKeys = new List<float[]>() };
            var curve = Get(data, "curve") as Dictionary<string, object>;
            if (curve == null) return v;
            var keys = Get(curve, "m_Curve") as List<object>;
            if (keys == null) return v;
            foreach (var item in keys)
            {
                var k = item as Dictionary<string, object>;
                if (k == null) continue;
                v.CurveKeys.Add(new[]
                {
                    (float)ToDouble(Get(k, "time")),
                    (float)ToDouble(Get(k, "value")),
                    (float)ToDouble(Get(k, "inSlope")),
                    (float)ToDouble(Get(k, "outSlope")),
                });
            }
            return v;
        }

        // ---- small helpers over the decoded dictionaries -------------------------------

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        static string GetString(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v == null ? "" : v.ToString();
        }

        static long GetLong(Dictionary<string, object> d, string key) { return ToLong(Get(d, key)); }

        static long GetPPtr(Dictionary<string, object> d, string key)
        {
            var p = Get(d, key) as Dictionary<string, object>;
            return p == null ? 0 : GetLong(p, "m_PathID");
        }

        internal static long ToLong(object v)
        {
            if (v == null) return 0;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is uint) return (uint)v;
            if (v is ulong) return (long)(ulong)v;
            if (v is short) return (short)v;
            if (v is ushort) return (ushort)v;
            if (v is byte) return (byte)v;
            if (v is sbyte) return (sbyte)v;
            if (v is bool) return ((bool)v) ? 1 : 0;
            if (v is float) return (long)(float)v;
            if (v is double) return (long)(double)v;
            long parsed;
            return long.TryParse(v.ToString(), out parsed) ? parsed : 0;
        }

        internal static double ToDouble(object v)
        {
            if (v == null) return 0;
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            if (v is bool) return ((bool)v) ? 1 : 0;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is uint) return (uint)v;
            if (v is ulong) return (ulong)v;
            double parsed;
            return double.TryParse(v.ToString(), out parsed) ? parsed : 0;
        }

        public string Summarize()
        {
            var byType = new Dictionary<string, int>();
            foreach (var c in Components)
            {
                int n;
                byType.TryGetValue(c.TypeName, out n);
                byType[c.TypeName] = n + 1;
            }
            var sb = new StringBuilder();
            sb.Append(Components.Count).Append(" linked component(s): ");
            bool first = true;
            foreach (var kv in byType)
            {
                if (!first) sb.Append(", ");
                sb.Append(kv.Value).Append("x ").Append(kv.Key);
                first = false;
            }
            return sb.ToString();
        }
    }
}
