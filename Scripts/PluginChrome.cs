using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace WarudoImporter
{
    /// <summary>
    /// Window chrome shared by all of our VNyan plugins (the same file in each plugin, in that
    /// plugin's namespace; keep the copies identical apart from the namespace line):
    ///  - one identical "X" close button at the window's top-right corner,
    ///  - hiding any other Close button and letting the buttons on its row take its space,
    ///  - a small GitHub update check that shows an "Update vX" button opening the release page.
    /// C# 5 only (some plugins compile with -langversion:5). Needs UnityEngine.UnityWebRequestModule.
    /// </summary>
    internal static class PluginChrome
    {
        public const string CloseXName = "Button_CloseX";
        public const string UpdateName = "Button_UpdateAvailable";

        // the one X look, used by every plugin: the light default-skin "X" the Warudo and VRChat
        // importers already had (26x24, 12 px from the right edge, 8 px from the top)
        public const float XSize = 26f, XHeight = 24f, XInset = 12f, XTop = 8f;
        static readonly Color XInk = new Color(0.05f, 0.05f, 0.07f, 1f);
        static readonly Color UpdBg = new Color(0.16f, 0.52f, 0.30f, 1f);

        // ------------------------------------------------------------------ X close button

        /// <summary>
        /// Creates (or re-wires) the X at the top-right of <paramref name="window"/>. Safe to
        /// call more than once. <paramref name="font"/> may be null (builtin LegacyRuntime is used).
        /// </summary>
        public static Button EnsureCloseX(RectTransform window, UnityAction onClose, Font font)
        {
            if (window == null) return null;
            Transform found = window.Find(CloseXName);
            Button b = found != null ? found.GetComponent<Button>() : null;
            if (b == null)
            {
                GameObject go = new GameObject(CloseXName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                RectTransform rt = go.GetComponent<RectTransform>();
                rt.SetParent(window, false);
                Image img = go.GetComponent<Image>();
                img.color = Color.white;
                Sprite skin = UiSprite();
                if (skin != null) { img.sprite = skin; img.type = Image.Type.Sliced; }
                b = go.AddComponent<Button>();
                b.targetGraphic = img;
                ColorBlock cb = ColorBlock.defaultColorBlock;   // DefaultControls tint: 1 / 0.96 / 0.78
                b.colors = cb;
                Text t = MakeText(go.transform, "X", font, 13, FontStyle.Normal);
                t.color = XInk;
            }
            RectTransform r = (RectTransform)b.transform;
            r.anchorMin = new Vector2(1f, 1f);
            r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(1f, 1f);
            r.sizeDelta = new Vector2(XSize, XHeight);
            r.anchoredPosition = new Vector2(-XInset, -XTop);
            b.onClick.RemoveAllListeners();
            if (onClose != null) b.onClick.AddListener(onClose);
            r.SetAsLastSibling();
            return b;
        }

        /// <summary>
        /// Hides the named Close buttons (anywhere under the window) and lets the other buttons
        /// on the same row spread over the freed space. Hidden, not destroyed, so code that
        /// still holds a reference keeps working. Returns how many were hidden.
        /// </summary>
        public static int HideOtherClose(RectTransform window, params string[] names)
        {
            if (window == null || names == null) return 0;
            int n = 0;
            foreach (string name in names)
            {
                foreach (RectTransform rt in window.GetComponentsInChildren<RectTransform>(true))
                {
                    if (rt == null || rt.name != name || rt.name == CloseXName || !rt.gameObject.activeSelf) continue;
                    RespaceRowWithout(rt);
                    rt.gameObject.SetActive(false);
                    n++;
                }
            }
            return n;
        }

        // Buttons sharing the row (same parent, anchors, pivot, y and height) are spread evenly
        // over the row's old span, keeping the old gap between them.
        static void RespaceRowWithout(RectTransform gone)
        {
            RectTransform parent = gone.parent as RectTransform;
            if (parent == null) return;
            var row = new List<RectTransform>();
            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform c = parent.GetChild(i) as RectTransform;
                if (c == null || !c.gameObject.activeSelf || c.GetComponent<Button>() == null) continue;
                if (c.anchorMin != gone.anchorMin || c.anchorMax != gone.anchorMax || c.pivot != gone.pivot) continue;
                if (Mathf.Abs(c.anchoredPosition.y - gone.anchoredPosition.y) > 1f || Mathf.Abs(c.sizeDelta.y - gone.sizeDelta.y) > 1f) continue;
                if (Mathf.Abs(c.anchorMin.x - c.anchorMax.x) > 0.001f) return;   // stretched rows: leave alone
                row.Add(c);
            }
            if (row.Count < 2) return;
            row.Sort(delegate (RectTransform a, RectTransform b) { return a.anchoredPosition.x.CompareTo(b.anchoredPosition.x); });
            float left = float.MaxValue, right = float.MinValue, widthSum = 0f;
            foreach (RectTransform c in row)
            {
                float l = c.anchoredPosition.x - c.pivot.x * c.sizeDelta.x;
                left = Mathf.Min(left, l);
                right = Mathf.Max(right, l + c.sizeDelta.x);
                widthSum += c.sizeDelta.x;
            }
            float gap = Mathf.Max(0f, (right - left - widthSum) / (row.Count - 1));
            row.Remove(gone);
            int k = row.Count;
            float w = (right - left - gap * (k - 1)) / k;
            for (int i = 0; i < k; i++)
            {
                RectTransform c = row[i];
                c.sizeDelta = new Vector2(w, c.sizeDelta.y);
                float l = left + i * (w + gap);
                c.anchoredPosition = new Vector2(l + c.pivot.x * w, c.anchoredPosition.y);
            }
        }

        static Sprite uiSprite;
        static bool uiSpriteTried;

        /// <summary>Unity's built-in button skin (the one DefaultControls uses), or null.</summary>
        static Sprite UiSprite()
        {
            if (uiSpriteTried) return uiSprite;
            uiSpriteTried = true;
            try { uiSprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd"); } catch { uiSprite = null; }
            if (uiSprite == null)
                foreach (Sprite s in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (s != null && s.name == "UISprite") { uiSprite = s; break; }
            return uiSprite;
        }

        // ------------------------------------------------------------------ update check

        /// <summary>
        /// Checks GitHub (at most every 6 hours, cached between launches) for a release newer
        /// than <paramref name="current"/> and, when there is one, shows an "Update vX" button
        /// left of the X (<paramref name="extraRightInset"/> = room taken by other header controls
        /// between the X and the badge). Clicking it opens the release page to download.
        /// Quiet on any network problem; never blocks the plugin.
        /// </summary>
        public static void CheckForUpdate(MonoBehaviour host, RectTransform window, string repo, string current,
                                          string logTag, Font font, float extraRightInset)
        {
            CheckForUpdate(host, window, repo, current, logTag, font, extraRightInset, null);
        }

        /// <param name="onBadge">Called with the badge once it is shown (to make room in a crowded header). May be null.</param>
        public static void CheckForUpdate(MonoBehaviour host, RectTransform window, string repo, string current,
                                          string logTag, Font font, float extraRightInset, Action<RectTransform> onBadge)
        {
            if (host == null || string.IsNullOrEmpty(repo)) return;
            try { host.StartCoroutine(CheckRoutine(window, repo, current, logTag, font, extraRightInset, onBadge)); }
            catch (Exception e) { Debug.Log(logTag + "update check not started: " + e.Message); }
        }

        static IEnumerator CheckRoutine(RectTransform window, string repo, string current, string logTag, Font font, float extraRightInset,
                                        Action<RectTransform> onBadge)
        {
            string tag = null;
            string cache = null;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "PluginUpdates");
                Directory.CreateDirectory(dir);
                cache = Path.Combine(dir, repo.Replace('/', '_') + ".txt");
                if (File.Exists(cache))
                {
                    string[] lines = File.ReadAllLines(cache);
                    long ticks;
                    if (lines.Length >= 2 && long.TryParse(lines[0], out ticks) &&
                        (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalHours < 6.0)
                        tag = lines[1];
                }
            }
            catch { cache = null; }

            if (tag == null)
            {
                UnityWebRequest req = UnityWebRequest.Get("https://api.github.com/repos/" + repo + "/releases?per_page=20");
                req.SetRequestHeader("User-Agent", "VNyan-plugin-update-check");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 15;
                yield return req.SendWebRequest();
                bool ok = req.result == UnityWebRequest.Result.Success;
                string body = ok ? req.downloadHandler.text : "";
                long code = req.responseCode;
                req.Dispose();
                if (!ok && code != 404)
                {
                    Debug.Log(logTag + "update check skipped (" + (code > 0 ? "HTTP " + code : "offline") + ").");
                    yield break;
                }
                tag = ok ? NewestTag(body) : "";
                try { if (cache != null) File.WriteAllLines(cache, new[] { DateTime.UtcNow.Ticks.ToString(), tag ?? "" }); } catch { }
            }

            if (string.IsNullOrEmpty(tag)) yield break;   // no releases published
            if (!IsNewer(tag, current))
            {
                Debug.Log(logTag + "up to date (" + current + ", newest release " + tag + ").");
                yield break;
            }
            string url = "https://github.com/" + repo + "/releases/tag/" + Uri.EscapeDataString(tag);
            Debug.Log(logTag + "update available: " + tag + " (installed " + current + ") " + url);
            RectTransform badge = ShowBadge(window, tag, url, font, extraRightInset);
            if (badge != null && onBadge != null)
            {
                try { onBadge(badge); } catch (Exception e) { Debug.Log(logTag + "update badge layout: " + e.Message); }
            }
        }

        /// <summary>Highest version among the published (non-prerelease) releases in a /releases JSON list.</summary>
        static string NewestTag(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            MatchCollection tags = Regex.Matches(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            string best = "";
            for (int i = 0; i < tags.Count; i++)
            {
                int start = tags[i].Index;
                int end = i + 1 < tags.Count ? tags[i + 1].Index : json.Length;
                string seg = json.Substring(start, end - start);
                if (Regex.IsMatch(seg, "\"prerelease\"\\s*:\\s*true") || Regex.IsMatch(seg, "\"draft\"\\s*:\\s*true")) continue;
                string t = tags[i].Groups[1].Value;
                if (best.Length == 0 || IsNewer(t, best)) best = t;
            }
            return best;
        }

        /// <summary>True when version <paramref name="a"/> is higher than <paramref name="b"/> ("v2.10.0" &gt; "v2.9.3").</summary>
        public static bool IsNewer(string a, string b)
        {
            int[] x = Parse(a), y = Parse(b);
            for (int i = 0; i < 4; i++)
            {
                if (x[i] != y[i]) return x[i] > y[i];
            }
            return false;
        }

        static int[] Parse(string v)
        {
            var r = new int[4];
            if (string.IsNullOrEmpty(v)) return r;
            Match m = Regex.Match(v, "(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?(?:\\.(\\d+))?");
            for (int i = 0; i < 4; i++)
            {
                int n;
                if (m.Success && m.Groups[i + 1].Success && int.TryParse(m.Groups[i + 1].Value, out n)) r[i] = n;
            }
            return r;
        }

        static RectTransform ShowBadge(RectTransform window, string tag, string url, Font font, float extraRightInset)
        {
            if (window == null) return null;
            Transform old = window.Find(UpdateName);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
            GameObject go = new GameObject(UpdateName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(window, false);
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(112f, XHeight);
            rt.anchoredPosition = new Vector2(-(XInset + XSize + 6f + extraRightInset), -XTop);
            Image img = go.GetComponent<Image>();
            img.color = UpdBg;
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            ColorBlock cb = b.colors;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.colorMultiplier = 1.3f;
            b.colors = cb;
            Text t = MakeText(go.transform, "Update " + tag, font, 11, FontStyle.Bold);
            t.color = Color.white;
            string link = url;
            b.onClick.AddListener(delegate { Application.OpenURL(link); });
            rt.SetAsLastSibling();
            return rt;
        }

        static Text MakeText(Transform parent, string s, Font font, int size, FontStyle style)
        {
            GameObject go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            Text t = go.GetComponent<Text>();
            t.text = s;
            t.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }
    }
}
