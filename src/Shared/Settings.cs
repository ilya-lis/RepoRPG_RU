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
                            case "Enabled": s.Enabled = b; break;
                            case "TranslateStrings": s.TranslateStrings = b; break;
                            case "TranslateData": s.TranslateData = b; break;
                            case "DisplayHooks": s.DisplayHooks = b; break;
                            case "LogUntranslated": s.LogUntranslated = b; break;
                            case "DumpStrings": s.DumpStrings = b; break;
                        }
                    }
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

        private string Render()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("## Settings file was created by plugin RepoRPG RU v").Append(BuildInfo.Version).Append('\n');
            sb.Append("## Plugin GUID: ").Append(BuildInfo.Id).Append('\n');
            sb.Append('\n');
            sb.Append("[General]\n\n");
            Item(sb, "Enabled", Enabled,
                "Главный выключатель русификатора. false = при следующем запуске переведённая копия RepoRPG.dll удаляется и мод снова английский.",
                "Master switch. false = the translated copy of RepoRPG.dll is removed on the next launch.");
            Item(sb, "TranslateStrings", TranslateStrings,
                "Переводить строки интерфейса, зашитые в код мода (кнопки, подсказки, сообщения).",
                "Translate the UI strings compiled into the mod's code.");
            Item(sb, "TranslateData", TranslateData,
                "Переводить встроенные данные мода (названия и описания классов, перков, предметов).",
                "Translate the mod's embedded data (names and descriptions of classes, perks, items).");
            Item(sb, "DisplayHooks", DisplayHooks,
                "Переводить слова, которые мод одновременно использует как внутренние идентификаторы (названия вкладок, имена настроек), прямо в момент вывода на экран.",
                "Translate words the mod also uses as internal ids (tab names, setting names) at the moment they are drawn.");
            sb.Append("[Debug]\n\n");
            Item(sb, "LogUntranslated", LogUntranslated,
                "Записывать непереведённые строки, которые реально появились на экране, в BepInEx/config/RepoRPG_RU/untranslated.txt (помогает дополнять перевод).",
                "Write untranslated strings that were actually shown to BepInEx/config/RepoRPG_RU/untranslated.txt.");
            Item(sb, "DumpStrings", DumpStrings,
                "При следующем запуске выгрузить все строки и встроенные JSON-данные оригинального мода в BepInEx/config/RepoRPG_RU/dump (нужно автору перевода после обновлений мода).",
                "On the next launch dump every string and the embedded JSON of the original mod to BepInEx/config/RepoRPG_RU/dump.");
            return sb.ToString();
        }

        private static void Item(StringBuilder sb, string key, bool value, string ru, string en)
        {
            sb.Append("## ").Append(ru).Append('\n');
            sb.Append("## ").Append(en).Append('\n');
            sb.Append("# Setting type: Boolean\n");
            sb.Append("# Default value: ").Append(value ? "true" : "false").Append('\n');
            sb.Append(key).Append(" = ").Append(value ? "true" : "false").Append("\n\n");
        }
    }

    internal static class BuildInfo
    {
        public const string Version = "0.1.1";
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
