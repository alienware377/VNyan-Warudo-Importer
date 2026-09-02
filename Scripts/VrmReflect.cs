using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// Talks to UniVRM entirely through reflection.
    ///
    /// Two reasons this is not a normal reference: the plugin must compile without UniVRM in the
    /// project at all (so the same sources build for the VNyan runtime plugin and the editor
    /// converter), and the host decides which UniVRM version is loaded. VNyan ships the classic
    /// 0.x line, where every field we need is a plain public field - verified against its
    /// VRM.dll: VRMMeta.Meta, VRMMetaObject.{Title,Author,Thumbnail,...},
    /// VRMBlendShapeProxy.BlendShapeAvatar, BlendShapeAvatar.Clips,
    /// BlendShapeClip.{BlendShapeName,Preset,Values,IsBinary},
    /// BlendShapeBinding.{RelativePath,Index,Weight}, VRMHumanoidDescription.Avatar,
    /// VRMFirstPerson.FirstPersonBone, VRMLookAtHead.Head.
    /// </summary>
    public static class VrmReflect
    {
        static bool s_scanned;
        static Type tMeta, tMetaObject, tProxy, tAvatarSO, tClip, tBinding, tPreset, tFirstPerson, tLookAt, tHumanoidDesc;

        // ------------------------------------------------------------------ discovery

        static void Scan()
        {
            if (s_scanned) return;
            s_scanned = true;

            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                Type[] types;
                try { types = all[i].GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                for (int j = 0; j < types.Length; j++)
                {
                    Type t = types[j];
                    if (t == null || t.Namespace != "VRM") continue;
                    switch (t.Name)
                    {
                        case "VRMMeta": tMeta = t; break;
                        case "VRMMetaObject": tMetaObject = t; break;
                        case "VRMBlendShapeProxy": tProxy = t; break;
                        case "BlendShapeAvatar": tAvatarSO = t; break;
                        case "BlendShapeClip": tClip = t; break;
                        case "BlendShapeBinding": tBinding = t; break;
                        case "BlendShapePreset": tPreset = t; break;
                        case "VRMFirstPerson": tFirstPerson = t; break;
                        case "VRMLookAtHead": tLookAt = t; break;
                        case "VRMHumanoidDescription": tHumanoidDesc = t; break;
                    }
                }
                if (tProxy != null && tMeta != null && tHumanoidDesc != null) break;
            }
        }

        /// <summary>True when the host actually has UniVRM loaded (VNyan always does).</summary>
        public static bool Available
        {
            get { Scan(); return tMeta != null && tProxy != null && tHumanoidDesc != null; }
        }

        public static string MissingReport()
        {
            Scan();
            List<string> miss = new List<string>();
            if (tMeta == null) miss.Add("VRMMeta");
            if (tMetaObject == null) miss.Add("VRMMetaObject");
            if (tProxy == null) miss.Add("VRMBlendShapeProxy");
            if (tAvatarSO == null) miss.Add("BlendShapeAvatar");
            if (tClip == null) miss.Add("BlendShapeClip");
            if (tHumanoidDesc == null) miss.Add("VRMHumanoidDescription");
            return miss.Count == 0 ? null : string.Join(", ", miss.ToArray());
        }

        // ------------------------------------------------------------------ small helpers

        static object GetFieldValue(object target, string name)
        {
            if (target == null) return null;
            FieldInfo f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return null;
            try { return f.GetValue(target); }
            catch { return null; }
        }

        static string GetStringField(object target, string name)
        {
            return GetFieldValue(target, name) as string;
        }

        /// <summary>How many clips a BlendShapeAvatar defines, for the log.</summary>
        public static int ClipCount(UnityEngine.Object blendShapeAvatar)
        {
            IList l = GetFieldValue(blendShapeAvatar, "Clips") as IList;
            return l != null ? l.Count : 0;
        }

        static void SetField(object target, string name, object value)
        {
            if (target == null) return;
            FieldInfo f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;
            try { f.SetValue(target, value); }
            catch (Exception e) { Debug.LogWarning("[WarudoImporter] could not set " + name + ": " + e.Message); }
        }

        static Component AddOrGet(GameObject go, Type t)
        {
            if (t == null) return null;
            Component c = go.GetComponent(t);
            if (c == null) c = go.AddComponent(t);
            return c;
        }

        /// <summary>UniVRM enums are plain ints, so the name lookup is enough.</summary>
        static object PresetValue(string presetName)
        {
            if (tPreset == null) return null;
            try { return Enum.Parse(tPreset, presetName, true); }
            catch { return Enum.Parse(tPreset, "Unknown", true); }
        }

        // ------------------------------------------------------------------ components

        public static Component AddMeta(GameObject root, string title, string author, string version, Texture2D thumbnail)
        {
            Scan();
            if (tMeta == null || tMetaObject == null) return null;

            ScriptableObject metaObj = ScriptableObject.CreateInstance(tMetaObject);
            metaObj.name = (string.IsNullOrEmpty(title) ? root.name : title) + "_Meta";
            SetField(metaObj, "Title", string.IsNullOrEmpty(title) ? root.name : title);
            SetField(metaObj, "Author", string.IsNullOrEmpty(author) ? "Unknown" : author);
            SetField(metaObj, "Version", string.IsNullOrEmpty(version) ? "1.0.0" : version);
            SetField(metaObj, "ExporterVersion", "WarudoImporter");
            if (thumbnail != null) SetField(metaObj, "Thumbnail", thumbnail);

            Component meta = AddOrGet(root, tMeta);
            SetField(meta, "Meta", metaObj);
            return meta;
        }

        public static Component AddHumanoidDescription(GameObject root, Avatar avatar)
        {
            Scan();
            if (tHumanoidDesc == null) return null;
            Component c = AddOrGet(root, tHumanoidDesc);
            SetField(c, "Avatar", avatar);
            return c;
        }

        public static Component AddFirstPerson(GameObject root, Transform head)
        {
            Scan();
            if (tFirstPerson == null) return null;
            Component c = AddOrGet(root, tFirstPerson);
            SetField(c, "FirstPersonBone", head);
            // UniVRM's own default: roughly eye height ahead of the head bone.
            SetField(c, "FirstPersonOffset", new Vector3(0f, 0.06f, 0f));
            return c;
        }

        public static Component AddLookAtHead(GameObject root, Transform head)
        {
            Scan();
            if (tLookAt == null) return null;
            Component c = AddOrGet(root, tLookAt);
            SetField(c, "Head", head);
            return c;
        }

        /// <summary>
        /// Builds a BlendShapeAvatar from the classified clips and attaches a proxy.
        /// Every preset UniVRM knows about gets a clip even when the model has no matching shape,
        /// because consumers look clips up by preset and a missing entry reads as a hard error
        /// rather than "expression unavailable".
        /// </summary>
        public static Component AddBlendShapeProxy(GameObject root, List<ClipPlan> plans, out int boundShapes)
        {
            return AddBlendShapeProxy(root, plans, null, out boundShapes);
        }

        /// <summary>
        /// Builds (or extends) the avatar's blendshape proxy.
        ///
        /// When the mod ships its own BlendShapeAvatar it is used as the base and only clips it
        /// does NOT already define are added. The creator's clips are authored: they carry the
        /// right binary flags, material bindings and multi-mesh bindings, none of which can be
        /// recovered by inspecting mesh names. Overwriting them with reconstructions would be a
        /// downgrade, so reconstruction is only ever used to fill gaps.
        /// </summary>
        public static Component AddBlendShapeProxy(GameObject root, List<ClipPlan> plans,
                                                   UnityEngine.Object existingAvatar, out int boundShapes)
        {
            boundShapes = 0;
            Scan();
            if (tProxy == null || tAvatarSO == null || tClip == null || tBinding == null) return null;

            ScriptableObject avatarSO = existingAvatar as ScriptableObject;
            bool reusing = avatarSO != null && tAvatarSO.IsInstanceOfType(avatarSO);
            if (!reusing)
            {
                avatarSO = ScriptableObject.CreateInstance(tAvatarSO);
                avatarSO.name = root.name + "_BlendShapes";
            }

            FieldInfo clipsField = tAvatarSO.GetField("Clips", BindingFlags.Public | BindingFlags.Instance);
            IList clips = clipsField != null ? clipsField.GetValue(avatarSO) as IList : null;
            if (clips == null)
            {
                Type listType = typeof(List<>).MakeGenericType(tClip);
                clips = (IList)Activator.CreateInstance(listType);
                if (clipsField != null) clipsField.SetValue(avatarSO, clips);
            }

            // Names the mod already defines. Compared case-insensitively so a reconstruction
            // never shadows an authored clip that differs only in spelling.
            HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> existingPresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (reusing)
            {
                foreach (object cl in clips)
                {
                    if (cl == null) continue;
                    string nm = GetStringField(cl, "BlendShapeName");
                    if (!string.IsNullOrEmpty(nm)) existingNames.Add(nm);
                    object pv = GetFieldValue(cl, "Preset");
                    if (pv != null)
                    {
                        string pn = pv.ToString();
                        if (!string.IsNullOrEmpty(pn) && pn != "Unknown") existingPresets.Add(pn);
                    }
                }
            }
            else clips.Clear();

            Dictionary<string, ClipPlan> byPreset = new Dictionary<string, ClipPlan>(StringComparer.OrdinalIgnoreCase);
            List<ClipPlan> custom = new List<ClipPlan>();
            if (plans != null)
                foreach (ClipPlan p in plans)
                {
                    if (p == null) continue;
                    if (!string.IsNullOrEmpty(p.customName)) { custom.Add(p); continue; }
                    if (!string.IsNullOrEmpty(p.presetName) && !byPreset.ContainsKey(p.presetName))
                        byPreset[p.presetName] = p;
                }

            string[] presetOrder = Enum.GetNames(tPreset);
            for (int i = 0; i < presetOrder.Length; i++)
            {
                string presetName = presetOrder[i];
                if (presetName == "value__") continue;
                // The mod already defines this expression - leave the author's version alone.
                if (existingPresets.Contains(presetName) || existingNames.Contains(presetName)) continue;

                ClipPlan plan;
                byPreset.TryGetValue(presetName, out plan);

                ScriptableObject clip = ScriptableObject.CreateInstance(tClip);
                clip.name = presetName;
                SetField(clip, "BlendShapeName", presetName);
                SetField(clip, "Preset", PresetValue(presetName));
                SetField(clip, "IsBinary", plan != null && plan.isBinary);

                Array bindings;
                if (plan != null && plan.shapes != null && plan.shapes.Count > 0)
                {
                    List<object> made = new List<object>();
                    foreach (ShapeRef s in plan.shapes)
                    {
                        if (s == null || s.renderer == null) continue;
                        string rel = RelativePath(root.transform, s.renderer.transform);
                        if (rel == null) continue;
                        object b = Activator.CreateInstance(tBinding);
                        SetField(b, "RelativePath", rel);
                        SetField(b, "Index", s.index);
                        SetField(b, "Weight", s.weight <= 0f ? 100f : s.weight);
                        made.Add(b);
                        boundShapes++;
                    }
                    bindings = Array.CreateInstance(tBinding, made.Count);
                    for (int k = 0; k < made.Count; k++) bindings.SetValue(made[k], k);
                }
                else
                {
                    bindings = Array.CreateInstance(tBinding, 0);
                }
                SetField(clip, "Values", bindings);
                clips.Add(clip);
            }

            // Perfect Sync / ARKit clips. These carry preset Unknown and are found by NAME, which
            // is the only way the host can reach them - it builds its lookup key with
            // BlendShapeKey.CreateUnknown(name) and never inspects mesh blendshapes.
            for (int c = 0; c < custom.Count; c++)
            {
                ClipPlan plan = custom[c];
                if (plan.shapes == null || plan.shapes.Count == 0) continue;
                // Never shadow a clip the mod author already shipped.
                if (existingNames.Contains(plan.customName)) continue;

                List<object> made = new List<object>();
                foreach (ShapeRef s in plan.shapes)
                {
                    if (s == null || s.renderer == null) continue;
                    string rel = RelativePath(root.transform, s.renderer.transform);
                    if (rel == null) continue;
                    object b = Activator.CreateInstance(tBinding);
                    SetField(b, "RelativePath", rel);
                    SetField(b, "Index", s.index);
                    SetField(b, "Weight", s.weight <= 0f ? 100f : s.weight);
                    made.Add(b);
                }
                if (made.Count == 0) continue;

                ScriptableObject clip = ScriptableObject.CreateInstance(tClip);
                clip.name = plan.customName;
                SetField(clip, "BlendShapeName", plan.customName);
                SetField(clip, "Preset", PresetValue("Unknown"));
                SetField(clip, "IsBinary", false);
                Array arr = Array.CreateInstance(tBinding, made.Count);
                for (int k = 0; k < made.Count; k++) arr.SetValue(made[k], k);
                SetField(clip, "Values", arr);
                clips.Add(clip);
                boundShapes += made.Count;
            }

            Component proxy = AddOrGet(root, tProxy);
            SetField(proxy, "BlendShapeAvatar", avatarSO);

            MethodInfo reinit = tProxy.GetMethod("Reinitialize", BindingFlags.Public | BindingFlags.Instance);
            if (reinit != null)
            {
                try { reinit.Invoke(proxy, null); }
                catch (Exception e) { Debug.LogWarning("[WarudoImporter] proxy Reinitialize: " + e.Message); }
            }
            return proxy;
        }

        /// <summary>UniVRM bindings address renderers by slash path relative to the avatar root.</summary>
        public static string RelativePath(Transform root, Transform target)
        {
            if (root == null || target == null) return null;
            if (target == root) return "";
            string path = target.name;
            Transform t = target.parent;
            while (t != null && t != root)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return t == root ? path : null; // target is not under root
        }
    }
}
