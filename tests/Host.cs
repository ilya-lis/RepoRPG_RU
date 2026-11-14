// Test host: a tiny imitation of what BepInEx 5 does at start-up, enough to exercise the real DLLs end to end.
//   1. preloader  : load every patcher from BepInEx/patchers with Assembly.LoadFile, call Initialize()
//   2. chainloader: scan BepInEx/plugins with Cecil, keep the highest version per GUID, Assembly.LoadFile it
//   3. run the plugin's entry point and print what it "drew"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using Mono.Cecil;

public static class Host
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string root = Path.GetFullPath(args[0]);
        bool rich = args.Length > 1 && args[1] == "rich";
        Paths.BepInExRootPath = root;
        Paths.PluginPath = Path.Combine(root, "plugins");
        Paths.PatcherPluginPath = Path.Combine(root, "patchers");
        Paths.ConfigPath = Path.Combine(root, "config");
        Paths.GameRootPath = Path.Combine(Path.GetDirectoryName(root), "game");      // with a mod manager the game is not where the profile is
        Directory.CreateDirectory(Paths.ConfigPath);
        AppDomain.CurrentDomain.AssemblyResolve += LocalResolve;

        // ---- preloader
        if (Directory.Exists(Paths.PatcherPluginPath))
        {
            foreach (string dll in Directory.GetFiles(Paths.PatcherPluginPath, "*.dll", SearchOption.AllDirectories))
            {
                List<string> types = new List<string>();
                using (AssemblyDefinition ad = AssemblyDefinition.ReadAssembly(dll))
                    foreach (TypeDefinition t in ad.MainModule.Types)
                        if (IsPatcher(t)) types.Add(t.FullName);
                if (types.Count == 0) continue;
                Assembly asm = Assembly.LoadFile(dll);
                foreach (string name in types)
                {
                    Type type = asm.GetType(name);
                    IEnumerable<string> targets = (IEnumerable<string>)type.GetProperty("TargetDLLs").GetValue(null, null);   // not guarded in BepInEx either
                    Console.WriteLine("[host] patcher " + name + ", targets: " + targets.Count());
                    MethodInfo init = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name.Equals("Initialize", StringComparison.InvariantCultureIgnoreCase) && m.GetParameters().Length == 0);
                    try { if (init != null) init.Invoke(null, null); }
                    catch (Exception e) { Console.WriteLine("[host] Failed to run Initializer: " + e); }
                }
            }
        }

        // ---- chainloader
        var found = new List<Tuple<string, string, Version, string>>();
        foreach (string dll in Directory.GetFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                using (AssemblyDefinition ad = AssemblyDefinition.ReadAssembly(dll))
                    foreach (TypeDefinition t in ad.MainModule.Types)
                        foreach (CustomAttribute a in t.CustomAttributes)
                            if (a.AttributeType.FullName == "BepInEx.BepInPlugin")
                                found.Add(Tuple.Create((string)a.ConstructorArguments[0].Value, t.FullName, new Version((string)a.ConstructorArguments[2].Value), dll));
            }
            catch (BadImageFormatException) { Console.WriteLine("[host] skipping non-assembly " + Path.GetFileName(dll)); }
        }
        int code = 0;
        foreach (var group in found.GroupBy(f => f.Item1))
        {
            Tuple<string, string, Version, string> loaded = null;
            foreach (var p in group.OrderByDescending(x => x.Item3))
            {
                if (loaded != null) { Console.WriteLine("[host] Skipping [" + p.Item3 + "] because a newer version exists (" + loaded.Item3 + ")"); continue; }
                loaded = p;
            }
            Console.WriteLine("[host] Loading [" + group.Key + " " + loaded.Item3 + "] from " + loaded.Item4.Substring(root.Length + 1).Replace('\\', '/'));
            try
            {
                Assembly asm = Assembly.LoadFile(loaded.Item4);
                MethodInfo run = asm.GetType(loaded.Item2).GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                foreach (string line in (List<string>)run.Invoke(null, new object[] { rich })) Console.WriteLine(line.Replace("\n", "\\n"));
            }
            catch (Exception e)
            {
                Console.WriteLine("[host] PLUGIN FAILED: " + e);
                code = 3;
            }
        }
        if (found.Count == 0) Console.WriteLine("[host] no plugins");
        return code;
    }

    private static bool IsPatcher(TypeDefinition t)
    {
        if (t.IsInterface || (t.IsAbstract && !t.IsSealed)) return false;
        MethodDefinition targets = t.Methods.FirstOrDefault(m => m.Name == "get_TargetDLLs" && m.IsPublic && m.IsStatic);
        if (targets == null || targets.ReturnType.FullName != "System.Collections.Generic.IEnumerable`1<System.String>") return false;
        return t.Methods.Any(m => m.Name == "Patch" && m.IsPublic && m.IsStatic && m.ReturnType.FullName == "System.Void"
                                  && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName.StartsWith("Mono.Cecil.AssemblyDefinition"));
    }

    // same order as BepInEx: already loaded by simple name, then the plugin folders
    private static Assembly LocalResolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == name) return a;
        foreach (string dir in new[] { Paths.PatcherPluginPath, Paths.PluginPath })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.GetFiles(dir, name + ".dll", SearchOption.AllDirectories))
                return Assembly.LoadFile(f);
        }
        return null;
    }
}
