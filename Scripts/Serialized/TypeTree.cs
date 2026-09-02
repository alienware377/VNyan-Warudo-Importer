using System;
using System.Collections.Generic;

namespace WarudoImporter.Serialized
{
    /// <summary>One node of a Unity type tree: a field, its type, and its children.</summary>
    internal sealed class TypeTreeNode
    {
        public int Version;
        public int Level;
        public int TypeFlags;
        public string Type;
        public string Name;
        public int ByteSize;
        public int Index;
        public int MetaFlag;
        public ulong RefTypeHash;
        public List<TypeTreeNode> Children = new List<TypeTreeNode>();

        public bool Aligned { get { return (MetaFlag & 0x4000) != 0; } }

        public override string ToString() { return Type + " " + Name; }

        /// <summary>
        /// Parses the flat "blob" type-tree layout (Unity 5.0+). Nodes arrive in
        /// depth-first order carrying an explicit level, so the tree is rebuilt with a stack.
        /// </summary>
        public static TypeTreeNode ParseBlob(EndianReader r, int fileVersion)
        {
            int nodeCount = r.ReadInt32();
            int stringBufferSize = r.ReadInt32();

            var flat = new TypeTreeNode[nodeCount];
            var typeOffsets = new uint[nodeCount];
            var nameOffsets = new uint[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                var nd = new TypeTreeNode();
                nd.Version = r.ReadUInt16();
                nd.Level = r.ReadByte();
                nd.TypeFlags = r.ReadByte();
                typeOffsets[i] = r.ReadUInt32();
                nameOffsets[i] = r.ReadUInt32();
                nd.ByteSize = r.ReadInt32();
                nd.Index = r.ReadInt32();
                nd.MetaFlag = r.ReadInt32();
                if (fileVersion >= 19) nd.RefTypeHash = r.ReadUInt64();
                flat[i] = nd;
            }

            byte[] strings = r.ReadBytes(stringBufferSize);
            var sr = new EndianReader(strings, false);
            for (int i = 0; i < nodeCount; i++)
            {
                flat[i].Type = ResolveString(sr, typeOffsets[i]);
                flat[i].Name = ResolveString(sr, nameOffsets[i]);
            }

            // Rebuild parent/child links from the level column.
            var root = new TypeTreeNode { Level = -1, Type = "", Name = "" };
            var stack = new List<TypeTreeNode> { root };
            TypeTreeNode parent = root, prev = root;

            for (int i = 0; i < nodeCount; i++)
            {
                var nd = flat[i];
                if (nd.Level > prev.Level)
                {
                    stack.Add(parent);
                    parent = prev;
                }
                else if (nd.Level < prev.Level)
                {
                    while (nd.Level <= parent.Level)
                    {
                        parent = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                parent.Children.Add(nd);
                prev = nd;
            }

            return root.Children.Count > 0 ? root.Children[0] : root;
        }

        static string ResolveString(EndianReader stringBuffer, uint value)
        {
            // Bit 31 clear means "offset into this file's string buffer";
            // set means "index into Unity's built-in common string table".
            if ((value & 0x80000000u) == 0)
            {
                stringBuffer.Position = (int)value;
                return stringBuffer.ReadStringToNull();
            }
            uint offset = value & 0x7FFFFFFFu;
            string s;
            if (CommonStrings.Table.TryGetValue(offset, out s)) return s;
            return offset.ToString();
        }
    }

    /// <summary>
    /// Turns a type tree plus a byte range into plain dictionaries, lists and boxed
    /// primitives. This mirrors Unity's own serialized layout rules, including the
    /// alignment quirks around arrays and the managed-reference registry.
    /// </summary>
    internal static class TypeTreeReader
    {
        internal sealed class Context
        {
            public SerializedAssetFile File;
            public bool HasRegistry;
            public Context Copy() { return new Context { File = File, HasRegistry = HasRegistry }; }
        }

        public static object ReadValue(TypeTreeNode node, EndianReader r, Context ctx)
        {
            bool align = node.Aligned;
            object value;

            switch (node.Type)
            {
                case "SInt8": value = r.ReadSByte(); break;
                case "UInt8":
                case "char": value = r.ReadByte(); break;
                case "short":
                case "SInt16": value = r.ReadInt16(); break;
                case "unsigned short":
                case "UInt16": value = r.ReadUInt16(); break;
                case "int":
                case "SInt32": value = r.ReadInt32(); break;
                case "unsigned int":
                case "UInt32":
                case "Type*": value = r.ReadUInt32(); break;
                case "long long":
                case "SInt64": value = r.ReadInt64(); break;
                case "unsigned long long":
                case "UInt64":
                case "FileSize": value = r.ReadUInt64(); break;
                case "float": value = r.ReadSingle(); break;
                case "double": value = r.ReadDouble(); break;
                case "bool": value = r.ReadBool(); break;
                case "string": value = r.ReadAlignedString(); break;
                case "TypelessData":
                {
                    int len = r.ReadInt32();
                    value = r.ReadBytes(len);
                    break;
                }
                case "pair":
                {
                    var first = ReadValue(node.Children[0], r, ctx);
                    var second = ReadValue(node.Children[1], r, ctx);
                    value = new object[] { first, second };
                    break;
                }
                case "ReferencedObject":
                    value = ReadReferencedObject(node, r, ctx);
                    break;
                default:
                    if (node.Children.Count > 0 && node.Children[0].Type == "Array")
                    {
                        var arrayNode = node.Children[0];
                        if (arrayNode.Aligned) align = true;

                        int size = r.ReadInt32();
                        if (size < 0) throw new InvalidOperationException("Negative array length in type tree");
                        var sub = arrayNode.Children[1];

                        if (sub.Aligned)
                        {
                            value = ReadValueArray(sub, r, ctx, size);
                        }
                        else
                        {
                            var list = new List<object>(size);
                            for (int i = 0; i < size; i++) list.Add(ReadValue(sub, r, ctx));
                            value = list;
                        }
                    }
                    else
                    {
                        var dict = new Dictionary<string, object>();
                        var childCtx = ctx;
                        foreach (var child in node.Children)
                        {
                            if (child.Type == "ManagedReferencesRegistry")
                            {
                                // A nested registry belongs to the outer object; read it once.
                                if (childCtx.HasRegistry) continue;
                                childCtx = childCtx.Copy();
                                childCtx.HasRegistry = true;
                            }
                            dict[child.Name] = ReadValue(child, r, childCtx);
                        }
                        value = dict;
                    }
                    break;
            }

            if (align) r.Align();
            return value;
        }

        static Dictionary<string, object> ReadReferencedObject(TypeTreeNode node, EndianReader r, Context ctx)
        {
            var item = new Dictionary<string, object>();
            foreach (var child in node.Children)
            {
                if (child.Type == "ReferencedObjectData")
                {
                    var refNode = ResolveRefTypeNode(item, ctx);
                    if (refNode == null) continue;
                    item[child.Name] = ReadValue(refNode, r, ctx);
                }
                else
                {
                    item[child.Name] = ReadValue(child, r, ctx);
                }
            }
            return item;
        }

        static TypeTreeNode ResolveRefTypeNode(Dictionary<string, object> item, Context ctx)
        {
            object typeObj;
            if (!item.TryGetValue("type", out typeObj)) return null;
            var t = typeObj as Dictionary<string, object>;
            if (t == null) return null;

            string cls = Str(t, "class"), ns = Str(t, "ns"), asm = Str(t, "asm");
            if (string.IsNullOrEmpty(cls)) return null;
            if (ctx.File == null) throw new InvalidOperationException("No serialized file for reference types");

            foreach (var rt in ctx.File.RefTypes)
                if (rt.ClassName == cls && rt.NameSpace == ns && rt.AssemblyName == asm)
                    return rt.Node;

            throw new InvalidOperationException("Referenced type not found: " + asm + " " + ns + "." + cls);
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        static object ReadValueArray(TypeTreeNode node, EndianReader r, Context ctx, int size)
        {
            bool align = node.Aligned;
            object value;

            switch (node.Type)
            {
                case "SInt8":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadSByte());
                    value = a; break;
                }
                case "UInt8":
                case "char":
                {
                    // Byte arrays are by far the most common bulk case.
                    value = r.ReadBytes(size);
                    break;
                }
                case "bool":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadBool());
                    value = a; break;
                }
                case "short":
                case "SInt16":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadInt16());
                    value = a; break;
                }
                case "unsigned short":
                case "UInt16":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadUInt16());
                    value = a; break;
                }
                case "int":
                case "SInt32":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadInt32());
                    value = a; break;
                }
                case "unsigned int":
                case "UInt32":
                case "Type*":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadUInt32());
                    value = a; break;
                }
                case "long long":
                case "SInt64":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadInt64());
                    value = a; break;
                }
                case "unsigned long long":
                case "UInt64":
                case "FileSize":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadUInt64());
                    value = a; break;
                }
                case "float":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadSingle());
                    value = a; break;
                }
                case "double":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadDouble());
                    value = a; break;
                }
                case "string":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(r.ReadAlignedString());
                    value = a; break;
                }
                case "pair":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++)
                    {
                        var first = ReadValue(node.Children[0], r, ctx);
                        var second = ReadValue(node.Children[1], r, ctx);
                        a.Add(new object[] { first, second });
                    }
                    value = a; break;
                }
                case "ReferencedObject":
                {
                    var a = new List<object>(size);
                    for (int i = 0; i < size; i++) a.Add(ReadReferencedObject(node, r, ctx));
                    value = a; break;
                }
                default:
                    if (node.Children.Count > 0 && node.Children[0].Type == "Array")
                    {
                        var arrayNode = node.Children[0];
                        if (arrayNode.Aligned) align = true;
                        var sub = arrayNode.Children[1];
                        var a = new List<object>(size);
                        for (int i = 0; i < size; i++)
                        {
                            int inner = r.ReadInt32();
                            if (sub.Aligned)
                            {
                                a.Add(ReadValueArray(sub, r, ctx, inner));
                            }
                            else
                            {
                                var l = new List<object>(inner);
                                for (int j = 0; j < inner; j++) l.Add(ReadValue(sub, r, ctx));
                                a.Add(l);
                            }
                        }
                        value = a;
                    }
                    else
                    {
                        var a = new List<object>(size);
                        for (int i = 0; i < size; i++)
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (var child in node.Children) dict[child.Name] = ReadValue(child, r, ctx);
                            a.Add(dict);
                        }
                        value = a;
                    }
                    break;
            }

            if (align) r.Align();
            return value;
        }
    }
}
