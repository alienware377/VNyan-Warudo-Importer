using System;
using System.Collections.Generic;

namespace WarudoImporter.Serialized
{
    /// <summary>
    /// Reader for the metadata of a Unity serialized asset file: its type trees, its
    /// object table and its managed reference types. Object payloads stay in the bundle
    /// and are decoded on demand.
    /// </summary>
    internal sealed class SerializedAssetFile
    {
        public sealed class SType
        {
            public int ClassId;
            public bool IsStripped;
            public short ScriptTypeIndex = -1;
            public byte[] ScriptId;
            public byte[] OldTypeHash;
            public TypeTreeNode Node;
            public string ClassName, NameSpace, AssemblyName; // reference types only
        }

        public sealed class ObjectInfo
        {
            public long PathId;
            public long ByteStart;
            public int ByteSize;
            public int TypeId;
            public SType Type;
            public int ClassId;
        }

        public int Version;
        public string UnityVersion;
        public bool EnableTypeTree;
        public bool BigEndian;
        public long DataOffset;

        public readonly List<SType> Types = new List<SType>();
        public readonly List<SType> RefTypes = new List<SType>();
        public readonly List<ObjectInfo> Objects = new List<ObjectInfo>();

        readonly UnityBundle bundle;
        readonly long nodeOffset;

        public SerializedAssetFile(UnityBundle bundle, UnityBundle.Node node)
        {
            this.bundle = bundle;
            this.nodeOffset = node.Offset;

            // The header is big-endian; peek enough of it to learn the real metadata size.
            byte[] peek = bundle.Read(node.Offset, (int)Math.Min(node.Size, 64));
            var pr = new EndianReader(peek, true);
            pr.Skip(8);                       // legacy metadata size + file size
            int version = pr.ReadInt32();
            pr.Skip(4);                       // legacy data offset

            long dataOffset;
            if (version >= 9)
            {
                pr.ReadByte();                // endianness, re-read below
                pr.Skip(3);
                if (version >= 22)
                {
                    pr.ReadUInt32();          // metadata size
                    pr.ReadInt64();           // file size
                    dataOffset = pr.ReadInt64();
                }
                else
                {
                    var back = new EndianReader(peek, true);
                    back.Skip(12);
                    dataOffset = back.ReadUInt32();
                }
            }
            else
            {
                var back = new EndianReader(peek, true);
                back.Skip(12);
                dataOffset = back.ReadUInt32();
            }

            // All metadata lives before the data section, so one read covers it.
            int metaLen = (int)Math.Min(dataOffset, node.Size);
            byte[] meta = bundle.Read(node.Offset, metaLen);
            Parse(meta);
        }

        void Parse(byte[] meta)
        {
            var r = new EndianReader(meta, true);

            r.Skip(8);                        // legacy metadata size + file size
            Version = r.ReadInt32();
            r.Skip(4);                        // legacy data offset

            if (Version >= 9)
            {
                BigEndian = r.ReadBool();
                r.Skip(3);
                if (Version >= 22)
                {
                    r.ReadUInt32();           // metadata size (still big-endian here)
                    r.ReadInt64();            // file size
                    DataOffset = r.ReadInt64();
                    r.ReadInt64();            // reserved
                }
            }
            else
            {
                throw new NotSupportedException("Serialized file version " + Version + " is too old");
            }

            r.BigEndian = BigEndian;

            if (Version >= 7) UnityVersion = r.ReadStringToNull();
            if (Version >= 8) r.ReadInt32();  // target platform
            EnableTypeTree = Version < 13 || r.ReadBool();

            if (!EnableTypeTree)
                throw new NotSupportedException(
                    "This bundle was built without type trees, so component data cannot be read.");

            int typeCount = r.ReadInt32();
            for (int i = 0; i < typeCount; i++) Types.Add(ReadType(r, false));

            int objectCount = r.ReadInt32();
            for (int i = 0; i < objectCount; i++)
            {
                var o = new ObjectInfo();
                r.Align();
                o.PathId = r.ReadInt64();
                o.ByteStart = Version >= 22 ? r.ReadInt64() : r.ReadUInt32();
                o.ByteStart += DataOffset;
                o.ByteSize = (int)r.ReadUInt32();
                o.TypeId = r.ReadInt32();
                o.Type = Types[o.TypeId];
                o.ClassId = o.Type.ClassId;
                Objects.Add(o);
            }

            if (Version >= 11)
            {
                int scriptCount = r.ReadInt32();
                for (int i = 0; i < scriptCount; i++)
                {
                    r.ReadInt32();
                    r.Align();
                    r.ReadInt64();
                }
            }

            int externalCount = r.ReadInt32();
            for (int i = 0; i < externalCount; i++)
            {
                if (Version >= 6) r.ReadStringToNull();
                if (Version >= 5) { r.Skip(16); r.ReadInt32(); }
                r.ReadStringToNull();
            }

            if (Version >= 20)
            {
                int refCount = r.ReadInt32();
                for (int i = 0; i < refCount; i++) RefTypes.Add(ReadType(r, true));
            }
        }

        SType ReadType(EndianReader r, bool isRefType)
        {
            var t = new SType();
            t.ClassId = r.ReadInt32();
            if (Version >= 16) t.IsStripped = r.ReadBool();
            if (Version >= 17) t.ScriptTypeIndex = r.ReadInt16();

            if (Version >= 13)
            {
                if ((isRefType && t.ScriptTypeIndex >= 0)
                    || (Version < 16 && t.ClassId < 0)
                    || (Version >= 16 && t.ClassId == 114))
                {
                    t.ScriptId = r.ReadBytes(16);
                }
                t.OldTypeHash = r.ReadBytes(16);
            }

            if (EnableTypeTree)
            {
                t.Node = TypeTreeNode.ParseBlob(r, Version);

                if (Version >= 21)
                {
                    if (isRefType)
                    {
                        t.ClassName = r.ReadStringToNull();
                        t.NameSpace = r.ReadStringToNull();
                        t.AssemblyName = r.ReadStringToNull();
                    }
                    else
                    {
                        int deps = r.ReadInt32();
                        r.Skip(deps * 4);
                    }
                }
            }

            return t;
        }

        /// <summary>Decodes one object into dictionaries/lists of plain values.</summary>
        public object ReadObject(ObjectInfo o)
        {
            byte[] data = bundle.Read(nodeOffset + o.ByteStart, o.ByteSize);
            var r = new EndianReader(data, BigEndian);
            var ctx = new TypeTreeReader.Context { File = this };
            return TypeTreeReader.ReadValue(o.Type.Node, r, ctx);
        }

        public Dictionary<string, object> ReadObjectDict(ObjectInfo o)
        {
            return ReadObject(o) as Dictionary<string, object>;
        }
    }
}
