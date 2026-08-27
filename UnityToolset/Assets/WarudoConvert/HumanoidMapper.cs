using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using HB = UnityEngine.HumanBodyBones; // keeps the alias tables readable at one line per bone

namespace WarudoImporter
{
    /// <summary>Result of an automatic humanoid bone mapping pass.</summary>
    public class MapResult
    {
        public Dictionary<HumanBodyBones, Transform> map = new Dictionary<HumanBodyBones, Transform>();
        public List<HumanBodyBones> missingRequired = new List<HumanBodyBones>();
        public List<string> notes = new List<string>();

        public bool CanBuild { get { return missingRequired.Count == 0; } }

        public Transform Get(HumanBodyBones b)
        {
            Transform t;
            if (map.TryGetValue(b, out t)) return t;
            return null;
        }
    }

    /// <summary>
    /// Name/topology driven mapper from an arbitrary bone hierarchy to Unity's HumanBodyBones,
    /// plus a runtime humanoid Avatar builder. Runtime-safe (no UnityEditor dependency).
    /// </summary>
    public static class HumanoidMapper
    {
        // Unity's 15 required humanoid bones. Neck / Chest / shoulders / toes / fingers are optional.
        public static readonly HumanBodyBones[] Required = new HumanBodyBones[]
        {
            HB.Hips, HB.Spine, HB.Head,
            HB.LeftUpperLeg, HB.LeftLowerLeg, HB.LeftFoot,
            HB.RightUpperLeg, HB.RightLowerLeg, HB.RightFoot,
            HB.LeftUpperArm, HB.LeftLowerArm, HB.LeftHand,
            HB.RightUpperArm, HB.RightLowerArm, HB.RightHand
        };

        private const int SIDE_NONE = 0, SIDE_LEFT = 1, SIDE_RIGHT = 2;

        private static Dictionary<HB, List<string>> _alias;        // normalised match tokens
        private static Dictionary<HB, List<string>> _aliasDisplay; // pretty examples for the UI
        private static Dictionary<HB, int> _side;                  // SIDE_* this bone belongs to
        private static Dictionary<HB, HB[]> _parents;              // acceptable humanoid ancestors
        private static HB[] _order;                                // parents resolved before children
        private static HashSet<HB> _deferred;                      // solved after the limb chains, not before

        // Auxiliary-bone vocabulary. A bone whose name carries one of these as a whole word is a
        // jiggle / twist / IK / marker bone, never part of the deformation chain. Penalty, not a
        // hard exclusion, because rigs like "Hips_root" legitimately name a real bone this way.
        private static readonly string[] HELPER_TOKENS =
        { "jiggle", "twist", "end", "helper", "dummy", "null", "ik", "pole", "target", "root", "sway", "wobble" };
        private const int HELPER_PENALTY = 220;

        // joint limb-chain search
        private const int CHAIN_CANDIDATES = 8;      // top-N name matches considered per slot
        private const int CHAIN_COMPLETENESS = 1000000; // dominates any sum of name scores

        private static readonly string[] LPRE = { "left", "l", "lf", "jbipl" }, LSUF = { "l", "left" };
        private static readonly string[] RPRE = { "right", "r", "rt", "jbipr" }, RSUF = { "r", "right" };
        private static readonly string[] FINGERS = { "Thumb", "Index", "Middle", "Ring", "Little" };
        private static readonly string[] JOINTWORD = { "Proximal", "Intermediate", "Distal" };

        // ------------------------------------------------------------------ tables

        private static void EnsureTables()
        {
            if (_alias != null) return;
            _alias = new Dictionary<HB, List<string>>();
            _aliasDisplay = new Dictionary<HB, List<string>>();
            _side = new Dictionary<HB, int>();
            _parents = new Dictionary<HB, HB[]>();

            AddCenter(HB.Hips, "Hips", "Hip", "Pelvis", "Waist", "Bip01Pelvis");
            AddCenter(HB.Spine, "Spine", "Spine1", "Spine01", "LowerBody", "Abdomen");
            AddCenter(HB.Chest, "Chest", "Spine2", "Spine02", "UpperBody", "Bust", "Torso");
            AddCenter(HB.UpperChest, "UpperChest", "Chest2", "Spine3", "Spine03");
            AddCenter(HB.Neck, "Neck", "Neck1");
            AddCenter(HB.Head, "Head");
            AddSided(HB.LeftShoulder, HB.RightShoulder, "Shoulder", "Clavicle", "Collar");
            AddSided(HB.LeftUpperArm, HB.RightUpperArm, "UpperArm", "UpArm", "Arm", "Shoulder2");
            AddSided(HB.LeftLowerArm, HB.RightLowerArm, "LowerArm", "ForeArm", "Elbow", "LoArm");
            AddSided(HB.LeftHand, HB.RightHand, "Hand", "Wrist");
            AddSided(HB.LeftUpperLeg, HB.RightUpperLeg, "UpperLeg", "UpLeg", "Thigh", "Leg");
            AddSided(HB.LeftLowerLeg, HB.RightLowerLeg, "LowerLeg", "Knee", "Shin", "Calf", "Leg");
            AddSided(HB.LeftFoot, HB.RightFoot, "Foot", "Ankle");
            AddSided(HB.LeftToes, HB.RightToes, "Toes", "ToeBase", "Toe", "ToeEnd");
            AddFingerTables();

            // hierarchy expectations (nearest-first list of acceptable humanoid ancestors)
            _parents[HB.Spine] = new HB[] { HB.Hips };
            _parents[HB.Chest] = new HB[] { HB.Spine, HB.Hips };
            _parents[HB.UpperChest] = new HB[] { HB.Chest, HB.Spine };
            _parents[HB.Neck] = new HB[] { HB.UpperChest, HB.Chest, HB.Spine };
            _parents[HB.Head] = new HB[] { HB.Neck, HB.UpperChest, HB.Chest };
            AddParents(HB.LeftShoulder, HB.RightShoulder, HB.UpperChest, HB.Chest, HB.Spine);
            AddParents(HB.LeftUpperArm, HB.RightUpperArm, HB.LeftShoulder, HB.UpperChest, HB.Chest);
            AddParents(HB.LeftLowerArm, HB.RightLowerArm, HB.LeftUpperArm);
            AddParents(HB.LeftHand, HB.RightHand, HB.LeftLowerArm);
            AddParents(HB.LeftUpperLeg, HB.RightUpperLeg, HB.Hips);
            AddParents(HB.LeftLowerLeg, HB.RightLowerLeg, HB.LeftUpperLeg);
            AddParents(HB.LeftFoot, HB.RightFoot, HB.LeftLowerLeg);
            AddParents(HB.LeftToes, HB.RightToes, HB.LeftFoot);
            for (int f = 0; f < FINGERS.Length; f++)
            {
                AddParents(FingerBone(SIDE_LEFT, f, 0), FingerBone(SIDE_RIGHT, f, 0), HB.LeftHand);
                AddParents(FingerBone(SIDE_LEFT, f, 1), FingerBone(SIDE_RIGHT, f, 1), FingerBone(SIDE_LEFT, f, 0));
                AddParents(FingerBone(SIDE_LEFT, f, 2), FingerBone(SIDE_RIGHT, f, 2), FingerBone(SIDE_LEFT, f, 1));
            }
            BuildOrder();
        }

        /// <summary>Left-side parents are mirrored automatically for the right-side bone.</summary>
        private static void AddParents(HB left, HB right, params HB[] leftParents)
        {
            _parents[left] = leftParents;
            HB[] rp = new HB[leftParents.Length];
            for (int i = 0; i < leftParents.Length; i++) rp[i] = Mirror(leftParents[i]);
            _parents[right] = rp;
        }

        private static HB Mirror(HB b)
        {
            string n = b.ToString();
            if (n.StartsWith("Left")) return (HB)Enum.Parse(typeof(HB), "Right" + n.Substring(4));
            if (n.StartsWith("Right")) return (HB)Enum.Parse(typeof(HB), "Left" + n.Substring(5));
            return b;
        }

        private static HB FingerBone(int side, int finger, int joint)
        {
            return (HB)Enum.Parse(typeof(HB), (side == SIDE_RIGHT ? "Right" : "Left") + FINGERS[finger] + JOINTWORD[joint]);
        }

        private static void BuildOrder()
        {
            List<HB> o = new List<HB>(new HB[] { HB.Hips, HB.Spine, HB.Chest, HB.UpperChest, HB.Neck, HB.Head });
            HB[] sided = { HB.LeftShoulder, HB.LeftUpperArm, HB.LeftLowerArm, HB.LeftHand,
                           HB.LeftUpperLeg, HB.LeftLowerLeg, HB.LeftFoot, HB.LeftToes };
            for (int i = 0; i < sided.Length; i++) { o.Add(sided[i]); o.Add(Mirror(sided[i])); }
            for (int j = 0; j < 3; j++)
                for (int f = 0; f < FINGERS.Length; f++)
                { o.Add(FingerBone(SIDE_LEFT, f, j)); o.Add(FingerBone(SIDE_RIGHT, f, j)); }
            _order = o.ToArray();

            // The four limb chains are solved as units, and everything hanging off a hand or a foot
            // needs those solved first, so all of them are held back from the plain per-bone pass.
            _deferred = new HashSet<HB>();
            HB[] chain = { HB.LeftUpperArm, HB.LeftLowerArm, HB.LeftHand, HB.LeftUpperLeg, HB.LeftLowerLeg, HB.LeftFoot, HB.LeftToes };
            for (int i = 0; i < chain.Length; i++) { _deferred.Add(chain[i]); _deferred.Add(Mirror(chain[i])); }
            for (int j = 0; j < 3; j++)
                for (int f = 0; f < FINGERS.Length; f++)
                { _deferred.Add(FingerBone(SIDE_LEFT, f, j)); _deferred.Add(FingerBone(SIDE_RIGHT, f, j)); }
        }

        private static void AddFingerTables()
        {
            for (int f = 0; f < FINGERS.Length; f++)
            {
                string fn = FINGERS[f];
                for (int j = 0; j < 3; j++)
                {
                    int n1 = j + 1;
                    List<string> cores = new List<string>();
                    cores.Add(fn + JOINTWORD[j]);   // Unity:  LeftIndexProximal
                    cores.Add(fn + n1);             // VRM:    J_Bip_L_Index1
                    cores.Add("Hand" + fn + n1);    // Mixamo: LeftHandIndex1
                    cores.Add(fn + "Finger" + n1);  // VRChat: Left IndexFinger1
                    cores.Add("Finger" + fn + n1);
                    cores.Add(fn + "0" + n1);       // Index01
                    cores.Add("f" + fn + "0" + n1); // Rigify: f_index.01.L
                    if (fn == "Little")
                    { cores.Add("Pinky" + n1); cores.Add("HandPinky" + n1); cores.Add("PinkyFinger" + n1); cores.Add("Pinky" + JOINTWORD[j]); }
                    AddSided(FingerBone(SIDE_LEFT, f, j), FingerBone(SIDE_RIGHT, f, j), cores.ToArray());
                }
            }
        }

        // ------------------------------------------------------------------ alias helpers

        // "J_Bip_C_Hips" style VRM names collapse to "jbipc" + core, hence the pseudo prefix.
        private static void AddCenter(HB b, params string[] cores)
        {
            AddAliases(b, SIDE_NONE, cores, new string[] { "", "jbipc" }, new string[0], "", "C");
        }

        private static void AddSided(HB left, HB right, params string[] cores)
        {
            AddAliases(left, SIDE_LEFT, cores, LPRE, LSUF, "Left", "L");
            AddAliases(right, SIDE_RIGHT, cores, RPRE, RSUF, "Right", "R");
        }

        private static void AddAliases(HB b, int side, string[] cores, string[] pre, string[] suf, string dispPre, string dispSide)
        {
            List<string> norm = new List<string>();
            List<string> disp = new List<string>();
            for (int i = 0; i < cores.Length; i++)
            {
                string c = cores[i].ToLowerInvariant();
                for (int p = 0; p < pre.Length; p++) AddUnique(norm, pre[p] + c);
                for (int s = 0; s < suf.Length; s++) AddUnique(norm, c + suf[s]);
                AddUnique(disp, dispPre + cores[i]);
                if (side != SIDE_NONE) AddUnique(disp, cores[i] + "_" + dispSide);
            }
            AddUnique(disp, "J_Bip_" + dispSide + "_" + cores[0]);
            if (side != SIDE_NONE) AddUnique(disp, dispPre + " " + cores[cores.Length > 1 ? 1 : 0]);
            _alias[b] = norm;
            _aliasDisplay[b] = disp;
            _side[b] = side;
        }

        private static void AddUnique(List<string> list, string s)
        {
            if (!list.Contains(s)) list.Add(s);
        }

        /// <summary>Alias hints for one bone, for a manual-override UI.</summary>
        public static string[] AliasesFor(HumanBodyBones b)
        {
            EnsureTables();
            List<string> l;
            if (_aliasDisplay.TryGetValue(b, out l)) return l.ToArray();
            return new string[0];
        }

        // ------------------------------------------------------------------ name normalisation

        /// <summary>
        /// lowercase, drop mixamorig prefixes, drop Blender ".001" duplicate suffixes and
        /// collapse all separators so "Left Ankle" / "left_ankle" / "LeftAnkle" / "Ankle.L"
        /// all reduce to comparable tokens.
        /// </summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.ToLowerInvariant();
            int mi = s.IndexOf("mixamorig");
            if (mi >= 0)
            {
                s = s.Substring(mi + 9);
                while (s.Length > 0 && IsSep(s[0])) s = s.Substring(1);
            }
            s = StripNumericSuffix(s);
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) if (!IsSep(s[i])) sb.Append(s[i]);
            return sb.ToString();
        }

        private static readonly char[] SEPS = { ' ', '_', '-', '.', ':', '|', '/' };

        private static bool IsSep(char c)
        {
            for (int i = 0; i < SEPS.Length; i++) if (SEPS[i] == c) return true;
            return false;
        }

        // "Spine.001" -> "spine". Only trims a trailing .NNN / _NNN of 2-3 digits so that
        // legitimately numbered joints ("Spine1", "Index1", "Thumb0") survive untouched.
        private static string StripNumericSuffix(string s)
        {
            int cut = -1;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') continue;
                if ((c == '.' || c == '_') && i < s.Length - 1 && s.Length - 1 - i >= 2) cut = i;
                break;
            }
            if (cut >= 0) return s.Substring(0, cut);
            return s;
        }

        /// <summary>
        /// Split a raw bone name into lowercase words on separators, camel-case humps and
        /// letter/digit boundaries: "L_ThighJiggle.001" -> l, thigh, jiggle, 001.
        /// </summary>
        private static List<string> SplitTokens(string raw)
        {
            List<string> outp = new List<string>();
            if (string.IsNullOrEmpty(raw)) return outp;
            StringBuilder sb = new StringBuilder();
            char prev = '\0';
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (IsSep(c))
                {
                    if (sb.Length > 0) { outp.Add(sb.ToString().ToLowerInvariant()); sb.Length = 0; }
                    prev = '\0';
                    continue;
                }
                bool hump = sb.Length > 0 &&
                    ((char.IsUpper(c) && !char.IsUpper(prev)) || (char.IsDigit(c) != char.IsDigit(prev)));
                if (hump) { outp.Add(sb.ToString().ToLowerInvariant()); sb.Length = 0; }
                sb.Append(c);
                prev = c;
            }
            if (sb.Length > 0) outp.Add(sb.ToString().ToLowerInvariant());
            return outp;
        }

        /// <summary>True when the name reads as an auxiliary bone (jiggle / twist / IK / end marker).</summary>
        private static bool HasHelperToken(string raw)
        {
            List<string> toks = SplitTokens(raw);
            for (int i = 0; i < toks.Count; i++)
                for (int j = 0; j < HELPER_TOKENS.Length; j++)
                    if (toks[i] == HELPER_TOKENS[j]) return true;
            return false;
        }

        /// <summary>Detect the side a raw bone name declares, without ever guessing across sides.</summary>
        private static int DetectSide(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return SIDE_NONE;
            string s = raw.ToLowerInvariant();
            int mi = s.IndexOf("mixamorig");
            if (mi >= 0) s = s.Substring(mi + 9);

            // token pass: handles "L_Arm", "arm.L", "Left arm", "J_Bip_L_UpperArm"
            string[] tokens = s.Split(SEPS, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = StripNumericSuffix(tokens[i]);
                if (t == "l" || t == "left" || t == "lft" || t == "lf") return SIDE_LEFT;
                if (t == "r" || t == "right" || t == "rgt" || t == "rt") return SIDE_RIGHT;
            }

            // fused camel-case names ("LeftHandIndex1", "UpperArmRight")
            string flat = Normalize(raw);
            if (flat.IndexOf("left") >= 0) return SIDE_LEFT;
            if (flat.IndexOf("right") >= 0) return SIDE_RIGHT;
            return SIDE_NONE;
        }

        // ------------------------------------------------------------------ auto mapping

        public static MapResult AutoMap(Transform root) { return AutoMap(root, null); }

        public static MapResult AutoMap(Transform root, Dictionary<HumanBodyBones, string> overridesByName)
        {
            EnsureTables();
            MapResult m = new MapResult();
            if (root == null)
            {
                m.notes.Add("AutoMap: root transform is null.");
                for (int i = 0; i < Required.Length; i++) m.missingRequired.Add(Required[i]);
                return m;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true); // inactive included
            int n = all.Length;
            string[] norms = new string[n];
            int[] sides = new int[n];
            bool[] helpers = new bool[n];
            for (int i = 0; i < n; i++)
            {
                norms[i] = Normalize(all[i].name);
                sides[i] = DetectSide(all[i].name);
                helpers[i] = HasHelperToken(all[i].name);
            }

            HashSet<Transform> used = new HashSet<Transform>();

            // 1. explicit user overrides win outright
            if (overridesByName != null)
            {
                foreach (KeyValuePair<HumanBodyBones, string> kv in overridesByName)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    Transform hit = FindByName(all, norms, kv.Value);
                    if (hit == null)
                    {
                        m.notes.Add("Override for " + kv.Key + " names '" + kv.Value + "' but no such transform exists.");
                        continue;
                    }
                    m.map[kv.Key] = hit;
                    used.Add(hit);
                    m.notes.Add("Override: " + kv.Key + " = " + hit.name);
                }
            }

            // 2. name scoring for the spine/head/shoulders, parents before children. The limb chains
            //    and everything below a hand or a foot are held back for the joint solve.
            for (int oi = 0; oi < _order.Length; oi++)
            {
                HumanBodyBones bone = _order[oi];
                if (m.map.ContainsKey(bone) || _deferred.Contains(bone)) continue;
                MapByName(bone, all, norms, sides, helpers, root, m, used);
            }

            // 3. limb chains chosen as consistent units so a jiggle bone cannot win a slot on name
            //    score alone and then strand the rest of the chain.
            SolveChain(all, norms, sides, helpers, root, m, used, HB.LeftUpperLeg, HB.LeftLowerLeg, HB.LeftFoot, "left leg");
            SolveChain(all, norms, sides, helpers, root, m, used, HB.RightUpperLeg, HB.RightLowerLeg, HB.RightFoot, "right leg");
            SolveChain(all, norms, sides, helpers, root, m, used, HB.LeftUpperArm, HB.LeftLowerArm, HB.LeftHand, "left arm");
            SolveChain(all, norms, sides, helpers, root, m, used, HB.RightUpperArm, HB.RightLowerArm, HB.RightHand, "right arm");

            // 4. toes, fingers, and any limb slot the chain solve could not fill: per-bone matching.
            for (int oi = 0; oi < _order.Length; oi++)
            {
                HumanBodyBones bone = _order[oi];
                if (m.map.ContainsKey(bone)) continue;
                MapByName(bone, all, norms, sides, helpers, root, m, used);
            }

            PreferToesUnderFoot(all, norms, sides, helpers, root, m, used, HB.LeftToes, HB.LeftFoot);
            PreferToesUnderFoot(all, norms, sides, helpers, root, m, used, HB.RightToes, HB.RightFoot);

            FixThumbNumbering(all, norms, sides, m, used);
            HierarchySanityPass(m, used);
            StructuralFallback(root, m, used);

            for (int i = 0; i < Required.Length; i++)
                if (!m.map.ContainsKey(Required[i]) || m.map[Required[i]] == null)
                    m.missingRequired.Add(Required[i]);

            m.notes.Add("Mapped " + m.map.Count + " bones from " + n + " transforms; " +
                m.missingRequired.Count + " required bone(s) missing.");
            return m;
        }

        private static Transform FindByName(Transform[] all, string[] norms, string wanted)
        {
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == wanted) return all[i];
            string wn = Normalize(wanted);
            for (int i = 0; i < all.Length; i++)
                if (norms[i] == wn) return all[i];
            return null;
        }

        /// <summary>Seat one bone on its single best-scoring name match.</summary>
        private static void MapByName(HumanBodyBones bone, Transform[] all, string[] norms, int[] sides,
            bool[] helpers, Transform root, MapResult m, HashSet<Transform> used)
        {
            int bestScore = int.MinValue, secondScore = int.MinValue;
            Transform best = null, second = null;
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == root || used.Contains(t)) continue;
                int s = ScoreName(bone, norms[i], sides[i], t, m, helpers[i]);
                if (s == int.MinValue) continue;
                if (s > bestScore) { secondScore = bestScore; second = best; bestScore = s; best = t; }
                else if (s > secondScore) { secondScore = s; second = t; }
            }
            if (best == null || bestScore < 50) return;
            if (second != null && secondScore == bestScore)
                m.notes.Add("Ambiguous name match for " + bone + ": '" + best.name + "' and '" + second.name +
                    "' scored equally; took '" + best.name + "'.");
            m.map[bone] = best;
            used.Add(best);
        }

        private static int ScoreName(HumanBodyBones bone, string norm, int nameSide, Transform t, MapResult m, bool isHelper)
        {
            if (string.IsNullOrEmpty(norm)) return int.MinValue;

            int boneSide = _side[bone];
            // never let a left alias claim a right bone (or a sided name claim a centre bone)
            if (boneSide == SIDE_NONE && nameSide != SIDE_NONE) return int.MinValue;
            if (boneSide != SIDE_NONE && nameSide != SIDE_NONE && nameSide != boneSide) return int.MinValue;

            List<string> aliases = _alias[bone];
            int best = int.MinValue;
            for (int i = 0; i < aliases.Count; i++)
            {
                string a = aliases[i];
                int s = int.MinValue;
                int slack = norm.Length - a.Length;
                if (norm == a) s = 1000 - i * 8;
                else if (a.Length >= 5 && norm.EndsWith(a)) s = 600 - i * 8 - slack;
                else if (a.Length >= 5 && norm.StartsWith(a)) s = 500 - i * 8 - slack;
                else if (a.Length >= 6 && norm.IndexOf(a) >= 0) s = 380 - i * 8 - slack;
                if (s > best) best = s;
            }
            if (best == int.MinValue) return int.MinValue;

            // hierarchy influence: a real bone sits under its humanoid parent. This is also what
            // disambiguates side-less finger names like "IndexFinger1" duplicated per hand.
            Transform pt = NearestMappedParent(bone, m);
            if (pt != null)
            {
                if (IsAncestorOf(pt, t)) best += 300;
                else best -= 400;
            }
            if (boneSide != SIDE_NONE && nameSide == SIDE_NONE) best -= 250;
            if (isHelper) best -= HELPER_PENALTY; // jiggle/twist/IK bones are never the deform chain
            return best;
        }

        private static Transform NearestMappedParent(HumanBodyBones bone, MapResult m)
        {
            HumanBodyBones[] ps;
            if (!_parents.TryGetValue(bone, out ps)) return null;
            for (int i = 0; i < ps.Length; i++)
            {
                Transform pt;
                if (m.map.TryGetValue(ps[i], out pt) && pt != null) return pt;
            }
            return null;
        }

        private static bool IsAncestorOf(Transform ancestor, Transform t)
        {
            if (ancestor == null || t == null) return false;
            for (Transform p = t.parent; p != null; p = p.parent) if (p == ancestor) return true;
            return false;
        }

        // ------------------------------------------------------------------ limb chain solve

        private class ChainCand
        {
            public Transform t;
            public int score;
            public ChainCand(Transform tr, int s) { t = tr; score = s; }
        }

        /// <summary>
        /// Choose (upper, lower, distal) for one limb as a unit rather than one bone at a time.
        /// Independent per-bone matching loses on rigs where an auxiliary bone outscores the real
        /// one - VRChat's "Thigh_L" jiggle root beats "Left Leg" on the name "thigh" alone, and the
        /// rest of the chain is then unreachable from it. Here every combination of the top name
        /// matches is tested against the structural invariant (lower strictly under upper, distal
        /// strictly under lower) and the most complete valid chain wins, ties broken by name score.
        /// </summary>
        private static void SolveChain(Transform[] all, string[] norms, int[] sides, bool[] helpers, Transform root,
            MapResult m, HashSet<Transform> used, HB upper, HB lower, HB distal, string label)
        {
            List<ChainCand> ca = TopCandidates(all, norms, sides, helpers, root, m, used, upper);
            List<ChainCand> cb = TopCandidates(all, norms, sides, helpers, root, m, used, lower);
            List<ChainCand> cc = TopCandidates(all, norms, sides, helpers, root, m, used, distal);
            if (ca.Count == 0 && cb.Count == 0 && cc.Count == 0) return;

            long bestKey = long.MinValue;
            int bestCount = 0;
            Transform ba = null, bb = null, bc = null;

            // index -1 means "leave this slot to the per-bone fallback"
            for (int i = -1; i < ca.Count; i++)
            {
                Transform ta = i < 0 ? null : ca[i].t;
                for (int j = -1; j < cb.Count; j++)
                {
                    Transform tb = j < 0 ? null : cb[j].t;
                    if (ta != null && tb != null && !IsAncestorOf(ta, tb)) continue;
                    for (int k = -1; k < cc.Count; k++)
                    {
                        Transform tc = k < 0 ? null : cc[k].t;
                        if (tb != null && tc != null && !IsAncestorOf(tb, tc)) continue;
                        if (ta != null && tc != null && !IsAncestorOf(ta, tc)) continue;

                        int count = 0, sum = 0;
                        if (ta != null) { count++; sum += ca[i].score; }
                        if (tb != null) { count++; sum += cb[j].score; }
                        if (tc != null) { count++; sum += cc[k].score; }
                        if (count == 0) continue;

                        long key = (long)count * CHAIN_COMPLETENESS + sum;
                        if (key > bestKey) { bestKey = key; bestCount = count; ba = ta; bb = tb; bc = tc; }
                    }
                }
            }
            if (bestCount == 0) return;

            SeatChain(m, used, upper, ba);
            SeatChain(m, used, lower, bb);
            SeatChain(m, used, distal, bc);

            if (bestCount == 3)
                m.notes.Add("Chain solve: " + label + " = '" + ba.name + "' > '" + bb.name + "' > '" + bc.name + "'.");
            else
                m.notes.Add("Chain solve: " + label + " resolved only " + bestCount + "/3 slots (" +
                    NameOr(ba) + " > " + NameOr(bb) + " > " + NameOr(bc) +
                    "); the rest falls back to per-bone matching.");
        }

        private static string NameOr(Transform t)
        {
            return t == null ? "?" : "'" + t.name + "'";
        }

        private static void SeatChain(MapResult m, HashSet<Transform> used, HB bone, Transform t)
        {
            if (t == null) return;
            m.map[bone] = t;
            used.Add(t);
        }

        /// <summary>
        /// Best <see cref="CHAIN_CANDIDATES"/> name matches for one slot, highest score first. An
        /// already-mapped slot (user override) collapses to exactly that transform so the chain is
        /// solved around it instead of over it.
        /// </summary>
        private static List<ChainCand> TopCandidates(Transform[] all, string[] norms, int[] sides, bool[] helpers,
            Transform root, MapResult m, HashSet<Transform> used, HB bone)
        {
            List<ChainCand> outp = new List<ChainCand>();
            Transform fixedT;
            if (m.map.TryGetValue(bone, out fixedT) && fixedT != null)
            {
                outp.Add(new ChainCand(fixedT, 1000));
                return outp;
            }
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == root || used.Contains(t)) continue;
                int s = ScoreName(bone, norms[i], sides[i], t, m, helpers[i]);
                if (s == int.MinValue || s < 50) continue;
                int at = outp.Count;
                while (at > 0 && outp[at - 1].score < s) at--;
                if (at >= CHAIN_CANDIDATES) continue;
                outp.Insert(at, new ChainCand(t, s));
                if (outp.Count > CHAIN_CANDIDATES) outp.RemoveAt(outp.Count - 1);
            }
            return outp;
        }

        /// <summary>Once the foot is known, a toe bone that actually hangs off it beats one that does not.</summary>
        private static void PreferToesUnderFoot(Transform[] all, string[] norms, int[] sides, bool[] helpers,
            Transform root, MapResult m, HashSet<Transform> used, HB toes, HB foot)
        {
            Transform ft = m.Get(foot);
            if (ft == null) return;
            Transform cur = m.Get(toes);
            if (cur != null && IsAncestorOf(ft, cur)) return;

            int bestScore = int.MinValue;
            Transform best = null;
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == root || !IsAncestorOf(ft, t)) continue;
                if (used.Contains(t) && t != cur) continue;
                int s = ScoreName(toes, norms[i], sides[i], t, m, helpers[i]);
                if (s == int.MinValue || s < 50) continue;
                if (s > bestScore) { bestScore = s; best = t; }
            }
            if (best == null || best == cur) return;

            if (cur != null) used.Remove(cur);
            m.map[toes] = best;
            used.Add(best);
            m.notes.Add("Toe preference: " + toes + " = '" + best.name + "' (a descendant of '" + ft.name + "')" +
                (cur != null ? " instead of '" + cur.name + "'." : "."));
        }

        /// <summary>
        /// VRChat/Blender legacy rigs number the thumb 0/1/2 while VRM and Mixamo use 1/2/3, so a
        /// plain "Thumb1" is genuinely ambiguous. If a *Thumb0 exists on a side, re-seat that
        /// side's whole thumb chain onto 0/1/2.
        /// </summary>
        private static void FixThumbNumbering(Transform[] all, string[] norms, int[] sides, MapResult m, HashSet<Transform> used)
        {
            for (int si = 0; si < 2; si++)
            {
                int side = si == 0 ? SIDE_LEFT : SIDE_RIGHT;
                Transform t0 = FindThumbNumbered(all, sides, side, 0, m);
                if (t0 == null) continue;
                Transform t1 = FindThumbNumbered(all, sides, side, 1, m);
                Transform t2 = FindThumbNumbered(all, sides, side, 2, m);
                if (t1 == null || t2 == null) continue;
                if (t0 == t1 || t1 == t2 || t0 == t2) continue;

                // release whatever this side's thumb chain used to hold before re-seating
                for (int k = 0; k < 3; k++)
                {
                    Transform prev;
                    if (!m.map.TryGetValue(FingerBone(side, 0, k), out prev) || prev == null) continue;
                    if (prev != t0 && prev != t1 && prev != t2) used.Remove(prev);
                }
                m.map[FingerBone(side, 0, 0)] = t0;
                m.map[FingerBone(side, 0, 1)] = t1;
                m.map[FingerBone(side, 0, 2)] = t2;
                used.Add(t0); used.Add(t1); used.Add(t2);
                m.notes.Add("Thumb chain on the " + (side == SIDE_LEFT ? "left" : "right") +
                    " is 0-based (" + t0.name + "/" + t1.name + "/" + t2.name + "); re-seated proximal onto Thumb0.");
            }
        }

        private static Transform FindThumbNumbered(Transform[] all, int[] sides, int side, int number, MapResult m)
        {
            Transform hand = null;
            m.map.TryGetValue(side == SIDE_LEFT ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, out hand);
            Transform fallback = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (!HasThumbNumber(all[i].name, number)) continue;
                if (sides[i] != SIDE_NONE && sides[i] != side) continue;
                if (hand != null && IsAncestorOf(hand, all[i])) return all[i];
                if (sides[i] == side && fallback == null) fallback = all[i];
            }
            return fallback;
        }

        /// <summary>
        /// True when the raw name carries a "thumb" word immediately followed by the given index,
        /// wherever it sits in the name: "Thumb0_L", "L_Thumb0", "LeftHandThumb1", "thumb0l".
        /// A single digit only, so Blender's ".001" duplicate suffix ("Thumb.001" -> thumb, 001)
        /// can never masquerade as "Thumb1".
        /// </summary>
        private static bool HasThumbNumber(string raw, int number)
        {
            List<string> toks = SplitTokens(raw);
            for (int i = 0; i + 1 < toks.Count; i++)
            {
                if (toks[i] != "thumb") continue;
                string d = toks[i + 1];
                if (d.Length != 1 || d[0] < '0' || d[0] > '9') continue;
                if (d[0] - '0' == number) return true;
            }
            return false;
        }

        /// <summary>
        /// Reject any mapping that is not a descendant of its humanoid parent (name matching alone
        /// happily grabs a twist/IK/decorative bone from the wrong chain), then try to recover the
        /// bone structurally by walking down from the parent.
        /// </summary>
        private static void HierarchySanityPass(MapResult m, HashSet<Transform> used)
        {
            for (int oi = 0; oi < _order.Length; oi++)
            {
                HumanBodyBones bone = _order[oi];
                Transform t;
                if (!m.map.TryGetValue(bone, out t) || t == null) continue;
                Transform pt = NearestMappedParent(bone, m);
                if (pt == null || IsAncestorOf(pt, t)) continue;

                m.notes.Add("Hierarchy check: '" + t.name + "' is not under '" + pt.name + "' - rejected as " + bone + ".");
                m.map.Remove(bone);
                used.Remove(t);
                Transform guess = LongestChild(pt, used);
                if (guess != null)
                {
                    m.map[bone] = guess;
                    used.Add(guess);
                    m.notes.Add("Structural fallback: " + bone + " = '" + guess.name + "' (deepest child of '" + pt.name + "').");
                }
            }
        }

        /// <summary>
        /// Child of <paramref name="parent"/> whose subtree carries the most bones. Subtrees rooted
        /// on an auxiliary bone are skipped outright: walking into a jiggle or twist branch is how
        /// the fallback used to invent limbs out of decorative bones.
        /// </summary>
        private static Transform LongestChild(Transform parent, HashSet<Transform> used)
        {
            return LongestChild(parent, used, true);
        }

        private static Transform LongestChild(Transform parent, HashSet<Transform> used, bool avoidHelpers)
        {
            Transform best = null;
            int bestCount = -1;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (used != null && used.Contains(c)) continue;
                if (avoidHelpers && HasHelperToken(c.name)) continue;
                int count = SubtreeCount(c);
                if (count > bestCount) { bestCount = count; best = c; }
            }
            return best;
        }

        private static int SubtreeCount(Transform t)
        {
            int c = 1;
            for (int i = 0; i < t.childCount; i++) c += SubtreeCount(t.GetChild(i));
            return c;
        }

        private static float SubtreeMinY(Transform t)
        {
            float y = t.position.y;
            for (int i = 0; i < t.childCount; i++) y = Mathf.Min(y, SubtreeMinY(t.GetChild(i)));
            return y;
        }

        private static float SubtreeMaxAbsX(Transform t, Transform origin, out float signedX)
        {
            Vector3 lp = origin.InverseTransformPoint(t.position);
            float bestAbs = Mathf.Abs(lp.x);
            signedX = lp.x;
            for (int i = 0; i < t.childCount; i++)
            {
                float cs;
                float ca = SubtreeMaxAbsX(t.GetChild(i), origin, out cs);
                if (ca > bestAbs) { bestAbs = ca; signedX = cs; }
            }
            return bestAbs;
        }

        // ------------------------------------------------------------------ structural fallback

        // Best effort only: infer the spine/limb roots from topology when names told us nothing.
        // Anything still unresolved is reported through missingRequired rather than guessed harder.
        private static void StructuralFallback(Transform root, MapResult m, HashSet<Transform> used)
        {
            if (!m.map.ContainsKey(HB.Hips))
                SeatGuess(m, used, HB.Hips, GuessHips(root, m), "branch point of the largest bone tree");

            Transform hipsT = m.Get(HB.Hips);
            if (hipsT != null)
            {
                if (!m.map.ContainsKey(HB.LeftUpperLeg) || !m.map.ContainsKey(HB.RightUpperLeg)) GuessLegs(hipsT, m, used);
                if (!m.map.ContainsKey(HB.Spine))
                    SeatGuess(m, used, HB.Spine, HighestChild(hipsT, used), "highest remaining child of Hips");
            }

            Transform chest = m.Get(HB.UpperChest);
            if (chest == null) chest = m.Get(HB.Chest);
            if (chest == null) chest = m.Get(HB.Spine);
            if (chest != null && (!m.map.ContainsKey(HB.LeftUpperArm) || !m.map.ContainsKey(HB.RightUpperArm)))
                GuessArms(chest, m, used);

            // finish partially-resolved limb chains by walking down from whatever we do have
            FillChain(m, used, HB.LeftUpperLeg, HB.LeftLowerLeg, HB.LeftFoot);
            FillChain(m, used, HB.RightUpperLeg, HB.RightLowerLeg, HB.RightFoot);
            FillChain(m, used, HB.LeftUpperArm, HB.LeftLowerArm, HB.LeftHand);
            FillChain(m, used, HB.RightUpperArm, HB.RightLowerArm, HB.RightHand);

            if (!m.map.ContainsKey(HB.Head))
            {
                Transform up = m.Get(HB.Neck);
                if (up == null) up = chest;
                if (up != null) SeatGuess(m, used, HB.Head, HighestChild(up, used), "highest child of '" + up.name + "'");
            }
        }

        private static void SeatGuess(MapResult m, HashSet<Transform> used, HB bone, Transform t, string why)
        {
            if (t == null || m.map.ContainsKey(bone)) return;
            m.map[bone] = t;
            used.Add(t);
            m.notes.Add("Structural fallback: " + bone + " = '" + t.name + "' (" + why + ").");
        }

        private static Transform GuessHips(Transform root, MapResult m)
        {
            Transform l = m.Get(HB.LeftUpperLeg);
            Transform r = m.Get(HB.RightUpperLeg);
            if (l != null && r != null)
                for (Transform a = l.parent; a != null; a = a.parent)
                    if (IsAncestorOf(a, r)) return a;

            // otherwise descend through single/dominant children until the tree really forks
            Transform cur = root;
            for (int guard = 0; guard < 64; guard++)
            {
                if (cur.childCount == 0) return null;
                // deliberately not helper-filtered: plenty of rigs really do hang the skeleton off a
                // transform called "Root", and refusing to descend would strand the whole search.
                Transform big = LongestChild(cur, null, false);
                if (big == null) return null;
                int total = SubtreeCount(cur) - 1;
                if (cur.childCount == 1 || (total > 0 && SubtreeCount(big) >= total * 0.8f))
                {
                    if (SubtreeCount(big) < 4) return cur == root ? null : cur;
                    cur = big;
                    continue;
                }
                return cur == root ? null : cur;
            }
            return null;
        }

        private static void GuessLegs(Transform hips, MapResult m, HashSet<Transform> used)
        {
            Transform lowA = null, lowB = null;
            float ya = float.MaxValue, yb = float.MaxValue;
            for (int i = 0; i < hips.childCount; i++)
            {
                Transform c = hips.GetChild(i);
                if (used.Contains(c)) continue;
                float y = SubtreeMinY(c);
                if (y < ya) { yb = ya; lowB = lowA; ya = y; lowA = c; }
                else if (y < yb) { yb = y; lowB = c; }
            }
            if (lowA == null || lowB == null) return;

            // Unity character space: +X is the character's left.
            float xa = hips.InverseTransformPoint(lowA.position).x;
            Transform left = xa >= 0f ? lowA : lowB;
            Transform right = xa >= 0f ? lowB : lowA;

            SeatGuess(m, used, HB.LeftUpperLeg, left, "lowest subtree under Hips, local +X side");
            SeatGuess(m, used, HB.RightUpperLeg, right, "lowest subtree under Hips, local -X side");
        }

        private static void GuessArms(Transform chest, MapResult m, HashSet<Transform> used)
        {
            Transform wideA = null, wideB = null;
            float xa = 0f, xb = 0f, aa = -1f, ab = -1f;
            for (int i = 0; i < chest.childCount; i++)
            {
                Transform c = chest.GetChild(i);
                if (used.Contains(c)) continue;
                float sx;
                float ax = SubtreeMaxAbsX(c, chest, out sx);
                if (ax > aa) { ab = aa; wideB = wideA; xb = xa; aa = ax; wideA = c; xa = sx; }
                else if (ax > ab) { ab = ax; wideB = c; xb = sx; }
            }
            if (wideA == null || wideB == null) return;
            if (xa * xb >= 0f) return; // both subtrees reach the same side - not a left/right pair

            Transform left = xa > 0f ? wideA : wideB;
            Transform right = xa > 0f ? wideB : wideA;
            SeatArmChain(left, SIDE_LEFT, m, used);
            SeatArmChain(right, SIDE_RIGHT, m, used);
            m.notes.Add("Structural fallback: arms from '" + chest.name + "' by widest subtree - left '" + left.name + "', right '" + right.name + "'.");
        }

        private static void SeatArmChain(Transform start, int side, MapResult m, HashSet<Transform> used)
        {
            List<Transform> chain = new List<Transform>();
            for (Transform cur = start; cur != null && chain.Count < 5; cur = LongestChild(cur, null)) chain.Add(cur);

            int offset = chain.Count >= 4 ? 1 : 0; // 4+ links means the branch started at the shoulder
            bool isL = side == SIDE_LEFT;
            if (offset == 1) Seat(m, used, isL ? HB.LeftShoulder : HB.RightShoulder, chain, 0);
            Seat(m, used, isL ? HB.LeftUpperArm : HB.RightUpperArm, chain, offset);
            Seat(m, used, isL ? HB.LeftLowerArm : HB.RightLowerArm, chain, offset + 1);
            Seat(m, used, isL ? HB.LeftHand : HB.RightHand, chain, offset + 2);
        }

        private static void Seat(MapResult m, HashSet<Transform> used, HB bone, List<Transform> chain, int idx)
        {
            if (idx < 0 || idx >= chain.Count || m.map.ContainsKey(bone)) return;
            m.map[bone] = chain[idx];
            used.Add(chain[idx]);
        }

        private static void FillChain(MapResult m, HashSet<Transform> used, HB a, HB b, HB c)
        {
            Transform ta = m.Get(a);
            if (ta == null) return;
            if (m.Get(b) == null) SeatGuess(m, used, b, LongestChild(ta, used), "child of '" + ta.name + "'");
            Transform tb = m.Get(b);
            if (tb != null && m.Get(c) == null) SeatGuess(m, used, c, LongestChild(tb, used), "child of '" + tb.name + "'");
        }

        private static Transform HighestChild(Transform parent, HashSet<Transform> used)
        {
            Transform best = HighestChild(parent, used, true);
            if (best == null) best = HighestChild(parent, used, false); // helper-only branch: better than nothing
            return best;
        }

        private static Transform HighestChild(Transform parent, HashSet<Transform> used, bool avoidHelpers)
        {
            Transform best = null;
            float bestY = float.MinValue;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (used != null && used.Contains(c)) continue;
                if (avoidHelpers && HasHelperToken(c.name)) continue;
                float y = c.position.y;
                if (y > bestY) { bestY = y; best = c; }
            }
            return best;
        }

        // ------------------------------------------------------------------ avatar build

        public static Avatar Build(GameObject root, MapResult m, out string error)
        {
            error = null;
            if (root == null) { error = "Build failed: root GameObject is null."; return null; }
            if (m == null || m.map.Count == 0) { error = "Build failed: no bone mapping supplied."; return null; }
            if (!m.CanBuild)
            {
                error = "Build failed: required bones are unmapped - " + Join(m.missingRequired, m.missingRequired.Count);
                return null;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);

            // Unity resolves skeleton bones by NAME, so duplicates make BuildHumanAvatar fail with
            // an unhelpful message. Detect it up front and say exactly which names collided.
            Dictionary<string, int> counts = new Dictionary<string, int>();
            List<string> dupes = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                int c;
                counts.TryGetValue(all[i].name, out c);
                counts[all[i].name] = c + 1;
                if (c == 1) dupes.Add(all[i].name);
            }
            if (dupes.Count > 0)
            {
                error = "Build failed: duplicate transform names under '" + root.name +
                    "' - Unity requires unique skeleton bone names. Rename these first: " + Join(dupes, 12) +
                    (dupes.Count > 12 ? " (+" + (dupes.Count - 12) + " more)" : "");
                return null;
            }

            List<HumanBone> humanBones = new List<HumanBone>();
            foreach (KeyValuePair<HumanBodyBones, Transform> kv in m.map)
            {
                if (kv.Value == null || kv.Key == HumanBodyBones.LastBone) continue;
                HumanLimit limit = new HumanLimit();
                limit.useDefaultValues = true;
                HumanBone hb = new HumanBone();
                hb.boneName = kv.Value.name;
                hb.humanName = HumanTrait.BoneName[(int)kv.Key]; // Unity's human name string, e.g. "Left Upper Arm"
                hb.limit = limit;
                humanBones.Add(hb);
            }

            SkeletonBone[] skeleton = new SkeletonBone[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                SkeletonBone sb = new SkeletonBone();
                sb.name = all[i].name;
                sb.position = all[i].localPosition;
                sb.rotation = all[i].localRotation;
                sb.scale = all[i].localScale;
                skeleton[i] = sb;
            }

            HumanDescription desc = new HumanDescription();
            desc.human = humanBones.ToArray();
            desc.skeleton = skeleton;
            desc.upperArmTwist = 0.5f; desc.lowerArmTwist = 0.5f;
            desc.upperLegTwist = 0.5f; desc.lowerLegTwist = 0.5f;
            desc.armStretch = 0.05f; desc.legStretch = 0.05f;
            desc.feetSpacing = 0f;
            desc.hasTranslationDoF = false;

            Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
            if (avatar == null || !avatar.isValid)
            {
                error = "Build failed: AvatarBuilder rejected the description (" + humanBones.Count +
                    " human bones, " + skeleton.Length + " skeleton bones). Check that the mapped bones form a " +
                    "connected hierarchy under '" + root.name + "' and that the T-pose is roughly correct.";
                return null;
            }

            avatar.name = root.name + "_HumanAvatar";
            error = null;
            return avatar;
        }

        private static string Join<T>(IList<T> items, int max)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < items.Count && i < max; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(items[i].ToString());
            }
            return sb.ToString();
        }
    }
}
