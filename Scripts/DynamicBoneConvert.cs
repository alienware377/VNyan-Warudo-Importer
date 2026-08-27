using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// Converts an imported avatar's source bone-physics into VNyan's NATIVE DynamicBone
    /// components - the same thing Warudo does when it turns VRC PhysBones into Dynamic Bones at
    /// load. Unlike the physbones.json path (which drives the separate VNyanPhysBones plugin),
    /// this produces live components that self-simulate with zero plugin dependency, because
    /// VNyan ships DynamicBone v1.3 compiled straight into Assembly-CSharp.
    ///
    /// Three sources, in priority order per bone (a bone already claimed by an earlier source is
    /// never double-driven):
    ///   1. VRCPhysBone   - revived from the bundle by the shipped VRC stub assemblies, then
    ///                      converted with the INVERSE of VRChat's own DynamicBone->PhysBone math
    ///                      (extracted from VRC SDK 3.10.4 PhysBoneMigration).
    ///   2. VRMSpringBone - already live in VNyan (it ships VRM.dll); mapped directly.
    ///   3. Heuristic     - optional fallback: build DynamicBones from detected sway chains for
    ///                      models that shipped neither (e.g. MagicaCloth-only rigs).
    ///
    /// Everything is reflection-only: DynamicBone lives in Assembly-CSharp with obfuscated enum
    /// type names (set via Enum.ToObject), and the VRC/VRM types may or may not be present. The
    /// class no-ops cleanly when a source type or DynamicBone itself is missing.
    /// </summary>
    public static class DynamicBoneConvert
    {
        public class Options
        {
            public bool fromPhysBone = true;
            public bool fromSpringBone = true;
            public bool heuristicFallback = true;   // synthesize for uncovered sway chains
            public GenOptions gen;                   // reused sway-chain detection knobs
        }

        public class Result
        {
            public int fromPhysBone;
            public int fromSpringBone;
            public int fromHeuristic;
            public int colliders;
            public bool dynamicBoneAvailable;
            public List<string> notes = new List<string>();
            public int Total { get { return fromPhysBone + fromSpringBone + fromHeuristic; } }
        }

        // ---- VNyan's DynamicBone types (Assembly-CSharp), resolved once ----
        static bool s_scanned;
        static Type tDB, tDBCol, tDBColBase, tDBPlane;

        static void Scan()
        {
            if (s_scanned) return;
            s_scanned = true;
            tDB = FindType("DynamicBone");
            tDBCol = FindType("DynamicBoneCollider");
            tDBColBase = FindType("DynamicBoneColliderBase");
            tDBPlane = FindType("DynamicBonePlaneCollider");
        }

        /// <summary>DynamicBone is unnamespaced in Assembly-CSharp; search every loaded assembly.</summary>
        static Type FindType(string name)
        {
            Type t = Type.GetType(name + ", Assembly-CSharp");
            if (t != null) return t;
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                try { t = all[i].GetType(name); }
                catch { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        public static bool DynamicBoneAvailable { get { Scan(); return tDB != null; } }

        // ------------------------------------------------------------------ entry point

        public static Result Convert(GameObject root, Animator anim, Options opt)
        {
            Result r = new Result();
            if (opt == null) opt = new Options();
            Scan();
            r.dynamicBoneAvailable = tDB != null;
            if (root == null) { r.notes.Add("No avatar to convert."); return r; }
            if (tDB == null)
            {
                r.notes.Add("This VNyan build has no DynamicBone type - cannot convert. Use the " +
                            "physbones.json path instead.");
                return r;
            }

            // Bones already owned by a DynamicBone, so later sources / the heuristic don't stack a
            // second simulation on them.
            HashSet<Transform> claimed = new HashSet<Transform>();

            // Shared collider cache so many chains reference one DynamicBoneCollider instance.
            Dictionary<Component, Component> pbColMap = new Dictionary<Component, Component>();

            if (opt.fromPhysBone) ConvertPhysBones(root, r, claimed, pbColMap);
            if (opt.fromSpringBone) ConvertSpringBones(root, r, claimed);
            if (opt.heuristicFallback) ConvertHeuristic(root, anim, opt, r, claimed);

            if (r.Total == 0)
                r.notes.Add("Found nothing to convert (no VRCPhysBone, no VRMSpringBone, no sway " +
                            "chains detected).");
            return r;
        }

        // ------------------------------------------------------------------ VRCPhysBone

        static void ConvertPhysBones(GameObject root, Result r, HashSet<Transform> claimed,
                                     Dictionary<Component, Component> pbColMap)
        {
            Type tPB = FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
            Type tPBBase = FindType("VRC.Dynamics.VRCPhysBoneBase");
            if (tPB == null || tPBBase == null)
            {
                r.notes.Add("VRCPhysBone types not loaded (VRC stub assemblies missing) - skipped.");
                return;
            }

            Component[] comps = root.GetComponentsInChildren(tPB, true);
            for (int i = 0; i < comps.Length; i++)
            {
                Component pb = comps[i];
                if (pb == null) continue;

                Transform chainRoot = (Transform)GetField(tPBBase, pb, "rootTransform");
                if (chainRoot == null) chainRoot = pb.transform;
                if (claimed.Contains(chainRoot)) continue;

                Component db = (Component)pb.gameObject.AddComponent(tDB);
                SetEnumField(tDB, db, "m_UpdateMode", 0);                 // Normal
                SetField(tDB, db, "m_Root", chainRoot);

                float pull = GetFloat(tPBBase, pb, "pull", 0.2f);
                float spring = GetFloat(tPBBase, pb, "spring", 0.2f);
                float stiffness = GetFloat(tPBBase, pb, "stiffness", 0.2f);
                float immobile = GetFloat(tPBBase, pb, "immobile", 0f);
                float radius = GetFloat(tPBBase, pb, "radius", 0f);
                float gravity = GetFloat(tPBBase, pb, "gravity", 0f);
                float gravFalloff = GetFloat(tPBBase, pb, "gravityFalloff", 0f);
                float maxAngleX = GetFloat(tPBBase, pb, "maxAngleX", 0f);
                int limitType = GetEnumInt(tPBBase, pb, "limitType", 1);   // 0 None,1 Angle,2 Hinge

                // Inverse of VRChat's official DynamicBone -> PhysBone conversion.
                SetField(tDB, db, "m_Elasticity", Mathf.Clamp01(pull));       // pull = m_Elasticity
                SetField(tDB, db, "m_Damping", Mathf.Clamp01(1f - spring));   // spring = 1 - m_Damping
                // The SDK encodes DynamicBone stiffness into maxAngleX via a fixed table when
                // limitType is Angle; invert that table. A hand-authored PhysBone with a real
                // stiffness field and no angle limit keeps its own value.
                float dbStiff = (limitType == 1)
                    ? InverseStiffFromMaxAngle(maxAngleX)
                    : Mathf.Clamp01(stiffness);
                SetField(tDB, db, "m_Stiffness", dbStiff);
                SetField(tDB, db, "m_Inert", Mathf.Clamp01(immobile));       // immobile = m_Inert
                SetField(tDB, db, "m_Radius", Mathf.Max(0f, radius));        // radius ~ m_Radius (scale 1)

                // Official: pb.gravity = (-m_Gravity.y * |scale|) / boneLength, falloff set to 1.
                // Invert with the chain's measured average bone length.
                float boneLen = AverageBoneLength(chainRoot);
                float scaleX = Mathf.Abs(pb.transform.lossyScale.x);
                if (scaleX < 1e-4f) scaleX = 1f;
                float gY = -(gravity * boneLen) / scaleX;
                SetField(tDB, db, "m_Gravity", new Vector3(0f, gY, 0f));
                SetField(tDB, db, "m_Force", Vector3.zero);

                // Freeze axis only when the PhysBone was hinge-limited.
                SetEnumField(tDB, db, "m_FreezeAxis", limitType == 2 ? 1 : 0);

                // Colliders + exclusions.
                object colList = BuildColliderList(GetField(tPBBase, pb, "colliders") as IList, r, pbColMap);
                if (colList != null) SetField(tDB, db, "m_Colliders", colList);
                IList ignore = GetField(tPBBase, pb, "ignoreTransforms") as IList;
                if (ignore != null) SetField(tDB, db, "m_Exclusions", ToTransformList(ignore));

                ClaimSubtree(chainRoot, claimed);
                r.fromPhysBone++;

                // The stub is inert; remove it now that its data lives on the DynamicBone.
                UnityEngine.Object.DestroyImmediate(pb);
            }
            if (r.fromPhysBone > 0) r.notes.Add("Converted " + r.fromPhysBone + " VRCPhysBone chain(s) to DynamicBone.");
        }

        /// <summary>Inverse of the SDK's StiffToMaxAngle table (stiffness 0..1 -> angle 180..0).</summary>
        static readonly float[] STIFF_ANGLE = { 180f, 129f, 106f, 89f, 74f, 60f, 47f, 35f, 23f, 11f, 0f };
        static float InverseStiffFromMaxAngle(float angle)
        {
            if (angle >= STIFF_ANGLE[0]) return 0f;
            if (angle <= STIFF_ANGLE[STIFF_ANGLE.Length - 1]) return 1f;
            for (int i = 0; i < STIFF_ANGLE.Length - 1; i++)
            {
                float hi = STIFF_ANGLE[i], lo = STIFF_ANGLE[i + 1];
                if (angle <= hi && angle >= lo)
                {
                    float t = Mathf.InverseLerp(hi, lo, angle);
                    return (i + t) / 10f;
                }
            }
            return 0.5f;
        }

        // ------------------------------------------------------------------ VRMSpringBone

        static void ConvertSpringBones(GameObject root, Result r, HashSet<Transform> claimed)
        {
            Type tSB = FindType("VRM.VRMSpringBone");
            if (tSB == null) { return; } // no VRM in this host - fine

            Component[] comps = root.GetComponentsInChildren(tSB, true);
            for (int i = 0; i < comps.Length; i++)
            {
                Component sb = comps[i];
                if (sb == null) continue;

                float stiffness = GetFloat(tSB, sb, "m_stiffnessForce", 1f);
                float gravityPow = GetFloat(tSB, sb, "m_gravityPower", 0f);
                Vector3 gravityDir = (Vector3)(GetField(tSB, sb, "m_gravityDir") ?? Vector3.down);
                float drag = GetFloat(tSB, sb, "m_dragForce", 0.4f);
                float hitRadius = GetFloat(tSB, sb, "m_hitRadius", 0.02f);
                IList rootBones = GetField(tSB, sb, "RootBones") as IList;
                if (rootBones == null) continue;

                for (int k = 0; k < rootBones.Count; k++)
                {
                    Transform rb = rootBones[k] as Transform;
                    if (rb == null || claimed.Contains(rb)) continue;

                    Component db = (Component)rb.gameObject.AddComponent(tDB);
                    SetEnumField(tDB, db, "m_UpdateMode", 0);
                    SetField(tDB, db, "m_Root", rb);
                    // VRM stiffnessForce is a spring strength; drag is damping. Map onto the
                    // 0..1 DynamicBone knobs with gentle scaling.
                    SetField(tDB, db, "m_Elasticity", Mathf.Clamp01(stiffness * 0.5f));
                    SetField(tDB, db, "m_Stiffness", Mathf.Clamp01(stiffness * 0.2f));
                    SetField(tDB, db, "m_Damping", Mathf.Clamp01(drag));
                    SetField(tDB, db, "m_Inert", 0f);
                    SetField(tDB, db, "m_Radius", Mathf.Max(0f, hitRadius));
                    float boneLen = AverageBoneLength(rb);
                    Vector3 g = gravityDir.sqrMagnitude > 1e-6f ? gravityDir.normalized : Vector3.down;
                    SetField(tDB, db, "m_Gravity", g * (gravityPow * boneLen));
                    SetField(tDB, db, "m_Force", Vector3.zero);
                    SetEnumField(tDB, db, "m_FreezeAxis", 0);

                    ClaimSubtree(rb, claimed);
                    r.fromSpringBone++;
                }
            }
            if (r.fromSpringBone > 0) r.notes.Add("Converted " + r.fromSpringBone + " VRMSpringBone chain(s) to DynamicBone.");
        }

        // ------------------------------------------------------------------ heuristic fallback

        static void ConvertHeuristic(GameObject root, Animator anim, Options opt, Result r,
                                     HashSet<Transform> claimed)
        {
            GenOptions gen = opt.gen != null ? opt.gen : new GenOptions();
            List<GenChain> chains = PhysBonesGen.Detect(root, anim, gen);
            if (chains == null) return;

            for (int i = 0; i < chains.Count; i++)
            {
                GenChain ch = chains[i];
                if (ch == null || !ch.enabled || string.IsNullOrEmpty(ch.rootBone)) continue;
                Transform rt = FindByName(root.transform, ch.rootBone);
                if (rt == null || claimed.Contains(rt)) continue;

                Component db = (Component)rt.gameObject.AddComponent(tDB);
                SetEnumField(tDB, db, "m_UpdateMode", 0);
                SetField(tDB, db, "m_Root", rt);

                // Category presets already tuned for the physbones.json path; reuse their intent.
                float pull, damping, stiff, grav, rad;
                HeuristicPreset(ch.category, out pull, out damping, out stiff, out grav, out rad);
                SetField(tDB, db, "m_Elasticity", pull);
                SetField(tDB, db, "m_Damping", damping);
                SetField(tDB, db, "m_Stiffness", stiff);
                SetField(tDB, db, "m_Inert", 0f);
                SetField(tDB, db, "m_Radius", rad * (gen.scale > 0f ? gen.scale : 1f));
                float boneLen = AverageBoneLength(rt);
                SetField(tDB, db, "m_Gravity", new Vector3(0f, -grav * boneLen, 0f));
                SetField(tDB, db, "m_Force", Vector3.zero);
                SetEnumField(tDB, db, "m_FreezeAxis", 0);

                ClaimSubtree(rt, claimed);
                r.fromHeuristic++;
            }
            if (r.fromHeuristic > 0) r.notes.Add("Synthesized " + r.fromHeuristic + " DynamicBone chain(s) from detected sway bones.");
        }

        static void HeuristicPreset(string cat, out float pull, out float damping, out float stiff,
                                    out float grav, out float rad)
        {
            // damping is DynamicBone's, i.e. 1 - spring; higher = calmer.
            if (cat == "hair") { pull = 0.10f; damping = 0.15f; stiff = 0.05f; grav = 0.05f; rad = 0.01f; }
            else if (cat == "skirt") { pull = 0.15f; damping = 0.20f; stiff = 0.10f; grav = 0.10f; rad = 0.015f; }
            else if (cat == "tail") { pull = 0.12f; damping = 0.15f; stiff = 0.08f; grav = 0.02f; rad = 0.02f; }
            else if (cat == "ears") { pull = 0.20f; damping = 0.25f; stiff = 0.15f; grav = 0.02f; rad = 0.01f; }
            else if (cat == "breast") { pull = 0.08f; damping = 0.10f; stiff = 0.05f; grav = 0.05f; rad = 0.02f; }
            else { pull = 0.15f; damping = 0.20f; stiff = 0.10f; grav = 0.05f; rad = 0.015f; }
        }

        // ------------------------------------------------------------------ collider conversion

        static object BuildColliderList(IList pbColliders, Result r, Dictionary<Component, Component> map)
        {
            if (tDBColBase == null) return null;
            Type listType = typeof(List<>).MakeGenericType(tDBColBase);
            IList outList = (IList)Activator.CreateInstance(listType);
            if (pbColliders == null) return outList;

            for (int i = 0; i < pbColliders.Count; i++)
            {
                Component pbc = pbColliders[i] as Component;
                if (pbc == null) continue;
                Component dbc;
                if (!map.TryGetValue(pbc, out dbc))
                {
                    dbc = MakeCollider(pbc, r);
                    map[pbc] = dbc;
                    if (dbc != null) r.colliders++;
                }
                if (dbc != null) outList.Add(dbc);
            }
            return outList;
        }

        static Component MakeCollider(Component pbc, Result r)
        {
            Type tPBColBase = FindType("VRC.Dynamics.VRCPhysBoneColliderBase");
            if (tPBColBase == null) return null;

            int shape = GetEnumInt(tPBColBase, pbc, "shapeType", 0);   // 0 Sphere,1 Capsule,2 Plane
            float radius = GetFloat(tPBColBase, pbc, "radius", 0.05f);
            float height = GetFloat(tPBColBase, pbc, "height", 0f);
            Vector3 position = (Vector3)(GetField(tPBColBase, pbc, "position") ?? Vector3.zero);
            Quaternion rotation = (Quaternion)(GetField(tPBColBase, pbc, "rotation") ?? Quaternion.identity);
            bool inside = GetBool(tPBColBase, pbc, "insideBounds", false);
            Transform colRoot = GetField(tPBColBase, pbc, "rootTransform") as Transform;
            GameObject host = colRoot != null ? colRoot.gameObject : pbc.gameObject;

            if (shape == 2 && tDBPlane != null)
            {
                Component pl = (Component)host.AddComponent(tDBPlane);
                SetField(tDBColBase, pl, "m_Center", position);
                SetEnumField(tDBColBase, pl, "m_Direction", DirFromRotation(rotation));
                SetEnumField(tDBColBase, pl, "m_Bound", inside ? 1 : 0);
                return pl;
            }

            if (tDBCol == null) return null;
            Component db = (Component)host.AddComponent(tDBCol);
            SetField(tDBCol, db, "m_Radius", radius);
            SetField(tDBCol, db, "m_Height", shape == 1 ? Mathf.Max(height, radius * 2f) : 0f);
            SetField(tDBColBase, db, "m_Center", position);
            SetEnumField(tDBColBase, db, "m_Direction", DirFromRotation(rotation));
            SetEnumField(tDBColBase, db, "m_Bound", inside ? 1 : 0);
            return db;
        }

        /// <summary>Inverse of the SDK's direction->rotation map (X=fwd90, Y=identity, Z=right90).</summary>
        static int DirFromRotation(Quaternion rot)
        {
            Vector3 up = rot * Vector3.up;
            float ax = Mathf.Abs(up.x), ay = Mathf.Abs(up.y), az = Mathf.Abs(up.z);
            if (ax >= ay && ax >= az) return 0; // X
            if (az >= ax && az >= ay) return 2; // Z
            return 1;                            // Y
        }

        // ------------------------------------------------------------------ helpers

        static void ClaimSubtree(Transform t, HashSet<Transform> claimed)
        {
            if (t == null) return;
            Transform[] all = t.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) claimed.Add(all[i]);
        }

        static float AverageBoneLength(Transform root)
        {
            if (root == null) return 0.1f;
            float sum = 0f; int n = 0;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t.parent != null && t != root)
                {
                    sum += (t.position - t.parent.position).magnitude;
                    n++;
                }
            }
            return n > 0 ? Mathf.Max(1e-4f, sum / n) : 0.1f;
        }

        static object ToTransformList(IList src)
        {
            List<Transform> l = new List<Transform>();
            for (int i = 0; i < src.Count; i++) { Transform t = src[i] as Transform; if (t != null) l.Add(t); }
            return l;
        }

        static Transform FindByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) if (all[i].name == name) return all[i];
            return null;
        }

        // ---- reflection accessors (fields resolved fresh; DynamicBone caches its own) ----
        static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static FieldInfo Fld(Type t, string name)
        {
            Type cur = t;
            while (cur != null)
            {
                FieldInfo fi = cur.GetField(name, F);
                if (fi != null) return fi;
                cur = cur.BaseType;
            }
            return null;
        }

        static object GetField(Type t, object o, string name)
        {
            FieldInfo fi = Fld(t, name); return fi != null ? fi.GetValue(o) : null;
        }
        static void SetField(Type t, object o, string name, object v)
        {
            FieldInfo fi = Fld(t, name);
            if (fi == null) return;
            try { fi.SetValue(o, v); } catch { }
        }
        static void SetEnumField(Type t, object o, string name, int v)
        {
            FieldInfo fi = Fld(t, name);
            if (fi == null) return;
            try { fi.SetValue(o, Enum.ToObject(fi.FieldType, v)); } catch { }
        }
        static float GetFloat(Type t, object o, string name, float d)
        {
            object v = GetField(t, o, name); return v is float ? (float)v : d;
        }
        static bool GetBool(Type t, object o, string name, bool d)
        {
            object v = GetField(t, o, name); return v is bool ? (bool)v : d;
        }
        static int GetEnumInt(Type t, object o, string name, int d)
        {
            object v = GetField(t, o, name);
            if (v == null) return d;
            try { return System.Convert.ToInt32(v); } catch { return d; }
        }
    }
}
