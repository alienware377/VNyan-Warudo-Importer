// PhysBonesGen.cs
// Part of the VNyan Warudo Importer.
//
// A .warudo / VRChat model ships with VRCPhysBone components. Outside of VRChat those
// are dead MonoBehaviours (missing script references), so the original tuning cannot be
// recovered at runtime. Instead of trying, we detect the dangly bone chains from the
// skeleton itself and emit a physbones.json config for the user's existing
// VNyanPhysBones plugin.
//
// The emitted JSON schema is fixed and case sensitive - it is deserialized by
// Newtonsoft on the plugin side. Do not rename fields.
//
// Hard constraints (do not break):
//   * Only UnityEngine, System, System.Collections.Generic, System.IO, System.Text.
//   * No UnityEditor, no UniVRM types, no Newtonsoft (the JSON is hand written).
//   * C# 5 syntax only (no string interpolation, no ?., no out var, no expression
//     bodied members, no nameof, no tuples) so it compiles both as a Unity 2022.3
//     runtime plugin and inside an editor tool assembly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>User facing generation switches.</summary>
    public class GenOptions
    {
        public bool includeBreast = true;
        public bool includeHair = true;
        public bool includeSkirt = true;
        public bool includeTail = true;
        public bool includeEars = true;
        public bool includeMisc = true;
        public float scale = 1f;            // avatar height scale factor, applied to radii
        public bool generateColliders = true;
    }

    /// <summary>One detected dangly chain, before it is turned into JSON.</summary>
    public class GenChain
    {
        public string name;
        public string rootBone;
        public string category;
        public bool enabled;
    }

    /// <summary>
    /// Detects secondary-motion bone chains and writes a VNyanPhysBones config.
    /// </summary>
    public static class PhysBonesGen
    {
        // ------------------------------------------------------------------
        // Categories
        // ------------------------------------------------------------------

        public const string CatHair = "hair";
        public const string CatSkirt = "skirt";
        public const string CatTail = "tail";
        public const string CatEars = "ears";
        public const string CatBreast = "breast";
        public const string CatMisc = "misc";

        // Tokens are matched against *name parts*, never as raw substrings - see
        // SplitNameParts / PartMatchesToken. A token of fewer than
        // TokenPrefixMinLength characters must equal a whole part, which is what keeps
        // "fin" out of "IndexFinger1_L" and "ass" out of "Glass"/"Tassel"/"Compass".
        // When adding tokens, prefer listing plurals explicitly over shortening a token
        // to buy prefix matching.
        //
        // A token may contain '_' to require several consecutive parts, e.g.
        // "thigh_jiggle" matches the parts [thigh, jiggle] of "L_ThighJiggle.001".

        private static readonly string[] HairTokens = new string[]
        {
            "hair", "hairs", "ponytail", "bang", "bangs", "fringe", "ahoge",
            "braid", "twintail", "sidetail"
        };

        private static readonly string[] SkirtTokens = new string[]
        {
            "skirt", "coat", "coats", "dress", "cloth", "hem", "hems", "apron", "robe",
            "robes", "ribbon", "scarf", "necktie", "tie", "ties", "sleeve", "strap"
        };

        private static readonly string[] TailTokens = new string[]
        {
            "tail", "tails"
        };

        private static readonly string[] EarsTokens = new string[]
        {
            // "ear" is exact-part only on purpose: it must not touch Beard, Head,
            // Forearm, Shear, Gear or Search.
            "ear", "ears", "earring", "antenna", "antennae", "horn", "horns"
        };

        private static readonly string[] BreastTokens = new string[]
        {
            "breast", "bust", "busts", "boob", "boobs", "chest_jiggle",
            "j_sec_l_bust", "j_sec_r_bust", "pecs"
        };

        private static readonly string[] MiscTokens = new string[]
        {
            "wing", "wings", "jiggle", "belly", "ass", "booty", "thigh_jiggle",
            "floppy", "chain", "tassel", "bell", "bells", "feather", "fluff",
            // Exact parts only - "fin" as a prefix swallows every finger bone.
            "fin", "fins"
        };

        // Order matters: "ponytail" and "twintail" must be claimed by hair before the
        // "tail" token gets a look at them.
        private static readonly string[] CategoryOrder = new string[]
        {
            CatBreast, CatHair, CatEars, CatTail, CatSkirt, CatMisc
        };

        // Pre-split forms of the token lists, so matching does not allocate per bone.
        // Declared after the arrays above: static field initialisers run in order.
        private static readonly string[][] HairTokenParts = SplitTokens(HairTokens);
        private static readonly string[][] SkirtTokenParts = SplitTokens(SkirtTokens);
        private static readonly string[][] TailTokenParts = SplitTokens(TailTokens);
        private static readonly string[][] EarsTokenParts = SplitTokens(EarsTokens);
        private static readonly string[][] BreastTokenParts = SplitTokens(BreastTokens);
        private static readonly string[][] MiscTokenParts = SplitTokens(MiscTokens);
        private static readonly string[][] NoTokenParts = new string[0][];

        private static string[][] SplitTokens(string[] tokens)
        {
            if (tokens == null)
            {
                return new string[0][];
            }
            string[][] result = new string[tokens.Length][];
            for (int i = 0; i < tokens.Length; i++)
            {
                string tok = tokens[i] == null ? "" : tokens[i];
                // char[] overload on purpose: Split('_') can bind to the netstandard 2.1
                // Split(char, StringSplitOptions), which is missing on older runtimes.
                result[i] = tok.Split(new char[] { '_' });
            }
            return result;
        }

        private static string[][] TokensFor(string category)
        {
            if (category == CatHair) return HairTokenParts;
            if (category == CatSkirt) return SkirtTokenParts;
            if (category == CatTail) return TailTokenParts;
            if (category == CatEars) return EarsTokenParts;
            if (category == CatBreast) return BreastTokenParts;
            if (category == CatMisc) return MiscTokenParts;
            return NoTokenParts;
        }

        private static bool CategoryEnabled(string category, GenOptions opt)
        {
            if (opt == null) return true;
            if (category == CatHair) return opt.includeHair;
            if (category == CatSkirt) return opt.includeSkirt;
            if (category == CatTail) return opt.includeTail;
            if (category == CatEars) return opt.includeEars;
            if (category == CatBreast) return opt.includeBreast;
            if (category == CatMisc) return opt.includeMisc;
            return true;
        }

        // ------------------------------------------------------------------
        // Per category physics presets
        // ------------------------------------------------------------------

        private class Preset
        {
            public float pull;
            public float spring;
            public float stiffness;
            public float gravity;
            public string limitType;
            public float maxAngle;
            public float radius;
        }

        private static Preset PresetFor(string category)
        {
            Preset p = new Preset();
            p.limitType = "angle";

            if (category == CatHair)
            {
                p.pull = 0.10f; p.spring = 0.30f; p.stiffness = 0.10f;
                p.gravity = 0.05f; p.maxAngle = 60f; p.radius = 0.01f;
            }
            else if (category == CatSkirt)
            {
                p.pull = 0.15f; p.spring = 0.25f; p.stiffness = 0.15f;
                p.gravity = 0.10f; p.maxAngle = 45f; p.radius = 0.015f;
            }
            else if (category == CatTail)
            {
                p.pull = 0.12f; p.spring = 0.35f; p.stiffness = 0.12f;
                p.gravity = 0.02f; p.maxAngle = 60f; p.radius = 0.02f;
            }
            else if (category == CatEars)
            {
                p.pull = 0.20f; p.spring = 0.40f; p.stiffness = 0.25f;
                p.gravity = 0.02f; p.maxAngle = 35f; p.radius = 0.01f;
            }
            else if (category == CatBreast)
            {
                // Measured against the actual solver: pull+stiffness apply per SUBSTEP (2x per
                // frame), so the old .35/.55/.40 removed ~43% of any deflection per frame - a
                // millimetre tremor decaying in ~100 ms that reads as completely dead on camera.
                // Soft pull + low stiffness + high spring (spring lowers damping) is what
                // actually reads as jiggle in this solver.
                p.pull = 0.08f; p.spring = 0.80f; p.stiffness = 0.05f;
                p.gravity = 0.05f; p.maxAngle = 45f; p.radius = 0.02f;
            }
            else
            {
                p.pull = 0.20f; p.spring = 0.30f; p.stiffness = 0.20f;
                p.gravity = 0.05f; p.maxAngle = 45f; p.radius = 0.015f;
            }

            return p;
        }

        // Upper body colliders: hair, ears, breast.
        private static readonly string[] UpperColliders = new string[]
        {
            "Head_col", "UpperChest_col", "Chest_col",
            "LeftUpperArm_col", "RightUpperArm_col",
            "LeftLowerArm_col", "RightLowerArm_col",
            "LeftHand_col", "RightHand_col"
        };

        // Lower body colliders: skirt, tail, misc.
        private static readonly string[] LowerColliders = new string[]
        {
            "Hips_col",
            "LeftUpperLeg_col", "RightUpperLeg_col",
            "LeftLowerLeg_col", "RightLowerLeg_col"
        };

        private static string[] CollidersFor(string category)
        {
            if (category == CatHair || category == CatEars || category == CatBreast)
            {
                return UpperColliders;
            }
            return LowerColliders;
        }

        // ------------------------------------------------------------------
        // Detection
        // ------------------------------------------------------------------

        /// <summary>
        /// Finds candidate chain roots: transforms that are (or descend from) an actual
        /// skinning bone, are not humanoid bones, whose name matches a category token,
        /// and which start a run of at least two bones.
        /// </summary>
        public static List<GenChain> Detect(GameObject avatarRoot, Animator anim, GenOptions opt)
        {
            List<GenChain> result = new List<GenChain>();
            if (avatarRoot == null)
            {
                return result;
            }
            if (opt == null)
            {
                opt = new GenOptions();
            }

            HashSet<Transform> influenced = CollectSkinnedBones(avatarRoot);
            HashSet<Transform> humanoid = CollectHumanoidBones(anim);
            HashSet<Transform> excludedRoots = CollectFingerAndToeBones(anim);

            List<Transform> chosen = new List<Transform>();
            HashSet<string> usedNames = new HashSet<string>(StringComparer.Ordinal);

            // Depth first, pre-order, so parents are always visited before children and
            // the topmost link of a run wins.
            Transform[] all = avatarRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == avatarRoot.transform)
                {
                    continue;
                }
                if (humanoid.Contains(t))
                {
                    continue; // VNyan drives humanoid bones from tracking
                }
                if (IsSelfOrDescendantOf(t, excludedRoots))
                {
                    continue; // finger/toe helpers ("Left Toe 3") are not dangly bones
                }
                if (!influenced.Contains(t))
                {
                    continue; // not a skinning bone nor a descendant of one
                }
                if (HasChosenAncestor(t, chosen))
                {
                    continue; // already covered by a chain further up
                }

                string category = MatchCategory(t.name);
                if (category == null)
                {
                    continue;
                }
                if (ChainDepth(t, influenced, humanoid, 0) < 2)
                {
                    continue; // a lone bone has nothing to swing
                }

                GenChain chain = new GenChain();
                chain.category = category;
                chain.rootBone = t.name;
                chain.name = UniqueName(category + "_" + t.name, usedNames);
                chain.enabled = CategoryEnabled(category, opt);
                result.Add(chain);
                chosen.Add(t);
            }

            return result;
        }

        private static string UniqueName(string baseName, HashSet<string> used)
        {
            string n = baseName;
            int suffix = 2;
            while (used.Contains(n))
            {
                n = baseName + "_" + suffix.ToString();
                suffix++;
            }
            used.Add(n);
            return n;
        }

        private static bool HasChosenAncestor(Transform t, List<Transform> chosen)
        {
            Transform p = t.parent;
            while (p != null)
            {
                for (int i = 0; i < chosen.Count; i++)
                {
                    if (chosen[i] == p)
                    {
                        return true;
                    }
                }
                p = p.parent;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Name part matching
        // ------------------------------------------------------------------

        // A token shorter than this must equal a whole name part. Longer tokens may
        // also match as a prefix of a part, so "breast" still catches "breastbone".
        private const int TokenPrefixMinLength = 5;

        private static bool IsPartSeparator(char c)
        {
            return c == '_' || c == '-' || c == '.' || c == ' ' || c == '\t'
                || c == '/' || c == '\\' || c == '|' || c == ':' || c == '+';
        }

        /// <summary>
        /// Splits a transform name into lowercase parts on separators and camelCase
        /// transitions, stripping trailing digits from each part. A part that is nothing
        /// but digits is kept as-is so it can still act as a boundary.
        /// "J_Opt_L_RabbitEar1_01" -> [j, opt, l, rabbit, ear, 01].
        /// </summary>
        private static List<string> SplitNameParts(string name)
        {
            List<string> parts = new List<string>();
            if (string.IsNullOrEmpty(name))
            {
                return parts;
            }

            StringBuilder cur = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];

                if (IsPartSeparator(c))
                {
                    FlushPart(cur, parts);
                    continue;
                }

                if (cur.Length > 0 && char.IsUpper(c))
                {
                    char prev = name[i - 1];
                    // "hairA" / "hair1A" -> boundary before the capital.
                    bool afterLowerOrDigit = char.IsLower(prev) || char.IsDigit(prev);
                    // "VRMEar" -> [vrm, ear]: last capital of a run starts a new word.
                    bool acronymTail = char.IsUpper(prev)
                        && i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (afterLowerOrDigit || acronymTail)
                    {
                        FlushPart(cur, parts);
                    }
                }

                cur.Append(char.ToLowerInvariant(c));
            }
            FlushPart(cur, parts);

            return parts;
        }

        private static void FlushPart(StringBuilder cur, List<string> parts)
        {
            if (cur.Length == 0)
            {
                return;
            }
            string s = cur.ToString();
            cur.Length = 0;

            int end = s.Length;
            while (end > 0 && s[end - 1] >= '0' && s[end - 1] <= '9')
            {
                end--;
            }
            if (end > 0 && end < s.Length)
            {
                s = s.Substring(0, end);
            }
            parts.Add(s);
        }

        private static bool PartMatchesToken(string part, string token)
        {
            if (part == null || token == null || token.Length == 0)
            {
                return false;
            }
            if (string.Equals(part, token, StringComparison.Ordinal))
            {
                return true;
            }
            if (token.Length >= TokenPrefixMinLength
                && part.Length > token.Length
                && part.StartsWith(token, StringComparison.Ordinal))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when any token matches the parts. A multi-part token must line up with
        /// consecutive parts; only its final segment is allowed the prefix rule.
        /// </summary>
        private static bool PartsMatchAnyToken(List<string> parts, string[][] tokens)
        {
            if (parts == null || parts.Count == 0 || tokens == null)
            {
                return false;
            }

            for (int t = 0; t < tokens.Length; t++)
            {
                string[] tok = tokens[t];
                if (tok == null || tok.Length == 0)
                {
                    continue;
                }
                for (int start = 0; start + tok.Length <= parts.Count; start++)
                {
                    bool ok = true;
                    for (int k = 0; k < tok.Length; k++)
                    {
                        string part = parts[start + k];
                        if (k == tok.Length - 1)
                        {
                            if (!PartMatchesToken(part, tok[k]))
                            {
                                ok = false;
                            }
                        }
                        else if (!string.Equals(part, tok[k], StringComparison.Ordinal))
                        {
                            ok = false;
                        }
                        if (!ok)
                        {
                            break;
                        }
                    }
                    if (ok)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>A trailing side or index part, e.g. the "L" and "001" of "Hair_L_001".</summary>
        private static bool IsSideOrIndexPart(string part)
        {
            if (string.IsNullOrEmpty(part))
            {
                return true;
            }
            if (part == "l" || part == "r" || part == "left" || part == "right"
                || part == "lf" || part == "rt")
            {
                return true;
            }
            for (int i = 0; i < part.Length; i++)
            {
                if (part[i] < '0' || part[i] > '9')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Rig helper bones that must never become chains: anything with a "twist" part
        /// ("Twist_Knee_L"), and terminators whose last meaningful part is "end"
        /// ("ThighEnd_L").
        /// </summary>
        private static bool IsHelperBoneName(List<string> parts)
        {
            if (parts == null || parts.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i] == "twist")
                {
                    return true;
                }
            }

            int last = parts.Count - 1;
            while (last >= 0 && IsSideOrIndexPart(parts[last]))
            {
                last--;
            }
            if (last >= 0)
            {
                string p = parts[last];
                if (p == "end" || p == "ends")
                {
                    return true;
                }
            }
            return false;
        }

        private static string MatchCategory(string boneName)
        {
            if (string.IsNullOrEmpty(boneName))
            {
                return null;
            }

            List<string> parts = SplitNameParts(boneName);
            if (parts.Count == 0)
            {
                return null;
            }
            if (IsHelperBoneName(parts))
            {
                return null;
            }

            for (int c = 0; c < CategoryOrder.Length; c++)
            {
                string category = CategoryOrder[c];
                if (PartsMatchAnyToken(parts, TokensFor(category)))
                {
                    return category;
                }
            }
            return null;
        }

        /// <summary>Every transform used as a skinning bone, plus all their descendants.</summary>
        private static HashSet<Transform> CollectSkinnedBones(GameObject avatarRoot)
        {
            HashSet<Transform> bones = new HashSet<Transform>();
            SkinnedMeshRenderer[] smrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < smrs.Length; r++)
            {
                SkinnedMeshRenderer smr = smrs[r];
                if (smr == null)
                {
                    continue;
                }
                Transform[] bs = smr.bones;
                if (bs != null)
                {
                    for (int b = 0; b < bs.Length; b++)
                    {
                        if (bs[b] != null)
                        {
                            bones.Add(bs[b]);
                        }
                    }
                }
                if (smr.rootBone != null)
                {
                    bones.Add(smr.rootBone);
                }
            }

            HashSet<Transform> expanded = new HashSet<Transform>();
            foreach (Transform b in bones)
            {
                AddSubtree(b, expanded);
            }
            return expanded;
        }

        private static void AddSubtree(Transform t, HashSet<Transform> into)
        {
            if (t == null || into.Contains(t))
            {
                return;
            }
            into.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                AddSubtree(t.GetChild(i), into);
            }
        }

        private static HashSet<Transform> CollectHumanoidBones(Animator anim)
        {
            HashSet<Transform> set = new HashSet<Transform>();
            if (anim == null || !anim.isHuman)
            {
                return set;
            }
            int last = (int)HumanBodyBones.LastBone;
            for (int i = 0; i < last; i++)
            {
                Transform t = null;
                try
                {
                    t = anim.GetBoneTransform((HumanBodyBones)i);
                }
                catch (Exception)
                {
                    t = null;
                }
                if (t != null)
                {
                    set.Add(t);
                }
            }
            return set;
        }

        /// <summary>
        /// The mapped finger and toe bones. Their whole subtrees are off limits: rigs
        /// hang extra helpers off them ("Left Toe 0".."Left Toe 4") that are neither
        /// humanoid bones nor dangly bones.
        /// </summary>
        private static HashSet<Transform> CollectFingerAndToeBones(Animator anim)
        {
            HashSet<Transform> set = new HashSet<Transform>();
            if (anim == null || !anim.isHuman)
            {
                return set;
            }

            // The finger bones are one contiguous run of the enum.
            int first = (int)HumanBodyBones.LeftThumbProximal;
            int last = (int)HumanBodyBones.RightLittleDistal;
            for (int i = first; i <= last; i++)
            {
                AddBone(anim, (HumanBodyBones)i, set);
            }
            AddBone(anim, HumanBodyBones.LeftToes, set);
            AddBone(anim, HumanBodyBones.RightToes, set);

            return set;
        }

        private static void AddBone(Animator anim, HumanBodyBones bone, HashSet<Transform> into)
        {
            Transform t = null;
            try
            {
                t = anim.GetBoneTransform(bone);
            }
            catch (Exception)
            {
                t = null;
            }
            if (t != null)
            {
                into.Add(t);
            }
        }

        private static bool IsSelfOrDescendantOf(Transform t, HashSet<Transform> roots)
        {
            if (t == null || roots == null || roots.Count == 0)
            {
                return false;
            }
            Transform p = t;
            int guard = 0;
            while (p != null && guard < 256)
            {
                if (roots.Contains(p))
                {
                    return true;
                }
                p = p.parent;
                guard++;
            }
            return false;
        }

        /// <summary>Longest run of usable bones starting at (and including) t.</summary>
        private static int ChainDepth(Transform t, HashSet<Transform> influenced, HashSet<Transform> humanoid, int guard)
        {
            if (t == null || guard > 128)
            {
                return 0;
            }
            int best = 0;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (c == null || humanoid.Contains(c) || !influenced.Contains(c))
                {
                    continue;
                }
                int d = ChainDepth(c, influenced, humanoid, guard + 1);
                if (d > best)
                {
                    best = d;
                }
            }
            return best + 1;
        }

        // ------------------------------------------------------------------
        // Collider table
        // ------------------------------------------------------------------

        private class ColliderSpec
        {
            public HumanBodyBones bone;
            public string label;        // used for "<label>_col"
            public bool sphere;
            public float radius;
            public HumanBodyBones toBone;
            public bool hasTo;
        }

        private static ColliderSpec Spec(HumanBodyBones bone, string label, float radius)
        {
            ColliderSpec s = new ColliderSpec();
            s.bone = bone;
            s.label = label;
            s.sphere = true;
            s.radius = radius;
            s.hasTo = false;
            return s;
        }

        private static ColliderSpec Spec(HumanBodyBones bone, string label, float radius, HumanBodyBones toBone)
        {
            ColliderSpec s = new ColliderSpec();
            s.bone = bone;
            s.label = label;
            s.sphere = false;
            s.radius = radius;
            s.toBone = toBone;
            s.hasTo = true;
            return s;
        }

        private static List<ColliderSpec> BuildColliderSpecs(Animator anim)
        {
            List<ColliderSpec> list = new List<ColliderSpec>();

            list.Add(Spec(HumanBodyBones.Head, "Head", 0.09f));

            // Prefer UpperChest when the rig has one, otherwise fall back to Chest, so we
            // never stack two overlapping torso capsules.
            bool hasUpperChest = anim != null && anim.isHuman
                && anim.GetBoneTransform(HumanBodyBones.UpperChest) != null;
            if (hasUpperChest)
            {
                list.Add(Spec(HumanBodyBones.UpperChest, "UpperChest", 0.11f, HumanBodyBones.Neck));
            }
            else
            {
                list.Add(Spec(HumanBodyBones.Chest, "Chest", 0.11f, HumanBodyBones.Neck));
            }

            list.Add(Spec(HumanBodyBones.Hips, "Hips", 0.12f, HumanBodyBones.Spine));

            list.Add(Spec(HumanBodyBones.LeftUpperArm, "LeftUpperArm", 0.045f, HumanBodyBones.LeftLowerArm));
            list.Add(Spec(HumanBodyBones.RightUpperArm, "RightUpperArm", 0.045f, HumanBodyBones.RightLowerArm));
            list.Add(Spec(HumanBodyBones.LeftLowerArm, "LeftLowerArm", 0.04f, HumanBodyBones.LeftHand));
            list.Add(Spec(HumanBodyBones.RightLowerArm, "RightLowerArm", 0.04f, HumanBodyBones.RightHand));

            list.Add(Spec(HumanBodyBones.LeftHand, "LeftHand", 0.04f));
            list.Add(Spec(HumanBodyBones.RightHand, "RightHand", 0.04f));

            list.Add(Spec(HumanBodyBones.LeftUpperLeg, "LeftUpperLeg", 0.075f, HumanBodyBones.LeftLowerLeg));
            list.Add(Spec(HumanBodyBones.RightUpperLeg, "RightUpperLeg", 0.075f, HumanBodyBones.RightLowerLeg));
            list.Add(Spec(HumanBodyBones.LeftLowerLeg, "LeftLowerLeg", 0.05f, HumanBodyBones.LeftFoot));
            list.Add(Spec(HumanBodyBones.RightLowerLeg, "RightLowerLeg", 0.05f, HumanBodyBones.RightFoot));

            return list;
        }

        private class ColliderData
        {
            public string name;
            public string type;
            public string bone;
            public Vector3 offset;
            public Vector3 offsetEnd;
            public bool hasEnd;
            public Vector3 axis;
            public float radius;
            public float height;
        }

        /// <summary>
        /// Average world units per local unit for this transform. Non-uniform bone scale is rare
        /// and meaningless for a capsule, so the three axes are averaged rather than picked from.
        /// </summary>
        private static float LocalToWorldScale(Transform t)
        {
            Vector3 s = t.lossyScale;
            float avg = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            return avg > 1e-6f ? avg : 1f;
        }

        /// <summary>
        /// Collider radii are authored for a roughly 1.6 m humanoid whose hips sit near 0.9 m.
        /// Reading the actual hip height keeps chibi and giant models sane without the user
        /// having to find the scale slider first.
        /// </summary>
        public static float MeasureScale(Animator anim)
        {
            if (anim == null || !anim.isHuman) return 1f;
            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            Transform foot = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            if (hips == null || foot == null) return 1f;
            float legSpan = Mathf.Abs(hips.position.y - foot.position.y);
            if (legSpan < 1e-4f) return 1f;
            return Mathf.Clamp(legSpan / 0.8f, 0.1f, 10f);
        }

        private static List<ColliderData> BuildColliders(Animator anim, GenOptions opt)
        {
            List<ColliderData> result = new List<ColliderData>();
            if (anim == null || !anim.isHuman || opt == null || !opt.generateColliders)
            {
                return result;
            }

            float scale = opt.scale <= 0f ? 1f : opt.scale;
            List<ColliderSpec> specs = BuildColliderSpecs(anim);

            for (int i = 0; i < specs.Count; i++)
            {
                ColliderSpec spec = specs[i];
                Transform t = anim.GetBoneTransform(spec.bone);
                if (t == null)
                {
                    continue;
                }

                ColliderData c = new ColliderData();
                c.name = spec.label + "_col";
                c.bone = t.name;
                c.radius = spec.radius * scale;
                c.offset = Vector3.zero;

                if (spec.sphere)
                {
                    c.type = "sphere";
                    c.height = 0f;
                    c.hasEnd = false;
                    result.Add(c);
                    continue;
                }

                c.type = "capsule";

                // Primary child: the humanoid continuation when the rig has one, else the
                // first real child, else a straight run along the bone's local up axis.
                Transform child = null;
                if (spec.hasTo)
                {
                    child = anim.GetBoneTransform(spec.toBone);
                }
                if (child == null && t.childCount > 0)
                {
                    child = t.GetChild(0);
                }

                // Units are mixed on purpose, because that is what the consumer expects:
                // PhysBoneCollider maps offset/offsetEnd through bone.TransformPoint (so they are
                // BONE-LOCAL) but compares radius/height against world-space distances (so those
                // are METRES). Rigs exported from Blender routinely carry a bone lossyScale near
                // 100, so mixing the two up makes capsules ~100x too short or too long.
                float boneScale = LocalToWorldScale(t);

                Vector3 end;
                if (child != null)
                {
                    end = t.InverseTransformPoint(child.position);
                }
                else
                {
                    end = Vector3.zero;
                }

                if (end.sqrMagnitude < 1e-10f)
                {
                    // Default stub length of 4 radii, expressed in local units.
                    end = Vector3.up * (c.radius * 4f / boneScale);
                }

                c.offsetEnd = end;
                c.hasEnd = true;
                // World length of the core segment plus the two end caps. Only read when
                // offsetEnd is absent, but wrong values here are a trap for anyone editing the
                // file by hand.
                c.height = end.magnitude * boneScale + c.radius * 2f;
                c.axis = end.normalized;
                result.Add(c);
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Rest-pose validation
        // ------------------------------------------------------------------

        /// <summary>
        /// Shrinks any collider whose keep-out shell (radius + chain radius) would contain the
        /// REST position of a bone belonging to a chain that references it. A chain whose rest
        /// pose starts inside a collider can never settle: the solver ejects it to the shell
        /// every frame while pull drags it back in, which reads as flailing hair or pinned
        /// breasts. This is exactly what happened on the first Warudo import (head collider
        /// scaled to 0.137 m swallowed the hair roots).
        /// </summary>
        private static void ShrinkCollidersAwayFromChains(List<ColliderData> colliders,
            List<GenChain> chains, GameObject avatarRoot, Animator anim, GenOptions opt)
        {
            if (colliders == null || colliders.Count == 0 || chains == null || avatarRoot == null)
            {
                return;
            }
            float scale = opt != null && opt.scale > 0f ? opt.scale : 1f;
            const float margin = 0.015f;   // metres of guaranteed clearance
            const float minRadius = 0.02f; // below this a collider does nothing useful

            for (int i = 0; i < colliders.Count; i++)
            {
                ColliderData c = colliders[i];
                Transform boneT = FindTransform(avatarRoot.transform, c.bone);
                if (boneT == null)
                {
                    continue;
                }
                float boneScale = LocalToWorldScale(boneT);
                Vector3 a = boneT.TransformPoint(c.offset);
                Vector3 b = c.hasEnd ? boneT.TransformPoint(c.offsetEnd) : a;

                for (int k = 0; k < chains.Count; k++)
                {
                    GenChain ch = chains[k];
                    if (ch == null || !ch.enabled || string.IsNullOrEmpty(ch.rootBone))
                    {
                        continue;
                    }
                    if (!CategoryEnabled(ch.category, opt))
                    {
                        continue;
                    }
                    if (System.Array.IndexOf(CollidersFor(ch.category), c.name) < 0)
                    {
                        continue;
                    }

                    Transform chainRoot = FindTransform(avatarRoot.transform, ch.rootBone);
                    if (chainRoot == null)
                    {
                        continue;
                    }
                    float chainRadius = PresetFor(ch.category).radius * scale;

                    // Every bone in the chain subtree must rest OUTSIDE the shell. The root
                    // itself is anchored (not simulated), so it is exempt.
                    Transform[] bones = chainRoot.GetComponentsInChildren<Transform>(true);
                    for (int m = 0; m < bones.Length; m++)
                    {
                        if (bones[m] == chainRoot)
                        {
                            continue;
                        }
                        float dist = DistancePointSegment(bones[m].position, a, b);
                        float needed = dist - chainRadius - margin;
                        if (needed < c.radius)
                        {
                            c.radius = Mathf.Max(minRadius, needed);
                        }
                    }
                }

                // Keep the emitted capsule height consistent with the (possibly shrunk) radius.
                if (c.hasEnd)
                {
                    c.height = c.offsetEnd.magnitude * boneScale + c.radius * 2f;
                }
            }
        }

        private static Transform FindTransform(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == name)
                {
                    return all[i];
                }
            }
            return null;
        }

        private static float DistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f)
            {
                return (p - a).magnitude;
            }
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return (p - (a + ab * t)).magnitude;
        }

        // ------------------------------------------------------------------
        // JSON emission
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the physbones.json payload for the VNyanPhysBones plugin.
        /// Field names are load bearing: they are deserialized by Newtonsoft on the
        /// plugin side and are case sensitive.
        /// </summary>
        public static string BuildJson(GameObject avatarRoot, Animator anim, List<GenChain> chains, GenOptions opt)
        {
            if (opt == null)
            {
                opt = new GenOptions();
            }
            float scale = opt.scale <= 0f ? 1f : opt.scale;

            List<ColliderData> colliders = BuildColliders(anim, opt);
            ShrinkCollidersAwayFromChains(colliders, chains, avatarRoot, anim, opt);

            HashSet<string> colliderNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < colliders.Count; i++)
            {
                colliderNames.Add(colliders[i].name);
            }

            JsonWriter w = new JsonWriter();
            w.BeginObject();

            // ---- settings ------------------------------------------------
            w.BeginObject("settings");
            w.Prop("enabled", true);
            w.Prop("substeps", 2);
            // gravityDir and nativePhysicsTypes are optional; omitted rather than null.
            // Scoped native-disable is the safe default for imported models: if any live spring
            // solver (MagicaCloth, VRM SpringBone) survived the import, two sims fighting over
            // the same bones reads as jitter or as one sim looking dead.
            w.Prop("disableNativePhysics", true);
            w.Prop("nativePhysicsScoped", true);
            w.EndObject();

            // ---- colliders -----------------------------------------------
            w.BeginArray("colliders");
            for (int i = 0; i < colliders.Count; i++)
            {
                ColliderData c = colliders[i];
                w.BeginObject();
                w.Prop("name", c.name);
                w.Prop("type", c.type);
                w.Prop("bone", c.bone);
                w.PropVector("offset", c.offset);
                if (c.hasEnd)
                {
                    w.PropVector("offsetEnd", c.offsetEnd);
                    w.PropVector("axis", c.axis);
                }
                w.Prop("radius", c.radius);
                w.Prop("height", c.height);
                w.EndObject();
            }
            w.EndArray();

            // ---- chains ---------------------------------------------------
            w.BeginArray("chains");
            if (chains != null)
            {
                for (int i = 0; i < chains.Count; i++)
                {
                    GenChain ch = chains[i];
                    if (ch == null || !ch.enabled)
                    {
                        continue;
                    }
                    if (string.IsNullOrEmpty(ch.rootBone))
                    {
                        continue;
                    }
                    if (!CategoryEnabled(ch.category, opt))
                    {
                        continue;
                    }

                    Preset p = PresetFor(ch.category);

                    // Only reference colliders that actually got emitted.
                    string[] wanted = CollidersFor(ch.category);
                    List<string> bound = new List<string>();
                    for (int k = 0; k < wanted.Length; k++)
                    {
                        if (colliderNames.Contains(wanted[k]))
                        {
                            bound.Add(wanted[k]);
                        }
                    }

                    w.BeginObject();
                    w.Prop("name", string.IsNullOrEmpty(ch.name) ? ch.rootBone : ch.name);
                    w.Prop("rootBone", ch.rootBone);
                    w.PropStringArray("ignore", null);
                    w.PropStringArray("colliders", bound);
                    w.Prop("pull", p.pull);
                    w.Prop("spring", p.spring);
                    w.Prop("stiffness", p.stiffness);
                    w.Prop("gravity", p.gravity);
                    w.Prop("gravityFalloff", 0f);
                    w.Prop("immobile", 0f);
                    w.Prop("immobileWorld", true);
                    w.Prop("limitType", p.limitType);
                    w.Prop("maxAngle", p.maxAngle);
                    w.Prop("radius", p.radius * scale);
                    w.Prop("maxStretch", 0f);
                    w.EndObject();
                }
            }
            w.EndArray();

            w.EndObject();
            return w.ToString();
        }

        // ------------------------------------------------------------------
        // Writing
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes the config as UTF-8 without a BOM, creating parent directories and
        /// backing up any existing file to "&lt;path&gt;.bak". Returns the final path, or
        /// null with <paramref name="error"/> set.
        /// </summary>
        public static string Write(string json, string path, out string error)
        {
            error = null;

            if (json == null)
            {
                error = "No JSON to write.";
                return null;
            }
            if (string.IsNullOrEmpty(path))
            {
                error = "No output path given.";
                return null;
            }

            try
            {
                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(full))
                {
                    // Timestamped, never overwritten. The single-slot ".bak" scheme destroyed a
                    // user's hand-tuned config: two exports in one session left both the file
                    // AND the backup holding generated data.
                    string stamp = File.GetLastWriteTimeUtc(full).ToString("yyyyMMdd-HHmmss");
                    string bak = full + "." + stamp + ".bak";
                    try
                    {
                        if (!File.Exists(bak))
                        {
                            File.Copy(full, bak);
                        }
                    }
                    catch (Exception bex)
                    {
                        // A failed backup must not block the write.
                        Debug.LogWarning("[WarudoImporter] Could not back up " + full + ": " + bex.Message);
                    }
                }

                UTF8Encoding utf8NoBom = new UTF8Encoding(false);
                File.WriteAllText(full, json, utf8NoBom);
                return full;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Minimal JSON writer
        // ------------------------------------------------------------------

        /// <summary>
        /// Tiny StringBuilder based JSON writer. Floats are always formatted with
        /// InvariantCulture - on a machine with a comma decimal separator the config
        /// would otherwise be silently corrupt.
        /// </summary>
        private class JsonWriter
        {
            private readonly StringBuilder sb = new StringBuilder(8192);
            private readonly List<int> marks = new List<int>();
            private int depth;
            private bool needComma;

            private static readonly System.Globalization.CultureInfo Inv =
                System.Globalization.CultureInfo.InvariantCulture;

            private const string FloatFormat = "0.######";

            public override string ToString()
            {
                return sb.ToString();
            }

            // ---- structure ----------------------------------------------

            public void BeginObject()
            {
                Pre();
                sb.Append('{');
                Push();
            }

            public void BeginObject(string name)
            {
                Pre();
                Key(name);
                sb.Append('{');
                Push();
            }

            public void EndObject()
            {
                Pop('}');
            }

            public void BeginArray(string name)
            {
                Pre();
                Key(name);
                sb.Append('[');
                Push();
            }

            public void EndArray()
            {
                Pop(']');
            }

            // ---- scalars -------------------------------------------------

            public void Prop(string name, string value)
            {
                Pre();
                Key(name);
                if (value == null)
                {
                    sb.Append("null");
                }
                else
                {
                    AppendString(value);
                }
            }

            public void Prop(string name, bool value)
            {
                Pre();
                Key(name);
                sb.Append(value ? "true" : "false");
            }

            public void Prop(string name, int value)
            {
                Pre();
                Key(name);
                sb.Append(value.ToString(Inv));
            }

            public void Prop(string name, float value)
            {
                Pre();
                Key(name);
                AppendFloat(value);
            }

            public void PropVector(string name, Vector3 v)
            {
                Pre();
                Key(name);
                sb.Append('[');
                AppendFloat(v.x);
                sb.Append(", ");
                AppendFloat(v.y);
                sb.Append(", ");
                AppendFloat(v.z);
                sb.Append(']');
            }

            public void PropStringArray(string name, List<string> values)
            {
                Pre();
                Key(name);
                sb.Append('[');
                if (values != null)
                {
                    for (int i = 0; i < values.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        AppendString(values[i]);
                    }
                }
                sb.Append(']');
            }

            // ---- internals -----------------------------------------------

            private void Push()
            {
                depth++;
                needComma = false;
                marks.Add(sb.Length);
            }

            private void Pop(char close)
            {
                int mark = marks.Count > 0 ? marks[marks.Count - 1] : sb.Length;
                if (marks.Count > 0)
                {
                    marks.RemoveAt(marks.Count - 1);
                }
                if (depth > 0)
                {
                    depth--;
                }
                if (sb.Length != mark)
                {
                    NewLine();
                }
                sb.Append(close);
                needComma = true;
            }

            private void Pre()
            {
                if (needComma)
                {
                    sb.Append(',');
                }
                if (sb.Length > 0)
                {
                    NewLine();
                }
                needComma = true;
            }

            private void NewLine()
            {
                sb.Append('\n');
                for (int i = 0; i < depth; i++)
                {
                    sb.Append("  ");
                }
            }

            private void Key(string name)
            {
                AppendString(name);
                sb.Append(": ");
            }

            private void AppendFloat(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    sb.Append('0');
                    return;
                }
                sb.Append(value.ToString(FloatFormat, Inv));
            }

            private void AppendString(string s)
            {
                sb.Append('"');
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        char c = s[i];
                        switch (c)
                        {
                            case '"': sb.Append("\\\""); break;
                            case '\\': sb.Append("\\\\"); break;
                            case '\b': sb.Append("\\b"); break;
                            case '\f': sb.Append("\\f"); break;
                            case '\n': sb.Append("\\n"); break;
                            case '\r': sb.Append("\\r"); break;
                            case '\t': sb.Append("\\t"); break;
                            default:
                                if (c < ' ' || c == '\u007f')
                                {
                                    sb.Append("\\u");
                                    sb.Append(((int)c).ToString("x4", Inv));
                                }
                                else
                                {
                                    sb.Append(c);
                                }
                                break;
                        }
                    }
                }
                sb.Append('"');
            }
        }
    }
}
