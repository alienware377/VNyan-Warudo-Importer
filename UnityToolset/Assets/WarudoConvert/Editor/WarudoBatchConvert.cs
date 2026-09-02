using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WarudoImporter.EditorTools
{
    /// <summary>
    /// Headless entry point for the .warudo -> .vsfavatar converter, so the VNyan plugin can
    /// drive a real AssetBundle build without anyone opening Unity by hand.
    ///
    /// AssetBundles can only be built by the editor - there is no runtime equivalent - so the
    /// plugin shells out to:
    ///
    ///   Unity.exe -batchmode -quit -projectPath &lt;project&gt;
    ///             -executeMethod WarudoImporter.EditorTools.WarudoBatchConvert.Run
    ///             -warudo "&lt;file.warudo&gt;" -out "&lt;folder&gt;" -logFile "&lt;log&gt;"
    ///
    /// Note there is deliberately no -nographics: re-encoding the mod's textures reads them
    /// back off the GPU, which needs a graphics device.
    /// </summary>
    public static class WarudoBatchConvert
    {
        public static void Run()
        {
            int exit = 1;
            try
            {
                string warudo = Arg("-warudo");
                string outDir = Arg("-out");

                if (string.IsNullOrEmpty(warudo) || !File.Exists(warudo))
                {
                    Debug.LogError("[WarudoConvert] -warudo is missing or does not point at a file.");
                    Finish(2);
                    return;
                }
                if (string.IsNullOrEmpty(outDir))
                    outDir = Path.GetDirectoryName(warudo);

                var w = ScriptableObject.CreateInstance<WarudoConvertWindow>();
                w.warudoPath = warudo;
                w.outputDir = outDir;
                w.writePhysBonesJson = !HasFlag("-nophysbonesjson");
                w.reencodeTextures = !HasFlag("-noreencode");
                w.stripAnimators = !HasFlag("-keepanimators");
                w.disableConstraints = !HasFlag("-keepconstraints");

                w.Stage();
                if (w.staged == null)
                {
                    Debug.LogError("[WarudoConvert] Staging failed - see the log above.");
                    Finish(3);
                    return;
                }

                w.Export();

                string expected = Path.Combine(outDir, Sanitize(w.staged.name) + ".vsfavatar");
                if (File.Exists(expected))
                {
                    Debug.Log("[WarudoConvert] BATCH OK " + expected);
                    exit = 0;
                }
                else
                {
                    Debug.LogError("[WarudoConvert] No .vsfavatar was produced.");
                    exit = 4;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[WarudoConvert] BATCH EXCEPTION: " + e);
                exit = 5;
            }
            Finish(exit);
        }

        static void Finish(int code)
        {
            // -quit alone would report success even when the conversion failed.
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        static bool HasFlag(string name)
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
