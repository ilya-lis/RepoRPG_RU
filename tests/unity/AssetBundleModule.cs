// Test double of UnityEngine.AssetBundleModule: AssetBundle with the signatures of Unity 6 (UnityCsReference, branch 6000.3,
// Modules/AssetBundle/Managed/AssetBundle.bindings.cs). Never shipped.
//
// It reads a real bundle file - the container (UnityFS, format 6) and the serialized file inside it (version 17, the one
// Unity 2017.1 writes) - with a reader of its own, written from the description of the format and not from the tool that
// packs the add-on's font (tools/mkbundle.py): a bundle the two disagree about fails here. Only what the add-on's bundle
// needs is understood: uncompressed data, objects of the classes Font and Material and the table of contents.
// Stricter than the engine in one thing: a font whose material or texture is not in the file, or a material whose shader
// is not the engine's built-in font shader, makes the whole bundle unreadable here (the engine would load such a font
// and draw nothing with it) - so a bundle with a loose end never passes the tests.
//
// Like the real thing:
//   * a bundle that cannot be read makes LoadFromMemory return null (the engine writes an error to its own log);
//   * a bundle whose inner file is loaded already is refused, until the first one is unloaded;
//   * Unload(false) lets the bundle go and keeps the objects made from it.
//
// FAKE_UNITY_NO_BUNDLES=1  the game takes no bundle at all.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityEngine
{
    public class AssetBundle : Object
    {
        /// <summary>What the engine would have written to its log.</summary>
        public static readonly List<string> Errors = new List<string>();
        private static readonly HashSet<string> LoadedFiles = new HashSet<string>(StringComparer.Ordinal);

        private string innerFile;
        private readonly List<FontRecord> fonts = new List<FontRecord>();
        private readonly List<string> assetNames = new List<string>();
        private readonly List<Object> made = new List<Object>();
        private bool unloaded;

        private sealed class FontRecord
        {
            public string Name;
            public byte[] Data;
            public string[] Names;
            public bool Dynamic;
            public float Size, Line, Ascent;
            public Pointer Material, Texture;
        }

        /// <summary>A reference to another object: 0 is this file, N the N-th file of the list at the end of the description.</summary>
        private struct Pointer
        {
            public int File;
            public long Id;
        }

        private const int ClassMaterial = 21, ClassTexture = 28, ClassFont = 128, ClassBundle = 142;
        private const long BuiltinFontShader = 10101;           // "GUI/Text Shader" in the engine's default resources
        private readonly Dictionary<long, int> classOf = new Dictionary<long, int>();        // every object of the file
        private readonly Dictionary<long, Pointer> shaderOf = new Dictionary<long, Pointer>();   // material -> its shader
        private readonly List<Pointer> listed = new List<Pointer>();                           // what the table of contents names

        private AssetBundle() { }

        public static AssetBundle LoadFromMemory(byte[] binary) { return LoadFromMemory(binary, 0); }

        public static AssetBundle LoadFromMemory(byte[] binary, uint crc)
        {
            if (Environment.GetEnvironmentVariable("FAKE_UNITY_NO_BUNDLES") == "1") { Errors.Add("Failed to read data for the AssetBundle 'Memory'."); return null; }
            AssetBundle bundle = new AssetBundle();
            try { bundle.Read(binary); }
            catch (Exception e)
            {
                Errors.Add("Failed to read data for the AssetBundle 'Memory'. (" + e.Message + ")");
                bundle.Gone();
                return null;
            }
            if (!LoadedFiles.Add(bundle.innerFile))
            {
                Errors.Add("The AssetBundle 'Memory' can't be loaded because another AssetBundle with the same files is already loaded.");
                bundle.Gone();
                return null;
            }
            return bundle;
        }

        public Object[] LoadAllAssets(Type type)
        {
            if (unloaded) throw new InvalidOperationException("the bundle is unloaded");
            List<Object> list = new List<Object>();
            if (type.IsAssignableFrom(typeof(Font)))
            {
                foreach (FontRecord r in fonts)
                {
                    Font f = new Font(r.Name, r.Data, r.Names, r.Dynamic, r.Size, r.Line, r.Ascent);
                    made.Add(f);
                    list.Add(f);
                }
            }
            return list.ToArray();
        }

        public string[] GetAllAssetNames() { return assetNames.ToArray(); }

        public void Unload(bool unloadAllLoadedObjects)
        {
            if (unloaded) return;
            unloaded = true;
            LoadedFiles.Remove(innerFile);
            if (unloadAllLoadedObjects) foreach (Object o in made) Destroy(o);
            Gone();
        }

        // ------------------------------------------------------------------ UnityFS, format 6

        private void Read(byte[] d)
        {
            int p = 0;
            if (CString(d, ref p) != "UnityFS") throw new IOException("not a UnityFS bundle");
            uint format = BE32(d, p); p += 4;
            if (format != 6) throw new IOException("bundle format " + format);
            CString(d, ref p);                                   // the oldest player that reads it
            CString(d, ref p);                                   // the engine that wrote it
            long total = (long)(((ulong)BE32(d, p) << 32) | BE32(d, p + 4)); p += 8;
            uint infoPacked = BE32(d, p), infoSize = BE32(d, p + 4), flags = BE32(d, p + 8); p += 12;
            if (total != d.Length) throw new IOException("the bundle says it is " + total + " bytes, it is " + d.Length);
            if ((flags & 0x3F) != 0 || infoPacked != infoSize) throw new IOException("compressed table of blocks: not for this test double");
            if ((flags & 0x40) == 0) throw new IOException("no directory");
            int info = (flags & 0x80) != 0 ? d.Length - (int)infoPacked : p;
            int data = (flags & 0x80) != 0 ? p : p + (int)infoPacked;
            int q = info + 16;                                  // a hash nobody checks
            int blocks = (int)BE32(d, q); q += 4;
            long stored = 0;
            for (int i = 0; i < blocks; i++)
            {
                uint plain = BE32(d, q), packed = BE32(d, q + 4);
                int blockFlags = (d[q + 8] << 8) | d[q + 9];
                q += 10;
                if ((blockFlags & 0x3F) != 0 || plain != packed) throw new IOException("compressed data: not for this test double");
                stored += packed;
            }
            int nodes = (int)BE32(d, q); q += 4;
            if (nodes != 1) throw new IOException(nodes + " files inside");
            long offset = (long)(((ulong)BE32(d, q) << 32) | BE32(d, q + 4)), size = (long)(((ulong)BE32(d, q + 8) << 32) | BE32(d, q + 12));
            q += 20;
            innerFile = CString(d, ref q);
            if (q != info + (int)infoSize) throw new IOException("the table of blocks is " + infoSize + " bytes, " + (q - info) + " were read");
            if (offset != 0 || size != stored || data + stored > d.Length) throw new IOException("the data does not fit the file");
            byte[] file = new byte[size];
            Buffer.BlockCopy(d, data, file, 0, (int)size);
            ReadSerialized(file);
        }

        // ------------------------------------------------------------------ a serialized file of version 17

        private void ReadSerialized(byte[] f)
        {
            uint metadata = BE32(f, 0), fileSize = BE32(f, 4), version = BE32(f, 8), dataOffset = BE32(f, 12);
            if (version != 17) throw new IOException("serialized file version " + version);
            if (fileSize != f.Length) throw new IOException("the file inside says it is " + fileSize + " bytes, it is " + f.Length);
            if (f[16] != 0) throw new IOException("big-endian data");
            int p = 20;
            CString(f, ref p);                                   // "2017.1.0f3"
            p += 4;                                              // the platform
            bool typeTrees = f[p++] != 0;
            int typeCount = LE32(f, p); p += 4;
            int[] classes = new int[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                classes[i] = LE32(f, p); p += 4;
                p += 1 + 2;                                      // stripped, script index
                if (classes[i] == 114) p += 16;
                p += 16;
                if (typeTrees)
                {
                    int nodes = LE32(f, p), strings = LE32(f, p + 4);
                    p += 8 + nodes * 24 + strings;
                }
            }
            int objects = LE32(f, p); p += 4;
            p = (p + 3) & ~3;
            for (int i = 0; i < objects; i++)
            {
                long id = LE64(f, p);
                uint start = (uint)LE32(f, p + 8), size = (uint)LE32(f, p + 12);
                int type = LE32(f, p + 16);
                p += 20;
                if (type < 0 || type >= typeCount) throw new IOException("an object of an unknown type");
                if (start % 8 != 0 || dataOffset + start + size > f.Length) throw new IOException("an object out of place");
                if (classOf.ContainsKey(id)) throw new IOException("two objects with one id");
                classOf[id] = classes[type];
                int at = (int)(dataOffset + start);
                if (classes[type] == ClassFont) fonts.Add(ReadFont(f, at, (int)size));
                else if (classes[type] == ClassMaterial) shaderOf[id] = ReadMaterial(f, at);
                else if (classes[type] == ClassBundle) ReadContents(f, at);
            }
            int scripts = LE32(f, p); p += 4;
            if (scripts != 0) throw new IOException("scripts in the bundle");
            int externals = LE32(f, p); p += 4;
            List<string> files = new List<string>();
            for (int i = 0; i < externals; i++)
            {
                CString(f, ref p);
                p += 16 + 4;
                files.Add(CString(f, ref p));
            }
            CString(f, ref p);
            if (p != metadata + 20) throw new IOException("the description of the file is " + metadata + " bytes, " + (p - 20) + " were read");

            // every reference must lead somewhere
            foreach (FontRecord r in fonts)
            {
                if (r.Material.File != 0 || !IsA(r.Material.Id, ClassMaterial)) throw new IOException("the font '" + r.Name + "' has no material in the bundle");
                if (r.Texture.File != 0 || !IsA(r.Texture.Id, ClassTexture)) throw new IOException("the font '" + r.Name + "' has no texture in the bundle");
                Pointer shader = shaderOf[r.Material.Id];
                if (shader.File < 1 || shader.File > files.Count || !files[shader.File - 1].EndsWith("unity default resources", StringComparison.OrdinalIgnoreCase)
                    || shader.Id != BuiltinFontShader)
                    throw new IOException("the material of the font '" + r.Name + "' does not use the engine's font shader");
            }
            foreach (Pointer l in listed)
            {
                if (l.File == 0 ? !classOf.ContainsKey(l.Id) : l.File > files.Count) throw new IOException("the table of contents names an object that is not there");
            }
        }

        private bool IsA(long id, int cls)
        {
            int found;
            return classOf.TryGetValue(id, out found) && found == cls;
        }

        /// <summary>class 21: only the shader, which follows the name.</summary>
        private static Pointer ReadMaterial(byte[] f, int at)
        {
            int p = at;
            Text(f, ref p);
            return Ref(f, ref p);
        }

        private static Pointer Ref(byte[] f, ref int p)
        {
            Pointer r = new Pointer();
            r.File = LE32(f, p);
            r.Id = LE64(f, p + 4);
            p += 12;
            return r;
        }

        /// <summary>class 128, the fields in the order of Unity 2017.1.</summary>
        private static FontRecord ReadFont(byte[] f, int at, int size)
        {
            int p = at;
            FontRecord r = new FontRecord();
            r.Name = Text(f, ref p);
            r.Line = Single(f, ref p);
            r.Material = Ref(f, ref p);
            r.Size = Single(f, ref p);
            r.Texture = Ref(f, ref p);
            p += 4 + 4 + 4 + 4;                                  // first character, tracking, spacing, padding
            r.Dynamic = LE32(f, p) == -2; p += 4;
            if (LE32(f, p) != 0 || LE32(f, p + 4) != 0) throw new IOException("a font with ready-made characters");
            p += 8;
            p += 4;                                              // pixel scale
            int length = LE32(f, p); p += 4;
            if (length < 0 || p + length > at + size) throw new IOException("the font data runs out of its object");
            r.Data = new byte[length];
            Buffer.BlockCopy(f, p, r.Data, 0, length);
            p = (p + length + 3) & ~3;
            r.Ascent = Single(f, ref p);
            p += 4 + 4;                                          // descent, default style
            int names = LE32(f, p); p += 4;
            r.Names = new string[names];
            for (int i = 0; i < names; i++) r.Names[i] = Text(f, ref p);
            int fallbacks = LE32(f, p); p += 4 + fallbacks * 12;
            p += 4;                                              // rendering mode
            if (p != at + size) throw new IOException("the font object is " + size + " bytes, " + (p - at) + " were read");
            return r;
        }

        /// <summary>class 142: the name, what to load first, and the paths of the assets.</summary>
        private void ReadContents(byte[] f, int at)
        {
            int p = at;
            Text(f, ref p);
            int preload = LE32(f, p); p += 4;
            for (int i = 0; i < preload; i++) listed.Add(Ref(f, ref p));
            int entries = LE32(f, p); p += 4;
            for (int i = 0; i < entries; i++)
            {
                string path = Text(f, ref p);
                int first = LE32(f, p), count = LE32(f, p + 4);
                p += 8;
                listed.Add(Ref(f, ref p));
                if (first < 0 || count < 0 || first + count > preload) throw new IOException("the table of contents points outside the preload table");
                if (!assetNames.Contains(path)) assetNames.Add(path);
            }
        }

        private static string Text(byte[] f, ref int p)
        {
            int length = LE32(f, p); p += 4;
            if (length < 0 || p + length > f.Length) throw new IOException("a text out of place");
            string s = Encoding.UTF8.GetString(f, p, length);
            p = (p + length + 3) & ~3;
            return s;
        }

        private static float Single(byte[] f, ref int p) { float v = BitConverter.ToSingle(f, p); p += 4; return v; }
        private static int LE32(byte[] d, int at) { return d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24); }
        private static long LE64(byte[] d, int at) { return (long)(((ulong)(uint)LE32(d, at + 4) << 32) | (uint)LE32(d, at)); }
        private static uint BE32(byte[] d, int at) { return ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3]; }

        private static string CString(byte[] d, ref int p)
        {
            int end = p;
            while (d[end] != 0) end++;
            string s = Encoding.UTF8.GetString(d, p, end - p);
            p = end + 1;
            return s;
        }
    }
}
