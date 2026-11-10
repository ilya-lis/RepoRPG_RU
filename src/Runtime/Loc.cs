// RepoRPG_RU.dll - the display-time half of the translation.
//
// The translated copy of RepoRPG.dll calls Loc.T(text) at the top of the mod's UI helpers (Label, Btn, Toast...).
// It is an exact-match lookup: words the mod also uses as internal ids ("Class", "Shop", setting keys) cannot be
// replaced inside the code, but they can be swapped safely at the very last moment before they are measured and drawn.
//
// Deliberately plain: no Unity, no BepInEx, no Harmony references - nothing here can break when the game updates.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: AssemblyTitle("RepoRPG RU - runtime")]
[assembly: AssemblyVersion("0.1.0.0")]

namespace RepoRPG_RU
{
    public static class Loc
    {
        private static readonly object Gate = new object();
        private static volatile bool ready;
        private static Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        private static int minLength = int.MaxValue, maxLength = -1;

        // second chances for texts the mod puts together itself (see Slow)
        private static bool fallbacks;
        private static Dictionary<string, CaseEntry> upper;
        private static Dictionary<string, Pattern[]> byHead, byTail;
        private static Pattern[] loose;
        private static KeyValuePair<string, string>[] prefixes = new KeyValuePair<string, string>[0], suffixes = new KeyValuePair<string, string>[0];
        private static Dictionary<string, string> memo;
        private const int MemoLimit = 6000;

        private static bool logging;
        private static HashSet<string> seen;
        private static List<string> pending;
        private static string logPath;
        private static int nextFlush;
        private const int MaxLogged = 4000;

        /// <summary>Number of loaded entries (for diagnostics).</summary>
        public static int Count { get { Init(); return map.Count; } }

        /// <summary>Returns the translation of <paramref name="text"/> or the text itself. Never throws.</summary>
        public static string T(string text)
        {
            if (text == null) return null;
            int n = text.Length;
            if (n == 0) return text;
            if (!ready) Init();
            if (n >= minLength && n <= maxLength)
            {
                string translated;
                if (map.TryGetValue(text, out translated)) return translated;
            }
            if (!fallbacks) return text;
            try { return Slow(text); }
            catch { fallbacks = false; return text; }
        }

        /// <summary>
        /// Texts that are not in the dictionary letter for letter:
        ///   "EQUIPPED"                 - the mod changed the case of a known text;
        ///   "<b>Shop</b>"              - a known text wrapped in rich text tags;
        ///   "Taken by Alice"           - a known pattern ("Taken by {0}") filled in by the mod, e.g. a notice from another player;
        ///   "Gear\nPerks"              - several known texts glued together (lines, columns, "Label: value");
        ///   "> BUY", "Shop:"           - a known text with punctuation around it.
        /// Every distinct text is worked out once and remembered. Nothing here can affect the mod's logic:
        /// it only changes what is about to be measured and drawn.
        /// </summary>
        private static string Slow(string text)
        {
            if (!HasLatin(text)) return text;      // already Russian, or just numbers: nothing to do, nothing to remember
            string result;
            lock (Gate)
            {
                if (memo.TryGetValue(text, out result)) return result ?? text;
            }
            result = Derive(text, 0);
            lock (Gate)
            {
                if (memo.Count >= MemoLimit) memo.Clear();   // timers and counters produce endless variants
                memo[text] = result;
                if (result == null && logging) Note(text);
            }
            return result ?? text;
        }

        private const int MaxDepth = 3;

        /// <summary>A part of a bigger text: same work, same memory.</summary>
        private static string Sub(string text, int depth)
        {
            if (text.Length < 2 || !HasLatin(text)) return null;
            string result;
            lock (Gate)
            {
                if (memo.TryGetValue(text, out result)) return result;
            }
            result = Derive(text, depth);
            lock (Gate)
            {
                if (memo.Count < MemoLimit) memo[text] = result;
            }
            return result;
        }

        private static string Derive(string text, int depth)
        {
            string r;
            if (map.TryGetValue(text, out r)) return r;
            if ((r = ByCase(text)) != null) return r;
            if (depth >= MaxDepth) return null;

            // a whole sentence the mod filled in: the most exact answer there is
            if (text.Length >= 2)
            {
                Pattern[] bucket;
                if (byHead.TryGetValue(text.Substring(0, 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                if (byTail.TryGetValue(text.Substring(text.Length - 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                if (loose.Length > 0 && (r = Try(loose, text, depth)) != null) return r;
            }

            // rich text wrapper: <color=#fff><b>TEXT</b></color>
            int head = 0, tail = text.Length;
            while (head < tail && text[head] == '<')
            {
                int close = text.IndexOf('>', head);
                if (close < 0 || close - head > 40 || text[head + 1] == '/') break;
                head = close + 1;
            }
            while (tail > head && text[tail - 1] == '>')
            {
                int open = text.LastIndexOf('<', tail - 1);
                if (open < head || tail - open > 40 || open + 1 >= tail || text[open + 1] != '/') break;
                tail = open;
            }
            if ((head > 0 || tail < text.Length) && tail > head)
            {
                string core = Sub(text.Substring(head, tail - head), depth + 1);
                if (core != null) return text.Substring(0, head) + core + text.Substring(tail);
            }

            // texts glued together by the mod
            if ((r = Parts(text, "\n", false, depth)) != null) return r;
            if ((r = Edge(text, depth)) != null) return r;
            if ((r = Runs(text, depth)) != null) return r;
            if ((r = Parts(text, "  \u00B7  ", false, depth)) != null) return r;
            if ((r = Parts(text, " \u00B7 ", false, depth)) != null) return r;
            if ((r = Parts(text, " | ", false, depth)) != null) return r;
            if ((r = Parts(text, " + ", false, depth)) != null) return r;
            if ((r = Parts(text, " - ", false, depth)) != null) return r;
            if ((r = Parts(text, "   ", false, depth)) != null) return r;
            if ((r = Parts(text, ": ", true, depth)) != null) return r;
            if ((r = Parts(text, ", ", false, depth)) != null) return r;
            if ((r = Peel(text, depth)) != null) return r;
            return Islands(text, depth);
        }

        /// <summary>
        /// Last resort: "Куплено: Iron Plate (Rare)" - known English texts sitting between punctuation or inside a
        /// sentence that is already Russian (a name from the mod's data, a word the code had to keep).
        /// </summary>
        private static string Islands(string text, int depth)
        {
            StringBuilder sb = null;
            int n = text.Length, i = 0, copied = 0;
            while (i < n)
            {
                char c = text[i];
                if (c == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close > i && close - i <= 40) { i = close + 1; continue; }      // a rich text tag is not a word
                }
                if (!IsLatinLetter(c)) { i++; continue; }
                int start = i, end = i + 1, j = i + 1;
                while (j < n)
                {
                    char d = text[j];
                    if (IsLatinLetter(d) || (d >= '0' && d <= '9')) { end = j + 1; j++; continue; }
                    if (d == ' ' || d == '\'' || d == '-' || d == '/' || d == '&') { j++; continue; }
                    break;
                }
                // the longest stretch of whole words that is known wins: "Scrap 20" -> "Scrap", "Iron Plate of Doom" -> all of it
                int ws = start;
                while (ws < end)
                {
                    if (!IsLatinLetter(text[ws])) { ws++; continue; }
                    int firstEnd = -1, tries = 0;
                    int[] ends = new int[6];
                    for (int k = ws + 1; k <= end && tries < ends.Length; k++)
                    {
                        bool wordEnd = k == end || !(IsLatinLetter(text[k]) || (text[k] >= '0' && text[k] <= '9'));
                        bool afterWord = IsLatinLetter(text[k - 1]) || (text[k - 1] >= '0' && text[k - 1] <= '9');
                        if (wordEnd && afterWord) { ends[tries++] = k; if (firstEnd < 0) firstEnd = k; }
                    }
                    bool done = false;
                    for (int k = tries - 1; k >= 0 && !done; k--)
                    {
                        int e = ends[k];
                        if (e - ws < 2 || (ws == 0 && e == n)) continue;
                        string t = Sub(text.Substring(ws, e - ws), depth + 1);
                        if (t == null) continue;
                        if (sb == null) sb = new StringBuilder(n + 16);
                        sb.Append(text, copied, ws - copied).Append(t);
                        copied = e; ws = e; done = true;
                    }
                    if (!done) ws = firstEnd < 0 ? end : firstEnd;
                }
                i = end;
            }
            if (sb == null) return null;
            sb.Append(text, copied, n - copied);
            return sb.ToString();
        }

        private static bool IsLatinLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        /// <summary>"Rally Cry (copy)", "press E": a known beginning or ending (an entry that starts / ends with a space) and the rest.</summary>
        private static string Edge(string text, int depth)
        {
            for (int i = 0; i < suffixes.Length; i++)
            {
                string k = suffixes[i].Key;
                if (text.Length <= k.Length || string.CompareOrdinal(text, text.Length - k.Length, k, 0, k.Length) != 0) continue;
                string rest = text.Substring(0, text.Length - k.Length);
                return (Sub(rest, depth + 1) ?? rest) + suffixes[i].Value;
            }
            for (int i = 0; i < prefixes.Length; i++)
            {
                string k = prefixes[i].Key;
                if (text.Length <= k.Length || string.CompareOrdinal(text, 0, k, 0, k.Length) != 0) continue;
                string rest = text.Substring(k.Length);
                return prefixes[i].Value + (Sub(rest, depth + 1) ?? rest);
            }
            return null;
        }

        /// <summary>"A{sep}B{sep}C": every part on its own; null unless at least one part is known.</summary>
        private static string Parts(string text, string sep, bool firstOnly, int depth)
        {
            int at = text.IndexOf(sep, StringComparison.Ordinal);
            if (at < 0) return null;
            StringBuilder sb = null;
            int start = 0;
            bool any = false;
            while (true)
            {
                int end = at < 0 ? text.Length : at;
                string part = text.Substring(start, end - start);
                string t = Sub(part, depth + 1);
                if (t != null) any = true;
                if (sb == null) sb = new StringBuilder(text.Length + 16);
                sb.Append(t ?? part);
                if (at < 0) break;
                sb.Append(sep);
                start = at + sep.Length;
                at = firstOnly ? -1 : text.IndexOf(sep, start, StringComparison.Ordinal);
            }
            return any ? sb.ToString() : null;
        }

        /// <summary>"<b>Shop</b>  <color=#999>closed</color>": the text between rich text tags, run by run.</summary>
        private static string Runs(string text, int depth)
        {
            int lt = text.IndexOf('<');
            if (lt < 0) return null;
            StringBuilder sb = new StringBuilder(text.Length + 16);
            int start = 0, i = lt, tags = 0;
            bool any = false;
            while (i >= 0 && i < text.Length)
            {
                int close = text.IndexOf('>', i);
                bool tag = close > i && close - i <= 40 && i + 1 < text.Length
                           && (text[i + 1] == '/' || (text[i + 1] >= 'a' && text[i + 1] <= 'z') || (text[i + 1] >= 'A' && text[i + 1] <= 'Z'))
                           && text.IndexOf('<', i + 1, close - i - 1) < 0;
                if (!tag) { i = text.IndexOf('<', i + 1); continue; }
                if (i > start)
                {
                    string run = text.Substring(start, i - start);
                    string t = Sub(run, depth + 1);
                    if (t != null) any = true;
                    sb.Append(t ?? run);
                }
                sb.Append(text, i, close - i + 1);
                tags++;
                start = close + 1;
                i = text.IndexOf('<', start);
            }
            if (tags == 0) return null;
            if (start < text.Length)
            {
                string run = text.Substring(start);
                string t = Sub(run, depth + 1);
                if (t != null) any = true;
                sb.Append(t ?? run);
            }
            return any ? sb.ToString() : null;
        }

        /// <summary>"> BUY", "Shop:", "(equipped)", "  Gear  ".</summary>
        private static string Peel(string text, int depth)
        {
            int head = 0, tail = text.Length;
            if (tail > 2 && text[0] == '>' && text[1] == ' ') head = 2;        // "> BUY": the focused button
            while (head < tail && Peelable(text[head])) head++;
            while (tail > head && Peelable(text[tail - 1])) tail--;
            if ((head == 0 && tail == text.Length) || tail - head < 2) return null;
            string core = Sub(text.Substring(head, tail - head), depth + 1);
            return core == null ? null : text.Substring(0, head) + core + text.Substring(tail);
        }

        private static bool Peelable(char c)
        {
            if (c <= ' ') return true;
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '<' || c == '>' || c == '{' || c == '}' || c == '%') return false;
            if (c < 0x80) return true;                       // ASCII punctuation
            return c == '\u2022' || c == '\u00B7' || c == '\u2026' || c == '\u2192' || c == '\u2190' || c == '\u2191' || c == '\u2193'
                || c == '\u25B6' || c == '\u25B8' || c == '\u25BA' || c == '\u2605' || c == '\u2013' || c == '\u2014' || c == '\u00D7'
                || c == '\u2018' || c == '\u2019' || c == '\u201C' || c == '\u201D' || c == '\u00A0';
        }

        private sealed class CaseEntry
        {
            public string Key, Value;
        }

        /// <summary>The same text in another case: "EQUIPPED" / "equipped" for the entry "Equipped".</summary>
        private static string ByCase(string text)
        {
            if (text.Length > 120) return null;
            string u = text.ToUpperInvariant();
            CaseEntry e;
            if (!upper.TryGetValue(u, out e)) return null;
            if (string.Equals(text, u, StringComparison.Ordinal)) return Upper(e.Value);               // SHOUTED by the mod
            bool keyUpper = string.Equals(e.Key, u, StringComparison.Ordinal);
            if (string.Equals(text, text.ToLowerInvariant(), StringComparison.Ordinal))
                return keyUpper ? Lower(e.Value) : LowerFirst(e.Value);
            return keyUpper ? Sentence(e.Value) : e.Value;
        }

        private static string LowerFirst(string s)
        {
            if (s.Length == 0) return s;
            if (s.Length > 1 && IsUpperLetter(s[1])) return s;        // an abbreviation: leave it alone
            string first = Lower(s.Substring(0, 1));
            return first + s.Substring(1);
        }

        private static string Sentence(string s)
        {
            if (s.Length == 0) return s;
            return Upper(s.Substring(0, 1)) + Lower(s.Substring(1));
        }

        private static bool IsUpperLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= '\u0410' && c <= '\u042F') || c == '\u0401';
        }

        /// <summary>Lower case that does not depend on the casing tables of the runtime it happens to run on.</summary>
        private static string Lower(string s)
        {
            char[] a = s.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < a.Length; i++)
            {
                char c = a[i];
                if (c >= '\u0410' && c <= '\u042F') a[i] = (char)(c + 0x20);        // А-Я
                else if (c >= '\u0400' && c <= '\u040F') a[i] = (char)(c + 0x50);   // Ё and friends
            }
            return new string(a);
        }

        private static string Try(Pattern[] bucket, string text, int depth)
        {
            for (int i = 0; i < bucket.Length; i++)
            {
                string r = bucket[i].Apply(text, depth);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Upper case that does not depend on the casing tables of the runtime it happens to run on.</summary>
        private static string Upper(string s)
        {
            char[] a = s.ToUpperInvariant().ToCharArray();
            for (int i = 0; i < a.Length; i++)
            {
                char c = a[i];
                if (c >= '\u0430' && c <= '\u044F') a[i] = (char)(c - 0x20);        // а-я
                else if (c >= '\u0450' && c <= '\u045F') a[i] = (char)(c - 0x50);   // ё and friends
            }
            return new string(a);
        }

        private static bool HasLatin(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
            }
            return false;
        }

        /// <summary>"Bought {0} for {1}" as literal pieces around the placeholders.</summary>
        private sealed class Pattern
        {
            public string[] Lits;      // Lits.Length == Slots.Length + 1
            public int[] Slots;        // string.Format index of every placeholder, in order of appearance
            public string Template;    // the translation
            public int Weight;         // literal characters: the more, the more specific
            public int[] Ghosts;       // "{1}{2}": the first placeholder takes the whole text, the others come out empty

            public static Pattern Parse(string original, string translation, bool explicitPattern)
            {
                List<string> lits = new List<string>();
                List<int> slots = new List<int>();
                List<int> ghosts = null;
                string lastHole = null;
                StringBuilder lit = new StringBuilder();
                int letters = 0;
                for (int i = 0; i < original.Length; i++)
                {
                    char c = original[i];
                    if (c == '{')
                    {
                        if (i + 1 < original.Length && original[i + 1] == '{') { lit.Append('{'); i++; lastHole = null; continue; }
                        int close = original.IndexOf('}', i);
                        if (close < 0) return null;
                        string hole = original.Substring(i, close - i + 1);
                        int index = Fmt.FormatIndex(hole);
                        if (index < 0) return null;                       // {name}: not a string.Format text
                        if (lastHole != null && lit.Length == 0)
                        {
                            // "{0}{1}": nobody can tell where one ends - fine as long as the translation keeps them together
                            if (translation.IndexOf(lastHole + hole, StringComparison.Ordinal) < 0 || slots.Contains(index)) return null;
                            if (ghosts == null) ghosts = new List<int>();
                            ghosts.Add(index);
                            lastHole = hole;
                            i = close;
                            continue;
                        }
                        lastHole = hole;
                        lits.Add(lit.ToString());
                        lit.Length = 0;
                        slots.Add(index);
                        i = close;
                        continue;
                    }
                    if (c == '}')
                    {
                        if (i + 1 < original.Length && original[i + 1] == '}') { lit.Append('}'); i++; continue; }
                        return null;
                    }
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) letters++;
                    lit.Append(c);
                    lastHole = null;
                }
                lits.Add(lit.ToString());
                if (slots.Count == 0) return null;
                if (letters < (explicitPattern ? 1 : 3)) return null;     // "{0}: {1}" would swallow everything
                Pattern p = new Pattern();
                p.Lits = lits.ToArray();
                p.Slots = slots.ToArray();
                p.Template = translation;
                p.Ghosts = ghosts == null ? null : ghosts.ToArray();
                foreach (string l in p.Lits) p.Weight += l.Length;
                return p;
            }

            public string Apply(string s, int depth)
            {
                int last = Lits.Length - 1;
                string first = Lits[0], tailLit = Lits[last];
                if (s.Length < Weight) return null;
                if (first.Length > 0 && string.CompareOrdinal(s, 0, first, 0, first.Length) != 0) return null;
                int end = s.Length - tailLit.Length;
                if (tailLit.Length > 0 && string.CompareOrdinal(s, end, tailLit, 0, tailLit.Length) != 0) return null;
                int pos = first.Length;
                if (pos > end) return null;
                string[] caught = new string[Slots.Length];
                for (int k = 1; k < last; k++)
                {
                    int at = s.IndexOf(Lits[k], pos, end - pos, StringComparison.Ordinal);
                    if (at < 0) return null;
                    caught[k - 1] = s.Substring(pos, at - pos);
                    pos = at + Lits[k].Length;
                }
                caught[last - 1] = s.Substring(pos, end - pos);

                StringBuilder sb = new StringBuilder(Template.Length + s.Length);
                for (int i = 0; i < Template.Length; i++)
                {
                    char c = Template[i];
                    if (c == '{')
                    {
                        if (i + 1 < Template.Length && Template[i + 1] == '{') { sb.Append('{'); i++; continue; }
                        int close = Template.IndexOf('}', i);
                        if (close < 0) return null;
                        int index = Fmt.FormatIndex(Template.Substring(i, close - i + 1));
                        int slot = index < 0 ? -1 : Array.IndexOf(Slots, index);
                        if (slot < 0)
                        {
                            if (index >= 0 && Ghosts != null && Array.IndexOf(Ghosts, index) >= 0) { i = close; continue; }
                            return null;
                        }
                        string value = caught[slot];
                        // a name inside the text may be translatable on its own: "Equipped: Iron Plate"
                        string inner = Sub(value, depth + 1);
                        sb.Append(inner ?? value);
                        i = close;
                        continue;
                    }
                    if (c == '}' && i + 1 < Template.Length && Template[i + 1] == '}') { sb.Append('}'); i++; continue; }
                    sb.Append(c);
                }
                return sb.ToString();
            }
        }

        private static void Init()
        {
            if (ready) return;
            lock (Gate)
            {
                if (ready) return;
                try { Load(); }
                catch { /* stay empty: everything is shown as the mod wrote it */ }
                ready = true;
            }
        }

        private static void Load()
        {
            string home = null;
            try { home = Path.GetDirectoryName(typeof(Loc).Assembly.Location); } catch { }
            if (string.IsNullOrEmpty(home)) return;

            string config = FindConfigPath(home);
            Settings settings = config == null ? new Settings() : Settings.Load(Path.Combine(config, Settings.FileName), false);
            if (!settings.Enabled || !settings.DisplayHooks) return;

            Lang lang = Lang.Load(home);
            Dictionary<string, string> m = new Dictionary<string, string>(lang.Global.Count + lang.Terms.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in lang.Global) m[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, string> kv in lang.Terms) m[kv.Key] = kv.Value;   // terms win
            int lo = int.MaxValue, hi = -1;
            foreach (string k in m.Keys)
            {
                if (k.Length < lo) lo = k.Length;
                if (k.Length > hi) hi = k.Length;
            }

            // "Equipped" -> also "EQUIPPED" / "equipped": the mod changes the case of many captions right before drawing them
            Dictionary<string, CaseEntry> up = new Dictionary<string, CaseEntry>(m.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in m)
            {
                if (kv.Key.Length > 120 || kv.Key.IndexOf('{') >= 0) continue;
                string k = kv.Key.ToUpperInvariant();
                CaseEntry old;
                if (up.TryGetValue(k, out old))
                {
                    // two spellings of one text: the one written like a normal caption describes the translator's casing best
                    bool oldPlain = !string.Equals(old.Key, k, StringComparison.Ordinal) && !string.Equals(old.Key, old.Key.ToLowerInvariant(), StringComparison.Ordinal);
                    bool newPlain = !string.Equals(kv.Key, k, StringComparison.Ordinal) && !string.Equals(kv.Key, kv.Key.ToLowerInvariant(), StringComparison.Ordinal);
                    if (oldPlain || !newPlain) continue;
                }
                CaseEntry e = new CaseEntry();
                e.Key = kv.Key;
                e.Value = kv.Value;
                up[k] = e;
            }

            // " (copy)", "press ": pieces the mod sticks to the end / the beginning of other texts
            List<KeyValuePair<string, string>> pre = new List<KeyValuePair<string, string>>(), suf = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> kv in m)
            {
                string k = kv.Key;
                if (k.Length < 3 || k.Length > 60 || k.IndexOf('{') >= 0 || !HasLatin(k)) continue;
                if (k[k.Length - 1] == ' ') pre.Add(kv);
                else if (k[0] == ' ' || k[0] == ',') suf.Add(kv);
            }
            Comparison<KeyValuePair<string, string>> longest = delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
            {
                int c = b.Key.Length.CompareTo(a.Key.Length);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            };
            pre.Sort(longest);
            suf.Sort(longest);

            List<Pattern> all = new List<Pattern>();
            foreach (KeyValuePair<string, string> kv in lang.Patterns)
            {
                Pattern p = Pattern.Parse(kv.Key, kv.Value, true);
                if (p != null) all.Add(p);
            }
            foreach (KeyValuePair<string, string> kv in m)
            {
                if (kv.Key.IndexOf('{') < 0 || lang.Patterns.ContainsKey(kv.Key)) continue;
                Pattern p = Pattern.Parse(kv.Key, kv.Value, false);
                if (p != null) all.Add(p);
            }
            all.Sort(delegate(Pattern a, Pattern b)
            {
                int c = b.Weight.CompareTo(a.Weight);
                return c != 0 ? c : string.CompareOrdinal(a.Template, b.Template);
            });
            Dictionary<string, List<Pattern>> heads = new Dictionary<string, List<Pattern>>(StringComparer.Ordinal);
            Dictionary<string, List<Pattern>> tails = new Dictionary<string, List<Pattern>>(StringComparer.Ordinal);
            List<Pattern> rest = new List<Pattern>();
            foreach (Pattern p in all)
            {
                string first = p.Lits[0], lastLit = p.Lits[p.Lits.Length - 1];
                if (first.Length >= 2) Bucket(heads, first.Substring(0, 2), p);
                else if (lastLit.Length >= 2) Bucket(tails, lastLit.Substring(lastLit.Length - 2), p);
                else rest.Add(p);
            }

            minLength = lo;
            maxLength = hi;
            upper = up;
            prefixes = pre.ToArray();
            suffixes = suf.ToArray();
            byHead = Freeze(heads);
            byTail = Freeze(tails);
            loose = rest.ToArray();
            memo = new Dictionary<string, string>(StringComparer.Ordinal);
            map = m;
            fallbacks = m.Count > 0;

            if (settings.LogUntranslated && config != null)
            {
                string dir = Path.Combine(config, "RepoRPG_RU");
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "untranslated.txt");
                seen = new HashSet<string>(StringComparer.Ordinal);
                pending = new List<string>();
                try
                {
                    if (File.Exists(logPath))
                        foreach (string line in File.ReadAllLines(logPath, Encoding.UTF8))
                            if (line.Length > 0) seen.Add(line);
                }
                catch { }
                nextFlush = Environment.TickCount;
                try { AppDomain.CurrentDomain.ProcessExit += delegate { Flush(); }; } catch { }
                logging = true;
            }
        }

        private static void Bucket(Dictionary<string, List<Pattern>> table, string key, Pattern p)
        {
            List<Pattern> list;
            if (!table.TryGetValue(key, out list)) { list = new List<Pattern>(); table[key] = list; }
            list.Add(p);
        }

        private static Dictionary<string, Pattern[]> Freeze(Dictionary<string, List<Pattern>> table)
        {
            Dictionary<string, Pattern[]> r = new Dictionary<string, Pattern[]>(table.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<Pattern>> kv in table) r[kv.Key] = kv.Value.ToArray();
            return r;
        }

        /// <summary>BepInEx/config, asked from BepInEx itself when possible (works for any mod manager layout).</summary>
        private static string FindConfigPath(string home)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "BepInEx") continue;
                    Type paths = a.GetType("BepInEx.Paths", false);
                    if (paths == null) continue;
                    PropertyInfo p = paths.GetProperty("ConfigPath", BindingFlags.Public | BindingFlags.Static);
                    if (p == null) continue;
                    string value = p.GetValue(null, null) as string;
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            catch { }
            try
            {
                // .../BepInEx/plugins/<folder>/RepoRPG_RU.dll  ->  .../BepInEx/config
                DirectoryInfo d = new DirectoryInfo(home);
                for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                {
                    string candidate = Path.Combine(d.FullName, "config");
                    if (string.Equals(d.Name, "BepInEx", StringComparison.OrdinalIgnoreCase) && Directory.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ "what is still English?" log

        private static void Note(string text)
        {
            try
            {
                if (seen.Count < MaxLogged && text.Length <= 400 && LooksEnglish(text))
                {
                    string line = Flatten(text);
                    if (seen.Add(line)) pending.Add(line);
                }
                if (pending.Count == 0) return;
                int now = Environment.TickCount;
                if (now - nextFlush < 0) return;
                nextFlush = now + 3000;
                Flush();
            }
            catch
            {
                logging = false;
            }
        }

        private static void Flush()
        {
            try
            {
                lock (Gate)
                {
                    if (pending == null || pending.Count == 0) return;
                    File.AppendAllText(logPath, string.Join("\n", pending.ToArray()) + "\n", new UTF8Encoding(false));
                    pending.Clear();
                }
            }
            catch
            {
                logging = false;
            }
        }

        private static bool LooksEnglish(string s)
        {
            int run = 0, best = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '\u0400' && c <= '\u04FF') return false;      // already Russian
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) { run++; if (run > best) best = run; }
                else if (c == '<')
                {
                    // skip rich text tags: <color=#fff>, </b> ...
                    int close = s.IndexOf('>', i);
                    if (close > i && close - i < 24) i = close;
                    run = 0;
                }
                else run = 0;
            }
            return best >= 3;
        }

        private static string Flatten(string s)
        {
            if (s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0 && s.IndexOf('\\') < 0) return s;
            return s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
