// BlendShapeMapper.cs
// Part of the VNyan Warudo Importer.
//
// A .warudo / VRChat model carries raw mesh blendshapes but no VRM BlendShapeClips.
// VNyan drives facial expressions through VRM clip presets, so before we can hand the
// model to VNyan we have to classify the raw blendshape names into VRM presets.
//
// Hard constraints (do not break):
//   * Only UnityEngine, System, System.Collections.Generic, System.IO, System.Text.
//   * No UnityEditor, no UniVRM types, no Newtonsoft.
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
    /// <summary>
    /// The VRM BlendShapeClip presets we can target. The names match the UniVRM
    /// BlendShapePreset enum exactly, because the actual clip creation is done later
    /// through reflection using <see cref="ClipPlan.presetName"/>.
    /// </summary>
    public enum VrmPreset
    {
        Unknown,
        Neutral,
        A,
        I,
        U,
        E,
        O,
        Blink,
        Blink_L,
        Blink_R,
        Joy,
        Angry,
        Sorrow,
        Fun,
        LookUp,
        LookDown,
        LookLeft,
        LookRight
    }

    /// <summary>One concrete mesh blendshape that will be driven by a clip.</summary>
    public class ShapeRef
    {
        public SkinnedMeshRenderer renderer;
        public int index;
        public string name;
        public float weight;
    }

    /// <summary>One planned VRM BlendShapeClip, possibly fed by several mesh shapes.</summary>
    public class ClipPlan
    {
        public VrmPreset preset;
        public string presetName;
        public List<ShapeRef> shapes;
        public bool isBinary;

        /// <summary>
        /// Set for clips that are NOT one of the VRM presets - Perfect Sync / ARKit shapes get a
        /// clip named after the shape itself, with preset Unknown. Null for preset clips.
        /// </summary>
        public string customName;
    }

    /// <summary>
    /// Classifies raw mesh blendshape names into VRM clip presets.
    /// Everything is static and side effect free apart from a single informational log.
    /// </summary>
    public static class BlendShapeMapper
    {
        // ------------------------------------------------------------------
        // Name normalisation
        // ------------------------------------------------------------------

        // Prefixes that carry no meaning for classification.
        private static readonly string[] StripablePrefixes = new string[]
        {
            "vrc.", "vrc_", "blendshape.", "blendshape_", "bs_", "key.", "key_", "shapekey_", "shapekey."
        };

        /// <summary>
        /// Lowercases, drops a "MeshName." qualifier, strips the common exporter
        /// prefixes, removes '_', '-', '.' and spaces, then folds a trailing
        /// "left"/"right" down to "l"/"r" so ARKit's two spellings collapse together
        /// (eyeBlinkLeft and eyeBlink_L both become "eyeblinkl").
        /// </summary>
        private static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            string s = raw.Trim().ToLowerInvariant();
            s = StripPrefixes(s);

            // "Body.vrc.v_aa" -> "vrc.v_aa" -> "v_aa". Only drop the qualifier when
            // what follows is long enough to be a real name, so "Blink.L" survives.
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && (s.Length - dot - 1) > 2)
            {
                s = s.Substring(dot + 1);
                s = StripPrefixes(s);
            }

            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_' || c == '-' || c == ' ' || c == '.')
                {
                    continue;
                }
                sb.Append(c);
            }
            s = sb.ToString();

            if (s.Length > 5 && s.EndsWith("left", StringComparison.Ordinal))
            {
                s = s.Substring(0, s.Length - 4) + "l";
            }
            else if (s.Length > 6 && s.EndsWith("right", StringComparison.Ordinal))
            {
                s = s.Substring(0, s.Length - 5) + "r";
            }

            return s;
        }

        private static string StripPrefixes(string s)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < StripablePrefixes.Length; i++)
                {
                    string p = StripablePrefixes[i];
                    if (s.Length > p.Length && s.StartsWith(p, StringComparison.Ordinal))
                    {
                        s = s.Substring(p.Length);
                        changed = true;
                    }
                }
            }
            return s;
        }

        // ------------------------------------------------------------------
        // Classification table
        // ------------------------------------------------------------------

        private static Dictionary<string, VrmPreset> _map;
        private static HashSet<string> _approx;

        // VRChat consonant visemes have no VRM equivalent, so they are folded onto the
        // nearest vowel. Tracked separately so an exact vowel always wins over them.
        private static HashSet<string> ApproxKeys
        {
            get
            {
                if (_approx == null)
                {
                    _approx = new HashSet<string>(StringComparer.Ordinal);
                    string[] keys = new string[]
                    {
                        "vdd", "dd", "vnn", "nn", "vth", "th",
                        "vff", "ff", "vpp", "pp",
                        "vkk", "kk", "vss", "ss", "vch", "ch", "vrr", "rr"
                    };
                    for (int i = 0; i < keys.Length; i++)
                    {
                        _approx.Add(keys[i]);
                    }
                }
                return _approx;
            }
        }

        private static Dictionary<string, VrmPreset> Map
        {
            get
            {
                if (_map != null)
                {
                    return _map;
                }

                Dictionary<string, VrmPreset> m = new Dictionary<string, VrmPreset>(StringComparer.Ordinal);

                // ---- Neutral / silence -------------------------------------
                Add(m, VrmPreset.Neutral, "neutral", "fclallneutral", "basis", "default", "vsil", "sil", "silence");

                // ---- Vowels: classic VRM, Japanese kana, VRoid, VRChat ------
                Add(m, VrmPreset.A, "a", "あ", "vaa", "aa", "ah", "fclmtha", "mtha", "vowela");
                Add(m, VrmPreset.I, "i", "い", "vih", "ih", "fclmthi", "mthi", "voweli");
                Add(m, VrmPreset.U, "u", "う", "vou", "ou", "fclmthu", "mthu", "vowelu");
                Add(m, VrmPreset.E, "e", "え", "ve", "eh", "fclmthe", "mthe", "vowele");
                Add(m, VrmPreset.O, "o", "お", "voh", "oh", "fclmtho", "mtho", "vowelo");

                // ---- VRChat consonant visemes -> nearest vowel (approximate) -
                Add(m, VrmPreset.I, "vdd", "dd", "vnn", "nn", "vth", "th");
                Add(m, VrmPreset.U, "vff", "ff", "vpp", "pp");
                Add(m, VrmPreset.E, "vkk", "kk", "vss", "ss", "vch", "ch", "vrr", "rr");

                // ---- Blink --------------------------------------------------
                Add(m, VrmPreset.Blink,
                    "blink", "blinkboth", "eyeblink", "eyeclose", "eyeclosed", "eyesclose", "eyesclosed",
                    "closeeye", "closeeyes", "fcleyeclose", "eyesblink");
                Add(m, VrmPreset.Blink_L,
                    "blinkl", "eyeblinkl", "eyeclosel", "eyesclosel", "fcleyeclosel",
                    "lblink", "leyeclose", "eyelclose", "eyelblink", "closeeyel", "winkl");
                Add(m, VrmPreset.Blink_R,
                    "blinkr", "eyeblinkr", "eyecloser", "eyescloser", "fcleyecloser",
                    "rblink", "reyeclose", "eyerclose", "eyerblink", "closeeyer", "winkr");

                // ---- Emotions ------------------------------------------------
                Add(m, VrmPreset.Joy, "joy", "happy", "smile", "fclalljoy", "fclmthjoy", "joyful");
                Add(m, VrmPreset.Angry, "angry", "anger", "mad", "fclallangry", "fclmthangry");
                Add(m, VrmPreset.Sorrow, "sorrow", "sad", "sadness", "fclallsorrow", "fclmthsorrow");
                Add(m, VrmPreset.Fun, "fun", "fclallfun", "surprised", "surprise", "fclmthfun");

                // ---- Eye look ------------------------------------------------
                // ARKit splits per eye; both halves feed one preset clip.
                Add(m, VrmPreset.LookUp,
                    "lookup", "eyelookup", "eyelookupl", "eyelookupr", "eyeup", "eyeupl", "eyeupr",
                    "eyesup", "fcleyelookup", "eyeslookup");
                Add(m, VrmPreset.LookDown,
                    "lookdown", "eyelookdown", "eyelookdownl", "eyelookdownr", "eyedown", "eyedownl", "eyedownr",
                    "eyesdown", "fcleyelookdown", "eyeslookdown");
                // Looking left  = left eye out  + right eye in.
                Add(m, VrmPreset.LookLeft,
                    "lookl", "eyelookl", "eyelookoutl", "eyelookinr", "eyesl", "eyeslookl", "fcleyelookl");
                // Looking right = left eye in   + right eye out.
                Add(m, VrmPreset.LookRight,
                    "lookr", "eyelookr", "eyelookinl", "eyelookoutr", "eyesr", "eyeslookr", "fcleyelookr");

                _map = m;
                return _map;
            }
        }

        private static void Add(Dictionary<string, VrmPreset> m, VrmPreset preset, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i];
                if (string.IsNullOrEmpty(k))
                {
                    continue;
                }
                if (!m.ContainsKey(k))
                {
                    m.Add(k, preset);
                }
            }
        }

        // ------------------------------------------------------------------
        // ARKit
        // ------------------------------------------------------------------

        private static readonly string[] ArKit52 = new string[]
        {
            "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight",
            "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
            "eyeBlinkLeft", "eyeBlinkRight",
            "eyeLookDownLeft", "eyeLookDownRight", "eyeLookInLeft", "eyeLookInRight",
            "eyeLookOutLeft", "eyeLookOutRight", "eyeLookUpLeft", "eyeLookUpRight",
            "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight",
            "jawForward", "jawLeft", "jawOpen", "jawRight",
            "mouthClose", "mouthDimpleLeft", "mouthDimpleRight",
            "mouthFrownLeft", "mouthFrownRight", "mouthFunnel", "mouthLeft",
            "mouthLowerDownLeft", "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight",
            "mouthPucker", "mouthRight", "mouthRollLower", "mouthRollUpper",
            "mouthShrugLower", "mouthShrugUpper", "mouthSmileLeft", "mouthSmileRight",
            "mouthStretchLeft", "mouthStretchRight", "mouthUpperUpLeft", "mouthUpperUpRight",
            "noseSneerLeft", "noseSneerRight", "tongueOut"
        };

        /// <summary>
        /// Plans one clip per ARKit shape actually present on the avatar.
        ///
        /// This is what makes Perfect Sync work, and it is NOT optional: the host applies
        /// tracking through VRMBlendShapeProxy.AccumulateValue with a key built by
        /// BlendShapeKey.CreateUnknown(name), which resolves a CLIP by name - it never looks at
        /// mesh blendshapes. A model can carry all 52 ARKit shapes on its mesh and still be
        /// completely unreadable to the host until each one also exists as a clip.
        ///
        /// Two clips are emitted per shape when the authored name is not already lower case: one
        /// under the exact mesh name and one lower-cased. BlendShapeKey compares
        /// "Unknown_" + Name as a case-SENSITIVE string, and hosts differ in whether they
        /// lower-case incoming tracking names, so covering both spellings is what makes this
        /// robust. The two clips share the same binding, and only whichever one the host asks
        /// for is ever accumulated, so nothing is double-applied.
        /// </summary>
        public static List<ClipPlan> PlanArKit(GameObject avatarRoot)
        {
            List<ClipPlan> plans = new List<ClipPlan>();
            if (avatarRoot == null) return plans;

            // Canonical spelling per normalized name, so "eyeBlink_L" still yields a clip named
            // the way ARKit spells it as well as the mesh's own spelling.
            Dictionary<string, string> canonical = new Dictionary<string, string>();
            for (int i = 0; i < ArKit52.Length; i++) canonical[Normalize(ArKit52[i])] = ArKit52[i];

            HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            SkinnedMeshRenderer[] smrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < smrs.Length; r++)
            {
                SkinnedMeshRenderer smr = smrs[r];
                if (smr == null || smr.sharedMesh == null) continue;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    string raw = smr.sharedMesh.GetBlendShapeName(i);
                    string bare = raw;
                    int dot = bare.LastIndexOf('.');
                    if (dot >= 0) bare = bare.Substring(dot + 1);   // strip the "Mesh." qualifier

                    string norm = Normalize(bare);
                    if (!canonical.ContainsKey(norm)) continue;      // not an ARKit shape

                    // Names to expose this shape under: the mesh's own spelling, the canonical
                    // ARKit spelling, and the lower-cased form of each.
                    List<string> names = new List<string>();
                    AddName(names, bare);
                    AddName(names, canonical[norm]);
                    AddName(names, bare.ToLowerInvariant());
                    AddName(names, canonical[norm].ToLowerInvariant());

                    for (int n = 0; n < names.Count; n++)
                    {
                        if (!emitted.Add(names[n])) continue;        // first mesh wins a given name
                        ClipPlan p = new ClipPlan();
                        p.preset = VrmPreset.Unknown;
                        p.presetName = null;
                        p.customName = names[n];
                        p.isBinary = false;
                        p.shapes = new List<ShapeRef>();
                        ShapeRef s = new ShapeRef();
                        s.renderer = smr;
                        s.index = i;
                        s.name = raw;
                        s.weight = 100f;
                        p.shapes.Add(s);
                        plans.Add(p);
                    }
                }
            }
            return plans;
        }

        private static void AddName(List<string> list, string n)
        {
            if (string.IsNullOrEmpty(n)) return;
            for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], n, StringComparison.Ordinal)) return;
            list.Add(n);
        }

        /// <summary>The 52 canonical ARKit blendshape names.</summary>
        public static List<string> ArKitNames
        {
            get
            {
                List<string> list = new List<string>(ArKit52.Length);
                for (int i = 0; i < ArKit52.Length; i++)
                {
                    list.Add(ArKit52[i]);
                }
                return list;
            }
        }

        /// <summary>
        /// True when the avatar carries a usable Perfect Sync set, defined as at least
        /// 40 of the 52 ARKit shapes present. Matching is case insensitive and accepts
        /// the "_L"/"_R" suffix spelling.
        /// </summary>
        public static bool IsPerfectSync(GameObject avatarRoot)
        {
            if (avatarRoot == null)
            {
                return false;
            }

            HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
            SkinnedMeshRenderer[] smrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < smrs.Length; r++)
            {
                SkinnedMeshRenderer smr = smrs[r];
                if (smr == null || smr.sharedMesh == null)
                {
                    continue;
                }
                Mesh mesh = smr.sharedMesh;
                int count = mesh.blendShapeCount;
                for (int i = 0; i < count; i++)
                {
                    string n = Normalize(mesh.GetBlendShapeName(i));
                    if (n.Length > 0)
                    {
                        present.Add(n);
                    }
                }
            }

            int hits = 0;
            for (int i = 0; i < ArKit52.Length; i++)
            {
                if (present.Contains(Normalize(ArKit52[i])))
                {
                    hits++;
                }
            }
            return hits >= 40;
        }

        // ------------------------------------------------------------------
        // Classification
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the UniVRM preset name for a raw blendshape name, or null when the
        /// shape does not correspond to any VRM preset (which is fine - VNyan can still
        /// drive it as a raw mesh blendshape).
        /// </summary>
        public static string Classify(string blendShapeName)
        {
            VrmPreset p = ClassifyPreset(blendShapeName);
            if (p == VrmPreset.Unknown)
            {
                return null;
            }
            return PresetName(p);
        }

        private static VrmPreset ClassifyPreset(string blendShapeName)
        {
            string key = Normalize(blendShapeName);
            if (key.Length == 0)
            {
                return VrmPreset.Unknown;
            }

            VrmPreset preset;
            if (Map.TryGetValue(key, out preset))
            {
                return preset;
            }
            return VrmPreset.Unknown;
        }

        /// <summary>Exact UniVRM BlendShapePreset enum name, consumed later by reflection.</summary>
        public static string PresetName(VrmPreset preset)
        {
            switch (preset)
            {
                case VrmPreset.Neutral: return "Neutral";
                case VrmPreset.A: return "A";
                case VrmPreset.I: return "I";
                case VrmPreset.U: return "U";
                case VrmPreset.E: return "E";
                case VrmPreset.O: return "O";
                case VrmPreset.Blink: return "Blink";
                case VrmPreset.Joy: return "Joy";
                case VrmPreset.Angry: return "Angry";
                case VrmPreset.Sorrow: return "Sorrow";
                case VrmPreset.Fun: return "Fun";
                case VrmPreset.LookUp: return "LookUp";
                case VrmPreset.LookDown: return "LookDown";
                case VrmPreset.LookLeft: return "LookLeft";
                case VrmPreset.LookRight: return "LookRight";
                case VrmPreset.Blink_L: return "Blink_L";
                case VrmPreset.Blink_R: return "Blink_R";
                default: return "Unknown";
            }
        }

        // Emission order for the generated clip list.
        private static readonly VrmPreset[] PresetOrder = new VrmPreset[]
        {
            VrmPreset.Neutral,
            VrmPreset.A, VrmPreset.I, VrmPreset.U, VrmPreset.E, VrmPreset.O,
            VrmPreset.Blink, VrmPreset.Blink_L, VrmPreset.Blink_R,
            VrmPreset.Joy, VrmPreset.Angry, VrmPreset.Sorrow, VrmPreset.Fun,
            VrmPreset.LookUp, VrmPreset.LookDown, VrmPreset.LookLeft, VrmPreset.LookRight
        };

        private static bool IsLookPreset(VrmPreset p)
        {
            return p == VrmPreset.LookUp || p == VrmPreset.LookDown
                || p == VrmPreset.LookLeft || p == VrmPreset.LookRight;
        }

        private static bool IsBinaryPreset(VrmPreset p)
        {
            return p == VrmPreset.Blink || p == VrmPreset.Blink_L || p == VrmPreset.Blink_R;
        }

        private class Candidate
        {
            public VrmPreset preset;
            public ShapeRef shape;
            public int rank;
        }

        // ------------------------------------------------------------------
        // Plan
        // ------------------------------------------------------------------

        /// <summary>
        /// Walks every SkinnedMeshRenderer under <paramref name="avatarRoot"/> (inactive
        /// included) and returns one ClipPlan per preset that received at least one shape.
        /// Unmapped shapes are deliberately left alone.
        /// </summary>
        public static List<ClipPlan> Plan(GameObject avatarRoot)
        {
            List<ClipPlan> result = new List<ClipPlan>();
            if (avatarRoot == null)
            {
                return result;
            }

            List<Candidate> candidates = new List<Candidate>();
            List<string> approxNotes = new List<string>();

            SkinnedMeshRenderer[] smrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < smrs.Length; r++)
            {
                SkinnedMeshRenderer smr = smrs[r];
                if (smr == null || smr.sharedMesh == null)
                {
                    continue;
                }

                Mesh mesh = smr.sharedMesh;
                int count = mesh.blendShapeCount;
                for (int i = 0; i < count; i++)
                {
                    string raw = mesh.GetBlendShapeName(i);
                    string key = Normalize(raw);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    VrmPreset preset;
                    if (!Map.TryGetValue(key, out preset) || preset == VrmPreset.Unknown)
                    {
                        continue; // leave it as a raw mesh blendshape
                    }

                    bool approximate = ApproxKeys.Contains(key);
                    if (approximate)
                    {
                        approxNotes.Add(raw + " -> " + PresetName(preset));
                    }

                    ShapeRef sr = new ShapeRef();
                    sr.renderer = smr;
                    sr.index = i;
                    sr.name = raw;
                    sr.weight = 100f;

                    Candidate c = new Candidate();
                    c.preset = preset;
                    c.shape = sr;
                    c.rank = approximate ? 1 : 2;
                    candidates.Add(c);
                }
            }

            for (int p = 0; p < PresetOrder.Length; p++)
            {
                VrmPreset preset = PresetOrder[p];
                List<ShapeRef> picked = new List<ShapeRef>();

                if (IsLookPreset(preset))
                {
                    // Look presets legitimately combine a left and a right shape.
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        if (candidates[c].preset == preset)
                        {
                            picked.Add(candidates[c].shape);
                        }
                    }
                }
                else
                {
                    // One shape per renderer, best rank wins, so an exact vowel beats a
                    // folded consonant viseme and we never stack two shapes to 200%.
                    List<SkinnedMeshRenderer> seen = new List<SkinnedMeshRenderer>();
                    List<Candidate> best = new List<Candidate>();

                    for (int c = 0; c < candidates.Count; c++)
                    {
                        Candidate cand = candidates[c];
                        if (cand.preset != preset)
                        {
                            continue;
                        }

                        int slot = -1;
                        for (int s = 0; s < seen.Count; s++)
                        {
                            if (seen[s] == cand.shape.renderer)
                            {
                                slot = s;
                                break;
                            }
                        }

                        if (slot < 0)
                        {
                            seen.Add(cand.shape.renderer);
                            best.Add(cand);
                        }
                        else if (cand.rank > best[slot].rank)
                        {
                            best[slot] = cand;
                        }
                    }

                    for (int b = 0; b < best.Count; b++)
                    {
                        picked.Add(best[b].shape);
                    }
                }

                if (picked.Count == 0)
                {
                    continue;
                }

                ClipPlan plan = new ClipPlan();
                plan.preset = preset;
                plan.presetName = PresetName(preset);
                plan.shapes = picked;
                plan.isBinary = IsBinaryPreset(preset);
                result.Add(plan);
            }

            if (approxNotes.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("[WarudoImporter] ");
                sb.Append(approxNotes.Count.ToString());
                sb.Append(" VRChat consonant viseme(s) were mapped to the nearest VRM vowel: ");
                for (int i = 0; i < approxNotes.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(approxNotes[i]);
                }
                Debug.Log(sb.ToString());
            }

            return result;
        }
    }
}
