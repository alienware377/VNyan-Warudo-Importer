using System;
using System.Text;

namespace WarudoImporter.Serialized
{
    /// <summary>
    /// Binary reader over a byte[] that can flip endianness mid-stream, which Unity's
    /// serialized files require: the header is big-endian, the payload usually little.
    /// </summary>
    internal sealed class EndianReader
    {
        readonly byte[] data;
        public int Position;
        public bool BigEndian;

        public EndianReader(byte[] data, bool bigEndian)
        {
            this.data = data;
            BigEndian = bigEndian;
        }

        public byte[] Buffer { get { return data; } }
        public int Length { get { return data.Length; } }
        public int Remaining { get { return data.Length - Position; } }

        void Need(int n)
        {
            if (Position + n > data.Length)
                throw new InvalidOperationException("Read past end of buffer (need " + n + " at " + Position + " of " + data.Length + ")");
        }

        public byte ReadByte() { Need(1); return data[Position++]; }
        public sbyte ReadSByte() { return (sbyte)ReadByte(); }
        public bool ReadBool() { return ReadByte() != 0; }

        public byte[] ReadBytes(int n)
        {
            Need(n);
            var b = new byte[n];
            System.Buffer.BlockCopy(data, Position, b, 0, n);
            Position += n;
            return b;
        }

        public void Skip(int n) { Need(n); Position += n; }

        public short ReadInt16() { return (short)ReadUInt16(); }
        public ushort ReadUInt16()
        {
            Need(2);
            ushort v = BigEndian
                ? (ushort)((data[Position] << 8) | data[Position + 1])
                : (ushort)((data[Position + 1] << 8) | data[Position]);
            Position += 2;
            return v;
        }

        public int ReadInt32() { return (int)ReadUInt32(); }
        public uint ReadUInt32()
        {
            Need(4);
            uint v = BigEndian
                ? ((uint)data[Position] << 24) | ((uint)data[Position + 1] << 16) | ((uint)data[Position + 2] << 8) | data[Position + 3]
                : ((uint)data[Position + 3] << 24) | ((uint)data[Position + 2] << 16) | ((uint)data[Position + 1] << 8) | data[Position];
            Position += 4;
            return v;
        }

        public long ReadInt64() { return (long)ReadUInt64(); }
        public ulong ReadUInt64()
        {
            Need(8);
            ulong v = 0;
            if (BigEndian) { for (int i = 0; i < 8; i++) v = (v << 8) | data[Position + i]; }
            else { for (int i = 7; i >= 0; i--) v = (v << 8) | data[Position + i]; }
            Position += 8;
            return v;
        }

        public float ReadSingle()
        {
            uint u = ReadUInt32();
            return BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
        }

        public double ReadDouble()
        {
            ulong u = ReadUInt64();
            return BitConverter.Int64BitsToDouble((long)u);
        }

        public void Align(int alignment = 4)
        {
            int m = Position % alignment;
            if (m != 0) Position += alignment - m;
        }

        public string ReadStringToNull(int max = 4096)
        {
            int start = Position;
            int end = start;
            while (end < data.Length && data[end] != 0 && end - start < max) end++;
            string s = Encoding.UTF8.GetString(data, start, end - start);
            Position = end < data.Length ? end + 1 : end;
            return s;
        }

        /// <summary>Length-prefixed UTF-8 string, padded to a 4-byte boundary.</summary>
        public string ReadAlignedString()
        {
            int n = ReadInt32();
            if (n < 0 || Position + n > data.Length)
                throw new InvalidOperationException("Bad aligned-string length " + n);
            string s = Encoding.UTF8.GetString(data, Position, n);
            Position += n;
            Align();
            return s;
        }
    }
}
