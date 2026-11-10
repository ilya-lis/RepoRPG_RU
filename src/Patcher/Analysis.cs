// IL helpers: where does a string literal end up?  (source scope of a method, consumer of a stack value,
// calls that mean "this is an id / key / log text, leave it alone").
using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RepoRPG_RU
{
    internal enum Use { Unknown = 0, Display = 1, Keep = 2, Id = 3 }

    internal struct Consumer
    {
        public Instruction Ins;   // null = unknown
        public int FromTop;       // 0 = our value was the last one pushed
        public int Pops;

        public bool IsCall
        {
            get
            {
                if (Ins == null) return false;
                Code c = Ins.OpCode.Code;
                return c == Code.Call || c == Code.Callvirt || c == Code.Newobj;
            }
        }

        public MethodReference Method { get { return IsCall ? Ins.Operand as MethodReference : null; } }

        /// <summary>0-based parameter index, -1 = the receiver ("this").</summary>
        public int Param
        {
            get
            {
                MethodReference m = Method;
                if (m == null) return -2;
                int stackIndex = Pops - 1 - FromTop;
                bool hasThis = m.HasThis && !m.ExplicitThis && Ins.OpCode.Code != Code.Newobj;
                return hasThis ? stackIndex - 1 : stackIndex;
            }
        }
    }

    internal enum ProbeKind { Prefix, Suffix, Inside }

    /// <summary>
    /// A literal the mod tests other strings against: why.StartsWith("Load too big"), result.Contains(" x") ...
    /// If such a test can see a text, translating that text would silently flip the test. So a text that passes a probe
    /// keeps passing it after translation - or it is not replaced inside the code at all (it is still translated on screen).
    /// </summary>
    internal sealed class Probe
    {
        public string Text;
        public ProbeKind Kind;
        public bool IgnoreCase;
        public string Where;

        public bool Hits(string s)
        {
            if (s == null || s.Length < Text.Length) return false;
            StringComparison c = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            switch (Kind)
            {
                case ProbeKind.Prefix: return s.StartsWith(Text, c);
                case ProbeKind.Suffix: return s.EndsWith(Text, c);
                default: return s.IndexOf(Text, c) >= 0;
            }
        }

        public string Key { get { return (int)Kind + (IgnoreCase ? "i" : "c") + Text; } }

        public override string ToString()
        {
            return (Kind == ProbeKind.Prefix ? "StartsWith" : Kind == ProbeKind.Suffix ? "EndsWith" : "Contains") + "(\"" + Builder.Escape(Text) + "\")";
        }
    }

    internal static class Analysis
    {
        /// <summary>Is this consumer a string test with our literal as the thing searched for?</summary>
        public static bool IsProbe(Consumer c, out ProbeKind kind, out bool ignoreCase)
        {
            kind = ProbeKind.Inside;
            ignoreCase = false;
            MethodReference m = c.Method;
            if (m == null || c.Param != 0 || m.Parameters.Count == 0) return false;
            if (TypeName(m.DeclaringType) != "System.String" || m.Parameters[0].ParameterType.FullName != "System.String") return false;
            switch (m.Name)
            {
                case "StartsWith": kind = ProbeKind.Prefix; break;
                case "EndsWith": kind = ProbeKind.Suffix; break;
                case "Contains":
                case "IndexOf":
                case "LastIndexOf": kind = ProbeKind.Inside; break;
                // text.Replace("pick waiting", reward): a text the mod edits later must still contain those words
                case "Replace": kind = ProbeKind.Inside; break;
                default: return false;
            }
            if (!m.HasThis) return false;
            for (int i = 1; i < m.Parameters.Count; i++)
            {
                string pt = m.Parameters[i].ParameterType.FullName;
                if (pt == "System.StringComparison" && i == m.Parameters.Count - 1)
                {
                    // text.StartsWith("gear", StringComparison.Ordinal): the mode is written right there, as the last argument.
                    // Taking every such test for a case-blind one made "Gear: " and every "... XP" look like texts the mod
                    // recognises (StartsWith("gear"), LastIndexOf(" x")) - and kept them out of the code for no reason.
                    int mode;
                    if (c.Ins != null && Constant(c.Ins.Previous, out mode)) { if ((mode & 1) != 0) ignoreCase = true; }
                    else ignoreCase = true;                     // a run-time value: assume the loosest one
                }
                else if (pt == "System.StringComparison" || pt == "System.Boolean" || pt == "System.Globalization.CultureInfo") ignoreCase = true;
            }
            return true;
        }

        /// <summary>ldc.i4 of any shape -> its value.</summary>
        private static bool Constant(Instruction ins, out int value)
        {
            value = 0;
            if (ins == null) return false;
            switch (ins.OpCode.Code)
            {
                case Code.Ldc_I4_M1: value = -1; return true;
                case Code.Ldc_I4_0: value = 0; return true;
                case Code.Ldc_I4_1: value = 1; return true;
                case Code.Ldc_I4_2: value = 2; return true;
                case Code.Ldc_I4_3: value = 3; return true;
                case Code.Ldc_I4_4: value = 4; return true;
                case Code.Ldc_I4_5: value = 5; return true;
                case Code.Ldc_I4_6: value = 6; return true;
                case Code.Ldc_I4_7: value = 7; return true;
                case Code.Ldc_I4_8: value = 8; return true;
                case Code.Ldc_I4_S: value = (sbyte)ins.Operand; return true;
                case Code.Ldc_I4: value = (int)ins.Operand; return true;
                default: return false;
            }
        }

        /// <summary>Probes that can be about interface text. A bare word looked for anywhere inside a string ("fix", "Text",
        /// "valuable") is how the mod recognises object, field and plugin names - protecting those would block half the dictionary.</summary>
        public static bool ProbeMatters(Probe p, Rules rules)
        {
            if (rules.ProbeIgnore.Contains(p.Text)) return false;
            if (rules.ProbeProtect.Contains(p.Text)) return true;
            // "{t}", "{item}": the mod's own placeholders - whether a translation keeps them is checked elsewhere
            if (p.Text.Length >= 3 && p.Text[0] == '{' && p.Text[p.Text.Length - 1] == '}') return false;
            bool letter = false, plainWord = true;
            foreach (char ch in p.Text)
            {
                bool l = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
                if (l) letter = true;
                else if (!(ch >= '0' && ch <= '9')) plainWord = false;
            }
            if (!letter) return false;                      // ": ", ". ", "<" - layout only, a translation keeps or loses them harmlessly
            if (p.Kind == ProbeKind.Inside && plainWord) return false;
            return true;
        }

        // ------------------------------------------------------------------ scopes

        /// <summary>"Outer.Inner" for user types; compiler generated nested types are folded into their owner.</summary>
        public static string TypeKey(TypeDefinition t)
        {
            List<string> chain = new List<string>();
            for (TypeDefinition d = t; d != null; d = d.DeclaringType)
            {
                string n = d.Name;
                if (n.Length > 0 && n[0] == '<') continue;
                int tick = n.IndexOf('`');
                if (tick > 0) n = n.Substring(0, tick);
                chain.Add(n);
            }
            chain.Reverse();
            return string.Join(".", chain.ToArray());
        }

        /// <summary>Name of the source member a (possibly compiler generated) method belongs to.</summary>
        public static string MemberName(MethodDefinition m)
        {
            string name = Angle(m.Name);
            if (name == null)
            {
                // iterator / async state machine: <Member>d__12::MoveNext
                for (TypeDefinition d = m.DeclaringType; d != null; d = d.DeclaringType)
                {
                    if (d.Name.Length > 0 && d.Name[0] == '<')
                    {
                        string inner = Angle(d.Name);
                        if (!string.IsNullOrEmpty(inner)) { name = inner; break; }
                    }
                    else break;
                }
            }
            if (name == null) name = m.Name;
            if (name != ".ctor" && name != ".cctor")
            {
                int dot = name.LastIndexOf('.');
                if (dot >= 0 && dot + 1 < name.Length) name = name.Substring(dot + 1); // explicit interface implementation
            }
            if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal)) name = name.Substring(4);
            else if (name.StartsWith("add_", StringComparison.Ordinal)) name = name.Substring(4);
            else if (name.StartsWith("remove_", StringComparison.Ordinal)) name = name.Substring(7);
            return name;
        }

        private static string Angle(string n)
        {
            if (n.Length < 3 || n[0] != '<') return null;
            int close = n.IndexOf('>');
            if (close <= 1) return null;
            return n.Substring(1, close - 1);
        }

        public static string Scope(MethodDefinition m)
        {
            return TypeKey(m.DeclaringType) + "::" + MemberName(m);
        }

        public static string TypeName(TypeReference t)
        {
            if (t == null) return "";
            TypeReference e = t.GetElementType();
            return e.FullName.Replace('/', '.');
        }

        // ------------------------------------------------------------------ stack walking

        private static bool Effect(MethodDefinition owner, Instruction ins, out int pop, out int push)
        {
            OpCode op = ins.OpCode;
            pop = 0; push = 0;
            switch (op.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: pop = 0; break;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref: pop = 1; break;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: pop = 2; break;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref: pop = 3; break;
                case StackBehaviour.PopAll: return false;
                case StackBehaviour.Varpop:
                    if (op.Code == Code.Ret)
                    {
                        pop = owner.ReturnType.FullName == "System.Void" ? 0 : 1;
                    }
                    else if (op.Code == Code.Calli)
                    {
                        CallSite cs = ins.Operand as CallSite;
                        if (cs == null) return false;
                        pop = cs.Parameters.Count + 1 + (cs.HasThis && !cs.ExplicitThis ? 1 : 0);
                    }
                    else
                    {
                        MethodReference m = ins.Operand as MethodReference;
                        if (m == null) return false;
                        pop = m.Parameters.Count;
                        if (m.HasThis && !m.ExplicitThis && op.Code != Code.Newobj) pop++;
                    }
                    break;
                default: return false;
            }
            switch (op.StackBehaviourPush)
            {
                case StackBehaviour.Push0: push = 0; break;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref: push = 1; break;
                case StackBehaviour.Push1_push1: push = 2; break;
                case StackBehaviour.Varpush:
                    if (op.Code == Code.Calli)
                    {
                        CallSite cs = ins.Operand as CallSite;
                        if (cs == null) return false;
                        push = cs.ReturnType.FullName == "System.Void" ? 0 : 1;
                    }
                    else
                    {
                        MethodReference m = ins.Operand as MethodReference;
                        if (m == null) return false;
                        push = m.ReturnType.FullName == "System.Void" ? 0 : 1;
                    }
                    break;
                default: return false;
            }
            return true;
        }

        /// <summary>Finds the instruction that pops the value which is on top of the stack right before <paramref name="start"/>.</summary>
        public static Consumer Find(MethodDefinition owner, Instruction start)
        {
            Consumer none = new Consumer();
            Instruction ins = start;
            int above = 0;
            for (int steps = 0; ins != null && steps < 4000; steps++)
            {
                Code code = ins.OpCode.Code;
                if (code == Code.Leave || code == Code.Leave_S || code == Code.Endfinally || code == Code.Endfilter || code == Code.Rethrow)
                    return none;
                int pop, push;
                if (!Effect(owner, ins, out pop, out push)) return none;
                if (above < pop)
                {
                    Consumer c = new Consumer();
                    c.Ins = ins;
                    c.FromTop = above;
                    c.Pops = pop;
                    return c;
                }
                above += push - pop;
                if (code == Code.Ret || code == Code.Throw) return none;
                if (ins.OpCode.FlowControl == FlowControl.Branch)
                {
                    ins = ins.Operand as Instruction;   // br: same stack at the target
                    continue;
                }
                ins = ins.Next;
            }
            return none;
        }

        /// <summary>Like <see cref="Find"/>, but looks through string concatenation / formatting and casts.</summary>
        public static Consumer Final(MethodDefinition owner, Instruction afterValue, Rules rules)
        {
            Consumer c = Find(owner, afterValue);
            for (int hops = 0; hops < 12 && c.Ins != null; hops++)
            {
                Code code = c.Ins.OpCode.Code;
                if (code == Code.Castclass || code == Code.Isinst)
                {
                    c = Find(owner, c.Ins.Next);
                    continue;
                }
                if (code == Code.Call || code == Code.Callvirt)
                {
                    MethodReference m = c.Method;
                    if (m != null && m.ReturnType.FullName != "System.Void" && IsThrough(m, c.Param, rules))
                    {
                        c = Find(owner, c.Ins.Next);
                        continue;
                    }
                }
                break;
            }
            return c;
        }

        private static bool IsThrough(MethodReference m, int param, Rules rules)
        {
            string dt = TypeName(m.DeclaringType);
            string name = m.Name;
            if (dt == "System.String")
            {
                switch (name)
                {
                    case "Concat":
                    case "Format":
                    case "Join":
                        return true;
                    case "ToUpper":
                    case "ToUpperInvariant":
                    case "ToLower":
                    case "ToLowerInvariant":
                    case "Trim":
                    case "TrimStart":
                    case "TrimEnd":
                    case "PadLeft":
                    case "PadRight":
                    case "Substring":
                    case "Insert":
                    case "Remove":
                    case "Replace":
                    case "ToString":
                    case "Normalize":
                        return param == -1;
                }
                return false;
            }
            if (dt == "System.Object" && name == "ToString") return param == -1;
            for (int i = 0; i < rules.Through.Count; i++)
                if (Match(rules.Through[i], m, dt, name, param)) return true;
            return false;
        }

        // ------------------------------------------------------------------ rules

        public static bool Match(CallRule rule, MethodReference m, string dt, string name, int param)
        {
            if (rule.Args != null && Array.IndexOf(rule.Args, param) < 0) return false;
            if (!Rules.Glob(rule.Method, name)) return false;
            if (rule.External) return Rules.Glob(rule.Type, dt);
            // short patterns only apply to types defined in the patched module
            if (!(m.DeclaringType.GetElementType() is TypeDefinition)) return false;
            return TypeMatches(rule.Type, dt);
        }

        /// <summary>Pattern "Outer.Inner" matches "Namespace.Outer.Inner" as well.</summary>
        public static bool TypeMatches(string pattern, string fullName)
        {
            if (pattern == "*") return true;
            if (Rules.Glob(pattern, fullName)) return true;
            if (pattern.IndexOf('*') < 0)
                return fullName.Length > pattern.Length && fullName.EndsWith(pattern, StringComparison.Ordinal) && fullName[fullName.Length - pattern.Length - 1] == '.';
            return Rules.Glob("*." + pattern, fullName);
        }

        /// <summary>
        /// What happens to a string once it has been pushed:
        ///   Display - goes straight to the screen (possibly through concatenation / formatting);
        ///   Id      - compared, looked up, parsed, used as a key or a name: it is an identifier;
        ///   Keep    - leaves the UI (log line, exception, text sent to other players): must stay as written;
        ///   Unknown - stored or handed to code we know nothing about.
        /// </summary>
        public static Use Classify(MethodDefinition owner, Consumer c, Rules rules, Dictionary<MethodDefinition, HashSet<int>> hooked, int depth, out string why)
        {
            why = null;
            if (c.Ins == null) return Use.Unknown;
            Code code = c.Ins.OpCode.Code;

            int local = LocalIndex(c.Ins, true);
            if (local >= 0)
            {
                if (depth > 0) return Use.Unknown;
                // the literal is parked in a local: look at every read of that slot (flow-insensitive, so conservative)
                bool any = false, allDisplay = true;
                string keep = null;
                foreach (Instruction ins in owner.Body.Instructions)
                {
                    if (LocalIndex(ins, false) != local) continue;
                    any = true;
                    string w;
                    Use u = Classify(owner, Final(owner, ins.Next, rules), rules, hooked, depth + 1, out w);
                    if (u == Use.Id) { why = "local -> " + w; return Use.Id; }
                    if (u == Use.Keep && keep == null) keep = w;
                    if (u != Use.Display) allDisplay = false;
                }
                if (keep != null) { why = "local -> " + keep; return Use.Keep; }
                return any && allDisplay ? Use.Display : Use.Unknown;
            }

            if (!c.IsCall) return Use.Unknown;
            MethodReference m = c.Method;
            if (m == null) return Use.Unknown;
            string dt = TypeName(m.DeclaringType);
            string name = m.Name;
            int param = c.Param;

            // rules first: they describe the mod itself and may refine the built-in knowledge
            MethodDefinition def = m.GetElementMethod() as MethodDefinition;
            HashSet<int> set;
            if (def != null && hooked != null && hooked.TryGetValue(def, out set) && set.Contains(param)) return Use.Display;
            for (int i = 0; i < rules.Display.Count; i++)
                if (Match(rules.Display[i], m, dt, name, param)) return Use.Display;
            for (int i = 0; i < rules.Keep.Count; i++)
                if (Match(rules.Keep[i], m, dt, name, param)) { why = dt + "::" + name; return Use.Keep; }
            for (int i = 0; i < rules.Veto.Count; i++)
                if (Match(rules.Veto[i], m, dt, name, param)) { why = dt + "::" + name; return Use.Id; }

            Use builtin = Builtin(m, dt, name, param, code);
            if (builtin == Use.Id || builtin == Use.Keep) why = dt + "::" + name;
            return builtin;
        }

        private static int LocalIndex(Instruction ins, bool store)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Stloc_0: return store ? 0 : -1;
                case Code.Stloc_1: return store ? 1 : -1;
                case Code.Stloc_2: return store ? 2 : -1;
                case Code.Stloc_3: return store ? 3 : -1;
                case Code.Stloc:
                case Code.Stloc_S:
                    return store ? Index(ins.Operand) : -1;
                case Code.Ldloc_0: return store ? -1 : 0;
                case Code.Ldloc_1: return store ? -1 : 1;
                case Code.Ldloc_2: return store ? -1 : 2;
                case Code.Ldloc_3: return store ? -1 : 3;
                case Code.Ldloc:
                case Code.Ldloc_S:
                    return store ? -1 : Index(ins.Operand);
            }
            return -1;
        }

        private static int Index(object operand)
        {
            VariableDefinition v = operand as VariableDefinition;
            return v == null ? -1 : v.Index;
        }

        private static Use Builtin(MethodReference m, string dt, string name, int param, Code code)
        {
            if (dt == "System.String")
            {
                switch (name)
                {
                    case "op_Equality":
                    case "op_Inequality":
                    case "Equals":
                    case "StartsWith":
                    case "EndsWith":
                    case "Contains":
                    case "IndexOf":
                    case "LastIndexOf":
                    case "IndexOfAny":
                    case "Compare":
                    case "CompareOrdinal":
                    case "CompareTo":
                    case "Split":
                    case "IsNullOrEmpty":
                    case "IsNullOrWhiteSpace":
                    case "Intern":
                        return Use.Id;
                    case "Replace":
                        return param == 0 ? Use.Id : Use.Unknown;
                    case "Trim":
                    case "TrimStart":
                    case "TrimEnd":
                        return param >= 0 ? Use.Id : Use.Unknown;
                }
                return Use.Unknown;
            }

            if (dt.StartsWith("System.Collections.", StringComparison.Ordinal))
            {
                switch (name)
                {
                    case "ContainsKey":
                    case "TryGetValue":
                    case "get_Item":
                    case "Contains":
                    case "IndexOf":
                    case "LastIndexOf":
                    case "Remove":
                    case "TryRemove":
                    case "GetValueOrDefault":
                    case "ContainsValue":
                        return Use.Id;
                    case "set_Item":
                    case "Add":
                    case "TryAdd":
                    case ".ctor":
                        if (dt.IndexOf("Dictionary", StringComparison.Ordinal) >= 0 || dt.IndexOf("SortedList", StringComparison.Ordinal) >= 0
                            || dt.IndexOf("Hashtable", StringComparison.Ordinal) >= 0 || dt.IndexOf("KeyValuePair", StringComparison.Ordinal) >= 0)
                            return param == 0 ? Use.Id : Use.Unknown;
                        if (dt.IndexOf("Set`", StringComparison.Ordinal) >= 0) return Use.Id;
                        return Use.Unknown;
                }
                return Use.Unknown;
            }

            if (dt.StartsWith("System.", StringComparison.Ordinal))
            {
                if (code == Code.Newobj && dt.EndsWith("Exception", StringComparison.Ordinal)) return Use.Keep;
                switch (dt)
                {
                    case "System.Array":
                    case "System.Linq.Enumerable":
                        return (name == "IndexOf" || name == "LastIndexOf" || name == "Contains" || name == "BinarySearch") ? Use.Id : Use.Unknown;
                    case "System.Console":
                        return Use.Keep;
                    case "System.Enum":
                    case "System.Type":
                    case "System.Activator":
                    case "System.Convert":
                    case "System.Environment":
                    case "System.Guid":
                    case "System.Version":
                    case "System.Uri":
                    case "System.AppDomain":
                    case "System.Text.Encoding":
                    case "System.StringComparer":
                    case "System.Int16":
                    case "System.Int32":
                    case "System.Int64":
                    case "System.UInt16":
                    case "System.UInt32":
                    case "System.UInt64":
                    case "System.Byte":
                    case "System.SByte":
                    case "System.Single":
                    case "System.Double":
                    case "System.Decimal":
                    case "System.Boolean":
                    case "System.Char":
                    case "System.DateTime":
                    case "System.TimeSpan":
                    case "System.Math":
                        return Use.Id;
                    case "System.Text.StringBuilder":
                        return (name == "Replace" && param == 0) ? Use.Id : Use.Unknown;
                }
                if (dt.StartsWith("System.Diagnostics.", StringComparison.Ordinal)) return Use.Keep;
                if (dt.StartsWith("System.Reflection.", StringComparison.Ordinal)
                    || dt.StartsWith("System.IO.", StringComparison.Ordinal)
                    || dt.StartsWith("System.Text.RegularExpressions.", StringComparison.Ordinal)
                    || dt.StartsWith("System.Net.", StringComparison.Ordinal)
                    || dt.StartsWith("System.Threading.", StringComparison.Ordinal)
                    || dt.StartsWith("System.Globalization.", StringComparison.Ordinal)
                    || dt.StartsWith("System.Runtime.", StringComparison.Ordinal)
                    || dt.StartsWith("System.Security.", StringComparison.Ordinal)
                    || dt.StartsWith("System.Xml.", StringComparison.Ordinal))
                    return Use.Id;
                return Use.Unknown;
            }

            if (dt.StartsWith("BepInEx.Configuration.", StringComparison.Ordinal))
            {
                // Bind<T>(section, key, default, description): only the description is text
                if (name == "Bind")
                    return (m.Parameters.Count == 4 && param == 3 && m.Parameters[3].ParameterType.FullName == "System.String") ? Use.Display : Use.Id;
                if (dt == "BepInEx.Configuration.ConfigDescription" && name == ".ctor") return param == 0 ? Use.Display : Use.Id;
                return Use.Id;
            }
            if (dt.StartsWith("BepInEx.Logging.", StringComparison.Ordinal)) return Use.Keep;
            if (dt.StartsWith("BepInEx.", StringComparison.Ordinal)) return Use.Id;        // plugin metadata, paths
            if (dt.StartsWith("HarmonyLib.", StringComparison.Ordinal)
                || dt.StartsWith("MonoMod.", StringComparison.Ordinal)
                || dt.StartsWith("Mono.", StringComparison.Ordinal)
                || dt.StartsWith("Photon.", StringComparison.Ordinal)
                || dt.StartsWith("ExitGames.", StringComparison.Ordinal)
                || dt.StartsWith("Steamworks.", StringComparison.Ordinal))
                return Use.Id;

            if (dt.StartsWith("UnityEngine.", StringComparison.Ordinal))
            {
                if (IsUnityText(dt, name)) return Use.Display;
                if (dt == "UnityEngine.Debug" || dt == "UnityEngine.Logger") return Use.Keep;
                if (dt == "UnityEngine.GUIUtility" && name == "set_systemCopyBuffer") return Use.Keep;   // reports / presets meant to be pasted elsewhere
                return Use.Id;
            }
            if (dt.StartsWith("TMPro.", StringComparison.Ordinal))
                return (name == "set_text" || name == "SetText") ? Use.Display : Use.Id;
            return Use.Unknown;
        }

        private static bool IsUnityText(string dt, string name)
        {
            switch (dt)
            {
                case "UnityEngine.GUI":
                case "UnityEngine.GUILayout":
                    return true;
                case "UnityEngine.GUIContent":
                    return name == ".ctor" || name == "set_text" || name == "set_tooltip";
                case "UnityEngine.TextMesh":
                case "UnityEngine.UI.Text":
                    return name == "set_text";
            }
            return false;
        }

        /// <summary>A single "word" such as Class, All, Lv, Rare+ - the kind of literal that doubles as an id.</summary>
        public static bool IsTokenLike(string s)
        {
            if (s.Length == 0 || s.Length > 40) return false;
            bool letter = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) { letter = true; continue; }
                if (ch >= '0' && ch <= '9') continue;
                if (ch == '_' || ch == '.' || ch == '-' || ch == '+' || ch == '/' || ch == ':' || ch == '#' || ch == '%') continue;
                return false;
            }
            return letter;
        }

        public static string Describe(Consumer c)
        {
            if (c.Ins == null) return "?";
            MethodReference m = c.Method;
            if (m != null) return TypeName(m.DeclaringType) + "::" + m.Name + "#" + c.Param;
            return c.Ins.OpCode.Name;
        }
    }
}
