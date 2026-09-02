using System;

namespace WarudoImporter.Serialized
{
    /// <summary>
    /// LZMA1 decompressor for Unity AssetBundle blocks.
    ///
    /// Unity puts the 5-byte property header in front of every LZMA block but keeps the
    /// uncompressed length in the bundle's own tables, so the 8-byte size field that a
    /// stand-alone .lzma file carries is absent and decoding is bounded by dstLength.
    /// </summary>
    internal static class LzmaBlock
    {
        const int BitModelTotal = 1 << 11;
        const int MoveBits = 5;
        const uint TopValue = 1u << 24;

        const int NumStates = 12;
        const int NumPosBitsMax = 4;
        const int NumLenToPosStates = 4;
        const int NumAlignBits = 4;
        const int StartPosModelIndex = 4;
        const int EndPosModelIndex = 14;
        const int NumFullDistances = 1 << (EndPosModelIndex / 2);
        const int MatchMinLen = 2;

        // One length coder packed into a single array: choice, choice2, 16 low trees of
        // 8 slots, 16 mid trees of 8 slots, then one high tree of 256.
        const int LenLow = 2;
        const int LenMid = LenLow + ((1 << NumPosBitsMax) << 3);
        const int LenHigh = LenMid + ((1 << NumPosBitsMax) << 3);
        const int LenTotal = LenHigh + (1 << 8);

        public static int Decompress(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength)
        {
            if (srcLength < 5)
                throw new InvalidOperationException("LZMA: block is shorter than its 5-byte property header");
            if (dstOffset + dstLength > dst.Length)
                throw new InvalidOperationException("LZMA: output buffer is smaller than the declared size");

            Decoder decoder = new Decoder(src, srcOffset, srcLength, dst, dstOffset, dstLength);
            return decoder.Run();
        }

        sealed class Decoder
        {
            readonly byte[] src;
            readonly int srcEnd;
            int srcPos;
            int overrun;

            readonly byte[] dst;
            readonly int dstStart;
            readonly int dstEnd;
            int dstPos;

            uint range;
            uint code;

            readonly int lc;
            readonly uint posMask;
            readonly uint literalPosMask;

            readonly ushort[] literals;
            readonly ushort[] isMatch = new ushort[NumStates << NumPosBitsMax];
            readonly ushort[] isRep = new ushort[NumStates];
            readonly ushort[] isRepG0 = new ushort[NumStates];
            readonly ushort[] isRepG1 = new ushort[NumStates];
            readonly ushort[] isRepG2 = new ushort[NumStates];
            readonly ushort[] isRep0Long = new ushort[NumStates << NumPosBitsMax];
            readonly ushort[] posSlots = new ushort[NumLenToPosStates << 6];
            readonly ushort[] posDecoders = new ushort[NumFullDistances - EndPosModelIndex];
            readonly ushort[] posAlign = new ushort[1 << NumAlignBits];
            readonly ushort[] lenCoder = new ushort[LenTotal];
            readonly ushort[] repLenCoder = new ushort[LenTotal];

            public Decoder(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength)
            {
                this.src = src;
                this.srcPos = srcOffset;
                this.srcEnd = srcOffset + srcLength;
                this.dst = dst;
                this.dstStart = dstOffset;
                this.dstPos = dstOffset;
                this.dstEnd = dstOffset + dstLength;

                int props = src[srcPos++];
                if (props >= 9 * 5 * 5)
                    throw new InvalidOperationException("LZMA: invalid property byte " + props);
                lc = props % 9;
                int rest = props / 9;
                int lp = rest % 5;
                int pb = rest / 5;
                literalPosMask = (1u << lp) - 1;
                posMask = (1u << pb) - 1;
                srcPos += 4; // dictionary size, unused: the whole block is the window

                literals = new ushort[(1 << (lc + lp)) * 0x300];
                Fill(literals);
                Fill(isMatch); Fill(isRep); Fill(isRepG0); Fill(isRepG1); Fill(isRepG2);
                Fill(isRep0Long); Fill(posSlots); Fill(posDecoders); Fill(posAlign);
                Fill(lenCoder); Fill(repLenCoder);

                range = 0xFFFFFFFF;
                code = 0;
                for (int i = 0; i < 5; i++) code = (code << 8) | NextByte();
            }

            static void Fill(ushort[] probs)
            {
                for (int i = 0; i < probs.Length; i++) probs[i] = BitModelTotal >> 1;
            }

            byte NextByte()
            {
                if (srcPos < srcEnd) return src[srcPos++];

                // The range coder carries five bytes of lookahead, so a stream that ends on
                // its last useful byte can still ask for a few more. Past that the block is
                // genuinely truncated and continuing would just decode noise.
                if (++overrun > 8)
                    throw new InvalidOperationException("LZMA: input ended before the block was complete");
                return 0;
            }

            void Normalize()
            {
                if (range < TopValue)
                {
                    range <<= 8;
                    code = (code << 8) | NextByte();
                }
            }

            uint DecodeBit(ushort[] probs, int index)
            {
                uint prob = probs[index];
                uint bound = (range >> 11) * prob;
                if (code < bound)
                {
                    range = bound;
                    probs[index] = (ushort)(prob + ((BitModelTotal - prob) >> MoveBits));
                    Normalize();
                    return 0;
                }
                range -= bound;
                code -= bound;
                probs[index] = (ushort)(prob - (prob >> MoveBits));
                Normalize();
                return 1;
            }

            uint DecodeDirectBits(int count)
            {
                uint result = 0;
                for (int i = 0; i < count; i++)
                {
                    range >>= 1;
                    uint t = (code - range) >> 31;
                    code -= range & (t - 1);
                    result = (result << 1) | (1 - t);
                    Normalize();
                }
                return result;
            }

            uint BitTreeDecode(ushort[] probs, int offset, int levels)
            {
                uint m = 1;
                for (int i = 0; i < levels; i++) m = (m << 1) + DecodeBit(probs, offset + (int)m);
                return m - (1u << levels);
            }

            uint ReverseDecode(ushort[] probs, int offset, int levels)
            {
                uint m = 1;
                uint symbol = 0;
                for (int i = 0; i < levels; i++)
                {
                    uint bit = DecodeBit(probs, offset + (int)m);
                    m = (m << 1) + bit;
                    symbol |= bit << i;
                }
                return symbol;
            }

            uint DecodeLen(ushort[] probs, uint posState)
            {
                if (DecodeBit(probs, 0) == 0)
                    return BitTreeDecode(probs, LenLow + (int)(posState << 3), 3);
                if (DecodeBit(probs, 1) == 0)
                    return 8 + BitTreeDecode(probs, LenMid + (int)(posState << 3), 3);
                return 16 + BitTreeDecode(probs, LenHigh, 8);
            }

            byte DecodeLiteral(int offset)
            {
                uint symbol = 1;
                do { symbol = (symbol << 1) | DecodeBit(literals, offset + (int)symbol); }
                while (symbol < 0x100);
                return (byte)symbol;
            }

            byte DecodeMatchedLiteral(int offset, byte matchByte)
            {
                uint symbol = 1;
                uint match = matchByte;
                do
                {
                    uint matchBit = (match >> 7) & 1;
                    match <<= 1;
                    uint bit = DecodeBit(literals, offset + (int)(((1 + matchBit) << 8) + symbol));
                    symbol = (symbol << 1) | bit;

                    // Once the guess diverges from the previous match the remaining bits are
                    // decoded with the plain literal models.
                    if (matchBit != bit)
                    {
                        while (symbol < 0x100) symbol = (symbol << 1) | DecodeBit(literals, offset + (int)symbol);
                        break;
                    }
                } while (symbol < 0x100);
                return (byte)symbol;
            }

            public int Run()
            {
                uint state = 0;
                uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
                byte prev = 0;

                while (dstPos < dstEnd)
                {
                    uint pos = (uint)(dstPos - dstStart);
                    uint posState = pos & posMask;

                    if (DecodeBit(isMatch, (int)((state << NumPosBitsMax) + posState)) == 0)
                    {
                        int offset = (int)(((pos & literalPosMask) << lc) + (uint)(prev >> (8 - lc))) * 0x300;
                        prev = state < 7
                            ? DecodeLiteral(offset)
                            : DecodeMatchedLiteral(offset, dst[dstPos - (int)rep0 - 1]);
                        dst[dstPos++] = prev;
                        state = state < 4 ? 0 : (state < 10 ? state - 3 : state - 6);
                        continue;
                    }

                    uint len;
                    if (DecodeBit(isRep, (int)state) != 0)
                    {
                        if (pos == 0)
                            throw new InvalidOperationException("LZMA: repeat match before any output");

                        if (DecodeBit(isRepG0, (int)state) == 0)
                        {
                            if (DecodeBit(isRep0Long, (int)((state << NumPosBitsMax) + posState)) == 0)
                            {
                                state = state < 7 ? 9u : 11u;
                                prev = dst[dstPos - (int)rep0 - 1];
                                dst[dstPos++] = prev;
                                continue;
                            }
                        }
                        else
                        {
                            uint distance;
                            if (DecodeBit(isRepG1, (int)state) == 0)
                            {
                                distance = rep1;
                            }
                            else
                            {
                                if (DecodeBit(isRepG2, (int)state) == 0)
                                {
                                    distance = rep2;
                                }
                                else
                                {
                                    distance = rep3;
                                    rep3 = rep2;
                                }
                                rep2 = rep1;
                            }
                            rep1 = rep0;
                            rep0 = distance;
                        }
                        len = DecodeLen(repLenCoder, posState) + MatchMinLen;
                        state = state < 7 ? 8u : 11u;
                    }
                    else
                    {
                        rep3 = rep2; rep2 = rep1; rep1 = rep0;
                        len = DecodeLen(lenCoder, posState) + MatchMinLen;
                        state = state < 7 ? 7u : 10u;

                        uint lenState = len - MatchMinLen;
                        if (lenState >= NumLenToPosStates) lenState = NumLenToPosStates - 1;
                        uint slot = BitTreeDecode(posSlots, (int)(lenState << 6), 6);

                        if (slot >= StartPosModelIndex)
                        {
                            int directBits = (int)((slot >> 1) - 1);
                            rep0 = (2 | (slot & 1)) << directBits;
                            if (slot < EndPosModelIndex)
                            {
                                rep0 += ReverseDecode(posDecoders, (int)(rep0 - slot) - 1, directBits);
                            }
                            else
                            {
                                rep0 += DecodeDirectBits(directBits - NumAlignBits) << NumAlignBits;
                                rep0 += ReverseDecode(posAlign, 0, NumAlignBits);
                                if (rep0 == 0xFFFFFFFF) break; // end-of-stream marker
                            }
                        }
                        else
                        {
                            rep0 = slot;
                        }
                    }

                    if (rep0 >= pos)
                        throw new InvalidOperationException(
                            "LZMA: match distance " + (rep0 + 1) + " reaches past the " + pos + " bytes produced");

                    // A match may legitimately overrun the last block boundary; the surplus
                    // belongs to the next block and is simply not wanted here.
                    if (len > (uint)(dstEnd - dstPos)) len = (uint)(dstEnd - dstPos);

                    int from = dstPos - (int)rep0 - 1;
                    for (uint i = 0; i < len; i++) dst[dstPos++] = dst[from++];
                    prev = dst[dstPos - 1];
                }

                if (dstPos != dstEnd)
                    throw new InvalidOperationException(
                        "LZMA: stream ended after " + (dstPos - dstStart) + " of " + (dstEnd - dstStart) + " bytes");

                return dstPos - dstStart;
            }
        }
    }
}
