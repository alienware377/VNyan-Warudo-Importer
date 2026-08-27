using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace WarudoImporter
{
    /// <summary>
    /// A .warudo file is a uMod container: a 12-byte header ("UMOD" + 8 bytes) followed by a
    /// plain ZIP archive. The ZIP holds:
    ///     modinfo.dat        - .NET BinaryWriter stream: magic, timestamp, ints, strings, cover JPEG
    ///     sharedassets.bin   - a stock UnityFS AssetBundle (built by the mod's Unity version)
    ///     sharedassets.meta  - newline-free list of the asset paths inside the bundle
    /// Verified against Tsukki_Fairy v1.warudo (uMod 2.9.0, Unity 2021.3.18f1).
    /// </summary>
    public class WarudoContainer
    {
        public const int HeaderBytes = 12;
        static readonly byte[] Magic = { 0x55, 0x4D, 0x4F, 0x44 }; // "UMOD"

        public string sourcePath;
        public string bundlePath;      // extracted sharedassets.bin on disk
        public string cacheDir;

        // modinfo.dat
        public string umodVersion = "";
        public string unityVersion = "";
        public string sdkVersion = "";
        public string modName = "";
        public string modVersion = "";
        public string description = "";
        public string author = "";
        public string modGuid = "";
        public byte[] coverJpeg;

        // sharedassets.meta
        public List<string> assetPaths = new List<string>();

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(modName) ? Path.GetFileNameWithoutExtension(sourcePath) : modName; }
        }

        // ------------------------------------------------------------------ probing

        /// <summary>Cheap check that a file really is a uMod container, without unpacking it.</summary>
        public static bool LooksLikeWarudo(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    if (fs.Length < HeaderBytes + 4) return false;
                    byte[] head = new byte[HeaderBytes + 4];
                    if (fs.Read(head, 0, head.Length) != head.Length) return false;
                    for (int i = 0; i < 4; i++) if (head[i] != Magic[i]) return false;
                    // ZIP local file header right after the 12-byte uMod header
                    return head[12] == 0x50 && head[13] == 0x4B && head[14] == 0x03 && head[15] == 0x04;
                }
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ opening

        /// <summary>
        /// Unpacks the container into <paramref name="cacheRoot"/>. The AssetBundle is written to
        /// disk (it can be hundreds of MB, so we stream rather than buffer it) and its path is
        /// returned in <see cref="bundlePath"/>.
        /// </summary>
        public static WarudoContainer Open(string path, string cacheRoot)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("No such .warudo file", path);
            if (!LooksLikeWarudo(path))
                throw new InvalidDataException("Not a uMod container (missing UMOD/PK signature): " + path);

            WarudoContainer c = new WarudoContainer();
            c.sourcePath = path;

            // Cache per source file+timestamp so re-importing the same mod is instant but an
            // edited mod is never served stale.
            FileInfo fi = new FileInfo(path);
            string key = Sanitize(Path.GetFileNameWithoutExtension(path)) + "_" +
                         fi.Length.ToString("x") + "_" + fi.LastWriteTimeUtc.Ticks.ToString("x");
            c.cacheDir = Path.Combine(cacheRoot, key);
            c.bundlePath = Path.Combine(c.cacheDir, "sharedassets.bin");
            Directory.CreateDirectory(c.cacheDir);

            using (FileStream fs = File.OpenRead(path))
            {
                // ZipArchive resolves the central directory using offsets that are relative to the
                // START of the archive, but it treats stream position 0 as that start. Simply
                // seeking past the 12-byte uMod header is not enough - every offset then lands 12
                // bytes early and it throws "Number of entries expected in End Of Central
                // Directory does not correspond...". OffsetStream re-bases the file so position 0
                // is the ZIP's first byte, which avoids copying the whole (often 300 MB+) archive.
                using (OffsetStream zipView = new OffsetStream(fs, HeaderBytes))
                using (ZipArchive zip = new ZipArchive(zipView, ZipArchiveMode.Read, true))
                {
                    ZipArchiveEntry info = FindEntry(zip, "modinfo.dat");
                    if (info != null) c.ParseModInfo(ReadAll(info));

                    ZipArchiveEntry meta = FindEntry(zip, "sharedassets.meta");
                    if (meta != null) c.ParseMeta(ReadAll(meta));

                    ZipArchiveEntry bin = FindEntry(zip, "sharedassets.bin");
                    if (bin == null)
                        throw new InvalidDataException("Container has no sharedassets.bin (not a model mod?)");

                    bool haveCached = File.Exists(c.bundlePath) &&
                                      new FileInfo(c.bundlePath).Length == bin.Length;
                    if (!haveCached)
                    {
                        using (Stream src = bin.Open())
                        using (FileStream dst = File.Create(c.bundlePath))
                            src.CopyTo(dst, 1 << 20);
                    }
                }
            }
            return c;
        }

        /// <summary>Read-only seekable view of a stream starting at a fixed offset.</summary>
        sealed class OffsetStream : Stream
        {
            readonly Stream inner;
            readonly long origin;

            public OffsetStream(Stream inner, long origin)
            {
                this.inner = inner;
                this.origin = origin;
                inner.Position = origin;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return inner.CanSeek; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length - origin; } }

            public override long Position
            {
                get { return inner.Position - origin; }
                set { inner.Position = value + origin; }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin loc)
            {
                switch (loc)
                {
                    case SeekOrigin.Begin: return inner.Seek(origin + offset, SeekOrigin.Begin) - origin;
                    case SeekOrigin.Current: return inner.Seek(offset, SeekOrigin.Current) - origin;
                    default: return inner.Seek(offset, SeekOrigin.End) - origin;
                }
            }

            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            // The FileStream is owned by the caller's using block.
            protected override void Dispose(bool disposing) { }
        }

        static ZipArchiveEntry FindEntry(ZipArchive zip, string name)
        {
            foreach (ZipArchiveEntry e in zip.Entries)
                if (string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(e.FullName), name, StringComparison.OrdinalIgnoreCase))
                    return e;
            return null;
        }

        static byte[] ReadAll(ZipArchiveEntry e)
        {
            using (Stream s = e.Open())
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms, 1 << 16);
                return ms.ToArray();
            }
        }

        static string Sanitize(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ modinfo.dat

        /// <summary>
        /// Tolerant parse. The layout is a .NET BinaryWriter stream and uMod is free to add
        /// fields between versions, so anything past the strings we recognise is ignored and the
        /// cover image is located by JPEG signature rather than by offset.
        /// </summary>
        void ParseModInfo(byte[] data)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
                {
                    byte[] m = br.ReadBytes(4);
                    if (m.Length < 4 || m[0] != Magic[0] || m[1] != Magic[1] || m[2] != Magic[2] || m[3] != Magic[3])
                        return; // unknown dialect; metadata is cosmetic, keep going without it

                    br.ReadInt64();  // build timestamp (DateTime.ToBinary)
                    br.ReadInt32();  // content flags
                    br.ReadInt32();
                    br.ReadInt32();

                    umodVersion = br.ReadString();
                    unityVersion = br.ReadString();
                    sdkVersion = br.ReadString();
                    modName = br.ReadString();
                    modVersion = br.ReadString();
                    description = br.ReadString();
                    author = br.ReadString();

                    // Remaining strings vary (contributors, target app, guid). Take the last
                    // GUID-shaped one we can still read before the binary tail.
                    for (int i = 0; i < 8 && ms.Position < ms.Length; i++)
                    {
                        long mark = ms.Position;
                        string s;
                        try { s = br.ReadString(); }
                        catch { ms.Position = mark; break; }
                        if (s == null) break;
                        if (s.Length == 36 && s[8] == '-' && s[13] == '-') { modGuid = s; break; }
                        if (s.Length > 128) { ms.Position = mark; break; }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WarudoImporter] modinfo.dat parse fell short: " + e.Message);
            }

            coverJpeg = ExtractJpeg(data);
        }

        static byte[] ExtractJpeg(byte[] data)
        {
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF)
                {
                    int len = data.Length - i;
                    byte[] jpg = new byte[len];
                    Buffer.BlockCopy(data, i, jpg, 0, len);
                    return jpg;
                }
            }
            return null;
        }

        /// <summary>Cover art for the info panel, or null. Caller owns the texture.</summary>
        public Texture2D LoadCover()
        {
            if (coverJpeg == null || coverJpeg.Length == 0) return null;
            Texture2D t = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (t.LoadImage(coverJpeg)) return t;
            UnityEngine.Object.Destroy(t);
            return null;
        }

        // ------------------------------------------------------------------ sharedassets.meta

        /// <summary>
        /// The .meta is length-prefixed strings with no separators; scanning for the "Assets/"
        /// marker is more robust than trusting the prefix widths across uMod versions.
        /// </summary>
        void ParseMeta(byte[] data)
        {
            string all = Encoding.UTF8.GetString(data);
            int i = 0;
            while (true)
            {
                int at = all.IndexOf("Assets/", i, StringComparison.Ordinal);
                if (at < 0) break;
                int end = at;
                while (end < all.Length && all[end] >= 0x20 && all[end] < 0x7F) end++;
                string p = all.Substring(at, end - at);
                if (p.Length > "Assets/".Length && !assetPaths.Contains(p)) assetPaths.Add(p);
                i = end > at ? end : at + 1;
            }
        }
    }
}
