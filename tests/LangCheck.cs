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
        for (int i = 1; i < args.Length; i++) Console.WriteLine(args[i] + "  ->  " + RepoRPG_RU.Loc.T(Un(args[i])).Replace("\n", "\\n"));
        Console.WriteLine(n + " samples, " + bad + " wrong; dictionary entries: " + RepoRPG_RU.Loc.Count);
        return bad == 0 ? 0 : 1;
    }
}
