// Extra BepInEx look-alikes needed only by the fake RepoRPG used in tests (never shipped).
using System;
using System.Collections.Generic;

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public class BepInPlugin : Attribute
    {
        public string GUID, Name, Version;
        public BepInPlugin(string GUID, string Name, string Version) { this.GUID = GUID; this.Name = Name; this.Version = Version; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInDependency : Attribute
    {
        public string DependencyGUID;
        public BepInDependency(string DependencyGUID) { this.DependencyGUID = DependencyGUID; }
    }
}

namespace BepInEx.Configuration
{
    public class ConfigDescription
    {
        public string Description;
        public object[] Tags;
        public ConfigDescription(string description, object acceptableValues, params object[] tags) { Description = description; Tags = tags; }
    }

    public class ConfigEntry<T>
    {
        public string Section, Key, Description;
        public T Value { get; set; }                 // a property in BepInEx too: the mod's code calls get_Value
    }

    public class ConfigFile
    {
        public readonly List<string> Seen = new List<string>();
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            Seen.Add(section + "|" + key + "|" + defaultValue + "|" + description);
            return new ConfigEntry<T> { Section = section, Key = key, Value = defaultValue, Description = description };
        }
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription description)
        {
            Seen.Add(section + "|" + key + "|" + defaultValue + "|" + description.Description);
            return new ConfigEntry<T> { Section = section, Key = key, Value = defaultValue, Description = description.Description };
        }
    }
}
