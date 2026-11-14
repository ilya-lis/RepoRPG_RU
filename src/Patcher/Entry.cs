// BepInEx 5 preloader patcher.
//
// BepInEx can only hand *game* assemblies to a patcher, never another plugin, so nothing is patched through
// the Patch() callback. Instead Initialize() (which runs before any plugin is even looked at) writes a
// translated COPY of RepoRPG.dll next to this add-on. The copy carries the same plugin GUID with the version
// raised by ".1"; when two plugins share a GUID BepInEx loads the higher version and skips the other, so the
// original file is never modified and never loaded. Remove this add-on (or set Enabled = false) and the
// copy is deleted on the next start - the original mod is back, untouched.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using Mono.Cecil;

[assembly: AssemblyTitle("RepoRPG RU - patcher")]
[assembly: AssemblyVersion("0.2.1.0")]

namespace RepoRPG_RU
{
    public static class Patcher
    {
        private static readonly string[] None = new string[0];

        // --- the BepInEx patcher contract -------------------------------------------------------
        public static IEnumerable<string> TargetDLLs { get { return None; } }

        public static void Patch(AssemblyDefinition assembly) { }

        private static void AddPath(List<string> list, string property)
        {
            try
            {
                PropertyInfo p = typeof(BepInEx.Paths).GetProperty(property, BindingFlags.Public | BindingFlags.Static);
                if (p == null) return;
                object value = p.GetValue(null, null);
                string one = value as string;
                if (one != null) { list.Add(one); return; }
                string[] many = value as string[];
                if (many != null) list.AddRange(many);
            }
            catch { }
        }

        public static void Initialize()
        {
            ManualLogSource log = null;
            try
            {
                log = Logger.CreateLogSource("RepoRPG RU");
                Env env = new Env();
                env.PluginPath = BepInEx.Paths.PluginPath;
                env.ConfigPath = BepInEx.Paths.ConfigPath;
                // fallback search path for Cecil, in Mono's own order; read by reflection because older BepInEx 5 builds lack some of them
                AddPath(env.SearchDirs, "DllSearchPaths");
                AddPath(env.SearchDirs, "ManagedPath");
                AddPath(env.SearchDirs, "BepInExAssemblyDirectory");
                try { env.SearchDirs.Add(Path.GetDirectoryName(typeof(object).Assembly.Location)); } catch { }
                env.SearchDirs.Add(BepInEx.Paths.PluginPath);
                env.SearchDirs.Add(BepInEx.Paths.PatcherPluginPath);
                ManualLogSource sink = log;
                env.Info = delegate(string s) { sink.LogInfo(s); };
                env.Warn = delegate(string s) { sink.LogWarning(s); };
                env.Error = delegate(string s) { sink.LogError(s); };
                Worker.Run(env);
            }
            catch (Exception e)
            {
                try { if (log != null) log.LogError("Русификатор не запустился, мод RepoRPG останется английским: " + e); }
                catch { }
            }
        }
    }

    internal sealed class Env
    {
        public string PluginPath;
        public string ConfigPath;
        public readonly List<string> SearchDirs = new List<string>();
        public Action<string> Info, Warn, Error;
    }

    internal static class Worker
    {
        public const string StampResource = "RepoRPG_RU.stamp";
        private const string OutputName = BuildInfo.TargetFile;

        public static void Run(Env env)
        {
            if (string.IsNullOrEmpty(env.PluginPath) || !Directory.Exists(env.PluginPath)) return;

            Settings settings = Settings.Load(Path.Combine(env.ConfigPath ?? "", Settings.FileName), true);

            string runtimePath = FindFile(env.PluginPath, BuildInfo.RuntimeFile);
            string home = runtimePath != null ? Path.GetDirectoryName(runtimePath) : null;
            string output = home != null ? Path.Combine(home, OutputName) : null;

            // leftovers of an older layout / a moved folder: any generated copy that is not the current output
            Candidate original = null;
            foreach (string path in FindAll(env.PluginPath, OutputName))
            {
                Candidate c = Inspect(path);
                if (c == null) continue;
                if (c.Generated)
                {
                    if (output == null || !SamePath(path, output)) TryDelete(path, env, "устаревшая копия");
                    continue;
                }
                if (output != null && SamePath(path, output))
                {
                    // someone put the real mod where the copy is written - never overwrite a file we did not make
                    env.Warn("Оригинальный RepoRPG.dll лежит в папке русификатора (" + path + "). Перенесите его в отдельную папку, иначе перевод не применяется.");
                    return;
                }
                if (original == null || c.Version > original.Version) original = c;
            }

            if (home == null)
            {
                env.Warn("Не найден " + BuildInfo.RuntimeFile + " в BepInEx/plugins - русификатор установлен не полностью, перевод не применяется.");
                return;
            }
            if (!settings.Enabled)
            {
                TryDelete(output, env, "перевод выключен в " + Settings.FileName);
                env.Info("Перевод выключен (Enabled = false).");
                return;
            }
            if (original == null)
            {
                TryDelete(output, env, "оригинальный мод не найден");
                env.Warn("Мод RepoRPG (RepoRPG.dll) не найден или выключен - переводить нечего.");
                return;
            }

            try
            {
                Translate(env, settings, original, home, output);
            }
            catch (Exception e)
            {
                TryDelete(output, env, "ошибка при создании");
                env.Error("Не удалось создать переведённую копию RepoRPG " + original.VersionText + " - мод загрузится на английском. Причина: " + e);
            }
        }

        private static void Translate(Env env, Settings settings, Candidate original, string home, string output)
        {
            Lang lang = Lang.Load(home);
            Rules rules = Rules.Load(home);
            foreach (string p in lang.Problems) env.Warn("перевод: " + p);
            foreach (string p in rules.Problems) env.Warn("правила: " + p);
            if (lang.Entries == 0)
            {
                TryDelete(output, env, "нет файлов перевода");
                env.Warn("В папке " + home + " нет файлов перевода (*.po) - перевод не применяется.");
                return;
            }

            string runtimePath = Path.Combine(home, BuildInfo.RuntimeFile);
            Version runtimeVersion = null;
            try { runtimeVersion = AssemblyName.GetAssemblyName(runtimePath).Version; } catch { }

            ulong h = Hash.Fnv(Hash.Seed, original.Bytes);
            string stamp = "2|" + BuildInfo.Version + "|" + h.ToString("x16") + "|" + lang.Fingerprint.ToString("x16") + "|"
                           + rules.Fingerprint.ToString("x16") + "|" + settings.Signature + "|" + (runtimeVersion == null ? "-" : runtimeVersion.ToString());

            string dumpDir = Path.Combine(Path.Combine(env.ConfigPath ?? home, "RepoRPG_RU"), "dump");
            if (!settings.DumpStrings && File.Exists(output))
            {
                Candidate existing = Inspect(output);
                if (existing != null && existing.Generated && existing.Stamp == stamp)
                {
                    env.Info("RepoRPG " + original.VersionText + ": перевод актуален (" + lang.Entries + " строк в словаре).");
                    return;
                }
            }

            BuildOptions o = new BuildOptions();
            o.Settings = settings;
            o.Lang = lang;
            o.Rules = rules;
            o.HaveRuntime = runtimeVersion != null;
            if (runtimeVersion != null) o.RuntimeVersion = runtimeVersion;
            o.Dump = settings.DumpStrings;
            o.Stamp = stamp;
            o.SearchDirs.AddRange(env.SearchDirs);

            Report report = new Report();
            DateTime started = DateTime.UtcNow;
            byte[] result = Builder.Build(original.Bytes, o, report);

            string tmp = output + ".tmp";
            File.WriteAllBytes(tmp, result);
            if (File.Exists(output)) File.Delete(output);
            File.Move(tmp, output);

            double ms = (DateTime.UtcNow - started).TotalMilliseconds;
            env.Info("RepoRPG " + report.OriginalVersion + " переведён (копия " + report.NewVersion + "): строк в коде заменено " + report.Replaced
                     + ", в данных " + report.DataReplaced + ", точек перевода при выводе " + (report.HooksApplied + report.CallHooks) + "; " + ms.ToString("0") + " мс.");
            env.Info("Сообщение BepInEx «Skipping [RepoRPG " + report.OriginalVersion + "] because a newer version exists» ниже - это нормально: так загружается переведённая копия.");
            if (report.HookRulesMissed > 0)
                env.Warn(report.HookRulesMissed + " точек перевода не найдены в этой версии RepoRPG (мод обновился?) - часть текста может остаться английской. Подробности в report.txt.");
            if (report.FontRulesMissed > 0)
                env.Warn("Место, где мод выбирает шрифт меню, в этой версии RepoRPG не найдено (мод обновился?) - русские буквы будут рисоваться системным шрифтом, как раньше. Подробности: BepInEx/config/RepoRPG_RU/report.txt");

            try { WriteReport(env, home, original, lang, report, o, dumpDir); }
            catch (Exception e) { env.Warn("Отчёт не записан: " + e.Message); }
        }

        // ---------------------------------------------------------------------------------------- files

        internal sealed class Candidate
        {
            public string Path;
            public byte[] Bytes;
            public Version Version = new Version(0, 0);
            public string VersionText = "?";
            public bool Generated;
            public string Stamp;
        }

        /// <summary>Returns null unless the file is a RepoRPG plugin assembly (original or generated).</summary>
        internal static Candidate Inspect(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                ReaderParameters rp = new ReaderParameters();
                rp.ReadingMode = ReadingMode.Deferred;
                rp.InMemory = true;
                using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes, false), rp))
                {
                    Candidate c = new Candidate();
                    c.Path = path;
                    c.Bytes = bytes;
                    foreach (Resource r in asm.MainModule.Resources)
                    {
                        EmbeddedResource er = r as EmbeddedResource;
                        if (er == null || er.Name != StampResource) continue;
                        c.Generated = true;
                        c.Stamp = Encoding.UTF8.GetString(er.GetResourceData());
                    }
                    string version = Builder.ReadPluginVersion(asm.MainModule);
                    if (version == null) return c.Generated ? c : null;
                    c.VersionText = version;
                    try { c.Version = new Version(version); } catch { }
                    return c;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string FindFile(string root, string name)
        {
            List<string> all = FindAll(root, name);
            return all.Count > 0 ? all[0] : null;
        }

        private static List<string> FindAll(string root, string name)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(root, name, SearchOption.AllDirectories))
                    if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) result.Add(f);
            }
            catch { }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void TryDelete(string path, Env env, string why)
        {
            if (path == null) return;
            try
            {
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                if (!File.Exists(path)) return;
                Candidate c = Inspect(path);
                if (c != null && !c.Generated) return;      // not ours - hands off
                File.Delete(path);
                env.Info("Переведённая копия удалена (" + why + "): " + path);
            }
            catch (Exception e)
            {
                env.Warn("Не удалось удалить " + path + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------------------------------- report

        private static void WriteReport(Env env, string home, Candidate original, Lang lang, Report report, BuildOptions o, string dumpDir)
        {
            string dir = Path.Combine(env.ConfigPath ?? home, "RepoRPG_RU");
            Directory.CreateDirectory(dir);
            UTF8Encoding utf8 = new UTF8Encoding(false);

            StringBuilder sb = new StringBuilder();
            sb.Append("RepoRPG RU ").Append(BuildInfo.Version).Append(" - отчёт о последней сборке перевода\n");
            sb.Append("оригинал: ").Append(original.Path).Append("  (версия ").Append(report.OriginalVersion).Append(")\n");
            sb.Append("копия:    версия ").Append(report.NewVersion).Append('\n');
            sb.Append("словарь:  ").Append(lang.Entries).Append(" записей из ").Append(lang.Files.Count).Append(" файлов, отклонено ").Append(lang.Rejected).Append('\n');
            sb.Append("методов: ").Append(report.Methods).Append(", строковых литералов: ").Append(report.Literals).Append('\n');
            sb.Append("заменено в коде: ").Append(report.Replaced).Append(" (из них по области: ").Append(report.ReplacedScoped).Append(")\n");
            sb.Append("не тронуто (идентификатор/ключ/лог): ").Append(report.SkippedVeto).Append('\n');
            sb.Append("оставлено на перевод при выводе (одиночные слова): ").Append(report.SkippedToken).Append('\n');
            sb.Append("оставлено на перевод при выводе (мод узнаёт эти тексты по словам): ").Append(report.SkippedProbe).Append("  [проверок текста в моде: ").Append(report.Probes).Append("]\n");
            sb.Append("оставлено на перевод при выводе (часть составной фразы, которую нельзя заменить целиком): ").Append(report.SkippedSentence).Append('\n');
            sb.Append("не тронуто (исключённые типы): ").Append(report.SkippedType).Append('\n');
            sb.Append("точек перевода при выводе: ").Append(report.HooksApplied).Append(" + ").Append(report.CallHooks).Append(" в местах вызова").Append(", правил без цели: ").Append(report.HookRulesMissed).Append('\n');
            sb.Append("шрифт меню: мест, где мод теперь спрашивает шрифт у русификатора: ").Append(report.FontHooks).Append(", правил без цели: ").Append(report.FontRulesMissed)
              .Append("  (что из этого вышло в игре - в font.txt рядом с этим файлом)\n");
            sb.Append("данные (JSON): заменено ").Append(report.DataReplaced).Append(" значений в ").Append(report.DataFiles).Append(" файлах\n");
            sb.Append("перечисления в константах: известно ").Append(report.EnumsKnown).Append(", запрошено ").Append(report.EnumsAnswered)
              .Append("; обращений к чужим сборкам: ").Append(report.AssemblyRequests).Append('\n');

            int unusedGlobal = 0;
            StringBuilder unused = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in lang.Global)
            {
                if (report.UsedGlobal.Contains(kv.Key)) continue;
                unusedGlobal++;
                unused.Append("  ").Append(Builder.Escape(kv.Key)).Append('\n');
            }
            sb.Append("записей словаря, не найденных в коде этой версии: ").Append(unusedGlobal).Append('\n');
            sb.Append('\n');
            if (report.Notes.Count > 0)
            {
                sb.Append("--- подробности ---\n");
                foreach (string n in report.Notes) sb.Append(n).Append('\n');
                sb.Append('\n');
            }
            if (unusedGlobal > 0)
            {
                sb.Append("--- записи словаря без совпадений (работают только при выводе на экран) ---\n");
                sb.Append(unused);
            }
            File.WriteAllText(Path.Combine(dir, "report.txt"), sb.ToString(), utf8);

            if (!o.Dump) return;
            Directory.CreateDirectory(dumpDir);
            File.WriteAllText(Path.Combine(dumpDir, "literals.tsv"), "scope\tconsumer\ttext\n" + string.Join("\n", report.Dump.ToArray()) + "\n", utf8);
            File.WriteAllText(Path.Combine(dumpDir, "data-strings.tsv"), "file\tpath\ttranslatable\ttext\n" + string.Join("\n", report.DataDump.ToArray()) + "\n", utf8);
            foreach (KeyValuePair<string, byte[]> kv in report.Resources)
                File.WriteAllBytes(Path.Combine(dumpDir, kv.Key), kv.Value);
            env.Info("Строки и данные оригинального мода выгружены в " + dumpDir + " (выключите DumpStrings в " + Settings.FileName + ").");
        }
    }
}
