// Display-time check of the real dictionary: every English sample must come out as the expected Russian text.
using System;
using System.IO;
using System.Text;

internal static class LangCheck
{
    private static string Un(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { char d = s[++i]; sb.Append(d == 'n' ? '\n' : d); }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static int Main(string[] args)
    {
        int bad = 0, n = 0;
        foreach (string line in File.ReadAllLines(args[0], Encoding.UTF8))
        {
            string[] c = line.Split('\t');
            if (c.Length != 2) continue;
            string en = Un(c[0]), ru = Un(c[1]);
            n++;
            string got = RepoRPG_RU.Loc.T(en);
            if (got == ru) continue;
            bad++;
            if (bad <= 60) Console.WriteLine("MISS " + c[0] + "\n  want " + c[1] + "\n  got  " + got.Replace("\n", "\\n"));
        }
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("@"))
            {
                // speed: a file of texts nobody translated (the worst case - every way of taking a text apart is tried)
                string[] lines = File.ReadAllLines(args[i].Substring(1), Encoding.UTF8);
                System.Diagnostics.Stopwatch all = System.Diagnostics.Stopwatch.StartNew();
                double worst = 0; string worstText = ""; int changed = 0;
                foreach (string line in lines)
                {
                    string text = Un(line);
                    System.Diagnostics.Stopwatch one = System.Diagnostics.Stopwatch.StartNew();
                    string t = RepoRPG_RU.Loc.T(text);
                    double ms = one.Elapsed.TotalMilliseconds;
                    if (t != text) changed++;
                    if (ms > worst) { worst = ms; worstText = line; }
                }
                Console.WriteLine("bench: " + lines.Length + " texts in " + all.Elapsed.TotalMilliseconds.ToString("0") + " ms, slowest " + worst.ToString("0.0") + " ms ("
                                  + (worstText.Length > 70 ? worstText.Substring(0, 70) + "..." : worstText) + "), changed " + changed);
                continue;
            }
            if (args[i].StartsWith("%"))
            {
                // %in.txt:out.txt - what the translator does to texts it has no entry for, one "text => result" per changed line:
                // two such files, made before and after a change of the translator, show what the change did
                string[] io = args[i].Substring(1).Split(':');
                StringBuilder dump = new StringBuilder();
                int count = 0;
                foreach (string line in File.ReadAllLines(io[0], Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    string text = Un(line);
                    string t = RepoRPG_RU.Loc.T(text);
                    if (t == text) continue;
                    count++;
                    dump.Append(line).Append("  =>  ").Append(t.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
                }
                File.WriteAllText(io[1], dump.ToString(), new UTF8Encoding(false));
                Console.WriteLine("dump: " + count + " changed texts -> " + io[1]);
                continue;
            }
            Console.WriteLine(args[i] + "  ->  " + RepoRPG_RU.Loc.T(Un(args[i])).Replace("\n", "\\n"));
        }
        Console.WriteLine(n + " samples, " + bad + " wrong; dictionary entries: " + RepoRPG_RU.Loc.Count);
        return bad == 0 ? 0 : 1;
    }
}
