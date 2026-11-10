// RepoRPG_RU.rules - data-driven description of what may and may not be touched in RepoRPG.dll,
// so it can be adjusted for a new version of the mod without rebuilding the patcher.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RepoRPG_RU
{
    internal sealed class HookRule
    {
        public string Type;
        public string Method;
        public string[] Params;
        public string Source;
    }

    internal sealed class CallRule
    {
        public string Type;      // glob; no '.' => only types defined in the patched module
        public string Method;    // glob
        public int[] Args;       // null = every string argument
        public bool External;    // pattern carries a namespace => matches referenced assemblies too
    }

    internal sealed class Rules
    {
        public readonly List<HookRule> Hooks = new List<HookRule>();
        public readonly List<string> Skip = new List<string>();
        public readonly List<CallRule> Veto = new List<CallRule>();
        public readonly List<CallRule> Through = new List<CallRule>();
        public readonly List<CallRule> Display = new List<CallRule>();
        public readonly List<CallRule> Keep = new List<CallRule>();
        /// <summary>[calls] Type::Method - the text handed to this call (last argument) is translated right at the call site.</summary>
        public readonly List<CallRule> Calls = new List<CallRule>();
        /// <summary>stem ("*" = all) -> JSON property names whose string values are translatable.</summary>
        public readonly Dictionary<string, HashSet<string>> Data = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        /// <summary>"stem:property" pairs that must never be translated even if a wildcard allows them.</summary>
        public readonly HashSet<string> DataDeny = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>[probes] ignore "text": this StartsWith/Contains/... argument is never applied to interface text.</summary>
        public readonly HashSet<string> ProbeIgnore = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>[probes] protect "word": a plain word searched for with Contains/IndexOf that is applied to interface text.</summary>
        public readonly HashSet<string> ProbeProtect = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Problems = new List<string>();
        public ulong Fingerprint = Hash.Seed;
        public int FileCount;

        public static Rules Load(string dir)
        {
            Rules r = new Rules();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return r;
            string[] files = Directory.GetFiles(dir, "*.rules", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(f);
                    r.Fingerprint = Hash.Fnv(r.Fingerprint, bytes);
                    r.FileCount++;
                    r.Parse(Encoding.UTF8.GetString(bytes), Path.GetFileName(f));
                }
                catch (Exception ex)
                {
                    r.Problems.Add(Path.GetFileName(f) + ": " + ex.Message);
                }
            }
            return r;
        }

        internal void Parse(string text, string file)
        {
            string section = "";
            int n = 0;
            foreach (string raw in text.Split('\n'))
            {
                n++;
                string line = raw;
                if (section == "probes")
                {
                    // quoted texts may contain '#': only whole-line comments here
                    line = line.Trim().TrimStart('\uFEFF');
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line[0] != '[')
                    {
                        try { ParseProbe(line); }
                        catch (Exception ex) { Problems.Add(file + ":" + n + ": " + ex.Message); }
                        continue;
                    }
                }
                int hash = line.IndexOf('#');
                // '#' starts a comment unless it is the argument selector of a call rule ("Type::Method#0,1")
                while (hash >= 0)
                {
                    bool selector = hash > 0 && hash + 1 < line.Length && !char.IsWhiteSpace(line[hash - 1]) && char.IsDigit(line[hash + 1]);
                    if (!selector) { line = line.Substring(0, hash); break; }
                    hash = line.IndexOf('#', hash + 1);
                }
                line = line.Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                try
                {
                    switch (section)
                    {
                        case "hooks": ParseHook(line, file + ":" + n); break;
                        case "skip": Skip.Add(line); break;
                        case "id":
                        case "veto": Veto.Add(ParseCall(line)); break;
                        case "keep": Keep.Add(ParseCall(line)); break;
                        case "through": Through.Add(ParseCall(line)); break;
                        case "display": Display.Add(ParseCall(line)); break;
                        case "calls": Calls.Add(ParseCall(line)); break;
                        case "data": ParseData(line); break;
                        case "probes": ParseProbe(line); break;
                        default: Problems.Add(file + ":" + n + ": line outside of a known [section]"); break;
                    }
                }
                catch (Exception ex)
                {
                    Problems.Add(file + ":" + n + ": " + ex.Message);
                }
            }
        }

        private void ParseProbe(string line)
        {
            int q = line.IndexOf('"');
            int end = line.LastIndexOf('"');
            if (q < 0 || end <= q) throw new FormatException("expected: ignore \"text\"  or  protect \"text\"");
            string verb = line.Substring(0, q).Trim().ToLowerInvariant();
            StringBuilder sb = new StringBuilder();
            for (int i = q + 1; i < end; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < end)
                {
                    char e = line[++i];
                    sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e);
                }
                else sb.Append(c);
            }
            if (sb.Length == 0) throw new FormatException("empty probe text");
            if (verb == "ignore") ProbeIgnore.Add(sb.ToString());
            else if (verb == "protect") ProbeProtect.Add(sb.ToString());
            else throw new FormatException("expected: ignore \"text\"  or  protect \"text\"");
        }

        private void ParseHook(string line, string source)
        {
            int sep = line.IndexOf("::", StringComparison.Ordinal);
            int open = line.IndexOf('(');
            int close = line.LastIndexOf(')');
            if (sep <= 0 || open < sep || close < open) throw new FormatException("expected Type::Method(param, ...)");
            HookRule h = new HookRule();
            h.Type = line.Substring(0, sep).Trim();
            h.Method = line.Substring(sep + 2, open - sep - 2).Trim();
            string[] ps = line.Substring(open + 1, close - open - 1).Split(',');
            List<string> list = new List<string>();
            foreach (string p in ps) if (p.Trim().Length > 0) list.Add(p.Trim());
            if (list.Count == 0) throw new FormatException("no parameters listed");
            h.Params = list.ToArray();
            h.Source = source;
            Hooks.Add(h);
        }

        private static CallRule ParseCall(string line)
        {
            CallRule c = new CallRule();
            int hash = line.IndexOf('#');
            if (hash >= 0)
            {
                List<int> args = new List<int>();
                foreach (string a in line.Substring(hash + 1).Split(','))
                    if (a.Trim().Length > 0) args.Add(int.Parse(a.Trim()));
                c.Args = args.ToArray();
                line = line.Substring(0, hash).Trim();
            }
            int sep = line.IndexOf("::", StringComparison.Ordinal);
            if (sep < 0) { c.Type = line; c.Method = "*"; }
            else { c.Type = line.Substring(0, sep).Trim(); c.Method = line.Substring(sep + 2).Trim(); }
            if (c.Type.Length == 0) c.Type = "*";
            if (c.Method.Length == 0) c.Method = "*";
            c.External = c.Type.IndexOf('.') >= 0;
            return c;
        }

        private void ParseData(string line)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new FormatException("expected stem: property, property");
            string stem = line.Substring(0, colon).Trim();
            foreach (string p in line.Substring(colon + 1).Split(','))
            {
                string name = p.Trim();
                if (name.Length == 0) continue;
                if (name[0] == '!') { DataDeny.Add(stem + ":" + name.Substring(1).Trim()); continue; }
                HashSet<string> set;
                if (!Data.TryGetValue(stem, out set)) { set = new HashSet<string>(StringComparer.Ordinal); Data[stem] = set; }
                set.Add(name);
            }
        }

        public bool DataAllowed(string stem, string property)
        {
            if (property == null) return false;
            if (DataDeny.Contains(stem + ":" + property) || DataDeny.Contains("*:" + property)) return false;
            HashSet<string> set;
            if (Data.TryGetValue(stem, out set) && set.Contains(property)) return true;
            if (Data.TryGetValue("*", out set) && set.Contains(property)) return true;
            return false;
        }

        /// <summary>Glob with '*' only, ordinal.</summary>
        public static bool Glob(string pattern, string value)
        {
            if (pattern == "*") return true;
            if (pattern.IndexOf('*') < 0) return string.Equals(pattern, value, StringComparison.Ordinal);
            return GlobAt(pattern, 0, value, 0);
        }

        private static bool GlobAt(string p, int pi, string v, int vi)
        {
            while (pi < p.Length)
            {
                if (p[pi] == '*')
                {
                    while (pi < p.Length && p[pi] == '*') pi++;
                    if (pi == p.Length) return true;
                    for (int k = vi; k <= v.Length; k++)
                        if (GlobAt(p, pi, v, k)) return true;
                    return false;
                }
                if (vi >= v.Length || p[pi] != v[vi]) return false;
                pi++; vi++;
            }
            return vi == v.Length;
        }
    }
}
