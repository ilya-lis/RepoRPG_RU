// Cecil has to "resolve" a type in exactly one situation while it writes an assembly: a constant (const field or
// default parameter value) whose declared type lives in another assembly - it must be an enum, and Cecil wants
// to know its underlying integer type. RepoRPG has plenty of those (TextAnchor, FontStyle, KeyCode ...).
//
// Depending on the game folder layout to find UnityEngine*.dll at that moment would be the one thing that cannot be
// tested outside the game, so it is avoided: the underlying type is already known - it is the CLR type of the
// stored constant itself. EnumResolver answers those questions from memory and never opens a file for them.
// Anything else (nothing is expected) falls through to DirectoryResolver, which looks in the game's own folders.
using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace RepoRPG_RU
{
    internal sealed class DirectoryResolver : IAssemblyResolver
    {
        private readonly List<string> roots;
        private Dictionary<string, string> index;
        private readonly Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        public IMetadataResolver Metadata;
        public int Requests;

        public DirectoryResolver(IEnumerable<string> directories)
        {
            roots = new List<string>();
            if (directories != null)
                foreach (string d in directories)
                    if (!string.IsNullOrEmpty(d)) roots.Add(d);
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return Resolve(name, null);
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            Requests++;
            AssemblyDefinition found;
            if (cache.TryGetValue(name.Name, out found)) return found;
            if (index == null) BuildIndex();
            string path;
            if (!index.TryGetValue(name.Name, out path)) throw new AssemblyResolutionException(name);

            ReaderParameters rp = new ReaderParameters();
            rp.ReadingMode = ReadingMode.Deferred;
            rp.InMemory = true;                 // no file handle stays open
            rp.AssemblyResolver = this;
            if (Metadata != null) rp.MetadataResolver = Metadata;
            found = AssemblyDefinition.ReadAssembly(path, rp);
            cache[name.Name] = found;
            return found;
        }

        private void BuildIndex()
        {
            index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    string[] files = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        string simple = Path.GetFileNameWithoutExtension(f);
                        if (!index.ContainsKey(simple)) index[simple] = f;      // earlier roots win
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            foreach (AssemblyDefinition a in cache.Values)
            {
                try { a.Dispose(); } catch { }
            }
            cache.Clear();
        }
    }

    internal sealed class EnumResolver : MetadataResolver
    {
        private readonly Dictionary<string, TypeDefinition> known = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        public int Answered;

        public EnumResolver(IAssemblyResolver assemblies) : base(assemblies) { }

        public int Count { get { return known.Count; } }

        public override TypeDefinition Resolve(TypeReference type)
        {
            if (type != null)
            {
                TypeReference element = type.GetElementType();
                TypeDefinition answer;
                if (!(element is TypeDefinition) && known.TryGetValue(element.FullName, out answer))
                {
                    Answered++;
                    return answer;
                }
            }
            return base.Resolve(type);
        }

        /// <summary>Walks every constant of the module and remembers "this foreign type is an enum over that integer".</summary>
        public void Learn(ModuleDefinition module, IEnumerable<TypeDefinition> types)
        {
            TypeReference enumBase = new TypeReference("System", "Enum", module, module.TypeSystem.CoreLibrary);
            foreach (TypeDefinition t in types)
            {
                if (t.HasFields)
                    foreach (FieldDefinition f in t.Fields)
                        if (f.HasConstant) Note(module, enumBase, f.FieldType, f.Constant);
                if (t.HasProperties)
                    foreach (PropertyDefinition p in t.Properties)
                        if (p.HasConstant) Note(module, enumBase, p.PropertyType, p.Constant);
                if (t.HasMethods)
                    foreach (MethodDefinition m in t.Methods)
                    {
                        if (!m.HasParameters) continue;
                        foreach (ParameterDefinition p in m.Parameters)
                            if (p.HasConstant) Note(module, enumBase, p.ParameterType, p.Constant);
                    }
            }
        }

        private void Note(ModuleDefinition module, TypeReference enumBase, TypeReference declared, object constant)
        {
            if (constant == null || declared == null) return;
            TypeReference t = declared;
            for (int guard = 0; guard < 8; guard++)
            {
                GenericInstanceType generic = t as GenericInstanceType;
                if (generic != null)
                {
                    if (generic.GenericArguments.Count == 1 && generic.ElementType.Namespace == "System" && generic.ElementType.Name == "Nullable`1")
                    {
                        t = generic.GenericArguments[0];
                        continue;
                    }
                    return;
                }
                TypeSpecification spec = t as TypeSpecification;     // modifiers, by-ref
                if (spec == null) break;
                t = spec.ElementType;
            }
            if (t is TypeDefinition || t.IsGenericParameter) return;
            MetadataType kind = t.MetadataType;
            if (kind != MetadataType.ValueType && kind != MetadataType.Class) return;      // primitives, string, object: no question asked

            TypeReference underlying;
            TypeSystem ts = module.TypeSystem;
            if (constant is int) underlying = ts.Int32;
            else if (constant is byte) underlying = ts.Byte;
            else if (constant is uint) underlying = ts.UInt32;
            else if (constant is short) underlying = ts.Int16;
            else if (constant is ushort) underlying = ts.UInt16;
            else if (constant is sbyte) underlying = ts.SByte;
            else if (constant is long) underlying = ts.Int64;
            else if (constant is ulong) underlying = ts.UInt64;
            else return;

            string key = t.FullName;
            if (known.ContainsKey(key)) return;
            TypeDefinition stand = new TypeDefinition(t.Namespace, t.Name, TypeAttributes.Public | TypeAttributes.Sealed, enumBase);
            stand.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, underlying));
            known[key] = stand;
        }
    }
}
