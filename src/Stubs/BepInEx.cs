// Reference-only stand-in for the three BepInEx 5 types the patcher touches.
// It is compiled into build/ref/BepInEx.dll, used as a compile-time reference and as a test double;
// it is never shipped - in the game the real BepInEx.dll is what gets bound.
using System;
using System.Reflection;

[assembly: AssemblyVersion("5.4.23.5")]

namespace BepInEx
{
    public static class Paths
    {
        public static string BepInExRootPath { get; set; }
        public static string PluginPath { get; set; }
        public static string PatcherPluginPath { get; set; }
        public static string ConfigPath { get; set; }
        public static string CachePath { get; set; }
    }
}

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public static Action<string> Sink;
        private readonly string name;
        public ManualLogSource(string sourceName) { name = sourceName; }
        public string SourceName { get { return name; } }
        private void Write(string level, object data)
        {
            string line = "[" + level + ":" + name + "] " + data;
            if (Sink != null) Sink(line); else Console.WriteLine(line);
        }
        public void LogInfo(object data) { Write("Info   ", data); }
        public void LogMessage(object data) { Write("Message", data); }
        public void LogWarning(object data) { Write("Warning", data); }
        public void LogError(object data) { Write("Error  ", data); }
        public void LogDebug(object data) { Write("Debug  ", data); }
    }

    public static class Logger
    {
        public static ManualLogSource CreateLogSource(string sourceName) { return new ManualLogSource(sourceName); }
    }
}
