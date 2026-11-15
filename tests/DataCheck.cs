// What the patcher does to the mod's embedded data, tried on the real files (dump/RepoRPG.<stem>.json):
// the same rules, the same dictionary and the same JSON rewriter as in Builder.TranslateData.
// Usage: DataCheck <mod dir> <lang dir> <dump dir> <out dir>   - writes the rewritten files and "changes.tsv".
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RepoRPG_RU;

internal static class DataCheck
{
    private static int Main(string[] args)
    {
        Rules rules = Rules.Load(args[0]);
        Lang lang = Lang.Load(args[1]);
        Directory.CreateDirectory(args[3]);
        StringBuilder log = new StringBuilder();
        int files = 0, total = 0;
        foreach (string file in Directory.GetFiles(args[2], "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int dot = name.LastIndexOf('.');
            string stem = dot >= 0 ? name.Substring(dot + 1) : name;
            byte[] data = File.ReadAllBytes(file);
            bool bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
            string json = new UTF8Encoding(false, false).GetString(data, bom ? 3 : 0, data.Length - (bom ? 3 : 0));
            Dictionary<string, string> own, any;
            lang.Data.TryGetValue(stem, out own);
            lang.Data.TryGetValue("", out any);
            int replaced;
            string result = JsonText.Translate(json, delegate(string path, string property, string value)
            {
                bool allowed = rules.DataAllowed(stem, property, path);
                string t = null;
                if (allowed && !(own != null && own.TryGetValue(value, out t)) && !(any != null && any.TryGetValue(value, out t))) t = null;
                log.Append(stem).Append('\t').Append(path).Append('\t').Append(allowed ? (t != null ? "+" : "?") : "-").Append('\t')
                   .Append(value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t")).Append('\n');
                return allowed ? t : null;
            }, out replaced);
            File.WriteAllText(Path.Combine(args[3], Path.GetFileName(file)), result, new UTF8Encoding(bom));
            if (replaced > 0) files++;
            total += replaced;
            Console.WriteLine(stem + ": " + replaced + " replaced");
        }
        File.WriteAllText(Path.Combine(args[3], "changes.tsv"), log.ToString(), new UTF8Encoding(false));
        Console.WriteLine("data check: " + total + " values replaced in " + files + " file(s)");
        return 0;
    }
}
