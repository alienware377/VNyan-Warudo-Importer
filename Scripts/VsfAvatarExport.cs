using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// Writes a real .vsfavatar file from a .warudo mod.
    ///
    /// A .vsfavatar is a Unity AssetBundle, and AssetBundles can only be *built* by the Unity
    /// editor - a running player has no equivalent API. So instead of pretending, this drives a
    /// Unity editor in batch mode: it drops the offline converter into a Unity project, runs it
    /// headlessly, and hands back the finished file. The result is an ordinary .vsfavatar that
    /// VNyan (or VSeeFace) loads with its normal Load Avatar button, no plugin involved.
    ///
    /// The project has to be one that already has UniVRM and the shaders the mod uses - the
    /// Warudo SDK project is the obvious choice, since it has both by definition.
    /// </summary>
    public static class VsfAvatarExport
    {
        public class Options
        {
            public string warudoPath;
            public string projectPath;
            public string outputDir;
            /// <summary>The Unity version the mod was built with, from its modinfo.</summary>
            public string modUnityVersion;
            public bool writePhysBonesJson;
            public bool reencodeTextures = true;
            public bool stripAnimators = true;
            public bool disableConstraints = true;
        }

        public class Job
        {
            public Process process;
            public string logPath;
            public string outputDir;
            public string unityExe;
            public DateTime started;

            public bool Done { get { return process == null || process.HasExited; } }
            public int ExitCode { get { return process != null && process.HasExited ? process.ExitCode : -1; } }
            public TimeSpan Elapsed { get { return DateTime.Now - started; } }

            /// <summary>Reads the Unity log while it is still open, which needs shared access.</summary>
            public string Tail(int lines)
            {
                try
                {
                    if (!File.Exists(logPath)) return "";
                    var kept = new List<string>();
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            kept.Add(line);
                            if (kept.Count > lines) kept.RemoveAt(0);
                        }
                    }
                    return string.Join("\n", kept.ToArray());
                }
                catch { return ""; }
            }

            /// <summary>The line the batch converter prints on success carries the output path.</summary>
            public string ProducedFile()
            {
                try
                {
                    if (!File.Exists(logPath)) return null;
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                    {
                        string line, found = null;
                        while ((line = sr.ReadLine()) != null)
                        {
                            int i = line.IndexOf("BATCH OK ", StringComparison.Ordinal);
                            if (i >= 0) found = line.Substring(i + "BATCH OK ".Length).Trim();
                        }
                        return found;
                    }
                }
                catch { return null; }
            }
        }

        /// <summary>Where this plugin is installed, which is where the converter sources ship.</summary>
        public static string PluginDir
        {
            get
            {
                // Application.dataPath is <VNyan>/VNyan_Data.
                string root = Path.GetDirectoryName(Application.dataPath);
                return Path.Combine(root, Path.Combine("Items", Path.Combine("Assemblies", "WarudoImporter")));
            }
        }

        /// <summary>Set when the converter sources live somewhere other than beside the plugin.</summary>
        public static string ConfiguredToolsetDir { get; set; }

        public static string ToolsetSourceDir
        {
            get
            {
                if (!string.IsNullOrEmpty(ConfiguredToolsetDir) && LooksLikeToolset(ConfiguredToolsetDir))
                    return ConfiguredToolsetDir;
                string shipped = Path.Combine(PluginDir, Path.Combine("UnityToolset", "WarudoConvert"));
                return shipped;
            }
        }

        /// <summary>The converter is only usable if the batch entry point is actually in there.</summary>
        public static bool LooksLikeToolset(string dir)
        {
            return !string.IsNullOrEmpty(dir)
                && File.Exists(Path.Combine(dir, Path.Combine("Editor", "WarudoBatchConvert.cs")));
        }

        public static bool ToolsetAvailable { get { return LooksLikeToolset(ToolsetSourceDir); } }

        /// <summary>True if the folder looks like a Unity project we can drive.</summary>
        public static bool IsUnityProject(string dir)
        {
            return !string.IsNullOrEmpty(dir)
                && Directory.Exists(Path.Combine(dir, "Assets"))
                && Directory.Exists(Path.Combine(dir, "ProjectSettings"));
        }

        /// <summary>UniVRM has to be in the project, or the exported avatar has no VRM components.</summary>
        public static bool ProjectHasVrm(string dir)
        {
            try
            {
                string assets = Path.Combine(dir, "Assets");
                foreach (var d in Directory.GetDirectories(assets, "VRM*", SearchOption.AllDirectories)) return true;
                foreach (var f in Directory.GetFiles(assets, "VRMMeta.cs", SearchOption.AllDirectories)) return true;
                string pkg = Path.Combine(dir, Path.Combine("Packages", "manifest.json"));
                if (File.Exists(pkg) && File.ReadAllText(pkg).IndexOf("univrm", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch { }
            return false;
        }

        public static string ProjectUnityVersion(string dir)
        {
            try
            {
                string p = Path.Combine(dir, Path.Combine("ProjectSettings", "ProjectVersion.txt"));
                if (!File.Exists(p)) return null;
                foreach (string line in File.ReadAllLines(p))
                    if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                        return line.Substring("m_EditorVersion:".Length).Trim();
            }
            catch { }
            return null;
        }

        /// <summary>Finds the editor that owns the project, falling back to the newest installed one.</summary>
        public static string FindUnityExe(string projectPath, List<string> notes)
        {
            string version = ProjectUnityVersion(projectPath);
            var roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             Path.Combine("Unity", Path.Combine("Hub", "Editor"))),
                @"C:\Program Files\Unity\Hub\Editor",
            };

            string best = null;
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                if (!string.IsNullOrEmpty(version))
                {
                    string exact = Path.Combine(root, Path.Combine(version, Path.Combine("Editor", "Unity.exe")));
                    if (File.Exists(exact)) return exact;
                }
                var dirs = Directory.GetDirectories(root);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = dirs.Length - 1; i >= 0; i--)
                {
                    string exe = Path.Combine(dirs[i], Path.Combine("Editor", "Unity.exe"));
                    if (File.Exists(exe)) { best = exe; break; }
                }
                if (best != null) break;
            }

            if (best != null && !string.IsNullOrEmpty(version))
                notes.Add("The project was made with Unity " + version + ", which is not installed; " +
                          "using " + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(best))) +
                          " instead. Opening the project once in Unity first is safer.");
            return best;
        }

        /// <summary>Compares Unity versions like "2022.3.22f1"; negative when a is older than b.</summary>
        public static int CompareUnityVersions(string a, string b)
        {
            int[] pa = ParseUnityVersion(a), pb = ParseUnityVersion(b);
            for (int i = 0; i < 3; i++)
                if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
            return 0;
        }

        static int[] ParseUnityVersion(string v)
        {
            var parts = new int[3];
            if (string.IsNullOrEmpty(v)) return parts;
            var chunks = v.Split('.');
            for (int i = 0; i < 3 && i < chunks.Length; i++)
            {
                var digits = new StringBuilder();
                foreach (char c in chunks[i])
                {
                    if (char.IsDigit(c)) digits.Append(c);
                    else break;
                }
                int n;
                if (int.TryParse(digits.ToString(), out n)) parts[i] = n;
            }
            return parts;
        }

        /// <summary>Copies the converter scripts into the project so the batch method exists.</summary>
        public static bool InstallToolset(string projectPath, List<string> notes)
        {
            try
            {
                if (!ToolsetAvailable)
                {
                    notes.Add("Could not find the converter sources (looked in " + ToolsetSourceDir + ").");
                    return false;
                }
                string dst = Path.Combine(projectPath, Path.Combine("Assets", "WarudoConvert"));
                CopyTree(ToolsetSourceDir, dst);
                notes.Add("Converter installed into " + dst + ".");
                return true;
            }
            catch (Exception e)
            {
                notes.Add("Could not install the converter into the project: " + e.Message);
                return false;
            }
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".cs" && ext != ".asmdef" && ext != ".meta") continue;
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            }
            foreach (string d in Directory.GetDirectories(from))
                CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }

        public static Job Start(Options o, List<string> notes)
        {
            if (o == null || string.IsNullOrEmpty(o.warudoPath) || !File.Exists(o.warudoPath))
            { notes.Add("Choose a .warudo file first."); return null; }

            if (!IsUnityProject(o.projectPath))
            { notes.Add("That folder is not a Unity project (it needs Assets\\ and ProjectSettings\\)."); return null; }

            if (!ProjectHasVrm(o.projectPath))
                notes.Add("Heads up: no UniVRM found in that project. The export will still run, but the " +
                          "avatar will come out without VRM components unless the project has UniVRM.");

            // AssetBundles are forward compatible only: a newer editor reads an older bundle,
            // never the other way round. Catch that here rather than after a ten minute import.
            string projectVersion = ProjectUnityVersion(o.projectPath);
            if (!string.IsNullOrEmpty(o.modUnityVersion) && !string.IsNullOrEmpty(projectVersion)
                && CompareUnityVersions(projectVersion, o.modUnityVersion) < 0)
            {
                notes.Add("That project is on Unity " + projectVersion + ", but the mod was built with " +
                          o.modUnityVersion + ". Unity cannot open a bundle from a newer version, so the " +
                          "export would fail. Use a project on " + o.modUnityVersion + " or newer.");
                return null;
            }

            string unity = FindUnityExe(o.projectPath, notes);
            if (unity == null)
            { notes.Add("No Unity editor found under the Unity Hub folder."); return null; }

            if (!InstallToolset(o.projectPath, notes)) return null;

            Directory.CreateDirectory(o.outputDir);
            string log = Path.Combine(Path.GetTempPath(), "WarudoConvertBatch.log");
            try { if (File.Exists(log)) File.Delete(log); } catch { }

            var sb = new StringBuilder();
            sb.Append("-batchmode -quit");
            sb.Append(" -projectPath \"").Append(o.projectPath.TrimEnd('\\')).Append('"');
            sb.Append(" -executeMethod WarudoImporter.EditorTools.WarudoBatchConvert.Run");
            sb.Append(" -warudo \"").Append(o.warudoPath).Append('"');
            sb.Append(" -out \"").Append(o.outputDir.TrimEnd('\\')).Append('"');
            sb.Append(" -logFile \"").Append(log).Append('"');
            if (!o.writePhysBonesJson) sb.Append(" -nophysbonesjson");
            if (!o.reencodeTextures) sb.Append(" -noreencode");
            if (!o.stripAnimators) sb.Append(" -keepanimators");
            if (!o.disableConstraints) sb.Append(" -keepconstraints");

            var psi = new ProcessStartInfo(unity, sb.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = o.projectPath;

            var job = new Job { logPath = log, outputDir = o.outputDir, unityExe = unity, started = DateTime.Now };
            try { job.process = Process.Start(psi); }
            catch (Exception e) { notes.Add("Could not start Unity: " + e.Message); return null; }

            notes.Add("Unity is building the .vsfavatar in the background (" +
                      Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(unity))) + "). " +
                      "First run imports the project, so it can take several minutes.");
            return job;
        }
    }
}
