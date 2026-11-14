// Turns the bytes of the original RepoRPG.dll into the bytes of the translated copy. No file system access here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RepoRPG_RU
{
    internal sealed class Report
    {
        public string OriginalVersion = "?";
        public string NewVersion = "?";
        public int Methods;
        public int Literals;                 // ldstr seen
        public int Replaced;                 // ldstr rewritten
        public int ReplacedScoped;
        public int SkippedVeto;
        public int SkippedToken;
        public int SkippedType;
        public int SkippedProbe;
        public int SkippedSentence;
        public int Probes;
        public int HooksApplied;
        public int CallHooks;
        public int HookRulesMissed;
        public int DataReplaced;
        public int DataFiles;
        public int EnumsKnown, EnumsAnswered, AssemblyRequests;
        public readonly HashSet<string> UsedGlobal = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> UsedScoped = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> UsedData = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Notes = new List<string>();     // human readable details for report.txt
        public readonly List<string> Dump = new List<string>();      // literals.tsv lines when dumping
        public readonly List<string> DataDump = new List<string>();  // data-strings.tsv lines when dumping
        public readonly Dictionary<string, byte[]> Resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    }

    internal sealed class BuildOptions
    {
        public Settings Settings;
        public Lang Lang;
        public Rules Rules;
        public string RuntimeAssemblyName = BuildInfo.RuntimeAssembly;
        public Version RuntimeVersion = new Version(0, 0, 0, 0);
        public bool HaveRuntime;
        public bool Dump;
        public string Stamp;
        public bool RequirePlugin = true;   // tests run the builder over arbitrary assemblies
        public List<string> SearchDirs = new List<string>();   // only consulted if Cecil asks for a foreign assembly (not expected)
    }

    internal static class Builder
    {
        private sealed class Edit
        {
            public int Index;
            public string Value;
        }

        private sealed class Plan
        {
            public readonly Dictionary<uint, List<Edit>> Edits = new Dictionary<uint, List<Edit>>();
            public readonly Dictionary<uint, List<int>> Hooks = new Dictionary<uint, List<int>>();   // method token -> parameter indexes
            public readonly Dictionary<uint, List<int>> Shims = new Dictionary<uint, List<int>>();   // method token -> indexes of calls whose text argument is translated in place
            public readonly Dictionary<uint, int> Counts = new Dictionary<uint, int>();              // method token -> original instruction count
            public readonly Dictionary<uint, string> Names = new Dictionary<uint, string>();         // method token -> full name (tokens move when Cecil writes)
            public readonly List<Probe> Probes = new List<Probe>();                                  // string tests that interface text must keep passing
            public HashSet<string> Free;                                                             // texts that never reach those tests ([probes] free)
        }

        public static byte[] Build(byte[] original, BuildOptions o, Report report)
        {
            Plan plan = MakePlan(original, o, report);
            byte[] result = Apply(original, o, plan, report);
            Verify(result, plan, report);
            return result;
        }

        private static AssemblyDefinition Read(byte[] bytes)
        {
            return Read(bytes, null, null);
        }

        private static AssemblyDefinition Read(byte[] bytes, IAssemblyResolver assemblies, IMetadataResolver metadata)
        {
            ReaderParameters rp = new ReaderParameters();
            rp.ReadingMode = ReadingMode.Deferred;
            rp.InMemory = true;
            rp.ReadSymbols = false;
            if (assemblies != null) rp.AssemblyResolver = assemblies;
            if (metadata != null) rp.MetadataResolver = metadata;
            return AssemblyDefinition.ReadAssembly(new MemoryStream(bytes, false), rp);
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            Stack<TypeDefinition> todo = new Stack<TypeDefinition>();
            for (int i = module.Types.Count - 1; i >= 0; i--) todo.Push(module.Types[i]);
            while (todo.Count > 0)
            {
                TypeDefinition t = todo.Pop();
                yield return t;
                if (t.HasNestedTypes)
                    for (int i = t.NestedTypes.Count - 1; i >= 0; i--) todo.Push(t.NestedTypes[i]);
            }
        }

        // ------------------------------------------------------------------ pass 1: decide

        private static Plan MakePlan(byte[] original, BuildOptions o, Report report)
        {
            Plan plan = new Plan();
            plan.Free = o.Rules != null ? o.Rules.ProbeFree : null;
            using (AssemblyDefinition asm = Read(original))
            {
                ModuleDefinition module = asm.MainModule;
                Rules rules = o.Rules;
                Lang lang = o.Lang;

                // hook targets first: they are also the "this call shows text" evidence for single words
                Dictionary<MethodDefinition, HashSet<int>> hooked = new Dictionary<MethodDefinition, HashSet<int>>();
                foreach (HookRule rule in rules.Hooks)
                {
                    int found = 0;
                    foreach (TypeDefinition t in AllTypes(module))
                    {
                        if (!Analysis.TypeMatches(rule.Type, t.FullName.Replace('/', '.'))) continue;
                        foreach (MethodDefinition m in t.Methods)
                        {
                            if (m.Name != rule.Method || !m.HasBody) continue;
                            foreach (ParameterDefinition p in m.Parameters)
                            {
                                if (Array.IndexOf(rule.Params, p.Name) < 0) continue;
                                if (p.ParameterType.FullName != "System.String") continue;
                                HashSet<int> set;
                                if (!hooked.TryGetValue(m, out set)) { set = new HashSet<int>(); hooked[m] = set; }
                                if (set.Add(p.Index)) found++;
                            }
                        }
                    }
                    if (found == 0)
                    {
                        report.HookRulesMissed++;
                        report.Notes.Add("hook not found: " + rule.Type + "::" + rule.Method + "(" + string.Join(", ", rule.Params) + ")  [" + rule.Source + "]");
                    }
                }
                bool wantHooks = o.Settings.DisplayHooks && o.HaveRuntime;
                if (wantHooks)
                {
                    foreach (KeyValuePair<MethodDefinition, HashSet<int>> kv in hooked)
                    {
                        List<int> list = new List<int>(kv.Value);
                        list.Sort();
                        plan.Hooks[kv.Key.MetadataToken.ToUInt32()] = list;
                    }
                }

                // ---- every literal is classified first; the decision needs the whole picture
                bool wantStrings = o.Settings.TranslateStrings;
                List<Occurrence> occurrences = new List<Occurrence>();
                HashSet<string> usedAsId = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> probeKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (TypeDefinition t in AllTypes(module))
                {
                    string typeKey = Analysis.TypeKey(t);
                    foreach (MethodDefinition m in t.Methods)
                    {
                        if (!m.HasBody) continue;
                        report.Methods++;
                        MethodBody body = m.Body;
                        uint token = m.MetadataToken.ToUInt32();
                        plan.Counts[token] = body.Instructions.Count;
                        plan.Names[token] = m.FullName;
                        string scope = null;
                        Dictionary<string, string> scoped = null, typeScoped = null;
                        bool skipped = false;

                        if (wantHooks && rules.Calls.Count > 0)
                        {
                            List<int> shims = null;
                            for (int i = 0; i < body.Instructions.Count; i++)
                            {
                                Instruction ins = body.Instructions[i];
                                if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt) continue;
                                if (!IsCallHook(rules, ins.Operand as MethodReference)) continue;
                                // "constrained." / "tail." must stay glued to their call
                                if (ins.Previous != null && ins.Previous.OpCode.OpCodeType == OpCodeType.Prefix) continue;
                                if (shims == null)
                                {
                                    if (IsSkipped(rules, typeKey, typeKey + "::" + Analysis.MemberName(m))) break;
                                    shims = new List<int>();
                                }
                                shims.Add(i);
                            }
                            if (shims != null) plan.Shims[token] = shims;
                        }

                        for (int i = 0; i < body.Instructions.Count; i++)
                        {
                            Instruction ins = body.Instructions[i];
                            if (ins.OpCode.Code != Code.Ldstr) continue;
                            string value = ins.Operand as string;
                            if (value == null) continue;
                            report.Literals++;
                            if (value.Length == 0) continue;
                            if (scope == null)
                            {
                                scope = typeKey + "::" + Analysis.MemberName(m);
                                lang.Scoped.TryGetValue(scope, out scoped);
                                lang.Scoped.TryGetValue(typeKey + "::*", out typeScoped);
                                skipped = IsSkipped(rules, typeKey, scope);
                            }

                            string translated = null;
                            bool isScoped = (scoped != null && scoped.TryGetValue(value, out translated))
                                            || (typeScoped != null && typeScoped.TryGetValue(value, out translated));
                            bool isGlobal = !isScoped && lang.Global.TryGetValue(value, out translated);

                            Consumer final = Analysis.Final(m, ins.Next, rules);
                            ProbeKind kind;
                            bool ignoreCase;
                            if (Analysis.IsProbe(final, out kind, out ignoreCase))
                            {
                                Probe probe = new Probe();
                                probe.Text = value;
                                probe.Kind = kind;
                                probe.IgnoreCase = ignoreCase;
                                probe.Where = scope;
                                if (probeKeys.Add(probe.Key) && Analysis.ProbeMatters(probe, rules)) plan.Probes.Add(probe);
                            }
                            if (!o.Dump && !isScoped && !isGlobal && !lang.Terms.ContainsKey(value)) continue;   // nobody asks about this text

                            string why;
                            Use use = Analysis.Classify(m, final, rules, hooked, 0, out why);
                            if (use == Use.Id) usedAsId.Add(value);
                            if (o.Dump)
                                report.Dump.Add(scope + "\t" + use + "\t" + Analysis.Describe(final) + "\t" + Escape(value));
                            if (!isScoped && !isGlobal) continue;

                            Occurrence occ = new Occurrence();
                            occ.Token = token;
                            occ.Index = i;
                            occ.Scope = scope;
                            occ.TypeKey = typeKey;
                            occ.Value = value;
                            occ.Translated = translated;
                            occ.Scoped = isScoped;
                            occ.Skipped = skipped;
                            occ.Use = use;
                            occ.Why = why ?? Analysis.Describe(final);
                            occurrences.Add(occ);
                        }
                    }
                }

                report.Probes = plan.Probes.Count;
                if (!wantStrings) return plan;
                // ---- pass 1: every occurrence on its own merits
                List<Occurrence> accepted = new List<Occurrence>();
                Dictionary<string, HashSet<string>> refused = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, HashSet<string>> kv in lang.Unsafe) refused[kv.Key] = new HashSet<string>(kv.Value, StringComparer.Ordinal);
                foreach (Occurrence occ in occurrences)
                {
                    string reason = Refusal(occ, plan, usedAsId, report);
                    if (reason == null) { accepted.Add(occ); continue; }
                    report.Notes.Add(reason + ": " + occ.Scope + "  \"" + Escape(occ.Value) + "\"");
                    if (!occ.Scoped) continue;
                    HashSet<string> set;
                    if (!refused.TryGetValue(occ.Scope, out set)) { set = new HashSet<string>(StringComparer.Ordinal); refused[occ.Scope] = set; }
                    set.Add(occ.Value);
                }

                // ---- pass 2: a sentence the mod glues together ("Slotted " + a + " into " + b) is replaced whole or not at all:
                //      half a sentence in Russian would match no entry when it is drawn
                foreach (KeyValuePair<string, HashSet<string>> kv in refused)
                {
                    Dictionary<string, List<string>> sentences;
                    if (!lang.Sentences.TryGetValue(kv.Key, out sentences)) continue;
                    HashSet<string> bad = kv.Value;
                    bool grew = true;
                    while (grew)
                    {
                        grew = false;
                        foreach (List<string> pieces in sentences.Values)
                        {
                            bool hit = false;
                            foreach (string piece in pieces) if (bad.Contains(piece)) { hit = true; break; }
                            if (!hit) continue;
                            foreach (string piece in pieces) if (bad.Add(piece)) grew = true;
                        }
                    }
                }
                foreach (Occurrence occ in accepted)
                {
                    HashSet<string> bad;
                    if (occ.Scoped && refused.TryGetValue(occ.Scope, out bad) && bad.Contains(occ.Value))
                    {
                        report.SkippedSentence++;
                        report.Notes.Add("left to display time (another piece of the same sentence stays as it is): " + occ.Scope + "  \"" + Escape(occ.Value) + "\"");
                        continue;
                    }
                    AddEdit(plan, occ.Token, occ.Index, occ.Translated);
                    report.Replaced++;
                    if (occ.Scoped)
                    {
                        report.ReplacedScoped++;
                        report.UsedScoped.Add(occ.Scope + "\n" + occ.Value);
                        report.UsedScoped.Add(occ.TypeKey + "::*\n" + occ.Value);
                    }
                    else report.UsedGlobal.Add(occ.Value);
                }
            }
            return plan;
        }

        /// <summary>Why this literal must stay as it is inside the code; null = replace it.</summary>
        private static string Refusal(Occurrence occ, Plan plan, HashSet<string> usedAsId, Report report)
        {
            if (occ.Skipped)
            {
                // [skip]: network, saves, parsers - nothing in there is touched, whatever the dictionary says
                report.SkippedType++;
                return "skipped (excluded type)";
            }
            if (occ.Use == Use.Id || occ.Use == Use.Keep)
            {
                // an explicit "@Type::Member" entry is the translator's decision - but a proven id use,
                // a log line or a text sent to other players overrides even that
                report.SkippedVeto++;
                return (occ.Scoped ? "scoped entry ignored, " : "kept, ") + (occ.Use == Use.Id ? "used as id" : "log/network") + " (" + occ.Why + ")";
            }
            if (!occ.Scoped && occ.Use != Use.Display)
            {
                // destination unknown (field, array, return value, foreign method): only texts that cannot be ids
                bool token = Analysis.IsTokenLike(occ.Value);
                if (token || usedAsId.Contains(occ.Value))
                {
                    report.SkippedToken++;
                    return "left to display time (" + (token ? "single word" : "also used as id") + ", " + occ.Why + ")";
                }
            }
            // whatever the entry says: a text the mod recognises by its wording must keep that wording inside the code
            Probe broken = BrokenProbe(plan.Probes, plan.Free, occ.Value, occ.Translated);
            if (broken != null)
            {
                report.SkippedProbe++;
                return "left to display time (the mod tests texts with " + broken + " in " + broken.Where + ")";
            }
            return null;
        }

        private sealed class Occurrence
        {
            public uint Token;
            public int Index;
            public string Scope, TypeKey, Value, Translated, Why;
            public bool Scoped, Skipped;
            public Use Use;
        }

        /// <summary>First probe whose answer differs between the original text and its translation.</summary>
        private static Probe BrokenProbe(List<Probe> probes, HashSet<string> free, string original, string translated)
        {
            if (free != null && free.Contains(original)) return null;
            for (int i = 0; i < probes.Count; i++)
            {
                Probe p = probes[i];
                if (p.Hits(original) != p.Hits(translated)) return p;
            }
            return null;
        }

        private static bool IsCallHook(Rules rules, MethodReference target)
        {
            if (target == null || !target.HasParameters) return false;
            int last = target.Parameters.Count - 1;
            if (target.Parameters[last].ParameterType.FullName != "System.String") return false;   // the text must be on top of the stack
            string dt = Analysis.TypeName(target.DeclaringType);
            for (int i = 0; i < rules.Calls.Count; i++)
                if (Analysis.Match(rules.Calls[i], target, dt, target.Name, last)) return true;
            return false;
        }

        private static bool IsSkipped(Rules rules, string typeKey, string scope)
        {
            for (int i = 0; i < rules.Skip.Count; i++)
            {
                string p = rules.Skip[i];
                if (p.IndexOf("::", StringComparison.Ordinal) >= 0)
                {
                    if (Rules.Glob(p, scope)) return true;
                }
                else if (Rules.Glob(p, typeKey) || (typeKey.StartsWith(p + ".", StringComparison.Ordinal))) return true;
            }
            return false;
        }

        private static void AddEdit(Plan plan, uint token, int index, string value)
        {
            List<Edit> list;
            if (!plan.Edits.TryGetValue(token, out list)) { list = new List<Edit>(); plan.Edits[token] = list; }
            Edit e = new Edit();
            e.Index = index;
            e.Value = value;
            list.Add(e);
        }

        // ------------------------------------------------------------------ pass 2: apply to a fresh image

        private static byte[] Apply(byte[] original, BuildOptions o, Plan plan, Report report)
        {
            DirectoryResolver directories = new DirectoryResolver(o.SearchDirs);
            EnumResolver enums = new EnumResolver(directories);
            directories.Metadata = enums;
            try
            {
                return Apply(original, o, plan, report, directories, enums);
            }
            finally
            {
                report.EnumsKnown = enums.Count;
                report.EnumsAnswered = enums.Answered;
                report.AssemblyRequests = directories.Requests;
                directories.Dispose();
            }
        }

        private static byte[] Apply(byte[] original, BuildOptions o, Plan plan, Report report, DirectoryResolver directories, EnumResolver enums)
        {
            using (AssemblyDefinition asm = Read(original, directories, enums))
            {
                ModuleDefinition module = asm.MainModule;

                // only the bodies that really change are decoded; Cecil copies all others verbatim
                foreach (KeyValuePair<uint, List<Edit>> kv in plan.Edits)
                {
                    MethodDefinition m = module.LookupToken((int)kv.Key) as MethodDefinition;
                    if (m == null || !m.HasBody) throw new InvalidOperationException("method token " + kv.Key.ToString("x8") + " vanished");
                    MethodBody body = m.Body;
                    foreach (Edit e in kv.Value)
                    {
                        Instruction ins = body.Instructions[e.Index];
                        if (ins.OpCode.Code != Code.Ldstr) throw new InvalidOperationException("instruction mismatch in " + m.FullName);
                        ins.Operand = e.Value;
                    }
                }

                HashSet<string> referencesBefore = new HashSet<string>(StringComparer.Ordinal);
                foreach (AssemblyNameReference r in module.AssemblyReferences) referencesBefore.Add(r.Name);

                MethodDefinition guard = null;

                // call sites first: their indexes refer to the untouched bodies
                foreach (KeyValuePair<uint, List<int>> kv in plan.Shims)
                {
                    MethodDefinition m = module.LookupToken((int)kv.Key) as MethodDefinition;
                    if (m == null || !m.HasBody) throw new InvalidOperationException("call hook target " + kv.Key.ToString("x8") + " vanished");
                    MethodBody body = m.Body;
                    ILProcessor il = body.GetILProcessor();
                    for (int k = kv.Value.Count - 1; k >= 0; k--)
                    {
                        Instruction ins = body.Instructions[kv.Value[k]];
                        MethodReference target = ins.Operand as MethodReference;
                        if (target == null || (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt))
                            throw new InvalidOperationException("instruction mismatch in " + m.FullName);
                        if (guard == null) guard = AddGuard(module, o, target.Parameters[target.Parameters.Count - 1].ParameterType);
                        // The instruction itself becomes the translation call and the original call follows it:
                        // every branch that used to land on the call (cond ? a : b) now lands on the translation.
                        Instruction moved = il.Create(ins.OpCode, target);
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = guard;
                        il.InsertAfter(ins, moved);
                        report.CallHooks++;
                    }
                }

                if (plan.Hooks.Count > 0)
                {
                    foreach (KeyValuePair<uint, List<int>> kv in plan.Hooks)
                    {
                        MethodDefinition m = module.LookupToken((int)kv.Key) as MethodDefinition;
                        if (m == null || !m.HasBody) throw new InvalidOperationException("hook target " + kv.Key.ToString("x8") + " vanished");
                        MethodBody body = m.Body;
                        if (body.Instructions.Count == 0) continue;
                        ILProcessor il = body.GetILProcessor();
                        Instruction first = body.Instructions[0];
                        foreach (int index in kv.Value)
                        {
                            ParameterDefinition p = m.Parameters[index];
                            // the module's own System.String reference is reused, so no new core library reference appears
                            if (guard == null) guard = AddGuard(module, o, p.ParameterType);
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, p));
                            il.InsertBefore(first, il.Create(OpCodes.Call, guard));
                            il.InsertBefore(first, il.Create(OpCodes.Starg, p));
                            report.HooksApplied++;
                        }
                    }
                }

                if (o.RequirePlugin) BumpVersion(module, report);
                if (o.Settings.TranslateData || o.Dump) TranslateData(module, o, report, plan.Probes);
                if (o.Stamp != null)
                    module.Resources.Add(new EmbeddedResource(Worker.StampResource, ManifestResourceAttributes.Private, Encoding.UTF8.GetBytes(o.Stamp)));

                foreach (AssemblyNameReference r in module.AssemblyReferences)
                    if (!referencesBefore.Contains(r.Name) && r.Name != o.RuntimeAssemblyName)
                        throw new InvalidOperationException("unexpected new assembly reference: " + r.Name);

                // must be the last step before writing: it reads every constant of the module
                enums.Learn(module, AllTypes(module));

                MemoryStream ms = new MemoryStream(original.Length + 65536);
                asm.Write(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Adds to the copy:
        ///   static class <RepoRPG_RU> {
        ///       static bool off;
        ///       static string T(string s)    { if (off) return s; try { return Call(s); } catch { off = true; return s; } }
        ///       [NoInlining] static string Call(string s) { return RepoRPG_RU.Loc.T(s); }
        ///   }
        /// If RepoRPG_RU.dll is missing or broken, the first call fails inside Call (that is where the runtime
        /// has to bind the foreign assembly), the guard switches itself off and the mod keeps working untranslated.
        /// </summary>
        private static MethodDefinition AddGuard(ModuleDefinition module, BuildOptions o, TypeReference stringType)
        {
            TypeReference objectType = module.TypeSystem.Object;
            TypeReference boolType = module.TypeSystem.Boolean;

            AssemblyNameReference runtime = null;
            foreach (AssemblyNameReference r in module.AssemblyReferences)
                if (r.Name == o.RuntimeAssemblyName) { runtime = r; break; }
            if (runtime == null)
            {
                runtime = new AssemblyNameReference(o.RuntimeAssemblyName, o.RuntimeVersion);
                module.AssemblyReferences.Add(runtime);
            }
            TypeReference loc = new TypeReference("RepoRPG_RU", "Loc", module, runtime);
            MethodReference translate = new MethodReference("T", stringType, loc);
            translate.HasThis = false;
            translate.Parameters.Add(new ParameterDefinition(stringType));

            TypeDefinition holder = new TypeDefinition("", GuardType,
                TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, objectType);
            module.Types.Add(holder);

            FieldDefinition off = new FieldDefinition("off", FieldAttributes.Private | FieldAttributes.Static, boolType);
            holder.Fields.Add(off);

            MethodDefinition call = new MethodDefinition("Call", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, stringType);
            call.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.NoInlining;
            call.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, stringType));
            holder.Methods.Add(call);
            {
                ILProcessor il = call.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, translate));
                il.Append(il.Create(OpCodes.Ret));
            }

            MethodDefinition t = new MethodDefinition("T", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, stringType);
            t.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
            t.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, stringType));
            holder.Methods.Add(t);
            {
                MethodBody body = t.Body;
                body.InitLocals = true;
                VariableDefinition result = new VariableDefinition(stringType);
                body.Variables.Add(result);
                ILProcessor il = body.GetILProcessor();

                Instruction retOriginal = il.Create(OpCodes.Ldarg_0);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                Instruction handlerStart = il.Create(OpCodes.Pop);
                Instruction done = il.Create(OpCodes.Ldloc_0);

                il.Append(il.Create(OpCodes.Ldsfld, off));
                il.Append(il.Create(OpCodes.Brtrue, retOriginal));
                il.Append(tryStart);                                  // try {
                il.Append(il.Create(OpCodes.Call, call));
                il.Append(il.Create(OpCodes.Stloc_0));
                il.Append(il.Create(OpCodes.Leave, done));            // }
                il.Append(handlerStart);                              // catch (object) {
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Stsfld, off));
                il.Append(il.Create(OpCodes.Leave, retOriginal));     // }
                il.Append(done);
                il.Append(il.Create(OpCodes.Ret));
                il.Append(retOriginal);
                il.Append(il.Create(OpCodes.Ret));

                ExceptionHandler handler = new ExceptionHandler(ExceptionHandlerType.Catch);
                handler.CatchType = objectType;
                handler.TryStart = tryStart;
                handler.TryEnd = handlerStart;
                handler.HandlerStart = handlerStart;
                handler.HandlerEnd = done;
                body.ExceptionHandlers.Add(handler);
            }
            return t;
        }

        /// <summary>
        /// BepInEx keeps the highest version when two plugins share a GUID, so the copy gets "+ .1":
        /// the untouched original stays on disk and is simply skipped.
        /// </summary>
        private static void BumpVersion(ModuleDefinition module, Report report)
        {
            foreach (TypeDefinition t in AllTypes(module))
            {
                if (!t.HasCustomAttributes) continue;
                foreach (CustomAttribute a in t.CustomAttributes)
                {
                    if (a.AttributeType.FullName != "BepInEx.BepInPlugin") continue;
                    if (a.ConstructorArguments.Count != 3) continue;
                    if (!string.Equals(a.ConstructorArguments[0].Value as string, BuildInfo.TargetGuid, StringComparison.Ordinal)) continue;
                    string old = a.ConstructorArguments[2].Value as string;
                    string bumped = Bump(old);
                    a.ConstructorArguments[2] = new CustomAttributeArgument(a.ConstructorArguments[2].Type, bumped);
                    report.OriginalVersion = old;
                    report.NewVersion = bumped;
                    return;
                }
            }
            throw new InvalidOperationException("[BepInPlugin(\"" + BuildInfo.TargetGuid + "\", ...)] not found - is this really RepoRPG?");
        }

        internal static string Bump(string version)
        {
            Version v = new Version(version);   // same parser BepInEx uses
            int build = v.Build < 0 ? 0 : v.Build;
            int revision = v.Revision < 0 ? 1 : v.Revision + 1;
            return v.Major + "." + v.Minor + "." + build + "." + revision;
        }

        public static string ReadPluginVersion(ModuleDefinition module)
        {
            foreach (TypeDefinition t in AllTypes(module))
            {
                if (!t.HasCustomAttributes) continue;
                foreach (CustomAttribute a in t.CustomAttributes)
                {
                    if (a.AttributeType.FullName != "BepInEx.BepInPlugin" || a.ConstructorArguments.Count != 3) continue;
                    if (string.Equals(a.ConstructorArguments[0].Value as string, BuildInfo.TargetGuid, StringComparison.Ordinal))
                        return a.ConstructorArguments[2].Value as string;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ embedded JSON

        private static void TranslateData(ModuleDefinition module, BuildOptions o, Report report, List<Probe> probes)
        {
            for (int r = 0; r < module.Resources.Count; r++)
            {
                EmbeddedResource res = module.Resources[r] as EmbeddedResource;
                if (res == null || !res.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] data = res.GetResourceData();
                if (o.Dump) report.Resources[res.Name] = data;
                bool translate = o.Settings.TranslateData;

                string stem = res.Name.Substring(0, res.Name.Length - 5);
                int dot = stem.LastIndexOf('.');
                if (dot >= 0) stem = stem.Substring(dot + 1);

                bool bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
                string json = new UTF8Encoding(false, false).GetString(data, bom ? 3 : 0, data.Length - (bom ? 3 : 0));

                Dictionary<string, string> own, any;
                o.Lang.Data.TryGetValue(stem, out own);
                o.Lang.Data.TryGetValue("", out any);
                string stemCopy = stem;
                Rules rules = o.Rules;
                bool dump = o.Dump;
                int replaced;
                string result = JsonText.Translate(json, delegate(string path, string property, string value)
                {
                    bool allowed = rules.DataAllowed(stemCopy, property, path);
                    if (dump) report.DataDump.Add(stemCopy + "\t" + path + "\t" + (allowed ? "+" : "-") + "\t" + Escape(value));
                    if (!allowed || !translate) return null;
                    string t;
                    bool mine = own != null && own.TryGetValue(value, out t);
                    if (!mine)
                    {
                        if (any == null || !any.TryGetValue(value, out t)) return null;
                    }
                    else t = own[value];
                    Probe broken = BrokenProbe(probes, rules.ProbeFree, value, t);
                    if (broken != null)
                    {
                        report.SkippedProbe++;
                        report.Notes.Add("data left as is (the mod tests texts with " + broken + " in " + broken.Where + "): " + stemCopy + " " + path + "  \"" + Escape(value) + "\"");
                        return null;
                    }
                    report.UsedData.Add((mine ? stemCopy : "") + "\n" + value);
                    return t;
                }, out replaced);

                if (replaced == 0) continue;
                byte[] body = new UTF8Encoding(false).GetBytes(result);
                byte[] bytes = body;
                if (bom)
                {
                    bytes = new byte[body.Length + 3];
                    bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
                    Buffer.BlockCopy(body, 0, bytes, 3, body.Length);
                }
                module.Resources[r] = new EmbeddedResource(res.Name, res.Attributes, bytes);
                report.DataReplaced += replaced;
                report.DataFiles++;
            }
        }

        // ------------------------------------------------------------------ pass 3: verify the written image

        private const string GuardType = "<RepoRPG_RU>";

        private sealed class Expect
        {
            public int Count;
            public int Shift;
            public List<int> Shims;
            public List<Edit> Edits;
            public bool Seen;

            /// <summary>Where an instruction of the original body sits now.</summary>
            public int Moved(int index)
            {
                int n = index + Shift;
                if (Shims != null)
                    for (int i = 0; i < Shims.Count; i++)
                        if (Shims[i] < index) n++;
                return n;
            }
        }

        private static void Verify(byte[] result, Plan plan, Report report)
        {
            // Cecil renumbers metadata tokens when it writes, so methods are matched by full name.
            Dictionary<string, Expect> expected = new Dictionary<string, Expect>(StringComparer.Ordinal);
            HashSet<string> ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<uint, int> kv in plan.Counts)
            {
                string name = plan.Names[kv.Key];
                Expect e = new Expect();
                e.Count = kv.Value;
                List<int> hooks;
                e.Shift = plan.Hooks.TryGetValue(kv.Key, out hooks) ? hooks.Count * 3 : 0;
                plan.Shims.TryGetValue(kv.Key, out e.Shims);
                plan.Edits.TryGetValue(kv.Key, out e.Edits);
                if (expected.ContainsKey(name)) ambiguous.Add(name);
                else expected[name] = e;
            }

            using (AssemblyDefinition asm = Read(result))
            {
                ModuleDefinition module = asm.MainModule;
                int bodies = 0;
                foreach (TypeDefinition t in AllTypes(module))
                {
                    foreach (MethodDefinition m in t.Methods)
                    {
                        if (!m.HasBody) continue;
                        MethodBody body = m.Body;   // decoding every body proves the image is readable end to end
                        bodies++;
                        string name = m.FullName;
                        if (t.Name == GuardType && t.Namespace.Length == 0) { bodies--; continue; }
                        if (ambiguous.Contains(name)) continue;
                        Expect e;
                        if (!expected.TryGetValue(name, out e))
                            throw new InvalidOperationException("verification: unexpected method " + name);
                        e.Seen = true;
                        if (body.Instructions.Count != e.Count + e.Shift + (e.Shims == null ? 0 : e.Shims.Count))
                            throw new InvalidOperationException("verification: instruction count changed in " + name);
                        if (e.Shims != null)
                        {
                            foreach (int at in e.Shims)
                            {
                                Instruction ins = body.Instructions[e.Moved(at)];
                                MethodReference g = ins.Operand as MethodReference;
                                if (ins.OpCode.Code != Code.Call || g == null || g.DeclaringType.Name != GuardType || ins.Next == null
                                    || (ins.Next.OpCode.Code != Code.Call && ins.Next.OpCode.Code != Code.Callvirt))
                                    throw new InvalidOperationException("verification: call hook not written in " + name);
                            }
                        }
                        if (e.Edits == null) continue;
                        foreach (Edit edit in e.Edits)
                        {
                            Instruction ins = body.Instructions[e.Moved(edit.Index)];
                            if (ins.OpCode.Code != Code.Ldstr || !string.Equals(ins.Operand as string, edit.Value, StringComparison.Ordinal))
                                throw new InvalidOperationException("verification: literal not written in " + name);
                        }
                    }
                }
                if (bodies != plan.Counts.Count)
                    throw new InvalidOperationException("verification: method count changed (" + bodies + " vs " + plan.Counts.Count + ")");
                foreach (KeyValuePair<string, Expect> kv in expected)
                    if (!kv.Value.Seen) throw new InvalidOperationException("verification: method lost: " + kv.Key);
                foreach (Resource r in module.Resources)
                {
                    EmbeddedResource er = r as EmbeddedResource;
                    if (er != null) er.GetResourceData();
                }
            }
        }

        internal static string Escape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
