// Loads every *.po below a folder and sorts the entries into the tables the patcher / runtime need.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RepoRPG_RU
{
    internal sealed class Lang
    {
        /// <summary>Entries without msgctxt.</summary>
        public readonly Dictionary<string, string> Global = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>msgctxt "@Type::Member" -> (english -> russian).</summary>
        public readonly Dictionary<string, Dictionary<string, string>> Scoped = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        /// <summary>
        /// msgctxt "@Type::Member#7": the entry is one piece of sentence 7, which the mod glues together in that method
        /// ("Slotted " + a + " into " + b). Pieces are replaced inside the code all together or not at all.
        /// scope -> (sentence -> pieces).
        /// </summary>
        public readonly Dictionary<string, Dictionary<string, List<string>>> Sentences = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        /// <summary>scope -> texts that must not be replaced inside the code (two sentences disagree about them).</summary>
        public readonly Dictionary<string, HashSet<string>> Unsafe = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        /// <summary>msgctxt "term".</summary>
        public readonly Dictionary<string, string> Terms = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>msgctxt "numbers": sentence -> the placeholders that hold a number wherever the mod makes that sentence.</summary>
        public readonly Dictionary<string, int[]> Numbers = new Dictionary<string, int[]>(StringComparer.Ordinal);
        /// <summary>msgctxt "words": sentence -> the placeholders that hold one of the mod's own words.</summary>
        public readonly Dictionary<string, int[]> Words = new Dictionary<string, int[]>(StringComparer.Ordinal);
        /// <summary>msgctxt "pattern": "{0} cancelled the offer" - matched against finished texts at display time only.</summary>
        public readonly Dictionary<string, string> Patterns = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>msgctxt "data" (key "") and "data:stem" (key stem).</summary>
        public readonly Dictionary<string, Dictionary<string, string>> Data = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        /// <summary>
        /// msgctxt "shape": a text of the mod's data whose numbers change (a talent at rank 3, a contract with another target).
        /// "+{0}% max health for {1}s" - matched at display time against the text with every number taken out.
        /// </summary>
        public readonly Dictionary<string, string> Shapes = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>
        /// The words the mod builds names from ("Rare Sharp Striker Module of the Brawler"). In Russian they have to agree, so the
        /// name is put together at the moment it is drawn:
        ///   msgctxt "adj"   a word names start with, its four forms: "Острый|Острая|Острое|Острые" (he, she, it, they);
        ///   msgctxt "noun"  the thing itself: its gender (m, f, n, p), then "|" and its translation when the dictionary has none;
        ///   msgctxt "of"    whose it is ("... of the Brawler"): the genitive, "драчуна".
        /// </summary>
        public readonly Dictionary<string, string[]> Adjectives = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Nouns = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Owners = new Dictionary<string, string>(StringComparer.Ordinal);

        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Files = new List<string>();
        public int Entries;
        public int Rejected;
        public ulong Fingerprint = Hash.Seed;

        public static Lang Load(string dir)
        {
            Lang lang = new Lang();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return lang;
            string[] files = Directory.GetFiles(dir, "*.po", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(f);
                    lang.Fingerprint = Hash.Fnv(lang.Fingerprint, Path.GetFileName(f));
                    lang.Fingerprint = Hash.Fnv(lang.Fingerprint, bytes);
                    lang.Files.Add(f);
                    List<PoEntry> list = new List<PoEntry>();
                    Po.Parse(Encoding.UTF8.GetString(bytes), Path.GetFileName(f), list, lang.Problems);
                    foreach (PoEntry e in list) lang.Add(e);
                }
                catch (Exception ex)
                {
                    lang.Problems.Add(Path.GetFileName(f) + ": " + ex.Message);
                }
            }
            return lang;
        }

        private void Add(PoEntry e)
        {
            if (e.Ctx == "numbers" || e.Ctx == "words")
            {
                // not a translation: msgstr lists placeholders of the sentence ("0,2") - the ones that are always numbers,
                // or the ones that hold a word of the mod's own (a rarity, the name of a tab)
                List<int> idx = new List<int>();
                foreach (string part in e.Str.Split(','))
                {
                    int k;
                    if (int.TryParse(part.Trim(), out k) && k >= 0) idx.Add(k);
                }
                if (idx.Count > 0) (e.Ctx == "numbers" ? Numbers : Words)[e.Id] = idx.ToArray();
                return;
            }
            bool data = e.Ctx != null && (e.Ctx == "data" || e.Ctx.StartsWith("data:", StringComparison.Ordinal));
            if (data ? !Fmt.SameTokens(e.Id, e.Str) : !Fmt.SameHoles(e.Id, e.Str))
            {
                Rejected++;
                Problems.Add(e.File + ":" + e.Line + ": {placeholders} differ from the original, entry ignored");
                return;
            }
            Entries++;
            if (e.Ctx == "shape") { Shapes[e.Id] = e.Str; return; }
            if (e.Ctx == null) { Global[e.Id] = e.Str; return; }
            if (e.Ctx == "term") { Terms[e.Id] = e.Str; return; }
            if (e.Ctx == "adj" || e.Ctx == "noun" || e.Ctx == "of")
            {
                string[] parts = e.Str.Split('|');
                bool good = e.Id.Trim().Length > 0;
                foreach (string part in parts) if (part.Trim().Length == 0) good = false;
                if (e.Ctx == "adj") good = good && parts.Length == 4;
                else if (e.Ctx == "noun") good = good && parts.Length <= 2 && parts[0].Length == 1 && "mfnp".IndexOf(parts[0][0]) >= 0;
                else good = good && parts.Length == 1;
                if (!good)
                {
                    Entries--;
                    Rejected++;
                    Problems.Add(e.File + ":" + e.Line + ": " + (e.Ctx == "adj" ? "an adjective needs four forms (m|f|n|p)"
                        : e.Ctx == "noun" ? "a noun needs its gender (m, f, n or p)" : "one word form expected") + ", entry ignored");
                    return;
                }
                if (e.Ctx == "adj") Adjectives[e.Id] = parts;
                else if (e.Ctx == "noun") Nouns[e.Id] = e.Str;
                else Owners[e.Id] = e.Str;
                return;
            }
            if (e.Ctx == "pattern")
            {
                bool hole = false;
                foreach (string h in Fmt.Holes(e.Id)) if (Fmt.FormatIndex(h) >= 0) hole = true;
                if (!hole)
                {
                    Entries--;
                    Rejected++;
                    Problems.Add(e.File + ":" + e.Line + ": a pattern needs at least one {0} placeholder, entry ignored");
                    return;
                }
                Patterns[e.Id] = e.Str;
                return;
            }
            if (e.Ctx[0] == '@')
            {
                string scope = e.Ctx.Substring(1).Trim();
                int hash = scope.LastIndexOf('#');
                if (hash > 0)
                {
                    string sentence = scope.Substring(hash + 1).Trim();
                    scope = scope.Substring(0, hash).Trim();
                    Dictionary<string, List<string>> groups;
                    if (!Sentences.TryGetValue(scope, out groups)) { groups = new Dictionary<string, List<string>>(StringComparer.Ordinal); Sentences[scope] = groups; }
                    List<string> pieces;
                    if (!groups.TryGetValue(sentence, out pieces)) { pieces = new List<string>(); groups[sentence] = pieces; }
                    pieces.Add(e.Id);
                }
                Dictionary<string, string> have;
                string was;
                if (Scoped.TryGetValue(scope, out have) && have.TryGetValue(e.Id, out was) && !string.Equals(was, e.Str, StringComparison.Ordinal))
                {
                    HashSet<string> bad;
                    if (!Unsafe.TryGetValue(scope, out bad)) { bad = new HashSet<string>(StringComparer.Ordinal); Unsafe[scope] = bad; }
                    bad.Add(e.Id);
                    Problems.Add(e.File + ":" + e.Line + ": two different translations of one text inside " + scope + ", it stays untouched in the code");
                    return;
                }
                Put(Scoped, scope, e);
                return;
            }
            if (e.Ctx == "data") { Put(Data, "", e); return; }
            if (e.Ctx.StartsWith("data:", StringComparison.Ordinal)) { Put(Data, e.Ctx.Substring(5).Trim(), e); return; }
            Entries--;
            Rejected++;
            Problems.Add(e.File + ":" + e.Line + ": unknown msgctxt \"" + e.Ctx + "\", entry ignored");
        }

        private static void Put(Dictionary<string, Dictionary<string, string>> table, string key, PoEntry e)
        {
            Dictionary<string, string> map;
            if (!table.TryGetValue(key, out map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                table[key] = map;
            }
            map[e.Id] = e.Str;
        }
    }
}
