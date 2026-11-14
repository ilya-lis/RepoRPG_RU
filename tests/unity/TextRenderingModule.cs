// Test double of UnityEngine.TextRenderingModule: Font, FontStyle and CharacterInfo with the signatures of Unity 6
// (UnityCsReference, branch 6000.3, Modules/TextRendering/TextRendering.bindings.cs). Never shipped.
//
// Like the real thing, a Font made from a path is a dynamic font: letters must be asked for before they can be measured,
// and a file that cannot be read does not make the constructor fail - the letters then silently come from a system font.
// The double reads real TrueType files (head, hhea, hmtx and the Windows Unicode cmap), so the widths it reports for
// the add-on's font are the ones written in that file. A "system font" is 0.70 em per letter, the width of Arial's capitals.
//
// FAKE_UNITY_ASCII_PATHS=1 makes it unable to open paths with letters outside ASCII (what a native fopen() does on Windows).
// FAKE_UNITY_NO_PATHS=1     makes it unable to read a font from any path.
// FAKE_UNITY_INSTALLED=<family>=<file>  is a font installed in the system.
using System;
using System.Collections.Generic;
using System.IO;

namespace UnityEngine
{
    public enum FontStyle
    {
        Normal = 0,
        Bold = 1,
        Italic = 2,
        BoldAndItalic = 3
    }

    public struct CharacterInfo
    {
        public int index;
        public float width;
        public int size;
        public FontStyle style;

        public int advance
        {
            get { return (int)Math.Round(width, MidpointRounding.AwayFromZero); }
            set { width = value; }
        }
    }

    public sealed class Font : Object
    {
        private readonly Dictionary<long, bool> requested = new Dictionary<long, bool>();
        private Dictionary<int, int> glyphs;     // letter -> glyph, from the file
        private int[] advances;
        private int unitsPerEm;

        public static readonly List<string> Opened = new List<string>();

        public bool dynamic { get { return true; } }
        public string[] fontNames { get; set; }
        public int fontSize { get { return 32; } }
        public int lineHeight { get { return (int)Math.Round(fontSize * (glyphs == null ? 1.15f : lineUnits / (float)unitsPerEm)); } }
        public int ascent { get { return (int)Math.Round(fontSize * (glyphs == null ? 0.905f : ascentUnits / (float)unitsPerEm)); } }
        private int lineUnits, ascentUnits;

        public Font() { name = "Arial"; fontNames = new string[] { "Arial" }; }

        public Font(string name)
        {
            // the same test the real constructor makes: a bare name is a font name, anything else a path
            bool isFileName = Path.GetDirectoryName(name) == string.Empty;
            if (isFileName) { this.name = name; fontNames = new string[] { name }; return; }
            this.name = Path.GetFileNameWithoutExtension(name);
            fontNames = new string[] { this.name };
            Opened.Add(name);
            try { Read(name); }
            catch { glyphs = null; }                // unreadable: a system font takes over, nobody is told
        }

        private Font(string[] names, int size)
        {
            name = names.Length > 0 ? names[0] : "Arial";
            fontNames = names;
            string installed = Environment.GetEnvironmentVariable("FAKE_UNITY_INSTALLED");
            int eq = installed == null ? -1 : installed.IndexOf('=');
            if (eq > 0 && names.Length > 0 && names[0] == installed.Substring(0, eq))
            {
                try { Parse(File.ReadAllBytes(installed.Substring(eq + 1))); }
                catch { glyphs = null; }
            }
        }

        public static Font CreateDynamicFontFromOSFont(string fontname, int size) { return new Font(new string[] { fontname }, size); }
        public static Font CreateDynamicFontFromOSFont(string[] fontnames, int size) { return new Font(fontnames, size); }

        public bool HasCharacter(char c) { return glyphs == null || glyphs.ContainsKey(c); }

        public void RequestCharactersInTexture(string characters, int size, FontStyle style)
        {
            foreach (char c in characters) requested[Key(c, size, style)] = true;
        }

        public bool GetCharacterInfo(char ch, out CharacterInfo info, int size, FontStyle style)
        {
            info = new CharacterInfo();
            if (!requested.ContainsKey(Key(ch, size, style))) return false;
            int glyph;
            float em = 0.70f;                                              // the system font
            if (glyphs != null && glyphs.TryGetValue(ch, out glyph)) em = advances[Math.Min(glyph, advances.Length - 1)] / (float)unitsPerEm;
            info.index = ch;
            info.size = size;
            info.style = style;
            info.width = em * size;
            return true;
        }

        private static long Key(char c, int size, FontStyle style) { return ((long)c << 32) | ((long)size << 4) | (long)style; }

        // ------------------------------------------------------------------ a small TrueType reader

        private void Read(string path)
        {
            if (Environment.GetEnvironmentVariable("FAKE_UNITY_NO_PATHS") == "1") throw new IOException("cannot open");
            if (Environment.GetEnvironmentVariable("FAKE_UNITY_ASCII_PATHS") == "1")
                foreach (char c in path) if (c > '~') throw new IOException("cannot open");
            Parse(File.ReadAllBytes(path));
        }

        private void Parse(byte[] d)
        {
            if (U32(d, 0) != 0x00010000) throw new IOException("not a TrueType file");
            int tables = U16(d, 4);
            int head = -1, hhea = -1, hmtx = -1, cmap = -1;
            for (int i = 0; i < tables; i++)
            {
                int rec = 12 + i * 16;
                string tag = "" + (char)d[rec] + (char)d[rec + 1] + (char)d[rec + 2] + (char)d[rec + 3];
                int offset = (int)U32(d, rec + 8);
                if (tag == "head") head = offset; else if (tag == "hhea") hhea = offset; else if (tag == "hmtx") hmtx = offset; else if (tag == "cmap") cmap = offset;
            }
            if (head < 0 || hhea < 0 || hmtx < 0 || cmap < 0) throw new IOException("tables missing");
            unitsPerEm = U16(d, head + 18);
            ascentUnits = (short)U16(d, hhea + 4);
            lineUnits = ascentUnits - (short)U16(d, hhea + 6) + (short)U16(d, hhea + 8);
            int metrics = U16(d, hhea + 34);
            advances = new int[metrics];
            for (int i = 0; i < metrics; i++) advances[i] = U16(d, hmtx + i * 4);

            int sub = -1;
            int count = U16(d, cmap + 2);
            for (int i = 0; i < count; i++)
            {
                int rec = cmap + 4 + i * 8;
                if (U16(d, rec) == 3 && U16(d, rec + 2) == 1) sub = cmap + (int)U32(d, rec + 4);     // Windows, Unicode BMP
            }
            if (sub < 0 || U16(d, sub) != 4) throw new IOException("no Windows Unicode cmap of format 4");
            int seg = U16(d, sub + 6) / 2;
            int ends = sub + 14, starts = ends + seg * 2 + 2, deltas = starts + seg * 2, ranges = deltas + seg * 2;
            Dictionary<int, int> map = new Dictionary<int, int>();
            for (int s = 0; s < seg; s++)
            {
                int end = U16(d, ends + s * 2), start = U16(d, starts + s * 2), delta = U16(d, deltas + s * 2), range = U16(d, ranges + s * 2);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int g;
                    if (range == 0) g = (c + delta) & 0xFFFF;
                    else
                    {
                        g = U16(d, ranges + s * 2 + range + (c - start) * 2);
                        if (g != 0) g = (g + delta) & 0xFFFF;
                    }
                    if (g != 0) map[c] = g;
                }
            }
            glyphs = map;
        }

        private static int U16(byte[] d, int at) { return (d[at] << 8) | d[at + 1]; }
        private static uint U32(byte[] d, int at) { return ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3]; }
    }
}
