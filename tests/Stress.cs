// Robustness / speed check of the builder on a big real-world assembly (not RepoRPG - any managed DLL).
// Every literal is "translated" to itself, so the result must behave exactly like the input,
// while planning, body rewriting, hook injection, the guard class and verification all run for real.
using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RepoRPG_RU
{
    public static class Stress
    {
        public static int Main(string[] args)
        {
            string input = args[0], output = args[1];
            byte[] original = File.ReadAllBytes(input);

            BuildOptions o = new BuildOptions();
            o.Settings = new Settings();
            o.Lang = new Lang();
            o.Rules = new Rules();
            o.RequirePlugin = false;
            o.Dump = true;
            o.HaveRuntime = true;
            o.Stamp = "stress";
            for (int i = 2; i < args.Length; i++) o.Rules.Parse("[hooks]\n" + args[i] + "\n", "args");

            using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(new MemoryStream(original)))
                foreach (TypeDefinition t in asm.MainModule.GetTypes())
                    foreach (MethodDefinition m in t.Methods)
                    {
                        if (!m.HasBody) continue;
                        foreach (Instruction ins in m.Body.Instructions)
                        {
                            string s = ins.Operand as string;
                            if (ins.OpCode.Code == Code.Ldstr && !string.IsNullOrEmpty(s) && Fmt.SameHoles(s, s)) o.Lang.Global[s] = s;
                        }
                    }

            Report report = new Report();
            DateTime t0 = DateTime.UtcNow;
            byte[] result = Builder.Build(original, o, report);
            double ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            File.WriteAllBytes(output, result);

            Dictionary<string, int> uses = new Dictionary<string, int>();
            foreach (string line in report.Dump)
            {
                string use = line.Split('\t')[1];
                int n; uses.TryGetValue(use, out n); uses[use] = n + 1;
            }
            Console.WriteLine("methods {0}, literals {1}, dictionary {2}, rewritten {3}, kept {4}, left to display {5}, hooks {6} (missed rules {7}), {8:0} ms, {9} -> {10} bytes",
                report.Methods, report.Literals, o.Lang.Global.Count, report.Replaced, report.SkippedVeto, report.SkippedToken,
                report.HooksApplied, report.HookRulesMissed, ms, original.Length, result.Length);
            Console.WriteLine("  probes {0}, texts kept because of them {1}", report.Probes, report.SkippedProbe);
            Console.WriteLine("  foreign enums in constants: {0} known, {1} answered; foreign assemblies requested: {2}", report.EnumsKnown, report.EnumsAnswered, report.AssemblyRequests);
            foreach (KeyValuePair<string, int> kv in uses) Console.WriteLine("  {0}: {1}", kv.Key, kv.Value);
            foreach (string n in report.Notes) if (n.StartsWith("hook not found")) Console.WriteLine("  " + n);
            return 0;
        }
    }
}
