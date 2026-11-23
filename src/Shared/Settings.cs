// BepInEx/config/RepoRPG_RU.cfg - written in the usual BepInEx layout so mod managers can edit it.
// Parsed by hand: neither the patcher (runs before BepInEx config exists for us) nor the runtime is a plugin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RepoRPG_RU
{
    internal sealed class Settings
    {
        public const string FileName = "RepoRPG_RU.cfg";

        public bool Enabled = true;
        public bool TranslateStrings = true;
        public bool TranslateData = true;
        public bool DisplayHooks = true;
        public bool MenuFont = true;
        public bool LogUntranslated = true;
        public bool DumpStrings = false;

        /// <summary>Only the switches that change the generated DLL.</summary>
        public string Signature
        {
            get
            {
                return (Enabled ? "1" : "0") + (TranslateStrings ? "1" : "0") + (TranslateData ? "1" : "0") + (DisplayHooks ? "1" : "0");
            }
        }

        public static Settings Load(string path, bool createIfMissing)
        {
            Settings s = new Settings();
            try
            {
                if (File.Exists(path))
                {
                    int known = 0;
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == '[' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        bool b = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
                        switch (key)
                        {
                            case "Enabled": s.Enabled = b; known |= 1; break;
                            case "TranslateStrings": s.TranslateStrings = b; known |= 2; break;
                            case "TranslateData": s.TranslateData = b; known |= 4; break;
                            case "DisplayHooks": s.DisplayHooks = b; known |= 8; break;
                            case "MenuFont": s.MenuFont = b; known |= 16; break;
                            case "LogUntranslated": s.LogUntranslated = b; known |= 32; break;
                            case "DumpStrings": s.DumpStrings = b; known |= 64; break;
                        }
                    }
                    // a file written by an older version: the new switches appear in it, the old ones keep what the player set
                    if (createIfMissing && known != AllKeys) File.WriteAllText(path, s.Render(), new UTF8Encoding(false));
                }
                else if (createIfMissing)
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, s.Render(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // unreadable config: defaults
            }
            return s;
        }

        private const int AllKeys = 127;

        private string Render()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("## Settings file was created by plugin RepoRPG RU v").Append(BuildInfo.Version).Append('\n');
            sb.Append("## Plugin GUID: ").Append(BuildInfo.Id).Append('\n');
            sb.Append('\n');
            sb.Append("[General]\n\n");
            Item(sb, "Enabled", Enabled, true,
                "Главный выключатель русификатора. false = при следующем запуске переведённая копия RepoRPG.dll удаляется и мод снова английский.",
                "Master switch. false = the translated copy of RepoRPG.dll is removed on the next launch.");
            Item(sb, "TranslateStrings", TranslateStrings, true,
                "Переводить строки интерфейса, зашитые в код мода (кнопки, подсказки, сообщения).",
                "Translate the UI strings compiled into the mod's code.");
            Item(sb, "TranslateData", TranslateData, true,
                "Переводить тексты справочника (Guide) прямо во встроенных данных мода. Остальные данные - классы, перки, предметы - мод хранит и рассылает как есть; они переводятся в момент вывода на экран (DisplayHooks).",
                "Translate the Guide inside the mod's embedded data. Everything else in the data is translated when it is drawn (DisplayHooks).");
            Item(sb, "DisplayHooks", DisplayHooks, true,
                "Переводить в момент вывода на экран то, что нельзя заменить внутри мода: названия и описания из его данных (классы, перки, предметы, контракты), сообщения хоста, слова, которые мод использует и как внутренние имена.",
                "Translate at the moment of drawing whatever cannot be replaced inside the mod: names and descriptions from its data, messages of the host, words the mod also uses as internal ids.");
            Item(sb, "MenuFont", MenuFont, true,
                "Рисовать меню мода шрифтом игры (Teko), в который добавлены русские буквы. Без него русские буквы берутся из системного шрифта - он почти вдвое шире, и надписи не помещаются в кнопки. Работает, пока в настройках самого мода (Controls / UiFont) стоит auto: шрифт, выбранный там, главнее. Нужен DisplayHooks = true. Что получилось - в BepInEx/config/RepoRPG_RU/font.txt.",
                "Draw the mod's menus with the game's font (Teko) extended with Russian letters. Without it they come from a system font that is nearly twice as wide. Applies while the mod's own Controls / UiFont setting is 'auto'. Needs DisplayHooks = true. The outcome is written to BepInEx/config/RepoRPG_RU/font.txt.");
            sb.Append("[Debug]\n\n");
            Item(sb, "LogUntranslated", LogUntranslated, true,
                "Записывать непереведённые строки, которые реально появились на экране, в BepInEx/config/RepoRPG_RU/untranslated.txt (помогает дополнять перевод).",
                "Write untranslated strings that were actually shown to BepInEx/config/RepoRPG_RU/untranslated.txt.");
            Item(sb, "DumpStrings", DumpStrings, false,
                "При следующем запуске выгрузить все строки и встроенные JSON-данные оригинального мода в BepInEx/config/RepoRPG_RU/dump (нужно автору перевода после обновлений мода).",
                "On the next launch dump every string and the embedded JSON of the original mod to BepInEx/config/RepoRPG_RU/dump.");
            return sb.ToString();
        }

        private static void Item(StringBuilder sb, string key, bool value, bool byDefault, string ru, string en)
        {
            sb.Append("## ").Append(ru).Append('\n');
            sb.Append("## ").Append(en).Append('\n');
            sb.Append("# Setting type: Boolean\n");
            sb.Append("# Default value: ").Append(byDefault ? "true" : "false").Append('\n');
            sb.Append(key).Append(" = ").Append(value ? "true" : "false").Append("\n\n");
        }
    }

    internal static class BuildInfo
    {
        public const string Version = "0.2.2";
        public const string Id = "ilyalis.reporpg.ru";
        public const string TargetGuid = "rpgmod.semibotrpg";
        public const string TargetFile = "RepoRPG.dll";
        public const string RuntimeFile = "RepoRPG_RU.dll";
        public const string RuntimeAssembly = "RepoRPG_RU";
    }

    internal static class Hash
    {
        public const ulong Seed = 14695981039346656037UL;

        /// <summary>FNV-1a 64 - change detection only, no crypto dependency.</summary>
        public static ulong Fnv(ulong h, byte[] data)
        {
            unchecked
            {
                for (int i = 0; i < data.Length; i++)
                {
                    h ^= data[i];
                    h *= 1099511628211UL;
                }
                h ^= (ulong)data.Length;
                h *= 1099511628211UL;
            }
            return h;
        }

        public static ulong Fnv(ulong h, string s)
        {
            return Fnv(h, Encoding.UTF8.GetBytes(s ?? ""));
        }
    }
}
