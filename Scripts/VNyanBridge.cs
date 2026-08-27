using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// Hands a prepared avatar to VNyan's own loader.
    ///
    /// VNyanInterface has no "load this avatar" API - the only entry point is VNyan's internal
    /// UiManager. Its avatar coroutine (verified by reading the IL of UiManager.LoadVSFAvatar)
    /// runs in this order:
    ///
    ///     CurrentAvatarFilePath = Path.GetFileName(path)
    ///     if (AvatarCache.getInstance().isInUse)                // <- THE GATE
    ///         cached = AvatarCache.getInstance().GetAvatarCache(path)
    ///     if (cached == null) {
    ///         bundle = AssetBundle.LoadFromFileAsync(path)      // <- only reached on a cache miss
    ///         go     = bundle.LoadAssetAsync("VSFAvatar")
    ///         AvatarCache.getInstance().AddAvatarToCache(path, go)
    ///     }
    ///     instance = Object.Instantiate(cached-or-loaded)
    ///     ... destroy previous avatar, set currentAvatar ...
    ///
    /// That `isInUse` gate is VNyan's "AvatarCacheInUse" app setting and ships OFF, which makes
    /// the cache invisible and sends the loader straight to disk. We turn it on and keep it on:
    /// our key is not a real file, so every future reload of this avatar has to hit the cache too.
    /// Keys are lower-cased inside AvatarCache, so case never matters.
    ///
    /// So seeding the cache under a key and then asking VNyan to load that key makes it run its
    /// entire normal .vsfavatar path against our object: tracking, expressions, chains, colliders,
    /// hand gestures, the lot. Nothing is patched and no file is read.
    ///
    /// UiManager, LoadAvatarDelayed, LoadVSFAvatar, AvatarCache, GetAvatarCache, AddAvatarToCache
    /// and getInstance are NOT name-mangled in VNyan's obfuscated assembly, which is why this is
    /// stable enough to rely on. Every lookup is still null-checked, and failure produces an
    /// actionable message rather than an exception.
    /// </summary>
    public static class VNyanBridge
    {
        static Type tUiManager, tAvatarCache;
        static MethodInfo mGetInstance, mAddToCache, mGetCache, mLoadDelayed, mLoadVsf;
        static FieldInfo fInUse;
        static bool s_scanned;

        public static string LastError { get; private set; }

        /// <summary>True once we have handed an avatar over and the cache must stay enabled.</summary>
        public static bool NeedsCacheHeldOpen { get; private set; }

        // ------------------------------------------------------------------ discovery

        static void Scan()
        {
            if (s_scanned) return;
            s_scanned = true;

            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                string an = asms[i].GetName().Name;
                if (an != "Assembly-CSharp") continue;
                tUiManager = asms[i].GetType("UiManager", false);
                tAvatarCache = asms[i].GetType("AvatarCache", false);
                break;
            }
            // Fallback: some hosts rename the game assembly.
            if (tUiManager == null || tAvatarCache == null)
            {
                for (int i = 0; i < asms.Length && (tUiManager == null || tAvatarCache == null); i++)
                {
                    if (tUiManager == null) tUiManager = asms[i].GetType("UiManager", false);
                    if (tAvatarCache == null) tAvatarCache = asms[i].GetType("AvatarCache", false);
                }
            }

            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

            if (tAvatarCache != null)
            {
                mGetInstance = tAvatarCache.GetMethod("getInstance", BindingFlags.Public | BindingFlags.Static);
                mAddToCache = tAvatarCache.GetMethod("AddAvatarToCache", Any, null,
                                new Type[] { typeof(string), typeof(GameObject) }, null);
                mGetCache = tAvatarCache.GetMethod("GetAvatarCache", Any, null,
                                new Type[] { typeof(string) }, null);
                fInUse = tAvatarCache.GetField("isInUse", Any);
            }
            if (tUiManager != null)
            {
                mLoadDelayed = tUiManager.GetMethod("LoadAvatarDelayed", Any, null,
                                new Type[] { typeof(string) }, null);
                mLoadVsf = tUiManager.GetMethod("LoadVSFAvatar", Any, null,
                                new Type[] { typeof(string) }, null);
            }
        }

        public static bool Available
        {
            get
            {
                Scan();
                return mGetInstance != null && mAddToCache != null && (mLoadDelayed != null || mLoadVsf != null);
            }
        }

        public static string Diagnose()
        {
            Scan();
            return "UiManager=" + (tUiManager != null) +
                   " AvatarCache=" + (tAvatarCache != null) +
                   " getInstance=" + (mGetInstance != null) +
                   " AddAvatarToCache=" + (mAddToCache != null) +
                   " GetAvatarCache=" + (mGetCache != null) +
                   " isInUse=" + (fInUse != null) +
                   " LoadAvatarDelayed=" + (mLoadDelayed != null) +
                   " LoadVSFAvatar=" + (mLoadVsf != null);
        }

        // ------------------------------------------------------------------ the hook

        /// <summary>
        /// The cache key doubles as the path VNyan displays and as its identity for the
        /// already-loaded check, so it must look like a real avatar file and stay stable per mod.
        /// </summary>
        public static string CacheKeyFor(string warudoPath)
        {
            string dir = Path.GetDirectoryName(warudoPath);
            string leaf = Path.GetFileNameWithoutExtension(warudoPath) + ".warudo.vsfavatar";
            return string.IsNullOrEmpty(dir) ? leaf : Path.Combine(dir, leaf);
        }

        /// <summary>
        /// Seeds the cache and asks VNyan to load it. <paramref name="template"/> must be a
        /// GameObject that stays alive and inactive - VNyan instantiates from it every time the
        /// avatar is (re)loaded, so it is a prefab stand-in, not the live avatar.
        /// </summary>
        public static bool Load(string cacheKey, GameObject template)
        {
            LastError = null;
            Scan();

            if (template == null) { LastError = "Nothing to load (prepared avatar was null)."; return false; }
            if (!Available)
            {
                LastError = "This VNyan build does not expose the avatar loader this plugin hooks (" +
                            Diagnose() + "). Use the offline .warudo -> .vsfavatar converter instead.";
                return false;
            }

            object cache;
            try { cache = mGetInstance.Invoke(null, null); }
            catch (Exception e) { LastError = "AvatarCache.getInstance() failed: " + Unwrap(e); return false; }
            if (cache == null) { LastError = "VNyan's AvatarCache is not alive yet. Load any avatar once, then retry."; return false; }

            // Object.Instantiate() copies the SOURCE's own activeSelf flag onto the clone,
            // regardless of whether the source is currently invisible because a PARENT is
            // inactive. A genuine .vsfavatar's cached object is a prefab ASSET (activeSelf true
            // by convention, not a live scene object at all), so VNyan never has to think about
            // this. Ours is a real scene GameObject that the caller hides by parking it under an
            // inactive holder - so template.activeSelf must stay true, or every clone VNyan
            // instantiates from this cache entry (now and on every future reload) is born
            // inactive and never renders, even though loading/binding otherwise succeeds.
            if (!template.activeSelf) template.SetActive(true);

            // DontDestroyOnLoad throws if the target has a parent, so only apply it to a template
            // that is a genuine scene root; a parented template is expected to already be kept
            // alive by whatever (inactive, persistent) holder it sits under.
            if (template.transform.parent == null) UnityEngine.Object.DontDestroyOnLoad(template);

            // Without this the cache is skipped entirely and VNyan tries to read our key as a
            // file. Left on for the rest of the session - see the class comment.
            if (!EnableCache(cache))
            {
                LastError = "VNyan's avatar cache could not be enabled (the 'isInUse' flag was not " +
                            "found). Turn on Avatar Cache in VNyan's settings, or use the offline converter.";
                return false;
            }

            try { mAddToCache.Invoke(cache, new object[] { cacheKey, template }); }
            catch (Exception e) { LastError = "AddAvatarToCache failed: " + Unwrap(e); return false; }

            // Confirm the seed took before triggering the load; otherwise VNyan falls through to
            // LoadFromFileAsync on a path that holds no VSFAvatar asset and logs "Failed loading".
            if (mGetCache != null)
            {
                object back = null;
                try { back = mGetCache.Invoke(cache, new object[] { cacheKey }); }
                catch { }
                if (back as GameObject != template)
                {
                    LastError = "VNyan did not accept the cache entry, so its loader would try to read " +
                                "the key as a file. Use the offline converter instead.";
                    return false;
                }
            }

            object ui = FindUiManager();
            if (ui == null) { LastError = "Could not find VNyan's UiManager instance in the scene."; return false; }

            try
            {
                if (mLoadDelayed != null)
                {
                    mLoadDelayed.Invoke(ui, new object[] { cacheKey });
                    return true;
                }
                MonoBehaviour mb = ui as MonoBehaviour;
                IEnumerator co = mLoadVsf.Invoke(ui, new object[] { cacheKey }) as IEnumerator;
                if (mb != null && co != null) { mb.StartCoroutine(co); return true; }
                LastError = "LoadVSFAvatar returned nothing runnable.";
                return false;
            }
            catch (Exception e)
            {
                LastError = "VNyan's avatar loader threw: " + Unwrap(e);
                return false;
            }
        }

        static bool EnableCache(object cache)
        {
            if (fInUse == null) return false;
            try
            {
                fInUse.SetValue(cache, true);
                NeedsCacheHeldOpen = true;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// VNyan re-reads its app settings on save, which resets the flag to the stored value.
        /// Call this every frame once an avatar has been handed over, otherwise the next avatar
        /// reload looks for a file that does not exist. Cheap: one field read.
        /// </summary>
        public static void HoldCacheOpen()
        {
            if (!NeedsCacheHeldOpen || fInUse == null || mGetInstance == null) return;
            try
            {
                object cache = mGetInstance.Invoke(null, null);
                if (cache == null) return;
                object v = fInUse.GetValue(cache);
                if (v is bool && !((bool)v)) fInUse.SetValue(cache, true);
            }
            catch { }
        }

        /// <summary>FindObjectOfType rather than the mangled singleton field, which does get renamed.</summary>
        static object FindUiManager()
        {
            if (tUiManager == null) return null;
            UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(tUiManager);
            if (found != null) return found;

            FieldInfo[] fields = tUiManager.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
                if (fields[i].FieldType == tUiManager)
                {
                    object v = fields[i].GetValue(null);
                    if (v as UnityEngine.Object != null) return v;
                }
            return null;
        }

        static string Unwrap(Exception e)
        {
            TargetInvocationException tie = e as TargetInvocationException;
            if (tie != null && tie.InnerException != null) return tie.InnerException.Message;
            return e.Message;
        }
    }
}
