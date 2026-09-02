using System;
using System.Collections.Generic;
using System.IO;

namespace WarudoImporter.Serialized
{
    /// <summary>
    /// UnityFS AssetBundle reader with lazy block decompression.
    ///
    /// Warudo character bundles run to hundreds of megabytes but the component data we
    /// need is a fraction of a percent of that, so blocks are decompressed on demand and
    /// kept in a small cache rather than expanding the whole archive into memory.
    /// </summary>
    internal sealed class UnityBundle : IDisposable
    {
        public sealed class Node
        {
            public long Offset;
            public long Size;
            public uint Flags;
            public string Path;
        }

        struct Block
        {
            public uint UncompressedSize;
            public uint CompressedSize;
            public ushort Flags;
            public long FilePosition;      // where the compressed bytes start
            public long UncompressedStart; // offset within the virtual concatenated stream
        }

        readonly FileStream stream;
        readonly Block[] blocks;
        public readonly List<Node> Nodes = new List<Node>();
        public readonly string UnityRevision;

        // Small LRU of decompressed blocks. Bundle blocks are 128 KB, so this caps at ~2 MB.
        const int CacheSize = 16;
        readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();
        readonly List<int> cacheOrder = new List<int>();

        public UnityBundle(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

            // The bundle header is big-endian and small; read it in one gulp.
            byte[] head = new byte[256];
            int n = stream.Read(head, 0, head.Length);
            var hr = new EndianReader(head, true);

            string signature = hr.ReadStringToNull(32);
            if (signature != "UnityFS")
                throw new InvalidOperationException("Not a UnityFS bundle (signature " + signature + ")");

            int format = hr.ReadInt32();
            hr.ReadStringToNull(64);                 // player version
            UnityRevision = hr.ReadStringToNull(64); // engine version
            long bundleSize = hr.ReadInt64();
            uint compressedInfoSize = hr.ReadUInt32();
            uint uncompressedInfoSize = hr.ReadUInt32();
            uint dataFlags = hr.ReadUInt32();

            if (format >= 7) hr.Align(16);
            long afterHeader = hr.Position;
            if (afterHeader > n) throw new InvalidOperationException("Bundle header longer than expected");

            bool infoAtEnd = (dataFlags & 0x80) != 0;
            byte[] infoRaw = infoAtEnd
                ? ReadAt(bundleSize - compressedInfoSize, (int)compressedInfoSize)
                : ReadAt(afterHeader, (int)compressedInfoSize);

            byte[] info = DecompressChunk(infoRaw, (int)uncompressedInfoSize, (int)(dataFlags & 0x3F));
            var ir = new EndianReader(info, true);

            ir.Skip(16); // uncompressed data hash
            int blockCount = ir.ReadInt32();
            blocks = new Block[blockCount];
            for (int i = 0; i < blockCount; i++)
            {
                blocks[i].UncompressedSize = ir.ReadUInt32();
                blocks[i].CompressedSize = ir.ReadUInt32();
                blocks[i].Flags = ir.ReadUInt16();
            }

            int nodeCount = ir.ReadInt32();
            for (int i = 0; i < nodeCount; i++)
            {
                var node = new Node();
                node.Offset = ir.ReadInt64();
                node.Size = ir.ReadInt64();
                node.Flags = ir.ReadUInt32();
                node.Path = ir.ReadStringToNull();
                Nodes.Add(node);
            }

            // Where the first data block starts.
            long dataStart = infoAtEnd ? afterHeader : afterHeader + compressedInfoSize;
            if ((dataFlags & 0x200) != 0) dataStart = Align16(dataStart);

            long filePos = dataStart;
            long uncPos = 0;
            for (int i = 0; i < blockCount; i++)
            {
                blocks[i].FilePosition = filePos;
                blocks[i].UncompressedStart = uncPos;
                filePos += blocks[i].CompressedSize;
                uncPos += blocks[i].UncompressedSize;
            }
        }

        static long Align16(long v) { long m = v % 16; return m == 0 ? v : v + (16 - m); }

        byte[] ReadAt(long position, int length)
        {
            var buf = new byte[length];
            stream.Position = position;
            int got = 0;
            while (got < length)
            {
                int r = stream.Read(buf, got, length - got);
                if (r <= 0) throw new EndOfStreamException("Bundle truncated at " + position);
                got += r;
            }
            return buf;
        }

        static byte[] DecompressChunk(byte[] src, int uncompressedSize, int compression)
        {
            switch (compression)
            {
                case 0:
                    return src;
                case 2:
                case 3:
                {
                    var dst = new byte[uncompressedSize];
                    Lz4Block.Decompress(src, 0, src.Length, dst, 0, uncompressedSize);
                    return dst;
                }
                case 1:
                    throw new NotSupportedException(
                        "This bundle uses LZMA compression, which the importer cannot read. " +
                        "Re-export the mod with the default (LZ4) compression.");
                default:
                    throw new NotSupportedException("Unknown bundle compression type " + compression);
            }
        }

        byte[] GetBlock(int index)
        {
            byte[] cached;
            if (cache.TryGetValue(index, out cached))
            {
                cacheOrder.Remove(index);
                cacheOrder.Add(index);
                return cached;
            }

            var b = blocks[index];
            byte[] raw = ReadAt(b.FilePosition, (int)b.CompressedSize);
            byte[] plain = DecompressChunk(raw, (int)b.UncompressedSize, b.Flags & 0x3F);

            cache[index] = plain;
            cacheOrder.Add(index);
            if (cacheOrder.Count > CacheSize)
            {
                int drop = cacheOrder[0];
                cacheOrder.RemoveAt(0);
                cache.Remove(drop);
            }
            return plain;
        }

        int BlockIndexFor(long uncompressedOffset)
        {
            int lo = 0, hi = blocks.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                long start = blocks[mid].UncompressedStart;
                long end = start + blocks[mid].UncompressedSize;
                if (uncompressedOffset < start) hi = mid - 1;
                else if (uncompressedOffset >= end) lo = mid + 1;
                else return mid;
            }
            throw new InvalidOperationException("Offset " + uncompressedOffset + " is outside the bundle");
        }

        /// <summary>Reads a range out of the virtual concatenated (decompressed) stream.</summary>
        public byte[] Read(long offset, int length)
        {
            var outBuf = new byte[length];
            int written = 0;
            long pos = offset;
            while (written < length)
            {
                int bi = BlockIndexFor(pos);
                byte[] block = GetBlock(bi);
                int inBlock = (int)(pos - blocks[bi].UncompressedStart);
                int take = Math.Min(block.Length - inBlock, length - written);
                Buffer.BlockCopy(block, inBlock, outBuf, written, take);
                written += take;
                pos += take;
            }
            return outBuf;
        }

        public void Dispose()
        {
            cache.Clear();
            cacheOrder.Clear();
            if (stream != null) stream.Dispose();
        }
    }
}
