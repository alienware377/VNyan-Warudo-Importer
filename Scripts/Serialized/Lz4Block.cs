using System;

namespace WarudoImporter.Serialized
{
    /// <summary>
    /// LZ4 block-format decompressor. Unity's LZ4/LZ4HC AssetBundle blocks are raw LZ4
    /// blocks with no frame header, so only the block decoder is needed.
    /// </summary>
    internal static class Lz4Block
    {
        public static int Decompress(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength)
        {
            int ip = srcOffset;
            int iend = srcOffset + srcLength;
            int op = dstOffset;
            int oend = dstOffset + dstLength;

            while (ip < iend)
            {
                int token = src[ip++];

                int litLen = token >> 4;
                if (litLen == 15)
                {
                    int b;
                    do
                    {
                        if (ip >= iend) throw new InvalidOperationException("LZ4: truncated literal length");
                        b = src[ip++];
                        litLen += b;
                    } while (b == 255);
                }

                if (litLen > 0)
                {
                    if (ip + litLen > iend || op + litLen > oend)
                        throw new InvalidOperationException("LZ4: literal run overruns buffer");
                    Buffer.BlockCopy(src, ip, dst, op, litLen);
                    ip += litLen;
                    op += litLen;
                }

                // The final sequence of a block is literals only.
                if (ip >= iend) break;

                if (ip + 2 > iend) throw new InvalidOperationException("LZ4: truncated match offset");
                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                if (offset == 0) throw new InvalidOperationException("LZ4: zero match offset");

                int matchLen = token & 0x0F;
                if (matchLen == 15)
                {
                    int b;
                    do
                    {
                        if (ip >= iend) throw new InvalidOperationException("LZ4: truncated match length");
                        b = src[ip++];
                        matchLen += b;
                    } while (b == 255);
                }
                matchLen += 4;

                int mp = op - offset;
                if (mp < dstOffset) throw new InvalidOperationException("LZ4: match offset before window start");
                if (op + matchLen > oend) throw new InvalidOperationException("LZ4: match overruns buffer");

                // Overlapping copies are legal and must stay byte-by-byte.
                if (offset >= matchLen)
                {
                    Buffer.BlockCopy(dst, mp, dst, op, matchLen);
                    op += matchLen;
                }
                else
                {
                    for (int i = 0; i < matchLen; i++) dst[op++] = dst[mp++];
                }
            }

            return op - dstOffset;
        }
    }
}
