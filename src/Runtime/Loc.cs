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
            steps = 0;
            result = Derive(text, 0);
            lock (Gate)
            {
                if (memo.Count >= MemoLimit) memo.Clear();   // timers and counters produce endless variants
                memo[text] = result;
                if (result == null && logging) Note(text);
            }
            return result ?? text;
        }

        // How far a text is taken apart (a line of a block, a cell of the line, a sentence of the cell, a name in the sentence...)
        // and how much work one text may cost. Past either limit the answer is "unknown", never "half done".
        private const int MaxDepth = 6;
        private const int MaxSteps = 4000;

        /// <summary>A part of a bigger text: same work, same memory.</summary>
        private static string Sub(string text, int depth)
        {
            if (text.Length < 2 || !HasLatin(text)) return null;
            string result;
            lock (Gate)
            {
                if (memo.TryGetValue(text, out result)) return result;
            }
            int before = cuts;
            result = Derive(text, depth);
#if TRACE
            Console.Error.WriteLine(new string(' ', depth * 2) + depth + " [" + text + "] => " + (result ?? "-") + (cuts != before ? "   (cut)" : ""));
#endif
            if (result == null && cuts != before) return null;      // given up because of the depth, not because there is no answer
            lock (Gate)
            {
                if (memo.Count < MemoLimit) memo[text] = result;
            }
            return result;
        }

        [ThreadStatic] private static int cuts;
        [ThreadStatic] private static int steps;

        /// <summary>
        /// A piece of a bigger text. "unknown" is raised when the search was cut short: the piece may well have a
        /// translation, so the caller must not build a result that leaves it in English.
        /// </summary>
        private static string Piece(string text, int depth, ref bool unknown)
        {
            int before = cuts;
            string t = Sub(text, depth + 1);
            if (t == null && cuts != before) unknown = true;
            return t;
        }

        /// <summary>The dictionary itself: the text as it is, or in another case.</summary>
        private static string Exact(string text)
        {
            string r;
            if (map.TryGetValue(text, out r)) return r;
            return ByCase(text);
        }

        private static string Derive(string text, int depth)
        {
            string r = Exact(text);
            if (r != null) return r;
            if (depth >= MaxDepth || ++steps > MaxSteps) { cuts++; return null; }
            r = Compose(text, depth);
            // "+3% опыта, +3% Scrap": a known name that was left standing between translated parts
            if (r != null && HasLatin(r)) r = Islands(r, depth) ?? r;
            return r;
        }

        private static string Compose(string text, int depth)
        {
            string r;

            // a whole sentence the mod filled in: the most exact answer there is
            if (text.Length >= 2)
            {
                Pattern[] bucket;
                if (byHead.TryGetValue(text.Substring(0, 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                if (byTail.TryGetValue(text.Substring(text.Length - 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                if (loose.Length > 0 && (r = Try(loose, text, depth)) != null) return r;
            }

            // spaces and line breaks around the text say nothing: " Rare gear" is "Rare gear"
            int ws = 0, we = text.Length;
            while (ws < we && IsBlank(text[ws])) ws++;
            while (we > ws && IsBlank(text[we - 1])) we--;
            if (we - ws >= 2 && (ws > 0 || we < text.Length))
            {
                string core = Sub(text.Substring(ws, we - ws), depth + 1);
                if (core != null) return text.Substring(0, ws) + core + text.Substring(we);
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
            if ((r = Parts(text, "\n", Lines, depth)) != null) return r;
            if ((r = Edge(text, depth)) != null) return r;
            if ((r = Runs(text, depth)) != null) return r;
            if ((r = Parts(text, "  \u00B7  ", Cells, depth)) != null) return r;
            if ((r = Parts(text, " \u00B7 ", Cells, depth)) != null) return r;
            if ((r = Parts(text, " | ", Cells, depth)) != null) return r;
            if ((r = Parts(text, "   ", Cells, depth)) != null) return r;
            if ((r = Sentences(text, depth)) != null) return r;
            if ((r = Parts(text, " + ", Phrase, depth)) != null) return r;
            if ((r = Parts(text, " - ", Phrase, depth)) != null) return r;
            if ((r = Parts(text, ": ", Label, depth)) != null) return r;
            if ((r = Parts(text, ", ", Phrase, depth)) != null) return r;
            if ((r = Peel(text, depth)) != null) return r;
            return Islands(text, depth);
        }

        /// <summary>
        /// Last resort: "Куплено: Iron Plate (Rare)" - known English texts sitting between punctuation or inside a
        /// sentence that is already Russian (a name from the mod's data, a word the code had to keep).
        /// </summary>
        /// <summary>
        /// A name the mod took from its data or its settings and put into a sentence that is already Russian:
        /// "Куплено: Iron Plate (Cursed)", "Нужно ещё 30 Scrap". Only a whole run of English words is replaced, and only when
        /// the dictionary knows exactly that run - a word out of an English phrase is never picked out on its own.
        /// </summary>
        private static string Islands(string text, int depth)
        {
            bool russian = false;
            for (int q = 0; q < text.Length && !russian; q++) russian = text[q] >= '\u0400' && text[q] <= '\u04FF';
            if (!russian) return null;
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
                if (!IsLatinLetter(c) || (i > 0 && IsWordChar(text[i - 1]))) { i++; continue; }
                // a run: words that start with a letter, one space (or " & ", "/") between them
                int start = i, end = i;
                int j = i;
                while (j < n)
                {
                    if (!IsLatinLetter(text[j])) break;
                    while (j < n && (IsWordChar(text[j]) || ((text[j] == '\'' || text[j] == '-') && j + 1 < n && IsWordChar(text[j + 1])))) j++;
                    end = j;
                    if (j + 1 < n && (text[j] == ' ' || text[j] == '/') && IsLatinLetter(text[j + 1])) { j++; continue; }
                    if (j + 3 < n && text[j] == ' ' && text[j + 1] == '&' && text[j + 2] == ' ' && IsLatinLetter(text[j + 3])) { j += 3; continue; }
                    break;
                }
                if (end - start >= 2 && !(start == 0 && end == n))
                {
                    string t = Exact(text.Substring(start, end - start));
                    if (t != null)
                    {
                        if (sb == null) sb = new StringBuilder(n + 16);
                        sb.Append(text, copied, start - copied).Append(t);
                        copied = end;
                    }
                }
                i = end > start ? end : start + 1;
            }
            if (sb == null) return null;
            sb.Append(text, copied, n - copied);
            return sb.ToString();
        }

        private static bool IsWordChar(char c) { return IsLatinLetter(c) || (c >= '0' && c <= '9') || c == '_'; }

        private static bool IsBlank(char c) { return c == ' ' || c == '\n' || c == '\r' || c == '\t'; }

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
                bool unknown = false;
                string t = Piece(rest, depth, ref unknown);
                if (t == null && (unknown || !(Pattern.Simple(rest) && NameLike(rest, false)))) continue;       // a sentence of its own, not a name
                return (t ?? rest) + suffixes[i].Value;
            }
            for (int i = 0; i < prefixes.Length; i++)
            {
                string k = prefixes[i].Key;
                if (text.Length <= k.Length || string.CompareOrdinal(text, 0, k, 0, k.Length) != 0) continue;
                string rest = text.Substring(k.Length);
                bool unknown = false;
                string t = Piece(rest, depth, ref unknown);
                if (t == null && (unknown || !(Pattern.Simple(rest) && NameLike(rest, false)))) continue;
                return prefixes[i].Value + (t ?? rest);
            }
            return null;
        }

        /// <summary>
        /// What may stay as it is next to translated text: a name, a number, a key ("Iron Plate", "1,200 Scrap", "F1").
        /// A piece of an English sentence may not: better a whole text in one language than half of it in each.
        /// A name is written Like This (at most four words, every longer one with a capital) and closes every bracket it opens.
        /// lenient: a single word passes in any case - a player's nickname need not be capitalised.
        /// </summary>
        internal static bool NameLike(string v, bool lenient)
        {
            int words = 0, tokens = 0, visible = 0, round = 0, square = 0;
            bool inToken = false, title = true;
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '\n') return false;
                if (c == '<')
                {
                    int close = v.IndexOf('>', i);
                    if (close > i && close - i <= 40) { i = close; inToken = false; continue; }
                }
                visible++;
                if (c == '(') round++;
                else if (c == ')') { if (--round < 0) return false; }
                else if (c == '[') square++;
                else if (c == ']') { if (--square < 0) return false; }
                bool letter = IsLatinLetter(c);
                if (letter || (c >= '0' && c <= '9') || c == '\'' || c == '_')
                {
                    if (inToken) continue;
                    inToken = true;
                    tokens++;
                    if (!letter) continue;                              // "3s", "10px" are values, not words
                    words++;
                    if (c >= 'A' && c <= 'Z') continue;
                    int len = 1;
                    while (i + len < v.Length && IsLatinLetter(v[i + len])) len++;
                    if (words == 1 || len >= 4) title = false;
                }
                else inToken = false;
            }
            if (round != 0 || square != 0) return false;
            if (words == 0) return true;
            if (visible > 60) return false;
            if (lenient && tokens == 1) return true;
            return title && words <= 4;
        }

        private const int Lines = 0, Cells = 1, Phrase = 2, Label = 3;

        /// <summary>
        /// Texts the mod glued together with a separator.
        ///   Lines   every line is a text of its own: translated or left alone, one by one.
        ///   Cells   columns of one row ("a  ·  b  ·  c"): the same, unless most of the row would stay English.
        ///   Phrase  parts of one phrase ("a, b, c"): what cannot be translated has to be a name or a number,
        ///           otherwise the whole text is left as it is.
        ///   Label   "Label: value" - split at the first separator only; the label itself must be known.
        /// </summary>
        private static string Parts(string text, string sep, int mode, int depth)
        {
            int at = text.IndexOf(sep, StringComparison.Ordinal);
            if (at < 0) return null;
            StringBuilder sb = null;
            int start = 0, index = 0, done = 0, foreign = 0;
            while (true)
            {
                int end = at < 0 ? text.Length : at;
                string part = text.Substring(start, end - start);
                bool unknown = false;
                string t = Piece(part, depth, ref unknown);
                if (unknown) return null;
                if (t != null) done++;
                else if (mode != Lines && HasLatin(part))
                {
                    if (mode == Label && index == 0) return null;
                    if (!NameLike(part, false))
                    {
                        if (mode != Cells) return null;
                        foreign++;
                    }
                }
                if (sb == null) sb = new StringBuilder(text.Length + 16);
                sb.Append(t ?? part);
                if (at < 0) break;
                sb.Append(sep);
                start = at + sep.Length;
                index++;
                at = mode == Label ? -1 : text.IndexOf(sep, start, StringComparison.Ordinal);
            }
            return done > 0 && done >= foreign ? sb.ToString() : null;
        }

        /// <summary>"First sentence. Second one! Third?" - the mod often joins ready sentences. All of them or none.</summary>
        private static string Sentences(string text, int depth)
        {
            StringBuilder sb = null;
            int start = 0, count = 0;
            for (int i = 0; i + 2 < text.Length; i++)
            {
                char c = text[i];
                if ((c != '.' && c != '!' && c != '?') || text[i + 1] != ' ') continue;
                char next = text[i + 2];
                if (!((next >= 'A' && next <= 'Z') || next == '<' || next == '(' || next == '[' || (next >= '0' && next <= '9'))) continue;
                string part = text.Substring(start, i + 1 - start);
                string t = Sub(part, depth + 1);
                if (t == null && HasLatin(part)) return null;
                if (sb == null) sb = new StringBuilder(text.Length + 16);
                sb.Append(t ?? part).Append(' ');
                start = i + 2;
                count++;
            }
            if (count == 0) return null;
            string last = text.Substring(start);
            string tl = Sub(last, depth + 1);
            if (tl == null && HasLatin(last)) return null;
            return sb.Append(tl ?? last).ToString();
        }

        /// <summary>"<b>Shop</b>  <color=#999>closed</color>": the text between rich text tags, run by run.</summary>
        private static string Runs(string text, int depth)
        {
            int lt = text.IndexOf('<');
            if (lt < 0) return null;
            StringBuilder sb = new StringBuilder(text.Length + 16);
            int start = 0, i = lt, tags = 0;
            bool any = false, unknown = false;
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
                    string t = Piece(run, depth, ref unknown);
                    if (unknown) return null;
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
                string t = Piece(run, depth, ref unknown);
                if (unknown) return null;
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
            if (keyUpper) return Sentence(e.Value);
            // "Closes when the level starts" from the known "closes when the level starts"
            if (char.IsUpper(text[0]) && char.IsLower(e.Key[0])) return UpperFirst(e.Value);
            return e.Value;
        }

        private static string LowerFirst(string s)
        {
            if (s.Length == 0) return s;
            if (s.Length > 1 && IsUpperLetter(s[1])) return s;        // an abbreviation: leave it alone
            string first = Lower(s.Substring(0, 1));
            return first + s.Substring(1);
        }

        private static string UpperFirst(string s)
        {
            // the first letter may sit behind a bracket or a rich text tag: "(надето)", "<b>надето</b>"
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<')
                {
                    int close = s.IndexOf('>', i);
                    if (close > i && close - i <= 40) { i = close; continue; }
                }
                if (char.IsLetter(c)) return s.Substring(0, i) + Upper(s.Substring(i, 1)) + s.Substring(i + 1);
                if (char.IsDigit(c)) return s;
            }
            return s;
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
            public bool Tight;         // few words of its own ("{0} gear"): it may only catch something short and simple
            public bool[] Glued;       // "item{1}": the placeholder continues a word - an ending ("s") or a separate piece (" (new)")
            public byte[] Kind;        // what a placeholder stands for, judged by the text around it: Value, Clause or Line
            public bool[] Numeric;     // "{0:N0}", "{1:0.#}": only a number fits
            public bool Counter;       // "HEAT {0}", "{0} XP": one shouted word and a number - "HEAT SINK" is somebody's name, not heat level "SINK"
            public bool[] Dropped;     // the translation has no use for it: the English plural ending in "{0} item{1}"
            public bool[] Roomy;       // "Gift failed: {0}", "+{0} Scrap – {1}", "{0} (level {1})": a text of its own may stand here
            public bool[] Framed;      // "RepoRPG: '{0}' hit an error and was switched off...": the sentence's own words stand on both sides
            public bool[] Known;       // "{0} gear": a rarity stands here - a word the dictionary has, or this is another text ("Spawn gear")

            private const byte Value = 0, Clause = 1, Line = 2;

            public static Pattern Parse(string original, string translation, bool explicitPattern, int[] numbers, int[] words)
            {
                List<string> lits = new List<string>();
                List<int> slots = new List<int>();
                List<int> ghosts = null;
                List<bool> glued = new List<bool>(), numeric = new List<bool>();
                string lastHole = null;
                StringBuilder lit = new StringBuilder();
                int letters = 0, own = 0;                 // letters of the whole pattern / of the piece being read
                List<int> owns = new List<int>();
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
                        // "{0:0.#}" is a number by its format; "{0} players" is one because the mod's code says so (msgctxt "numbers")
                        numeric.Add(hole.IndexOf(':') > 0 || (numbers != null && Array.IndexOf(numbers, index) >= 0));
                        glued.Add(lit.Length > 0 && IsLatinLetter(lit[lit.Length - 1]));
                        lits.Add(lit.ToString());
                        owns.Add(own);
                        own = 0;
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
                    if (c == '<')
                    {
                        // a rich text tag is not text of the pattern's own
                        int tagEnd = original.IndexOf('>', i);
                        if (tagEnd > i && tagEnd - i <= 40 && original.IndexOf('{', i, tagEnd - i) < 0)
                        {
                            lit.Append(original, i, tagEnd - i + 1);
                            i = tagEnd;
                            lastHole = null;
                            continue;
                        }
                    }
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) { letters++; own++; }
                    lit.Append(c);
                    lastHole = null;
                }
                lits.Add(lit.ToString());
                owns.Add(own);
                if (slots.Count == 0) return null;
                if (letters < (explicitPattern ? 1 : 3)) return null;     // "{0}: {1}" would swallow everything
                Pattern p = new Pattern();
                p.Lits = lits.ToArray();
                p.Slots = slots.ToArray();
                p.Template = translation;
                p.Ghosts = ghosts == null ? null : ghosts.ToArray();
                p.Tight = letters < 14;
                p.Glued = glued.ToArray();
                p.Numeric = numeric.ToArray();
                if (p.Tight && p.Slots.Length == 1 && letters > 0)
                {
                    bool bare = true;
                    foreach (string l in p.Lits)
                        foreach (char c in l)
                            if (c != ' ' && !(c >= 'A' && c <= 'Z')) bare = false;
                    p.Counter = bare;
                }
                p.Dropped = new bool[p.Slots.Length];
                List<string> used = Fmt.Holes(translation);
                for (int k = 0; k < p.Slots.Length; k++)
                {
                    bool kept = false;
                    foreach (string h in used) if (Fmt.FormatIndex(h) == p.Slots[k]) kept = true;
                    p.Dropped[k] = !kept;
                }
                p.Kind = new byte[p.Slots.Length];
                p.Roomy = new bool[p.Slots.Length];
                p.Framed = new bool[p.Slots.Length];
                p.Known = new bool[p.Slots.Length];
                if (words != null)
                    for (int k = 0; k < p.Slots.Length; k++) p.Known[k] = Array.IndexOf(words, p.Slots[k]) >= 0;
                for (int k = 0; k < p.Slots.Length; k++)
                {
                    // a long sentence that goes on after the placeholder cannot be mistaken for another one: what it caught is
                    // its own - the name of a feature as the log has it, the name of another mod - and may stay as written
                    p.Framed[k] = letters >= 30 && owns[k] >= 4 && owns[k + 1] >= 4;
                    // "{0}/{1} voted", "Taken by {0}"        a value inside the sentence: a name, a number
                    // "{0} This can't be undone.", "... - {0}"  a clause or a sentence of its own
                    // "{0}\n<b>Reward</b> {1}"                  a line of its own: whatever it is, it is none of this pattern's business
                    string before = p.Lits[k], after = p.Lits[k + 1];
                    int bl = before.Length;
                    bool first = k == 0 && bl == 0, last = k == p.Slots.Length - 1 && after.Length == 0;
                    bool lineBefore = first || before[bl - 1] == '\n';
                    bool lineAfter = last || after[0] == '\n';
                    bool breakBefore = lineBefore || before[bl - 1] == '>'
                                       || (bl > 1 && before[bl - 1] == ' ' && (before[bl - 2] == '.' || before[bl - 2] == '!' || before[bl - 2] == '?'
                                                                                 || before[bl - 2] == ':' || before[bl - 2] == '-' || before[bl - 2] == '>'));
                    bool breakAfter = lineAfter || after[0] == '.' || after[0] == '!' || after[0] == '?' || after[0] == ':' || after[0] == '<'
                                      || (after.Length > 1 && after[0] == ' ' && ((after[1] >= 'A' && after[1] <= 'Z') || after[1] == '<' || after[1] == '-' || after[1] == '('));
                    p.Kind[k] = lineBefore && lineAfter ? Line : breakBefore && breakAfter ? Clause : Value;
                    bool dashBefore = bl > 1 && before[bl - 1] == ' ' && (before[bl - 2] == '\u2013' || before[bl - 2] == '\u2014');
                    p.Roomy[k] = (breakBefore || dashBefore) && breakAfter;
                }
                foreach (string l in p.Lits) p.Weight += l.Length;
                return p;
            }

            // What a short pattern may catch: a name, a number, a price. Not a sentence, not several things in a row,
            // not a piece of rich text that starts or ends somewhere else. The rest is left to the splitting further down.
            // "One boon for the whole team. 3" is not the "{0}" of "{0}/{1} voted": a value does not run over a sentence end or a line break
            private static bool Several(string v)
            {
                for (int i = 0; i < v.Length; i++)
                {
                    char c = v[i];
                    if (c == '\n') return true;
                    if ((c == '.' || c == '!' || c == '?' || c == ';') && i + 2 < v.Length && v[i + 1] == ' ') return true;
                }
                return false;
            }

            internal static bool Simple(string v)
            {
                return v.Length <= 80 && NameLike(v, true) && Whole(v);
            }

            // "big bob", "the legend 27": players call themselves anything, but briefly
            private static bool Short(string v)
            {
                if (v.Length > 32 || !Whole(v)) return false;
                int words = 0;
                bool inWord = false;
                for (int i = 0; i < v.Length; i++)
                {
                    if (v[i] == '<' || v[i] == '>') return false;
                    bool w = !IsBlank(v[i]);
                    if (w && !inWord) words++;
                    inWord = w;
                }
                return words <= 4;
            }

            // one thing, not several in a row and not a piece of rich text that starts or ends somewhere else
            private static bool Whole(string v)
            {
                if (v.Length > 0 && (IsBlank(v[0]) || IsBlank(v[v.Length - 1]))) return false;
                int open = 0;
                for (int i = 0; i < v.Length; i++)
                {
                    char c = v[i];
                    if (c == '\n') return false;
                    if (c == '<')
                    {
                        int close = v.IndexOf('>', i);
                        if (close > i && close - i <= 40 && i + 1 < v.Length)
                        {
                            if (v[i + 1] == '/') { if (--open < 0) return false; }
                            else if (IsLatinLetter(v[i + 1])) { if (open == 0 && i > 0) return false; open++; }
                            else continue;
                            if (open == 0 && close + 1 < v.Length) return false;      // text after the closing tag
                            i = close;
                        }
                        continue;
                    }
                    if (i + 1 >= v.Length) break;
                    char d = v[i + 1];
                    if (d == ' ' && (c == ':' || c == ',' || c == '.' || c == '!' || c == '?' || c == ';') && i + 2 < v.Length) return false;
                    if (c == ' ' && (d == '·' || d == '|') ) return false;
                    if (c == ' ' && d == ' ') return false;
                }
                return open == 0;
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
                int owed = 0;       // bit k: this placeholder caught more than a name - it counts only if the dictionary knows that text
                for (int k = 0; k < caught.Length; k++)
                {
                    string v = caught[k];
                    if (Numeric[k])
                    {
                        // a number has a digit and no letters: "more players" is not "{0} players", "kill XP" is not "{0} XP"
                        bool digit = false;
                        for (int q = 0; q < v.Length; q++)
                        {
                            if (char.IsLetter(v[q])) return null;
                            if (v[q] >= '0' && v[q] <= '9') digit = true;
                        }
                        if (!digit) return null;
                        continue;
                    }
                    if (Known[k] && Exact(v) == null) return null;
                    if (Tight && !Simple(v))
                    {
                        // "Gift failed: " + "that player isn't here", "+25 Scrap – " + "starter tool": the reason is a text of the
                        // mod's own. A short pattern may take it when - and only when - that text turns out to have a translation
                        if (!Roomy[k] || k >= 30 || v.Length > 80 || !Whole(v) || Dropped[k]) return null;
                        owed |= 1 << k;
                    }
                    if (Kind[k] == Value)
                    {
                        if (Several(v)) return null;
                        // " and your 3" + " skill points to spend": the text began before this pattern does
                        if (k == 0 && Lits[0].Length == 0 && v.Length > 0 && v[0] == ' ') return null;
                        // "Collector Frenzy (next level): Golden Statue" + " is worth double": a caption stands in front of this sentence,
                        // the name is only its last part - the splitting at ": " further down gets both right
                        if (k == 0 && Lits[0].Length == 0 && v.IndexOf(": ", StringComparison.Ordinal) >= 0) return null;
                        if (k == caught.Length - 1 && Lits[k + 1].Length == 0 && v.Length > 0 && v[v.Length - 1] == ' ') return null;
                    }
                    if (Dropped[k])
                    {
                        // what the translation throws away must be nothing but an ending: "", "s", "ies"
                        if (v.Length > 3) return null;
                        for (int q = 0; q < v.Length; q++) if (!IsLatinLetter(v[q])) return null;
                        continue;
                    }
                    // "Unlock{0}" + "s at Regular reputation", "upgrade{2}" + "s": the word goes on, so this is another word and
                    // another sentence. (An English plural ending is fine where the translation drops it - handled above.)
                    if (Glued[k] && v.Length > 0 && IsLatinLetter(v[0])) return null;
                }

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
                        bool unknown = false;
                        string inner = Piece(value, depth, ref unknown);
                        if (unknown) return null;
                        if (inner == null && (owed & (1 << slot)) != 0) return null;
                        // ...and a clause has to be: "<English sentence> Это нельзя отменить." helps nobody
                        bool asWritten = inner == null && Framed[slot] && value.Length <= 200 && value.IndexOf('<') < 0 && !Several(value);
                        if (inner == null && Kind[slot] == Clause && HasLatin(value) && !Simple(value) && !asWritten) return null;
                        if (inner == null && Counter && HasLatin(value)) return null;
                        // ...and what stays English inside a sentence is a name or a nickname, not the rest of another sentence
                        if (inner == null && Kind[slot] == Value && HasLatin(value) && !Simple(value) && !Short(value) && !asWritten) return null;
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
                int[] nums, words;
                lang.Numbers.TryGetValue(kv.Key, out nums);
                lang.Words.TryGetValue(kv.Key, out words);
                Pattern p = Pattern.Parse(kv.Key, kv.Value, true, nums, words);
                if (p != null) all.Add(p);
            }
            foreach (KeyValuePair<string, string> kv in m)
            {
                if (kv.Key.IndexOf('{') < 0 || lang.Patterns.ContainsKey(kv.Key)) continue;
                int[] nums, words;
                lang.Numbers.TryGetValue(kv.Key, out nums);
                lang.Words.TryGetValue(kv.Key, out words);
                Pattern p = Pattern.Parse(kv.Key, kv.Value, false, nums, words);
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
