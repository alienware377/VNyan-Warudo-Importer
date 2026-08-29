using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// Turns a raw Warudo/VRChat rig into something VNyan will accept as an avatar.
    ///
    /// A .warudo mod is just a Unity prefab: it has meshes, materials and a skeleton, but the
    /// Animator avatar is frequently Generic and there are no VRM components at all. VNyan's
    /// avatar pipeline is VRM-shaped, so this class supplies the missing half - a humanoid
    /// Avatar plus VRMMeta / VRMHumanoidDescription / VRMBlendShapeProxy / VRMFirstPerson /
    /// VRMLookAtHead - without touching a single mesh or material (Poiyomi must survive intact).
    /// </summary>
    public static class AvatarPrep
    {
        public class Options
        {
            public string title;
            public string author;
            public string version;
            public Texture2D thumbnail;
            /// <summary>Manual bone assignments from the UI, keyed by HumanBodyBones, value = transform name.</summary>
            public Dictionary<HumanBodyBones, string> boneOverrides;
            /// <summary>Nested Animators fight the root one; off only for debugging.</summary>
            public bool stripNestedAnimators = true;
            /// <summary>Constraints authored for another host usually just yank bones around in VNyan.</summary>
            public bool disableConstraints = true;
        }

        public class Result
        {
            public GameObject root;
            public Animator animator;
            public Avatar builtAvatar;          // non-null when we had to synthesise one
            public MapResult boneMap;
            public List<ClipPlan> clipPlans;
            public int boundBlendShapes;
            public bool perfectSync;
            public int arKitClips;
            public List<string> notes = new List<string>();
            public List<string> errors = new List<string>();
            public bool Ok { get { return errors.Count == 0; } }

            public string Summary()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(root != null ? root.name : "(no root)");
                if (animator != null && animator.avatar != null)
                    sb.AppendLine("Avatar: " + animator.avatar.name + (animator.avatar.isHuman ? " (humanoid)" : " (GENERIC)"));
                if (boneMap != null)
                    sb.AppendLine("Humanoid bones mapped: " + boneMap.map.Count +
                                  (boneMap.missingRequired.Count > 0
                                      ? ", MISSING " + string.Join(", ", Names(boneMap.missingRequired))
                                      : ""));
                sb.AppendLine("Expression clips bound: " + boundBlendShapes);
                if (arKitClips > 0)
                    sb.AppendLine("Perfect Sync: " + arKitClips + " ARKit clips created (face tracking will drive them).");
                else if (perfectSync)
                    sb.AppendLine("Perfect Sync shapes detected but NO clips were created - face tracking will not reach them.");
                for (int i = 0; i < notes.Count; i++) sb.AppendLine("- " + notes[i]);
                for (int i = 0; i < errors.Count; i++) sb.AppendLine("! " + errors[i]);
                return sb.ToString();
            }

            static string[] Names(List<HumanBodyBones> bones)
            {
                string[] n = new string[bones.Count];
                for (int i = 0; i < bones.Count; i++) n[i] = bones[i].ToString();
                return n;
            }
        }

        // ------------------------------------------------------------------

        public static Result Prepare(GameObject root, Options opt)
        {
            Result r = new Result();
            r.root = root;
            if (opt == null) opt = new Options();
            if (root == null) { r.errors.Add("No GameObject to prepare."); return r; }

            // >4 influences per vertex are legal and Warudo mods can carry them; the renderer caps
            // at 4 unless this is raised, and the cap is a display setting, not mesh data.
            try { QualitySettings.skinWeights = SkinWeights.Unlimited; }
            catch (Exception e) { r.notes.Add("Could not force Unlimited skin weights: " + e.Message); }

            CleanUp(root, opt, r);

            r.animator = EnsureAnimator(root);
            BuildHumanoid(root, r, opt);

            if (!VrmReflect.Available)
            {
                r.errors.Add("UniVRM is not loaded in this process (" + (VrmReflect.MissingReport() ?? "?") +
                             "), so the VRM components cannot be attached.");
                return r;
            }

            Transform head = HeadTransform(r);
            VrmReflect.AddMeta(root, opt.title, opt.author, opt.version, opt.thumbnail);
            VrmReflect.AddHumanoidDescription(root, r.animator != null ? r.animator.avatar : null);
            if (head != null)
            {
                VrmReflect.AddFirstPerson(root, head);
                VrmReflect.AddLookAtHead(root, head);
            }
            else r.notes.Add("No head bone: first-person and look-at were skipped.");

            r.perfectSync = BlendShapeMapper.IsPerfectSync(root);
            r.clipPlans = BlendShapeMapper.Plan(root);

            // Perfect Sync needs a CLIP per ARKit shape, not just the shape on the mesh: the host
            // resolves tracking names through the blendshape proxy by clip name. Without these the
            // model has all 52 shapes and still cannot be driven by face tracking.
            List<ClipPlan> arkit = BlendShapeMapper.PlanArKit(root);
            if (arkit.Count > 0)
            {
                r.clipPlans.AddRange(arkit);
                r.arKitClips = arkit.Count;
            }

            int bound;
            Component proxy = VrmReflect.AddBlendShapeProxy(root, r.clipPlans, out bound);
            r.boundBlendShapes = bound;
            if (proxy == null) r.errors.Add("Failed to attach VRMBlendShapeProxy.");
            else if (bound == 0)
                r.notes.Add("No blendshape names matched a VRM preset. Expressions will need manual " +
                            "setup in VNyan, but raw mesh blendshapes still work.");

            return r;
        }

        // ------------------------------------------------------------------ steps

        /// <summary>
        /// Removes what would actively fight VNyan. Dead scripts (VRCPhysBone and friends) show up
        /// as null components and cannot be removed at runtime - they are inert, so we only count
        /// them for the report.
        /// </summary>
        static void CleanUp(GameObject root, Options opt, Result r)
        {
            int deadScripts = 0;
            Component[] all = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < all.Length; i++) if (all[i] == null) deadScripts++;
            if (deadScripts > 0)
                r.notes.Add(deadScripts + " components belong to scripts this host does not have " +
                            "(VRChat PhysBones etc). They are inert - use Export physbones.json for physics.");

            if (opt.stripNestedAnimators)
            {
                Animator[] anims = root.GetComponentsInChildren<Animator>(true);
                int killed = 0;
                for (int i = 0; i < anims.Length; i++)
                {
                    if (anims[i] == null || anims[i].gameObject == root) continue;
                    UnityEngine.Object.DestroyImmediate(anims[i]);
                    killed++;
                }
                if (killed > 0) r.notes.Add("Removed " + killed + " nested Animator(s) that would fight the root one.");
            }

            if (opt.disableConstraints)
            {
                int off = 0;
                Behaviour[] behaviours = root.GetComponentsInChildren<Behaviour>(true);
                for (int i = 0; i < behaviours.Length; i++)
                {
                    Behaviour b = behaviours[i];
                    if (b == null) continue;
                    Type t = b.GetType();
                    // Matched by name rather than by IConstraint so this compiles without the
                    // animation-constraint module being referenced.
                    if (t.Namespace == "UnityEngine.Animations" && t.Name.EndsWith("Constraint"))
                    {
                        b.enabled = false;
                        off++;
                    }
                }
                if (off > 0) r.notes.Add("Disabled " + off + " constraint component(s) authored for another host.");
            }
        }

        static Animator EnsureAnimator(GameObject root)
        {
            Animator a = root.GetComponent<Animator>();
            if (a == null) a = root.AddComponent<Animator>();
            // A controller left over from the source project can drive bones on top of tracking.
            a.runtimeAnimatorController = null;
            a.applyRootMotion = false;
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return a;
        }

        static void BuildHumanoid(GameObject root, Result r, Options opt)
        {
            Animator a = r.animator;
            if (a != null && a.avatar != null && a.avatar.isHuman && a.avatar.isValid)
            {
                r.notes.Add("Model already ships a humanoid avatar; kept as-is.");
                return;
            }

            r.boneMap = opt.boneOverrides != null && opt.boneOverrides.Count > 0
                ? HumanoidMapper.AutoMap(root.transform, opt.boneOverrides)
                : HumanoidMapper.AutoMap(root.transform);

            for (int i = 0; i < r.boneMap.notes.Count; i++) r.notes.Add(r.boneMap.notes[i]);

            if (!r.boneMap.CanBuild)
            {
                r.errors.Add("Could not identify these required humanoid bones: " +
                             string.Join(", ", BoneNames(r.boneMap.missingRequired)) +
                             ". Assign them by hand in the Bone Mapping list, then import again.");
                return;
            }

            string err;
            Avatar built = HumanoidMapper.Build(root, r.boneMap, out err);
            if (built == null)
            {
                r.errors.Add("AvatarBuilder rejected the rig: " + (err ?? "unknown reason"));
                return;
            }
            a.avatar = built;
            r.builtAvatar = built;
            r.notes.Add("Rebuilt the rig as a humanoid avatar (" + r.boneMap.map.Count + " bones).");
        }

        static string[] BoneNames(List<HumanBodyBones> bones)
        {
            string[] n = new string[bones.Count];
            for (int i = 0; i < bones.Count; i++) n[i] = bones[i].ToString();
            return n;
        }

        static Transform HeadTransform(Result r)
        {
            if (r.animator != null && r.animator.avatar != null && r.animator.avatar.isHuman)
            {
                Transform t = r.animator.GetBoneTransform(HumanBodyBones.Head);
                if (t != null) return t;
            }
            if (r.boneMap != null)
            {
                Transform t;
                if (r.boneMap.map.TryGetValue(HumanBodyBones.Head, out t)) return t;
            }
            return null;
        }
    }
}
