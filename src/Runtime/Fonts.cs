// The font of the mod's menus.
//
// RepoRPG draws its menus with the game's own font, Teko, and Teko has no Russian letters. Each of them then comes
// from whatever font the system offers instead - about twice as wide as Teko - and the captions run out of boxes
// that were made for a narrow font. The add-on brings font/RepoRPG_RU.ttf: Teko itself with Russian letters of
// the same height, weight and (nearly) width added (tools/mkfont.py).
//
// The mod asks for it through Loc.MenuFont: the patcher puts that question at the start of the method where the mod
// picks its font. null means "pick it yourself", and that is the answer whenever anything at all is not as expected:
//   * the player chose a font in the mod's settings, or switched ours off (MenuFont = false in RepoRPG_RU.cfg);
//   * the file is missing, or Unity did not really read it. That is measured, not assumed: a few letters are
//     asked for and their widths compared with the ones written next to the font when it was built
//     (RepoRPG_RU.ttf.check). A font that fell back to a system one gives very different numbers.
// A profile folder with Russian letters in its path (a Windows user name is enough) gets two more tries when the
// first one fails: the short DOS name of the same file, then a copy in C:\ProgramData\RepoRPG_RU. The last try is
// the font's family name: it works when the player installed the file as a system font.
// What was decided, and why, goes to BepInEx/config/RepoRPG_RU/font.txt.
//
// Unity is reached by reflection only: this assembly still references nothing but the core library.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace RepoRPG_RU
{
    internal static class Fonts
    {
        public const string FileName = "RepoRPG_RU.ttf";
        private const string ModConfigFile = BuildInfo.TargetGuid + ".cfg";
        private const int Tolerance = 3;        // pixels at the size of the check (100): rounding and hinting differ by one at most

        private static readonly object Gate = new object();
        private static object font;             // the UnityEngine.Font, once made and measured
        private static bool refused;            // tried and failed: not tried again until the game is restarted
        private static string made;             // how making it went: the file, the attempts, the widths
        private static string madeSummary;
        private static readonly List<string> decisions = new List<string>();   // every different answer given so far, in order: font.txt
        private static string written, said;
        private static string home, config;
        private static bool placed;

        private sealed class Expect
        {
            public char Char;
            public int Size, Advance;
        }

        public static object Pick(string setting)
        {
            lock (Gate)
            {
                StringBuilder log = new StringBuilder();
                object result = null;
                string summary;
                try
                {
                    result = Decide(setting, log, out summary);
                }
                catch (Exception e)
                {
                    result = null;
                    refused = true;
                    summary = madeSummary = "шрифт не подключён: " + e.GetType().Name + ": " + e.Message;
                }
                Report(log.ToString(), summary);
                return result;
            }
        }

        private static object Decide(string setting, StringBuilder log, out string summary)
        {
            if (!placed)
            {
                try { home = Path.GetDirectoryName(typeof(Fonts).Assembly.Location); } catch { }
                if (!string.IsNullOrEmpty(home)) config = Loc.FindConfigPath(home);
                placed = true;
            }
            if (string.IsNullOrEmpty(home)) { summary = "шрифт не подключён: неизвестно, где лежит русификатор"; return null; }

            Settings settings = config == null ? new Settings() : Settings.Load(Path.Combine(config, Settings.FileName), false);
            if (!settings.Enabled || !settings.DisplayHooks || !settings.MenuFont)
            {
                summary = "шрифт не подключён: выключено в " + Settings.FileName + " (MenuFont = false)";
                return null;
            }

            // the player's own choice in the mod's settings wins
            if (setting == null)
            {
                setting = ModSetting();
                log.Append("настройка мода Controls/UiFont (из файла ").Append(ModConfigFile).Append("): ").Append(setting ?? "не найдена, считаем auto").Append('\n');
            }
            else log.Append("настройка мода Controls/UiFont: ").Append(setting.Length == 0 ? "(пусто)" : setting).Append('\n');
            if (setting != null) setting = setting.Trim();
            if (!string.IsNullOrEmpty(setting) && !string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase))
            {
                summary = "шрифт не подключён: в настройках мода выбран свой шрифт («" + setting + "») - он и остаётся. Чтобы вернуть шрифт игры с русскими буквами, поставьте там auto";
                return null;
            }

            if (font != null)
            {
                if (Alive(font)) { summary = madeSummary; return font; }
                font = null;                                    // the game unloaded it: once more
                log.Append("шрифт был выгружен игрой - создаём заново\n");
            }
            if (refused) { summary = madeSummary; return null; }

            StringBuilder how = new StringBuilder();
            object result = Make(how, out summary);
            if (result == null) refused = true;
            font = result;
            made = how.ToString();
            madeSummary = summary;
            return result;
        }

        private static object Make(StringBuilder log, out string summary)
        {
            string file = Find(home);
            if (file == null) { summary = "шрифт не подключён: файл " + FileName + " не найден в папке " + home; return null; }
            string family;
            List<Expect> expect = ReadCheck(file + ".check", out family);
            if (expect.Count == 0) { summary = "шрифт не подключён: рядом с " + FileName + " нет файла с контрольными ширинами (" + FileName + ".check)"; return null; }
            long length = 0;
            try { length = new FileInfo(file).Length; } catch { }
            log.Append("файл: ").Append(file).Append(" (").Append(length).Append(" байт)\n");

            Unity unity = Unity.Find();
            if (unity.Problem != null) { summary = "шрифт не подключён: " + unity.Problem; return null; }

            // 1. Straight from where it lies.
            // 2. If that fails and the path has letters outside ASCII (a Windows user name in Russian is enough): through a
            //    path that has none - the short DOS name of the same file, then a copy.
            // 3. By its name, in case the player installed the file as a system font (what font.txt advises when 1 and 2 fail).
            List<string> paths = new List<string>();
            paths.Add(file);
            int attempt = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                log.Append("попытка ").Append(++attempt).Append(": файл ").Append(path).Append('\n');
                if (Try(unity, path, false, expect, log))
                {
                    Survey(unity, tried, log);
                    summary = "шрифт подключён: шрифт игры (Teko) с русскими буквами" + (i > 0 ? ", прочитан по пути " + path : "");
                    return tried;
                }
                if (i == 0 && !Ascii(file))
                {
                    string dos = ShortPath(file);
                    if (dos != null) paths.Add(dos);
                    string copy = Copy(file, log);
                    if (copy != null) paths.Add(copy);
                    if (paths.Count == 1) log.Append("  в пути к файлу есть буквы не из латиницы, а другого пути к нему нет\n");
                }
            }
            if (family != null && unity.CanAskSystem)
            {
                log.Append("попытка ").Append(++attempt).Append(": шрифт «").Append(family).Append("», установленный в системе\n");
                if (Try(unity, family, true, expect, log))
                {
                    Survey(unity, tried, log);
                    summary = "шрифт подключён: шрифт игры (Teko) с русскими буквами, установленный в системе («" + family + "»)";
                    return tried;
                }
            }
            Survey(unity, null, log);
            summary = "шрифт не подключён: игра не прочитала файл шрифта (подробности выше) - мод рисует своим шрифтом, как раньше. "
                      + "Что можно сделать: установить этот файл в Windows (правый щелчок по " + file + " - «Установить для всех пользователей») и перезапустить игру: русификатор найдёт шрифт по имени"
                      + (family != null ? " «" + family + "»" : "");
            return null;
        }

        /// <summary>
        /// For the author of the translation: the fonts the game holds, with their line metrics and the width of two Latin
        /// letters. It tells whether the add-on's font really matches the game's own (weight, line height) - something no
        /// screenshot shows exactly.
        /// </summary>
        private static void Survey(Unity unity, object ours, StringBuilder log)
        {
            try
            {
                List<object> fonts = unity.AllFonts();
                if (fonts == null || fonts.Count == 0) return;
                // "→" and "★" are in none of these fonts: the mod draws them all the same, the game takes them from a system font.
                // A "-" there for the add-on's font (marked *) would mean such signs are lost with it.
                log.Append("шрифты в игре (размер / высота строки / подъём; ширина H, W и знаков → ★ при 100 пикс.):\n");
                int shown = 0;
                foreach (object f in fonts)
                {
                    if (shown++ >= 16) { log.Append("  ... всего ").Append(fonts.Count).Append('\n'); break; }
                    log.Append("  ").Append(ReferenceEquals(f, ours) ? "* " : "").Append(unity.Describe(f)).Append('\n');
                }
            }
            catch { }
        }

        private static object tried;

        /// <summary>Makes the font from a file or by its system name and measures it; true when it is the font the check file describes (then it is in <see cref="tried"/>).</summary>
        private static bool Try(Unity unity, string what, bool system, List<Expect> expect, StringBuilder log)
        {
            object result = null;
            string why;
            tried = null;
            try
            {
                result = system ? unity.CreateSystem(what) : unity.Create(what);
                why = result == null ? "игра не создала шрифт" : Measure(unity, result, expect, log);
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                why = inner.GetType().Name + ": " + inner.Message;
            }
            if (why == null)
            {
                unity.Keep(result);
                tried = result;
                return true;
            }
            log.Append("  не подошло: ").Append(why).Append('\n');
            if (result != null) unity.Destroy(result);
            return false;
        }

        // ------------------------------------------------------------------ the file and its check

        private static string Find(string dir)
        {
            try
            {
                string direct = Path.Combine(Path.Combine(dir, "font"), FileName);
                if (File.Exists(direct)) return direct;
                direct = Path.Combine(dir, FileName);
                if (File.Exists(direct)) return direct;
                string[] found = Directory.GetFiles(dir, FileName, SearchOption.AllDirectories);   // a mod manager may flatten or nest the package
                if (found.Length > 0) { Array.Sort(found, StringComparer.OrdinalIgnoreCase); return found[0]; }
            }
            catch { }
            return null;
        }

        /// <summary>Lines "U+0416 TAB size TAB advance in pixels" and "family TAB name"; anything else is a comment.</summary>
        private static List<Expect> ReadCheck(string path, out string family)
        {
            List<Expect> list = new List<Expect>();
            family = null;
            try
            {
                if (!File.Exists(path)) return list;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] f = raw.Trim().Split('\t');
                    if (f.Length == 2 && f[0] == "family" && f[1].Trim().Length > 0) { family = f[1].Trim(); continue; }
                    if (f.Length != 3 || !f[0].StartsWith("U+", StringComparison.Ordinal)) continue;
                    int code, size, advance;
                    if (!int.TryParse(f[0].Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) continue;
                    if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) continue;
                    if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out advance)) continue;
                    if (code <= 0 || code > 0xFFFF || size < 8 || size > 400 || advance <= 0) continue;
                    Expect e = new Expect();
                    e.Char = (char)code;
                    e.Size = size;
                    e.Advance = advance;
                    list.Add(e);
                }
            }
            catch { list.Clear(); }
            return list;
        }

        /// <summary>null when every letter is as wide as the font file says; otherwise what is wrong.</summary>
        private static string Measure(Unity unity, object f, List<Expect> expect, StringBuilder log)
        {
            Dictionary<int, StringBuilder> bySize = new Dictionary<int, StringBuilder>();
            foreach (Expect e in expect)
            {
                StringBuilder sb;
                if (!bySize.TryGetValue(e.Size, out sb)) { sb = new StringBuilder(); bySize[e.Size] = sb; }
                sb.Append(e.Char);
            }
            foreach (KeyValuePair<int, StringBuilder> kv in bySize) unity.Request(f, kv.Value.ToString(), kv.Key);

            string wrong = null;
            foreach (Expect e in expect)
            {
                int got;
                bool known = unity.Advance(f, e.Char, e.Size, out got);
                log.Append("  ").Append(e.Char).Append(" (").Append(e.Size).Append(" пикс.): по файлу ").Append(e.Advance).Append(", в игре ")
                   .Append(known ? got.ToString(CultureInfo.InvariantCulture) : "нет такого знака").Append('\n');
                if (!known) { if (wrong == null) wrong = "в шрифте, который создал Unity, нет знака «" + e.Char + "»"; }
                else if (Math.Abs(got - e.Advance) > Tolerance && wrong == null)
                    wrong = "ширина «" + e.Char + "» " + got + " вместо " + e.Advance + " - игра подставила другой шрифт";
            }
            return wrong;
        }

        private static bool Ascii(string s)
        {
            foreach (char c in s) if (c > '~') return false;
            return true;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW", SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

        /// <summary>The 8.3 name of the same file ("C:\Users\9B8E~1\..."), when Windows keeps such names and it has only ASCII in it.</summary>
        private static string ShortPath(string file)
        {
            try
            {
                if (Path.DirectorySeparatorChar != '\\') return null;
                StringBuilder sb = new StringBuilder(1024);
                uint n = GetShortPathName(file, sb, (uint)sb.Capacity);
                if (n == 0 || n >= sb.Capacity) return null;
                string dos = sb.ToString();
                if (dos.Length == 0 || !Ascii(dos) || string.Equals(dos, file, StringComparison.OrdinalIgnoreCase) || !File.Exists(dos)) return null;
                return dos;
            }
            catch { return null; }
        }

        /// <summary>A copy of the font at a path without letters outside ASCII: the first of ProgramData, the game's folder, the temporary folder that can be written to.</summary>
        private static string Copy(string file, StringBuilder log)
        {
            List<string> roots = new List<string>();
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); } catch { }
            roots.Add(BepInExPath("GameRootPath"));
            try { roots.Add(Path.GetTempPath()); } catch { }

            byte[] bytes = null;
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Ascii(root)) continue;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    string dir = Path.Combine(root, "RepoRPG_RU");
                    string target = Path.Combine(dir, FileName);
                    if (bytes == null) bytes = File.ReadAllBytes(file);
                    if (!Same(target, bytes))
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(target, bytes);
                    }
                    return target;
                }
                catch (Exception e)
                {
                    log.Append("  копия в ").Append(root).Append(" не удалась: ").Append(e.Message).Append('\n');
                }
            }
            return null;
        }

        private static string BepInExPath(string property)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name != "BepInEx") continue;
                    Type paths = a.GetType("BepInEx.Paths", false);
                    if (paths == null) continue;
                    PropertyInfo p = paths.GetProperty(property, BindingFlags.Public | BindingFlags.Static);
                    if (p == null) continue;
                    return p.GetValue(null, null) as string;
                }
            }
            catch { }
            return null;
        }

        private static bool Same(string path, byte[] bytes)
        {
            try
            {
                if (!File.Exists(path)) return false;
                byte[] have = File.ReadAllBytes(path);
                if (have.Length != bytes.Length) return false;
                for (int i = 0; i < have.Length; i++) if (have[i] != bytes[i]) return false;
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ the mod's own setting, when the mod did not tell

        /// <summary>[Controls] UiFont from BepInEx/config/rpgmod.semibotrpg.cfg; null when it cannot be read.</summary>
        private static string ModSetting()
        {
            try
            {
                if (config == null) return null;
                string path = Path.Combine(config, ModConfigFile);
                if (!File.Exists(path)) return null;
                string section = "";
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']') { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || section != "Controls") continue;
                    if (line.Substring(0, eq).Trim() == "UiFont") return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ what happened

        /// <summary>
        /// font.txt: how the font was made (once), then every different answer the mod was given, in the order they first came.
        /// The mod asks again whenever it rebuilds its styles; the same answer is not written or logged twice.
        /// </summary>
        private static void Report(string log, string summary)
        {
            if (summary == null) return;
            string decision = log + "итог: " + summary + "\n";
            if (!decisions.Contains(decision) && decisions.Count < 16) decisions.Add(decision);

            StringBuilder sb = new StringBuilder();
            sb.Append("RepoRPG RU ").Append(BuildInfo.Version).Append(" - шрифт меню\n");
            if (!string.IsNullOrEmpty(made)) sb.Append('\n').Append(made);
            foreach (string d in decisions) sb.Append('\n').Append(d);
            string text = sb.ToString();
            if (text == written) return;
            written = text;

            if (summary != said)
            {
                said = summary;
                Say("Шрифт меню: " + summary + ".");
            }
            try
            {
                if (config == null) return;
                string dir = Path.Combine(config, "RepoRPG_RU");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "font.txt"), text.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            }
            catch { }
        }

        private static object logSource;
        private static MethodInfo logInfo;

        private static void Say(string message)
        {
            try
            {
                if (logInfo == null)
                {
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name != "BepInEx") continue;
                        Type logger = a.GetType("BepInEx.Logging.Logger", false);
                        if (logger == null) continue;
                        MethodInfo create = logger.GetMethod("CreateLogSource", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(string) }, null);
                        if (create == null) continue;
                        object source = create.Invoke(null, new object[] { "RepoRPG RU" });
                        if (source == null) continue;
                        MethodInfo info = source.GetType().GetMethod("LogInfo", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(object) }, null);
                        if (info == null) continue;
                        logSource = source;
                        logInfo = info;
                        break;
                    }
                }
                if (logInfo != null) logInfo.Invoke(logSource, new object[] { message });
            }
            catch { }
        }

        private static bool Alive(object o)
        {
            try { return Unity.Find().Exists(o); }
            catch { return true; }
        }

        // ------------------------------------------------------------------ Unity, by name

        /// <summary>
        /// The few members of UnityEngine this needs, as they are declared in Unity 6 (UnityCsReference, branch 6000.3):
        ///   Font(string name)                                      - a path makes it read that file
        ///   static Font Font.CreateDynamicFontFromOSFont(string, int)
        ///   void Font.RequestCharactersInTexture(string, int, FontStyle)
        ///   bool Font.GetCharacterInfo(char, out CharacterInfo, int, FontStyle);   int CharacterInfo.advance
        ///   Object.hideFlags, Object.Destroy(Object), implicit operator bool(Object)
        ///   only for the lines about the game's fonts in font.txt: Resources.FindObjectsOfTypeAll(Type), Object.name,
        ///   Font.fontSize, Font.lineHeight, Font.ascent, Font.dynamic
        /// </summary>
        private sealed class Unity
        {
            private static Unity found;
            public string Problem;
            private Type fontType, objectType;
            private ConstructorInfo create;
            private MethodInfo request, info, destroy, exists, system, findAll;
            private PropertyInfo advance, hideFlags, objectName, fontSize, lineHeight, ascent, isDynamic;
            private object normal;

            public static Unity Find()
            {
                if (found != null && found.Problem == null) return found;
                Unity u = new Unity();
                try { u.Bind(); }
                catch (Exception e) { u.Problem = "Unity: " + e.GetType().Name + ": " + e.Message; }
                found = u;
                return u;
            }

            private static Type Lookup(string name, string module)
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                foreach (Assembly a in loaded)
                {
                    if (a.GetName().Name != module) continue;
                    Type t = a.GetType(name, false);
                    if (t != null) return t;
                }
                foreach (Assembly a in loaded)                  // older or differently split engines
                {
                    if (!a.GetName().Name.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                    Type t = a.GetType(name, false);
                    if (t != null) return t;
                }
                try { return System.Type.GetType(name + ", " + module, false); } catch { return null; }
            }

            private void Bind()
            {
                const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
                fontType = Lookup("UnityEngine.Font", "UnityEngine.TextRenderingModule");
                Type style = Lookup("UnityEngine.FontStyle", "UnityEngine.TextRenderingModule");
                Type character = Lookup("UnityEngine.CharacterInfo", "UnityEngine.TextRenderingModule");
                objectType = Lookup("UnityEngine.Object", "UnityEngine.CoreModule");
                if (fontType == null || style == null || character == null) { Problem = "в игре не найден тип UnityEngine.Font"; return; }

                create = fontType.GetConstructor(Instance, null, new Type[] { typeof(string) }, null);
                request = fontType.GetMethod("RequestCharactersInTexture", Instance, null, new Type[] { typeof(string), typeof(int), style }, null);
                info = fontType.GetMethod("GetCharacterInfo", Instance, null, new Type[] { typeof(char), character.MakeByRefType(), typeof(int), style }, null);
                advance = character.GetProperty("advance", Instance);
                if (create == null || request == null || info == null || advance == null || advance.PropertyType != typeof(int))
                {
                    Problem = "у UnityEngine.Font в этой версии игры другие методы ("
                              + (create == null ? "конструктор " : "") + (request == null ? "RequestCharactersInTexture " : "")
                              + (info == null ? "GetCharacterInfo " : "") + (advance == null ? "CharacterInfo.advance" : "").TrimEnd() + ")";
                    return;
                }
                normal = Enum.ToObject(style, 0);

                // optional: the font works without them
                try { system = fontType.GetMethod("CreateDynamicFontFromOSFont", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(string), typeof(int) }, null); } catch { }
                try { fontSize = fontType.GetProperty("fontSize", Instance); } catch { }
                try { lineHeight = fontType.GetProperty("lineHeight", Instance); } catch { }
                try { ascent = fontType.GetProperty("ascent", Instance); } catch { }
                try { isDynamic = fontType.GetProperty("dynamic", Instance); } catch { }
                try
                {
                    Type resources = Lookup("UnityEngine.Resources", "UnityEngine.CoreModule");
                    if (resources != null) findAll = resources.GetMethod("FindObjectsOfTypeAll", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(Type) }, null);
                }
                catch { }
                if (objectType != null)
                {
                    try { objectName = objectType.GetProperty("name", Instance); } catch { }
                    try { hideFlags = objectType.GetProperty("hideFlags", Instance); } catch { }
                    try { destroy = objectType.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Static, null, new Type[] { objectType }, null); } catch { }
                    try { exists = objectType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new Type[] { objectType }, null); } catch { }
                    if (exists != null && exists.ReturnType != typeof(bool)) exists = null;
                }
            }

            public object Create(string path)
            {
                return create.Invoke(new object[] { path });
            }

            public bool CanAskSystem { get { return system != null; } }

            /// <summary>Every Font object the game holds right now (its own, the mod's, ours).</summary>
            public List<object> AllFonts()
            {
                if (findAll == null) return null;
                Array all = findAll.Invoke(null, new object[] { fontType }) as Array;
                if (all == null) return null;
                List<object> list = new List<object>();
                foreach (object o in all) if (o != null && Exists(o)) list.Add(o);
                return list;
            }

            /// <summary>"Teko-VariableFont_wght: 90 / 129 / 86; H 38 W 55 → 100 ★ 89"</summary>
            public string Describe(object f)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Read(objectName, f) ?? "?").Append(": ").Append(Read(fontSize, f) ?? "?").Append(" / ").Append(Read(lineHeight, f) ?? "?").Append(" / ").Append(Read(ascent, f) ?? "?");
                object dyn = null;
                try { if (isDynamic != null) dyn = isDynamic.GetValue(f, null); } catch { }
                if (dyn is bool && !(bool)dyn) return sb.Append("; не динамический").ToString();
                try
                {
                    const string probe = "HW\u2192\u2605";
                    Request(f, probe, 100);
                    sb.Append(';');
                    foreach (char c in probe)
                    {
                        int a;
                        sb.Append(' ').Append(c).Append(' ').Append(Advance(f, c, 100, out a) ? a.ToString(CultureInfo.InvariantCulture) : "-");
                    }
                }
                catch { sb.Append("; не измерен"); }
                return sb.ToString();
            }

            private static string Read(PropertyInfo p, object o)
            {
                try
                {
                    if (p == null) return null;
                    object v = p.GetValue(o, null);
                    return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
                }
                catch { return null; }
            }

            /// <summary>A font installed in the system, by its family name. An unknown name gives some other font, not an error.</summary>
            public object CreateSystem(string family)
            {
                return system.Invoke(null, new object[] { family, 32 });
            }

            public void Request(object f, string characters, int size)
            {
                request.Invoke(f, new object[] { characters, size, normal });
            }

            public bool Advance(object f, char c, int size, out int pixels)
            {
                object[] args = new object[] { c, null, size, normal };
                pixels = 0;
                if (!(bool)info.Invoke(f, args) || args[1] == null) return false;
                pixels = (int)advance.GetValue(args[1], null);
                return true;
            }

            /// <summary>Level changes unload what looks unused: the font must outlive them.</summary>
            public void Keep(object f)
            {
                try
                {
                    if (hideFlags != null && hideFlags.CanWrite) hideFlags.SetValue(f, Enum.ToObject(hideFlags.PropertyType, 61), null);   // HideFlags.HideAndDontSave
                }
                catch { }
            }

            public void Destroy(object f)
            {
                try { if (destroy != null) destroy.Invoke(null, new object[] { f }); } catch { }
            }

            public bool Exists(object f)
            {
                if (exists == null) return true;
                return (bool)exists.Invoke(null, new object[] { f });
            }
        }
    }
}
