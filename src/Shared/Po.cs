// Minimal gettext .po reader shared by the patcher and the runtime.
//
// Entry kinds (selected by msgctxt):
//   (no msgctxt)            global string: replaced in the IL of RepoRPG.dll and also matched at display time
//   msgctxt "@Type::Member" scoped string: replaced only inside that member (for words that are also used as ids)
//   msgctxt "term"          display-time only: exact match in the UI helpers, the IL is left alone
//   msgctxt "data"          value inside the embedded JSON data (any file)
//   msgctxt "data:<stem>"   value inside one embedded JSON file, e.g. data:classes
using System;
using System.Collections.Generic;
using System.Text;

namespace RepoRPG_RU
{
    internal sealed class PoEntry
    {
        public string Ctx;
        public string Id;
        public string Str;
        public string File;
        public int Line;
    }

    internal static class Po
    {
        public static void Parse(string text, string file, List<PoEntry> output, List<string> problems)
        {
            string ctx = null, id = null, str = null;
            int target = 0; // 1 ctx, 2 id, 3 str, 0 ignore
            int entryLine = 0, n = 0;
            int pos = 0, len = text.Length;
            while (pos <= len)
            {
                int eol = text.IndexOf('\n', pos);
                if (eol < 0) eol = len;
                string line = text.Substring(pos, eol - pos).Trim();
                pos = eol + 1;
                n++;
                if (n == 1 && line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1).Trim();

                if (line.Length == 0)
                {
                    Flush(ref ctx, ref id, ref str, file, entryLine, output);
                    target = 0;
                    continue;
                }
                if (line[0] == '#') continue;

                if (line[0] == '"')
                {
                    string part = Unquote(line, file, n, problems);
                    if (target == 1) ctx += part;
                    else if (target == 2) id += part;
                    else if (target == 3) str += part;
                    continue;
                }
                if (line.StartsWith("msgctxt", StringComparison.Ordinal))
                {
                    if (target == 3) Flush(ref ctx, ref id, ref str, file, entryLine, output);
                    ctx = Unquote(line, file, n, problems);
                    target = 1;
                    entryLine = n;
                }
                else if (line.StartsWith("msgid_plural", StringComparison.Ordinal))
                {
                    target = 0;
                }
                else if (line.StartsWith("msgid", StringComparison.Ordinal))
                {
                    if (target == 3) Flush(ref ctx, ref id, ref str, file, entryLine, output);
                    id = Unquote(line, file, n, problems);
                    if (target != 1) entryLine = n;
                    target = 2;
                }
                else if (line.StartsWith("msgstr[", StringComparison.Ordinal))
                {
                    if (line.StartsWith("msgstr[0]", StringComparison.Ordinal)) { str = Unquote(line, file, n, problems); target = 3; }
                    else target = 0;
                }
                else if (line.StartsWith("msgstr", StringComparison.Ordinal))
                {
                    str = Unquote(line, file, n, problems);
                    target = 3;
                }
                else
                {
                    if (problems != null) problems.Add(file + ":" + n + ": unrecognised line");
                    target = 0;
                }
            }
            Flush(ref ctx, ref id, ref str, file, entryLine, output);
        }

        private static void Flush(ref string ctx, ref string id, ref string str, string file, int line, List<PoEntry> output)
        {
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(str))
            {
                PoEntry e = new PoEntry();
                e.Ctx = string.IsNullOrEmpty(ctx) ? null : ctx;
                e.Id = id;
                e.Str = str;
                e.File = file;
                e.Line = line;
                output.Add(e);
            }
            ctx = null; id = null; str = null;
        }

        private static string Unquote(string line, string file, int n, List<string> problems)
        {
            int a = line.IndexOf('"');
            int b = line.LastIndexOf('"');
            if (a < 0 || b <= a)
            {
                if (problems != null) problems.Add(file + ":" + n + ": missing quotes");
                return "";
            }
            StringBuilder sb = new StringBuilder(b - a);
            for (int i = a + 1; i < b; i++)
            {
                char c = line[i];
                if (c != '\\' || i + 1 >= b) { sb.Append(c); continue; }
                char d = line[++i];
                switch (d)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (i + 4 < b + 0 && IsHex4(line, i + 1))
                        {
                            sb.Append((char)Convert.ToInt32(line.Substring(i + 1, 4), 16));
                            i += 4;
                        }
                        else sb.Append('u');
                        break;
                    default: sb.Append(d); break; // \" \\ and anything else
                }
            }
            return sb.ToString();
        }

        private static bool IsHex4(string s, int at)
        {
            if (at + 4 > s.Length) return false;
            for (int i = at; i < at + 4; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }

    /// <summary>Keeps a broken translation from ever reaching string.Format.</summary>
    internal static class Fmt
    {
        /// <summary>
        /// Placeholders of a translation must be compatible with the original:
        ///   {0}, {1:0.#} ... (string.Format) - only placeholders the original has, written exactly the same way; any order,
        ///                                       repeated or left out (the English plural "{1}" in "{0} item{1}"): string.Format
        ///                                       ignores unused arguments but throws on an index it was not given;
        ///   {name}                            - the mod substitutes these itself with Replace(): a translation may drop one
        ///                                       (e.g. "{s}") but never invent one;
        ///   stray / doubled braces            - exactly the same.
        /// </summary>
        public static bool SameHoles(string a, string b)
        {
            if (a.IndexOf('{') < 0 && a.IndexOf('}') < 0 && b.IndexOf('{') < 0 && b.IndexOf('}') < 0) return true;
            List<string> x = Holes(a), y = Holes(b);
            HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
            List<string> xStray = new List<string>(), yStray = new List<string>();
            foreach (string h in x) { if (IsNamed(h) || FormatIndex(h) >= 0) known.Add(h); else xStray.Add(h); }
            foreach (string h in y)
            {
                if (IsNamed(h) || FormatIndex(h) >= 0) { if (!known.Contains(h)) return false; }
                else yStray.Add(h);
            }
            if (xStray.Count != yStray.Count) return false;
            xStray.Sort(string.CompareOrdinal);
            yStray.Sort(string.CompareOrdinal);
            for (int i = 0; i < xStray.Count; i++)
                if (!string.Equals(xStray[i], yStray[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// Texts of the mod's data carry tokens the mod fills in itself: "{key:menu}", "{cfg:Section/Key}", "{t}", and
        /// "{cfg:Section/Key?text when on:text when off}" - there the text inside is translated, the part before '?' is not.
        /// A translation must have the very same tokens (any order).
        /// </summary>
        public static bool SameTokens(string a, string b)
        {
            if (a.IndexOf('{') < 0 && a.IndexOf('}') < 0 && b.IndexOf('{') < 0 && b.IndexOf('}') < 0) return true;
            List<string> x = Holes(a), y = Holes(b);
            if (x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++) x[i] = TokenHead(x[i]);
            for (int i = 0; i < y.Count; i++) y[i] = TokenHead(y[i]);
            x.Sort(string.CompareOrdinal);
            y.Sort(string.CompareOrdinal);
            for (int i = 0; i < x.Count; i++)
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static string TokenHead(string hole)
        {
            int q = hole.IndexOf('?');
            return q > 0 && hole.IndexOf(':') > 0 && hole.IndexOf(':') < q ? hole.Substring(0, q) : hole;
        }

        private static bool IsNamed(string hole)
        {
            return hole.Length >= 3 && hole[0] == '{' && hole[hole.Length - 1] == '}'
                && ((hole[1] >= 'a' && hole[1] <= 'z') || (hole[1] >= 'A' && hole[1] <= 'Z'));
        }

        /// <summary>Index of a string.Format placeholder ("{2:0.0}" -> 2), -1 for anything else.</summary>
        public static int FormatIndex(string hole)
        {
            if (hole.Length < 3 || hole[0] != '{' || hole[hole.Length - 1] != '}') return -1;
            int i = 1, n = 0;
            if (hole[i] < '0' || hole[i] > '9') return -1;
            while (i < hole.Length - 1 && hole[i] >= '0' && hole[i] <= '9') { n = n * 10 + (hole[i] - '0'); i++; if (n > 9999) return -1; }
            char next = hole[i];
            return (next == '}' || next == ':' || next == ',') ? n : -1;
        }

        /// <summary>
        /// "{0|осколок|осколка|осколков}" -> 0: a word of the translation in the form the number in {0} asks for. Only a text
        /// that is put together at display time may have it (msgctxt "pattern"): string.Format knows nothing of the kind.
        /// -1 for anything else.
        /// </summary>
        public static int ChoiceIndex(string hole)
        {
            if (hole.Length < 9 || hole[0] != '{' || hole[hole.Length - 1] != '}') return -1;
            int i = 1, n = 0;
            if (hole[i] < '0' || hole[i] > '9') return -1;
            while (i < hole.Length - 1 && hole[i] >= '0' && hole[i] <= '9') { n = n * 10 + (hole[i] - '0'); i++; if (n > 9999) return -1; }
            if (hole[i] != '|') return -1;
            int bars = 0, run = 0;
            for (i++; i < hole.Length - 1; i++)
            {
                if (hole[i] == '|') { if (run == 0) return -1; bars++; run = 0; }
                else run++;
            }
            return bars == 2 && run > 0 ? n : -1;
        }

        /// <summary>
        /// A display-time translation without its "{0|one|few|many}" words - what is left has to pass <see cref="SameHoles"/>.
        /// Null when such a word counts by a placeholder the original does not have.
        /// </summary>
        public static string WithoutChoices(string original, string translation)
        {
            if (translation.IndexOf('|') < 0) return translation;
            HashSet<int> have = new HashSet<int>();
            foreach (string h in Holes(original)) { int k = FormatIndex(h); if (k >= 0) have.Add(k); }
            StringBuilder sb = new StringBuilder(translation.Length);
            int i = 0, n = translation.Length;
            while (i < n)
            {
                char c = translation[i];
                if ((c == '{' || c == '}') && i + 1 < n && translation[i + 1] == c) { sb.Append(c).Append(c); i += 2; continue; }     // "{{", "}}": a brace
                if (c == '{')
                {
                    int close = translation.IndexOf('}', i);
                    if (close > i)
                    {
                        string hole = translation.Substring(i, close - i + 1);
                        if (hole.IndexOf('|') > 0)
                        {
                            int k = ChoiceIndex(hole);
                            if (k < 0 || !have.Contains(k)) return null;
                            i = close + 1;
                            continue;
                        }
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        public static List<string> Holes(string s)
        {
            List<string> r = new List<string>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '{')
                {
                    if (i + 1 < n && s[i + 1] == '{') { r.Add("{{"); i += 2; continue; }
                    int j = i + 1;
                    while (j < n && s[j] != '}' && s[j] != '{') j++;
                    if (j < n && s[j] == '}') { r.Add(s.Substring(i, j - i + 1)); i = j + 1; continue; }
                    r.Add("{"); i++; continue;
                }
                if (c == '}')
                {
                    if (i + 1 < n && s[i + 1] == '}') { r.Add("}}"); i += 2; continue; }
                    r.Add("}"); i++; continue;
                }
                i++;
            }
            return r;
        }
    }
}
