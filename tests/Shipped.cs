// Loads what is actually shipped (mod/RepoRPG_RU.rules, mod/lang/*.po) with the patcher's own readers
// and prints everything they complain about. Run by tests/langcheck.sh.
using System;
using RepoRPG_RU;

internal static class Shipped
{
    private static int Main(string[] args)
    {
        Rules rules = Rules.Load(args[0]);
        Lang lang = Lang.Load(args[1]);
        foreach (string p in rules.Problems) Console.WriteLine("  RULES " + p);
        foreach (string p in lang.Problems) Console.WriteLine("  DICTIONARY " + p);
        Console.WriteLine("shipped files: " + rules.FileCount + " rules file(s): " + rules.Hooks.Count + " hooks, " + rules.Veto.Count + " id, " + rules.Keep.Count + " keep, "
            + rules.Through.Count + " through, " + rules.Skip.Count + " skip, probes " + rules.ProbeIgnore.Count + "/" + rules.ProbeProtect.Count + "/" + rules.ProbeFree.Count
            + " (ignore/protect/free); dictionary: " + lang.Entries + " entries, " + lang.Rejected + " rejected; problems: " + (rules.Problems.Count + lang.Problems.Count));
        return rules.Problems.Count + lang.Problems.Count == 0 && rules.FileCount > 0 && lang.Entries > 0 ? 0 : 1;
    }
}
