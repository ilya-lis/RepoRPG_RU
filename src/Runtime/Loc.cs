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
[assembly: AssemblyVersion("0.2.2.0")]

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
        private static Dictionary<string, string> shapes;
        // names the mod builds from the words of its data (see Built); the keys are in upper case
        private static Dictionary<string, string[]> kinds;      // "SHARP" -> its four forms
        private static Dictionary<string, Thing> things;        // "STRIKER MODULE" -> the gender and the translation
        private static Dictionary<string, string> owners;       // "BRAWLER" -> "драчуна"
        private static Pattern[] loose;
        // the same patterns in capitals: the mod's HUD shouts whole lines ("HOLDING: COMMON VALUABLE  $3 300")
        private static Dictionary<string, Pattern[]> loudHead, loudTail;
        private static Pattern[] loudLoose;
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

        /// <summary>
        /// Asked by the mod where it picks the font of its menus (the patcher puts the call there). <paramref name="setting"/> is
        /// the mod's own Controls/UiFont, or null when the mod did not tell. Returns a UnityEngine.Font - the game's font
        /// with Russian letters - or null: "pick it yourself". Never throws. See Fonts.cs.
        /// </summary>
        public static object MenuFont(string setting)
        {
            try { return Fonts.Pick(setting); }
            catch { return null; }
        }

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
                if (logging)
                {
                    if (result == null) Note(LooksEnglish(text) ? text : Leftover(text) ? "~ " + text : null);
                    else if (Leftover(result)) Note("~ " + text + "  =>  " + result);
                }
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
            if (things != null && (r = Built(text)) != null) return r;
            if (shapes != null && (r = Shape(text)) != null) return r;
            if (depth >= MaxDepth || ++steps > MaxSteps) { cuts++; return null; }
            r = Compose(text, depth);
            // "+3% опыта, +3% Scrap": a known name that was left standing between translated parts
            if (r != null && HasLatin(r)) r = Islands(r, depth) ?? r;
            return r;
        }

        private const char NumberMark = '\u0001';

        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }

        /// <summary>
        /// Where a number ends: digits, groups of three after a no-break space ("8 000", the way the game's language settings
        /// write thousands), then a decimal part after '.' or ','. The same rule as NUMBER of tools/datapo.py.
        /// </summary>
        private static int NumberEnd(string s, int i)
        {
            int n = s.Length, j = i;
            while (j < n && IsDigit(s[j])) j++;
            while (j + 3 < n && (s[j] == '\u00A0' || s[j] == '\u202F') && IsDigit(s[j + 1]) && IsDigit(s[j + 2]) && IsDigit(s[j + 3])) j += 4;
            if (j + 1 < n && (s[j] == '.' || s[j] == ',') && IsDigit(s[j + 1]))
            {
                j += 2;
                while (j < n && IsDigit(s[j])) j++;
            }
            return j;
        }

        /// <summary>
        /// A text from the mod's data with other numbers in it: "+12% max health" for the entry "+6% max health" (a talent at
        /// rank 2), "Kill 3 elites" for "Kill {t} elite{s}", "+00% max health" (the mod measures a line with its digits zeroed).
        /// The text with every number taken out is looked up as a whole; the numbers go back into the translation.
        /// </summary>
        private static string Shape(string text)
        {
            int n = text.Length, i = 0;
            while (i < n && !IsDigit(text[i])) i++;
            if (i == n) return null;
            StringBuilder key = new StringBuilder(n);
            List<string> numbers = new List<string>(4);
            int copied = 0;
            while (i < n)
            {
                if (!IsDigit(text[i])) { i++; continue; }
                int end = NumberEnd(text, i);
                key.Append(text, copied, i - copied).Append(NumberMark);
                numbers.Add(text.Substring(i, end - i));
                copied = i = end;
            }
            key.Append(text, copied, n - copied);
            string template;
            if (!shapes.TryGetValue(key.ToString(), out template)) return null;
            StringBuilder sb = new StringBuilder(template.Length + 16);
            for (int k = 0; k < template.Length; k++)
            {
                char c = template[k];
                if (c == '{')
                {
                    int close = template.IndexOf('}', k);
                    int index = close < 0 ? -1 : Fmt.FormatIndex(template.Substring(k, close - k + 1));
                    if (index < 0 || index >= numbers.Count) return null;
                    sb.Append(numbers[index]);
                    k = close;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Compose(string text, int depth)
        {
            string r;

            // "Cursed Impact Gauntlet?", "SALVAGE RARE IRON PLATE?" + its last mark: a name the mod built is one thing, and a
            // pattern ("Cursed {0}") must not take it apart
            if (things != null && text.Length > 3)
            {
                char mark = text[text.Length - 1];
                if ((mark == '?' || mark == '!' || mark == '.' || mark == ':') && IsLatinLetter(text[text.Length - 2])
                    && (r = Built(text.Substring(0, text.Length - 1))) != null) return r + mark;
            }

            // a whole sentence the mod filled in: the most exact answer there is
            if (text.Length >= 2)
            {
                Pattern[] bucket;
                if (byHead.TryGetValue(text.Substring(0, 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                if (byTail.TryGetValue(text.Substring(text.Length - 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                if (loose.Length > 0 && (r = Try(loose, text, depth)) != null) return r;
                // "$7 300  33M AHEAD", "HOLDING: COMMON VALUABLE  $3 300": a sentence the mod filled in and then put in capitals
                if (loudLoose != null && Shouted(text))
                {
                    if (loudHead.TryGetValue(text.Substring(0, 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                    if (loudTail.TryGetValue(text.Substring(text.Length - 2), out bucket) && (r = Try(bucket, text, depth)) != null) return r;
                    if (loudLoose.Length > 0 && (r = Try(loudLoose, text, depth)) != null) return r;
                }
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
            int head = 0, tail = text.Length, head1 = -1, tail1 = -1;
            while (head < tail && text[head] == '<')
            {
                int close = text.IndexOf('>', head);
                if (close < 0 || close - head > 40 || text[head + 1] == '/') break;
                head = close + 1;
                if (head1 < 0) head1 = head;
            }
            while (tail > head && text[tail - 1] == '>')
            {
                int open = text.LastIndexOf('<', tail - 1);
                if (open < head || tail - open > 40 || open + 1 >= tail || text[open + 1] != '/') break;
                tail = open;
                if (tail1 < 0) tail1 = tail;
            }
            // "<color=#9a9ca0><color=#ffd75a>All milestones reached</color></color>": the entry has a colour of its own, the mod
            // wrapped it in another one - only the outer pair is not part of the text
            if (head1 > 0 && tail1 > 0 && (head1 < head || tail1 > tail) && tail1 > head1)
            {
                string own = Exact(text.Substring(head1, tail1 - head1));
                if (own != null) return text.Substring(0, head1) + own + text.Substring(tail1);
            }
            if ((head > 0 || tail < text.Length) && tail > head)
            {
                string core = Sub(text.Substring(head, tail - head), depth + 1);
                if (core != null) return text.Substring(0, head) + core + text.Substring(tail);
            }

            // texts glued together by the mod
            if ((r = Parts(text, "\n", Lines, depth)) != null) return r;
            if ((r = Quoted(text, depth)) != null) return r;
            if ((r = Edge(text, depth)) != null) return r;
            if ((r = Runs(text, depth)) != null) return r;
            if ((r = Parts(text, "  \u00B7  ", Cells, depth)) != null) return r;
            if ((r = Parts(text, " \u00B7 ", Cells, depth)) != null) return r;
            if ((r = Parts(text, " | ", Cells, depth)) != null) return r;
            if ((r = Parts(text, "   ", Cells, depth)) != null) return r;
            if ((r = Sentences(text, depth)) != null) return r;
            if ((r = Parts(text, " + ", Phrase, depth)) != null) return r;
            if ((r = Parts(text, " \u2013 ", Phrase, depth)) != null) return r;      // "Kill 15 monsters – weapon hits heal 1 HP more"
            if ((r = Parts(text, " - ", Phrase, depth)) != null) return r;
            if ((r = Parts(text, ": ", Label, depth)) != null) return r;
            if ((r = Parts(text, ", ", Phrase, depth)) != null) return r;
            if ((r = Bracket(text, depth)) != null) return r;
            if ((r = Peel(text, depth)) != null) return r;
            if ((r = Counted(text)) != null) return r;
            return Islands(text, depth);
        }

        /// <summary>
        /// "Heavy Riot Plating (Rare, equipped)": the rarity in the bracket takes the form of the item in front of it -
        /// «Тяжёлая штурмовая броня (редкая, надето)».
        /// </summary>
        private static string Agreed(string outer, string inner, int depth, ref bool unknown)
        {
            if (things == null) return null;
            int comma = inner.IndexOf(", ", StringComparison.Ordinal);
            string word = comma < 0 ? inner : inner.Substring(0, comma);
            string[] forms;
            if (word.Length > 24 || !kinds.TryGetValue(word.ToUpperInvariant(), out forms)) return null;
            if (string.Equals(word, word.ToUpperInvariant(), StringComparison.Ordinal)) return null;       // shouted: left to the usual way
            // the name is what the text in front ends with: "Предмет: Heavy Riot Plating"
            int from = outer.Length;
            while (from > 0 && (IsLatinLetter(outer[from - 1]) || outer[from - 1] == ' ' || outer[from - 1] == '\'' || outer[from - 1] == '-')) from--;
            while (from < outer.Length && outer[from] == ' ') from++;
            if (from >= outer.Length) return null;
            int gender = Gender(outer.Substring(from));
            if (gender < 0) return null;
            string form = LowerFirst(forms[gender]);
            if (comma < 0) return form;
            string more = inner.Substring(comma + 2);
            string rest = Piece(more, depth, ref unknown);
            if (rest == null && (unknown || (HasLatin(more) && !NameLike(more, false)))) return null;
            return form + ", " + (rest ?? more);
        }

        /// <summary>
        /// "Fleet Foot (+12% walk and sprint speed)", "Iron Plate (Rare, equipped)": a text and what the mod put in brackets
        /// after it. Each half is a text of its own; what stays as written has to be a name or a number.
        /// </summary>
        private static string Bracket(string text, int depth)
        {
            int n = text.Length;
            if (n < 6) return null;
            char close = text[n - 1];
            char open = close == ')' ? '(' : close == ']' ? '[' : '\0';
            if (open == '\0') return null;
            int level = 0, at = -1;
            for (int i = n - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == close) level++;
                else if (c == open && --level == 0) { at = i; break; }
            }
            if (at < 2 || text[at - 1] != ' ' || at + 1 >= n - 1) return null;
            string outer = text.Substring(0, at - 1), inner = text.Substring(at + 1, n - at - 2);
            bool unknown = false;
            string to = Piece(outer, depth, ref unknown);
            string ti = (open == '(' ? Agreed(outer, inner, depth, ref unknown) : null) ?? Piece(inner, depth, ref unknown);
            if (unknown || (to == null && ti == null)) return null;
            if (to == null && HasLatin(outer) && !NameLike(outer, false)) return null;
            if (ti == null && HasLatin(inner) && !NameLike(inner, false)) return null;
            return (to ?? outer) + " " + open + (ti ?? inner) + close;
        }

        /// <summary>
        /// 'Kaboom &amp; Sons: "Earplugs are in the truck."', '&lt;i&gt;"One could be luck. Two is a habit."&lt;/i&gt;': a line of the mod's
        /// data that the mod put in quotes at the end of a text. The line is a text of its own (it may have ": " or " - " inside,
        /// so it is taken out before anything is split); what stands before it is another one.
        /// </summary>
        private static string Quoted(string text, int depth)
        {
            int close = text.Length - 1;
            while (close > 0 && text[close] != '"')
            {
                char c = text[close];
                if (IsBlank(c)) { close--; continue; }
                if (c != '>') return null;
                int lt = text.LastIndexOf('<', close);
                if (lt < 0 || close - lt > 40) return null;
                close = lt - 1;
            }
            if (close <= 0) return null;
            int open = text.IndexOf('"');
            if (open >= close - 2) return null;
            bool unknown = false;
            string line = Piece(text.Substring(open + 1, close - open - 1), depth, ref unknown);
            if (line == null) return null;
            // the tags that open right before the quote belong to it: <i>"..."</i>
            int headEnd = open;
            while (headEnd > 0)
            {
                char c = text[headEnd - 1];
                if (IsBlank(c)) { headEnd--; continue; }
                if (c != '>') break;
                int lt = text.LastIndexOf('<', headEnd - 1);
                if (lt < 0 || headEnd - lt > 41 || lt + 1 >= headEnd || text[lt + 1] == '/') break;
                headEnd = lt;
            }
            string head = text.Substring(0, headEnd), h = null;
            if (head.Length > 0 && HasLatin(head))
            {
                h = Piece(head, depth, ref unknown);
                if (unknown || (h == null && !NameLike(head, false))) return null;
            }
            return (h ?? head) + text.Substring(headEnd, open - headEnd) + "\u00AB" + line + "\u00BB" + text.Substring(close + 1);
        }

        /// <summary>"Throw 20", "COMBAT 100", "Carry x3": a name the dictionary has and a number after it.</summary>
        private static string Counted(string text)
        {
            int sp = text.LastIndexOf(' ');
            if (sp < 2 || sp == text.Length - 1 || text.Length - sp > 12) return null;
            bool digit = false;
            for (int i = sp + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9') { digit = true; continue; }
                if (i == sp + 1 && (c == 'x' || c == '\u00D7')) continue;
                if (c != '.' && c != ',' && c != '%' && c != '+' && c != '-' && c != '/' && c != '\u00A0') return null;
            }
            if (!digit) return null;
            while (sp > 2 && text[sp - 1] == ' ') sp--;                 // "RPG SCRAP  91": two spaces set the number apart
            string name = Named(text.Substring(0, sp));
            return name == null ? null : name + text.Substring(sp);
        }

        /// <summary>
        /// A name the mod took from its data or its settings and put into a sentence that is already Russian:
        /// "Куплено: Iron Plate (Cursed)", "Нужно ещё 30 Scrap". Only a whole run of English words is replaced, and only when
        /// the dictionary knows exactly that run - a word out of an English phrase is never picked out on its own.
        /// The text is looked at stretch by stretch (from one rich text tag or line break to the next):
        ///   in a stretch with Russian in it, what stays English has to look like a name ("Игрок Bob купил Iron Plate"),
        ///   not like the rest of an English sentence ("Эволюция: Earn 3,000 XP – +10% more XP" is left alone);
        ///   in a stretch with no Russian the dictionary has to know every word ("<b>3 Scrap</b>", not "Earn 3,000 XP");
        ///   words with nothing but a number between them are one phrase ("Kill 15 monsters"), never two names.
        /// </summary>
        private static string Islands(string text, int depth)
        {
            if (!HasCyrillic(text)) return null;
            StringBuilder sb = null;
            int n = text.Length, i = 0, copied = 0;
            while (i < n)
            {
                int end = i;
                bool russian = false;
                while (end < n)
                {
                    char c = text[end];
                    if (c == '\n') break;
                    if (c == '<')
                    {
                        int close = text.IndexOf('>', end);
                        if (close > end && close - end <= 40) break;      // a rich text tag is not a word
                    }
                    if (c >= '\u0400' && c <= '\u04FF') russian = true;
                    end++;
                }
                Stretch(text, i, end, russian, depth, ref sb, ref copied);
                if (end >= n) break;
                i = text[end] == '\n' ? end + 1 : text.IndexOf('>', end) + 1;
            }
            if (sb == null) return null;
            sb.Append(text, copied, n - copied);
            return sb.ToString();
        }

        [ThreadStatic] private static List<int> runStart;
        [ThreadStatic] private static List<int> runEnd;
        [ThreadStatic] private static List<string> runText;

        /// <summary>One stretch of <see cref="Islands"/>: text[from..to), no tags and no line breaks inside.</summary>
        private static void Stretch(string text, int from, int to, bool russian, int depth, ref StringBuilder sb, ref int copied)
        {
            List<int> starts = runStart ?? (runStart = new List<int>(8)), ends = runEnd ?? (runEnd = new List<int>(8));
            List<string> found = runText ?? (runText = new List<string>(8));
            starts.Clear(); ends.Clear(); found.Clear();
            int n = text.Length, i = from;
            bool any = false;
            while (i < to)
            {
                char c = text[i];
                if (!IsLatinLetter(c) || (i > 0 && IsWordChar(text[i - 1]))) { i++; continue; }
                // a run: words that start with a letter, one space (or " & ", "/") between them
                int start = i, end = i;
                int j = i;
                while (j < to)
                {
                    if (!IsLatinLetter(text[j])) break;
                    // "Carry x3": how many times is not a part of the name
                    if (j > start && text[j] == 'x' && j + 1 < to && IsDigit(text[j + 1])) break;
                    while (j < to && (IsWordChar(text[j]) || ((text[j] == '\'' || text[j] == '-') && j + 1 < to && IsWordChar(text[j + 1])))) j++;
                    end = j;
                    if (j + 1 < to && (text[j] == ' ' || text[j] == '/') && IsLatinLetter(text[j + 1])) { j++; continue; }
                    if (j + 3 < to && text[j] == ' ' && text[j + 1] == '&' && text[j + 2] == ' ' && IsLatinLetter(text[j + 3])) { j += 3; continue; }
                    break;
                }
                if (end > start)
                {
                    string t = null;
                    if (end - start >= 2 && !(start == 0 && end == n))
                    {
                        string run = text.Substring(start, end - start);
                        t = Named(run) ?? Names(run);
                    }
                    // "Kill 15 monsters": nothing but a number between this run and the one before - one phrase, and not a known one
                    if (starts.Count > 0 && OnlyNumber(text, ends[ends.Count - 1], start)) return;
                    if (t == null && (!russian || !NameLike(text.Substring(start, end - start), true))) return;      // the rest of an English sentence
                    if (t != null)
                    {
                        // "Убит Gorak: +50 XP": a number in front of the word - together they may be a phrase of their own («50 опыта»)
                        int num = start;
                        if (num > from && text[num - 1] == ' ') num--;
                        int digits = num;
                        while (digits > from && (IsDigit(text[digits - 1]) || ((text[digits - 1] == '.' || text[digits - 1] == ',' || text[digits - 1] == '\u00A0')
                            && digits - 1 > from && IsDigit(text[digits - 2]) && digits < num && IsDigit(text[digits])))) digits--;
                        if (digits < num && num < start && (digits == from || !IsWordChar(text[digits - 1])) && depth < MaxDepth)
                        {
                            string phrase = text.Substring(digits, end - digits);
                            string counted = Sub(phrase, depth + 1);
                            if (counted != null && !string.Equals(counted, phrase, StringComparison.Ordinal)) { start = digits; t = counted; }
                        }
                    }
                    starts.Add(start); ends.Add(end); found.Add(t);
                    if (t != null) any = true;
                }
                i = end > start ? end : start + 1;
            }
            if (!any) return;
            for (int k = 0; k < starts.Count; k++)
            {
                if (found[k] == null) continue;
                if (sb == null) sb = new StringBuilder(n + 16);
                sb.Append(text, copied, starts[k] - copied).Append(found[k]);
                copied = ends[k];
            }
        }

        /// <summary>text[from..to) is a number with spaces around it and nothing else: "Kill" + " 15 " + "monsters".</summary>
        private static bool OnlyNumber(string text, int from, int to)
        {
            bool digit = false;
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (IsDigit(c)) digit = true;
                else if (c != ' ' && c != '\u00A0' && !((c == '.' || c == ',') && i > from && IsDigit(text[i - 1]) && i + 1 < to && IsDigit(text[i + 1]))) return false;
            }
            return digit;
        }

        /// <summary>A rarity ("Rare", "Starter", "Cursed") in the form asked for; null when the word is not one of them.</summary>
        private static string InForm(string word, int gender, bool inside)
        {
            string[] forms;
            if (things == null || word.Length < 2 || word.Length > 24 || !kinds.TryGetValue(word.ToUpperInvariant(), out forms)) return null;
            if (string.Equals(word, word.ToUpperInvariant(), StringComparison.Ordinal)) return Upper(forms[gender]);       // SHOUTED
            return inside ? LowerFirst(forms[gender]) : forms[gender];
        }

        /// <summary>
        /// "Plating · Rare", "PLATING</color>  <color=#4FA3FF>RARE", "Rare Plating</color>  <color=#ffd75a>Unique": a rarity that stands
        /// alone next to the thing it belongs to takes its form - «Броня · Редкая».
        /// </summary>
        private static string Beside(int gender, string word)
        {
            return gender < 0 ? null : InForm(word, gender, false);
        }

        /// <summary>Has the text written so far any words of its own (rich text tags are not words)?</summary>
        private static bool Worded(StringBuilder sb)
        {
            bool tag = false;
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                if (c == '<') tag = true;
                else if (c == '>') tag = false;
                else if (!tag && char.IsLetter(c)) return true;
            }
            return false;
        }

        /// <summary>The dictionary, or a name the mod built from the words of its data.</summary>
        private static string Named(string text)
        {
            return Exact(text) ?? (things != null ? Built(text) : null);
        }

        private sealed class Thing
        {
            public int Gender;          // 0 he, 1 she, 2 it, 3 they
            public string Name;
        }

        private const int MaxKinds = 4;

        /// <summary>
        /// "Rare Sharp Striker Module of the Brawler", "Starter Reinforced Plating", "Epic Weapon Module", "CURSED HUNGERING EDGE":
        /// a name the mod builds from words of its data - what kind of thing it is (a rarity, "Starter", "Cursed", a behaviour,
        /// a stat word), the thing itself, and whose it is. In Russian the kinds agree with the thing, so the name is put
        /// together here: «Редкий острый ударный модуль драчуна», «Стартовая усиленная броня», «Эпическое ядро».
        /// The thing alone is an ordinary entry of the dictionary: this is for a name with something around it.
        /// </summary>
        private static string Built(string text)
        {
            string[][] found = new string[MaxKinds][];
            int count;
            string owner;
            bool lower;
            Thing thing = Parse(text, found, out count, out owner, out lower);
            if (thing == null || (count == 0 && owner == null)) return null;
            StringBuilder sb = new StringBuilder(text.Length + 24);
            for (int k = 0; k < count; k++)
            {
                string form = found[k][thing.Gender];
                sb.Append(k == 0 ? form : LowerFirst(form)).Append(' ');
            }
            sb.Append(count == 0 ? thing.Name : LowerFirst(thing.Name));
            if (owner != null) sb.Append(' ').Append(owner);
            string r = sb.ToString();
            if (!lower) return Upper(r);                                    // SHOUTED by the mod
            return text[0] >= 'a' && text[0] <= 'z' ? LowerFirst(r) : UpperFirst(r);
        }

        /// <summary>Which form goes with this name - 0 he, 1 she, 2 it, 3 they; -1 when the text is not the name of a thing.</summary>
        private static int Gender(string text)
        {
            int count;
            string owner;
            bool lower;
            Thing thing = Parse(text, new string[MaxKinds][], out count, out owner, out lower);
            return thing == null ? -1 : thing.Gender;
        }

        /// <summary>Takes a name apart: the kinds in front (found, count), the thing, whose it is.</summary>
        private static Thing Parse(string text, string[][] found, out int count, out string owner, out bool lower)
        {
            count = 0;
            owner = null;
            lower = false;
            int n = text.Length;
            if (n < 2 || n > 120) return null;
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                if (c == ' ') { if (i == 0 || i == n - 1 || text[i - 1] == ' ') return null; }
                else if (c >= 'a' && c <= 'z') lower = true;
                else if (!((c >= 'A' && c <= 'Z') || c == '\'' || c == '-')) return null;
            }
            string u = lower ? text.ToUpperInvariant() : text;
            int cut = u.LastIndexOf(" OF THE ", StringComparison.Ordinal);
            if (cut > 0 && owners.TryGetValue(u.Substring(cut + 8), out owner)) u = u.Substring(0, cut);
            else owner = null;
            return Kinds(u, 0, found, ref count);
        }

        /// <summary>The kinds in front, one after another, and the thing they lead to. Null: the text is something else.</summary>
        private static Thing Kinds(string u, int from, string[][] found, ref int count)
        {
            Thing thing;
            if (things.TryGetValue(from == 0 ? u : u.Substring(from), out thing)) return thing;
            if (count == MaxKinds) return null;
            // a kind is one to three words: "SHARP", "STATIC FIELD"
            int space = from;
            for (int words = 0; words < 3; words++)
            {
                space = u.IndexOf(' ', space + 1);
                if (space < 0) break;
                string[] forms;
                if (!kinds.TryGetValue(u.Substring(from, space - from), out forms)) continue;
                found[count++] = forms;
                thing = Kinds(u, space + 1, found, ref count);
                if (thing != null) return thing;
                count--;
            }
            return null;
        }

        /// <summary>
        /// "Rare Sharp Striker Module of the Brawler", "Armored Swift Brutish Warlord": names the mod wrote one after another
        /// (a rarity and an item, the modifiers of an elite). Every part starts with a capital and is known to the dictionary
        /// as it stands; together they cover the whole run. The longest name wins at every step.
        /// </summary>
        private static string Names(string run)
        {
            if (run.Length > 120 || run.IndexOf(' ') < 0 || !IsUpperLetter(run[0])) return null;
            StringBuilder sb = new StringBuilder(run.Length + 16);
            int budget = 200;
            return NamesFrom(run, 0, sb, ref budget) ? sb.ToString() : null;
        }

        private static bool NamesFrom(string run, int from, StringBuilder sb, ref int budget)
        {
            int mark = sb.Length;
            for (int end = run.Length; end > from; end--)
            {
                if (end < run.Length && (run[end] != ' ' || end + 1 >= run.Length || !IsUpperLetter(run[end + 1]))) continue;
                if (from == 0 && end == run.Length) continue;         // the whole run was looked up already
                if (--budget < 0) return false;
                string t = Exact(run.Substring(from, end - from));
                if (t == null) continue;
                if (from > 0) sb.Append(' ');
                sb.Append(t);
                if (end == run.Length || NamesFrom(run, end + 1, sb, ref budget)) return true;
                sb.Length = mark;
            }
            return false;
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
            // "Rare Health upgrade" + «  * родство с классом»: the ending was replaced inside the mod, the sentence in front of it
            // is one the mod puts together and has to be translated here - all of it, or the text is left alone
            if (!IsLatinLetter(text[text.Length - 1]))
            {
                for (int i = 0; i < suffixes.Length; i++)
                {
                    string v = suffixes[i].Value;
                    if (v.Length < 3 || text.Length <= v.Length || !HasCyrillic(v)
                        || string.CompareOrdinal(text, text.Length - v.Length, v, 0, v.Length) != 0) continue;
                    string t = Sub(text.Substring(0, text.Length - v.Length), depth + 1);
                    if (t != null) return t + v;
                }
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
            string before = null;
            while (true)
            {
                int end = at < 0 ? text.Length : at;
                string part = text.Substring(start, end - start);
                bool unknown = false;
                // "Plating · Rare": the kind of an item right after its slot
                string t = (mode == Cells && before != null ? Beside(Gender(before), part) : null) ?? Piece(part, depth, ref unknown);
                before = part;
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
            int thing = -1;             // the gender of the thing this line has named so far
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
                    // "PLATING</color>  <color=#4FA3FF>RARE": the kind of the item in a slot, after the slot
                    string t = Beside(thing, run) ?? Piece(run, depth, ref unknown);
                    if (unknown) return null;
                    if (things != null && run.Length <= 120 && IsLatinLetter(run[0]))
                    {
                        int g = Gender(run);
                        if (g >= 0) thing = g;
                    }
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
                string t = Beside(thing, run) ?? Piece(run, depth, ref unknown);
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
            // "(Spend 1,000 Scrap in the mod shop (all runs))": the inner bracket belongs to the text, only the outer one was added
            int open = 0;
            for (int i = head; i < tail; i++)
            {
                if (text[i] == '(') open++;
                else if (text[i] == ')' && open > 0) open--;
            }
            while (open > 0 && tail < text.Length && text[tail] == ')') { tail++; open--; }
            // "x3: carried items take 8% less damage": how many points it takes, then a text of its own
            if (text.Length > 5 && text[0] == 'x' && text[1] >= '0' && text[1] <= '9')
            {
                int k = 2;
                while (k < text.Length && k < 5 && text[k] >= '0' && text[k] <= '9') k++;
                if (k + 2 < text.Length && text[k] == ':' && text[k + 1] == ' ')
                {
                    string rest = Sub(text.Substring(k + 2), depth + 1);
                    if (rest != null) return text.Substring(0, k + 2) + rest;
                }
            }
            // "[F] IRON WALL": the key of an ability in front of its name
            if (text.Length > 5 && text[0] == '[')
            {
                int close = text.IndexOf(']');
                if (close > 1 && close <= 14 && close + 2 < text.Length && text[close + 1] == ' ')
                {
                    string named = Sub(text.Substring(close + 2), depth + 1);
                    if (named != null) return text.Substring(0, close + 2) + named;
                }
            }
            if ((head == 0 && tail == text.Length) || tail - head < 2) return null;
            string core;
            // "· Four new run boons join the vote pool: Quick Study, Data Mine." - a bullet in front of a sentence of the mod's
            // data: the full stop at the end is the sentence's own
            if (head > 0 && tail < text.Length && text.Length - head >= 2)
            {
                core = Sub(text.Substring(head), depth + 1);
                if (core != null) return text.Substring(0, head) + core;
            }
            core = Sub(text.Substring(head, tail - head), depth + 1);
            if (core != null) return text.Substring(0, head) + core + text.Substring(tail);
            // "Medium (47 FPS in the first minute)" + ".": the bracket belongs to the text, only the full stop was added
            int n = text.Length;
            if (head == 0 && n - tail >= 2 && (text[n - 1] == '.' || text[n - 1] == '!' || text[n - 1] == '?' || text[n - 1] == ':' || text[n - 1] == ',')
                && (text[n - 2] == ')' || text[n - 2] == ']'))
            {
                core = Sub(text.Substring(0, n - 1), depth + 1);
                if (core != null) return core + text.Substring(n - 1);
            }
            return null;
        }

        private static bool Peelable(char c)
        {
            if (c <= ' ') return true;
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '<' || c == '>' || c == '{' || c == '}' || c == '%') return false;
            if (c < 0x80) return true;                       // ASCII punctuation
            // arrows, squares and triangles, stars, check marks and crosses: "> FEATURED \u2716", "\u25B2 CAN ADD", "\u2605 CAPSTONE"
            if ((c >= '\u2190' && c <= '\u21FF') || (c >= '\u25A0' && c <= '\u25FF') || (c >= '\u2600' && c <= '\u27BF')) return true;
            return c == '\u2022' || c == '\u00B7' || c == '\u2026' || c == '\u2013' || c == '\u2014' || c == '\u00D7'
                || c == '\u2018' || c == '\u2019' || c == '\u201C' || c == '\u201D' || c == '\u2039' || c == '\u203A' || c == '\u00A0';
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

        /// <summary>Written in capitals by the mod: no small Latin letter outside rich text tags, and at least three capital ones.</summary>
        private static bool Shouted(string s)
        {
            int capitals = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<')
                {
                    int close = s.IndexOf('>', i);
                    if (close > i && close - i <= 40) { i = close; continue; }
                }
                if (c >= 'a' && c <= 'z') return false;
                if (c >= 'A' && c <= 'Z') capitals++;
            }
            return capitals >= 3;
        }

        /// <summary>
        /// The text in capitals the way the mod's HUD writes it: rich text tags stay as they are, and so do the {0} placeholders
        /// of a pattern (<paramref name="holes"/>).
        /// </summary>
        private static string UpperOutside(string s, bool holes)
        {
            StringBuilder sb = null;
            int n = s.Length, from = 0;
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                int end = -1;
                if (c == '<')
                {
                    int close = s.IndexOf('>', i);
                    if (close > i && close - i <= 40) end = close;
                }
                else if (holes && c == '{')
                {
                    int close = s.IndexOf('}', i);
                    if (close > i) end = close;
                }
                if (end < 0) continue;
                if (sb == null) sb = new StringBuilder(n);
                if (i > from) sb.Append(Upper(s.Substring(from, i - from)));
                sb.Append(s, i, end - i + 1);
                from = end + 1;
                i = end;
            }
            if (sb == null) return Upper(s);
            if (from < n) sb.Append(Upper(s.Substring(from)));
            return sb.ToString();
        }

        private static bool HasCyrillic(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] >= '\u0400' && s[i] <= '\u04FF') return true;
            return false;
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
            public int[] Form;         // "Rarity: {0}": the form a rarity takes there (0 he, 1 she, 2 it, 3 they), -1 as the dictionary has it
            public bool[] Rarity;      // "{0} gear": nothing but a rarity may stand there ("team gear" is another text)
            public bool Loud;          // the copy in capitals of another pattern (see Shout)

            /// <summary>
            /// The same pattern for a text the mod put in capitals after filling it in ("TAKEN BY {0}" for "Taken by {0}"):
            /// what the pattern is and what its placeholders may take was worked out from the text as the mod wrote it, so
            /// all of that is kept - only the letters to look for and the letters of the answer change. Null when the pattern
            /// is written in capitals already.
            /// </summary>
            public Pattern Shout()
            {
                string[] lits = new string[Lits.Length];
                bool same = true;
                for (int i = 0; i < lits.Length; i++)
                {
                    lits[i] = UpperOutside(Lits[i], false);
                    if (!string.Equals(lits[i], Lits[i], StringComparison.Ordinal)) same = false;
                }
                if (same) return null;
                Pattern p = (Pattern)MemberwiseClone();
                p.Lits = lits;
                p.Template = UpperOutside(Template, true);
                p.Loud = true;
                return p;
            }

            private const byte Value = 0, Clause = 1, Line = 2;

            public static Pattern Parse(string original, string translation, bool explicitPattern, int[] numbers, int[] words, int[] forms)
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
                    foreach (string h in used) if (Fmt.FormatIndex(h) == p.Slots[k] || Fmt.ChoiceIndex(h) == p.Slots[k]) kept = true;
                    p.Dropped[k] = !kept;
                }
                p.Kind = new byte[p.Slots.Length];
                p.Roomy = new bool[p.Slots.Length];
                p.Framed = new bool[p.Slots.Length];
                p.Known = new bool[p.Slots.Length];
                p.Form = new int[p.Slots.Length];
                p.Rarity = new bool[p.Slots.Length];
                for (int k = 0; k < p.Slots.Length; k++)
                {
                    int w = words == null ? -1 : Array.IndexOf(words, p.Slots[k]);
                    int form = w >= 0 && forms != null && w < forms.Length ? forms[w] : -1;
                    p.Known[k] = w >= 0;
                    p.Form[k] = form < 0 ? -1 : form & 3;
                    p.Rarity[k] = form >= 4;
                }
                for (int k = 0; k < p.Slots.Length; k++)
                {
                    // a long sentence that goes on after the placeholder cannot be mistaken for another one: what it caught is
                    // its own - the name of a feature as the log has it, the name of another mod - and may stay as written
                    p.Framed[k] = letters >= 30 && owns[k] >= 3 && owns[k + 1] >= 3;
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
                                      || (after.Length > 1 && after[0] == ' ' && ((after[1] >= 'A' && after[1] <= 'Z') || after[1] == '<' || after[1] == '-' || after[1] == '('))
                                      // "{0}  (8 / 12 at Hard / Brutal)": two spaces set a remark apart from the text it belongs to
                                      || (after.Length > 2 && after[0] == ' ' && after[1] == ' ' && (after[2] == '(' || after[2] == '-' || after[2] == '<'));
                    p.Kind[k] = lineBefore && lineAfter ? Line : breakBefore && breakAfter ? Clause : Value;
                    bool dashBefore = bl > 1 && before[bl - 1] == ' ' && (before[bl - 2] == '\u2013' || before[bl - 2] == '\u2014');
                    p.Roomy[k] = (breakBefore || dashBefore) && breakAfter;
                }
                foreach (string l in p.Lits) p.Weight += l.Length;
                return p;
            }

            /// <summary>
            /// Which of the three forms of a Russian word goes with this number: 0 - «1 осколок», 1 - «2 осколка» (and fractions),
            /// 2 - «5 осколков». The number is the last one in the text: "+3", "1 250", "x2".
            /// </summary>
            internal static int Plural(string v)
            {
                int end = v.Length;
                while (end > 0 && !IsDigit(v[end - 1])) end--;
                if (end == 0) return 2;
                int start = end;
                while (start > 0 && IsDigit(v[start - 1])) start--;
                // a fraction: "0.5", "1,5" - but "1,200" is twelve hundred
                if (start > 1 && (v[start - 1] == '.' || v[start - 1] == ',') && IsDigit(v[start - 2]) && !(v[start - 1] == ',' && end - start == 3)) return 1;
                int tens = end - start >= 2 ? v[end - 2] - '0' : 0, ones = v[end - 1] - '0';
                if (tens == 1) return 2;
                return ones == 1 ? 0 : ones >= 2 && ones <= 4 ? 1 : 2;
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
                        // (and no blanks around it: in "</color> 3 cast" the space belongs to the line, not to the number)
                        if (v.Length > 0 && (IsBlank(v[0]) || IsBlank(v[v.Length - 1]))) return null;
                        bool digit = false;
                        for (int q = 0; q < v.Length; q++)
                        {
                            if (char.IsLetter(v[q])) return null;
                            if (v[q] >= '0' && v[q] <= '9') digit = true;
                        }
                        if (!digit) return null;
                        continue;
                    }
                    if (Known[k])
                    {
                        // one of the mod's own words belongs here ("Rare", "in the shop"): the dictionary has it, or this is another text
                        // (a word that arrives in Russian was replaced inside the mod already: "Rarity: " + «Все»)
                        if (Exact(v) == null && !HasCyrillic(v)) return null;
                        if (Rarity[k] && InForm(v, 0, false) == null) return null;
                    }
                    else if (Tight && !Simple(v) && !(things != null && Built(v) != null))      // (a long name the mod built is still a name)
                    {
                        // "Gift failed: " + "that player isn't here", "+25 Scrap – " + "starter tool": the reason is a text of the
                        // mod's own. A short pattern may take it when - and only when - that text turns out to have a translation
                        // (a text with commas of its own passes when the dictionary has it exactly as it stands:
                        //  "Hit 3+ monsters with one Breach Charge, 2 times" + "  (3 / 4 at Hard / Brutal)")
                        // (a text the dictionary has letter for letter may be of any length: the description of a talent
                        //  after "<b>Front Row</b> (replaces Ringside): ")
                        if (!Roomy[k] || k >= 30 || Dropped[k]) return null;
                        if ((v.Length > 80 || !Whole(v)) && Exact(v) == null && (shapes == null || Shape(v) == null)) return null;
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
                int lastSlot = -1, lastEnd = -1;        // the placeholder filled last and where the template went on after it
                for (int i = 0; i < Template.Length; i++)
                {
                    char c = Template[i];
                    if (c == '{')
                    {
                        if (i + 1 < Template.Length && Template[i + 1] == '{') { sb.Append('{'); i++; continue; }
                        int close = Template.IndexOf('}', i);
                        if (close < 0) return null;
                        int bar = Template.IndexOf('|', i, close - i);
                        if (bar > 0)
                        {
                            // "{0|сухожилие|сухожилия|сухожилий}": the word in the form the number in {0} asks for (1, 2-4, 5 and more)
                            int of = Fmt.ChoiceIndex(Template.Substring(i, close - i + 1));
                            int numberSlot = of < 0 ? -1 : Array.IndexOf(Slots, of);
                            if (numberSlot < 0) return null;
                            string[] wordForms = Template.Substring(bar + 1, close - bar - 1).Split('|');
                            string word = wordForms[Plural(caught[numberSlot])];
                            sb.Append(Loud ? Upper(word) : word);
                            i = close;
                            continue;
                        }
                        int index = Fmt.FormatIndex(Template.Substring(i, close - i + 1));
                        int slot = index < 0 ? -1 : Array.IndexOf(Slots, index);
                        if (slot < 0)
                        {
                            if (index >= 0 && Ghosts != null && Array.IndexOf(Ghosts, index) >= 0) { i = close; continue; }
                            return null;
                        }
                        string value = caught[slot];
                        // "{0} {1}", a rarity and the item next to it: together they are one name, and its words agree
                        if (things != null && slot + 1 < caught.Length && Lits[slot + 1] == " "
                            && close + 2 < Template.Length && Template[close + 1] == ' ' && Template[close + 2] == '{')
                        {
                            int close2 = Template.IndexOf('}', close + 2);
                            if (close2 > 0 && Fmt.FormatIndex(Template.Substring(close + 2, close2 - close - 1)) == Slots[slot + 1])
                            {
                                string next = caught[slot + 1];
                                string both = Built(value + " " + next);
                                if (both == null)
                                {
                                    // "(Rare Plating, 25% off)": the name ends at the comma, the rest is a text of its own
                                    int comma = next.IndexOf(", ", StringComparison.Ordinal);
                                    string name = comma > 0 ? Built(value + " " + next.Substring(0, comma)) : null;
                                    if (name != null)
                                    {
                                        bool cut = false;
                                        string rest = Piece(next.Substring(comma), depth, ref cut);       // ", 25% off" is an entry as it stands
                                        if (rest == null && !cut)
                                        {
                                            rest = Piece(next.Substring(comma + 2), depth, ref cut);
                                            if (rest != null) rest = ", " + rest;
                                        }
                                        if (cut) return null;
                                        if (rest != null) both = name + rest;
                                    }
                                }
                                if (both != null) { sb.Append(both); i = close2; continue; }
                            }
                        }
                        // "Rarity: {0}": the rarity in the form this sentence asks for - «Редкость: редкая»
                        if (Form[slot] >= 0 && things != null)
                        {
                            string formed = InForm(value, Form[slot], Worded(sb));
                            if (formed != null)
                            {
                                sb.Append(formed);
                                lastSlot = slot;
                                lastEnd = close + 1;
                                i = close;
                                continue;
                            }
                        }
                        // a name inside the text may be translatable on its own: "Equipped: Iron Plate"
                        bool unknown = false;
                        string inner = null;
                        // "{0} ({1})", an item and its rarity in a bracket: the rarity takes the form of the item
                        if (slot == lastSlot + 1 && slot > 0 && i - lastEnd == 2 && Template[lastEnd] == ' ' && Template[lastEnd + 1] == '(' && Lits[slot] == " (")
                            inner = Agreed(caught[slot - 1], value, depth, ref unknown);
                        if (inner == null) inner = Piece(value, depth, ref unknown);
                        if (unknown) return null;
                        if (inner == null && (owed & (1 << slot)) != 0) return null;
                        // ...and a clause has to be: "<English sentence> Это нельзя отменить." helps nobody
                        bool asWritten = inner == null && Framed[slot] && value.Length <= 200 && value.IndexOf('<') < 0 && !Several(value);
                        if (inner == null && Kind[slot] == Clause && HasLatin(value) && !Simple(value) && !asWritten) return null;
                        if (inner == null && Counter && HasLatin(value)) return null;
                        // ...and what stays English inside a sentence is a name or a nickname, not the rest of another sentence
                        if (inner == null && Kind[slot] == Value && HasLatin(value) && !Simple(value) && !Short(value) && !asWritten) return null;
                        sb.Append(inner ?? value);
                        lastSlot = slot;
                        lastEnd = close + 1;
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
                int[] nums, words, forms;
                lang.Numbers.TryGetValue(kv.Key, out nums);
                lang.Words.TryGetValue(kv.Key, out words);
                lang.WordForms.TryGetValue(kv.Key, out forms);
                Pattern p = Pattern.Parse(kv.Key, kv.Value, true, nums, words, forms);
                if (p != null) all.Add(p);
            }
            foreach (KeyValuePair<string, string> kv in m)
            {
                if (kv.Key.IndexOf('{') < 0 || lang.Patterns.ContainsKey(kv.Key)) continue;
                int[] nums, words, forms;
                lang.Numbers.TryGetValue(kv.Key, out nums);
                lang.Words.TryGetValue(kv.Key, out words);
                lang.WordForms.TryGetValue(kv.Key, out forms);
                Pattern p = Pattern.Parse(kv.Key, kv.Value, false, nums, words, forms);
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
            // ...and the same in capitals, kept apart: only a text written in capitals is ever compared with them
            Dictionary<string, List<Pattern>> loudHeads = new Dictionary<string, List<Pattern>>(StringComparer.Ordinal);
            Dictionary<string, List<Pattern>> loudTails = new Dictionary<string, List<Pattern>>(StringComparer.Ordinal);
            List<Pattern> loudRest = new List<Pattern>();
            foreach (Pattern p in all)
            {
                Sort(p, heads, tails, rest);
                Pattern loud = p.Shout();
                if (loud != null) Sort(loud, loudHeads, loudTails, loudRest);
            }

            Dictionary<string, string> sh = null;
            if (lang.Shapes.Count > 0)
            {
                sh = new Dictionary<string, string>(lang.Shapes.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in lang.Shapes)
                {
                    // "+{0}% max health for {1}s": the numbers of the entry in the order they stand
                    StringBuilder key = new StringBuilder(kv.Key.Length);
                    bool good = true;
                    for (int i = 0; i < kv.Key.Length && good; i++)
                    {
                        char c = kv.Key[i];
                        if (c != '{') { key.Append(c); continue; }
                        int close = kv.Key.IndexOf('}', i);
                        if (close < 0 || Fmt.FormatIndex(kv.Key.Substring(i, close - i + 1)) < 0) { good = false; break; }
                        key.Append(NumberMark);
                        i = close;
                    }
                    if (good) sh[key.ToString()] = kv.Value;
                }
                // "KILL 3 ELITES": the HUD writes the goal of a contract in capitals
                foreach (KeyValuePair<string, string> kv in new List<KeyValuePair<string, string>>(sh))
                {
                    string loud = UpperOutside(kv.Key, false);
                    if (!sh.ContainsKey(loud)) sh[loud] = UpperOutside(kv.Value, true);
                }
            }

            Dictionary<string, string[]> kn = null;
            Dictionary<string, Thing> th = null;
            Dictionary<string, string> ow = null;
            if (lang.Nouns.Count > 0)
            {
                kn = new Dictionary<string, string[]>(lang.Adjectives.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, string[]> kv in lang.Adjectives) kn[kv.Key.ToUpperInvariant()] = kv.Value;
                ow = new Dictionary<string, string>(lang.Owners.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in lang.Owners) ow[kv.Key.ToUpperInvariant()] = kv.Value;
                th = new Dictionary<string, Thing>(lang.Nouns.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in lang.Nouns)
                {
                    // "n" - the dictionary has the name itself; "n|снаряжение" - a common word, its translation is given here
                    Thing thing = new Thing();
                    thing.Gender = "mfnp".IndexOf(kv.Value[0]);
                    int bar = kv.Value.IndexOf('|');
                    if (bar > 0) thing.Name = kv.Value.Substring(bar + 1);
                    else if (!m.TryGetValue(kv.Key, out thing.Name)) continue;
                    if (thing.Gender >= 0 && !string.IsNullOrEmpty(thing.Name)) th[kv.Key.ToUpperInvariant()] = thing;
                }
                if (th.Count == 0) th = null;
            }

            minLength = lo;
            maxLength = hi;
            kinds = kn;
            owners = ow;
            things = th;
            shapes = sh;
            upper = up;
            prefixes = pre.ToArray();
            suffixes = suf.ToArray();
            byHead = Freeze(heads);
            byTail = Freeze(tails);
            loose = rest.ToArray();
            loudHead = Freeze(loudHeads);
            loudTail = Freeze(loudTails);
            loudLoose = loudRest.ToArray();
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
                // The file belongs to one version of the add-on: what an older one could not translate says nothing about this
                // one (and would hide the same text if it is still English - a text is written down once).
                string header = "# RepoRPG RU " + BuildInfo.Version + " - тексты мода, которые остались английскими (целиком или частично; «~» - частично). "
                                + "С каждой новой версией русификатора файл начинается заново.";
                try
                {
                    string[] lines = File.Exists(logPath) ? File.ReadAllLines(logPath, Encoding.UTF8) : new string[0];
                    if (lines.Length > 0 && string.Equals(lines[0], header, StringComparison.Ordinal))
                    {
                        foreach (string line in lines)
                            if (line.Length > 0) seen.Add(Zeroed(line));
                    }
                    else File.WriteAllText(logPath, header + "\n", new UTF8Encoding(false));
                }
                catch { }
                nextFlush = Environment.TickCount;
                try { AppDomain.CurrentDomain.ProcessExit += delegate { Flush(); }; } catch { }
                logging = true;
            }
        }

        /// <summary>Where a pattern is looked for: by the two letters it starts with, by the two it ends with, or among the rest.</summary>
        private static void Sort(Pattern p, Dictionary<string, List<Pattern>> heads, Dictionary<string, List<Pattern>> tails, List<Pattern> rest)
        {
            string first = p.Lits[0], lastLit = p.Lits[p.Lits.Length - 1];
            if (first.Length >= 2) Bucket(heads, first.Substring(0, 2), p);
            else if (lastLit.Length >= 2) Bucket(tails, lastLit.Substring(lastLit.Length - 2), p);
            else rest.Add(p);
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
        internal static string FindConfigPath(string home)
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

        /// <summary>
        /// A text that came out half Russian, half English ("Благословение: Быстрые ноги (+12% walk and sprint speed)"):
        /// it goes into the log with "~ " in front, the way it was and the way it was shown.
        /// </summary>
        private static bool Leftover(string s)
        {
            bool russian = false;
            int run = 0, best = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '\u0400' && c <= '\u04FF') { russian = true; run = 0; continue; }
                if (c == '<')
                {
                    int close = s.IndexOf('>', i);
                    if (close > i && close - i < 24) { i = close; run = 0; continue; }
                }
                // words written in small letters: names, keys and abbreviations ("RepoRPG", "HUD", "Tab") are no news
                if (c >= 'a' && c <= 'z') { if (run > 0 || i == 0 || !IsLatinLetter(s[i - 1])) run++; if (run > best) best = run; }
                else run = 0;
            }
            return russian && best >= 4;
        }

        private static void Note(string text)
        {
            try
            {
                if (text != null && seen.Count < MaxLogged && text.Length <= 400)
                {
                    string line = Flatten(text);
                    // "$7 300  33M AHEAD", "$7 300  32M AHEAD", ...: one text, whatever its numbers are
                    if (seen.Add(Zeroed(line))) pending.Add(line);
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

        /// <summary>The text with every digit turned into 0: counters and distances do not make it another text.</summary>
        private static string Zeroed(string s)
        {
            char[] a = null;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '1' || s[i] > '9') continue;
                if (a == null) a = s.ToCharArray();
                a[i] = '0';
            }
            return a == null ? s : new string(a);
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
