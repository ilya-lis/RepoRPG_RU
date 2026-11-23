// A miniature stand-in for RepoRPG.dll that reproduces the string patterns of the real mod:
// tab names that are also ids, literals in concat / format / ternary / switch / arrays / locals,
// config binding, logging, compiler generated closures and iterators, embedded JSON.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace RepoRPG
{
    [BepInPlugin("rpgmod.semibotrpg", "RepoRPG", "1.0.100")]
    [BepInDependency("nickklmao.menulib")]
    public class Plugin
    {
        public const string Version = "1.0.100";
        public static ManualLogSource Log = Logger.CreateLogSource("RepoRPG");
        public static ConfigFile Cfg = new ConfigFile();
        public static ModConfig Settings { get { return settings; } }
        private static readonly ModConfig settings = new ModConfig();

        // what the game would call
        public static List<string> Run(bool rich)
        {
            RpgRuntime r = new RpgRuntime();
            r.Setup();
            r.OnGUI(rich);
            r.Out.Add("handshake=" + Net.Hello());
            r.Out.AddRange(Cfg.Seen.Select(s => "cfg:" + s));
            r.Out.Add("data:" + ClassDatabase.ReadEmbedded("classes"));
            r.Out.Add("data:" + ClassDatabase.ReadEmbedded("perks"));
            return r.Out;
        }
    }

    public sealed class ModConfig
    {
        public ConfigEntry<string> UiFont;
    }

    public sealed class Content
    {
        private string _text = "";
        public string text { get { return _text; } set { _text = value; } }
    }

    /// <summary>A second place that picks a font, and not the way the add-on's rules were written for.</summary>
    public sealed class Hud
    {
        public UnityEngine.Font LoadFont()
        {
            if (DateTime.Now.Year < 2000) return null;
            return UnityEngine.Font.CreateDynamicFontFromOSFont("Impact", 32);
        }
    }

    public static class Net
    {
        public static string Hello() { return "RPG/" + Plugin.Version + "/proto4"; }
        public static void SendNotice(string target, int kind, float value, string text) { Sent.Add(text); }
        public static readonly List<string> Sent = new List<string>();
    }

    public static class Game
    {
        private static readonly Dictionary<string, float> stats = new Dictionary<string, float> { { "Gear", 2f }, { "Class", 1f } };
        public static float Get(string key) { float v; return stats.TryGetValue(key, out v) ? v : -1f; }
    }

    public static class ClassDatabase
    {
        public static string ReadEmbedded(string stem)
        {
            using (Stream s = typeof(ClassDatabase).Assembly.GetManifestResourceStream("RepoRPG." + stem + ".json"))
            {
                if (s == null) return "<none>";
                using (StreamReader r = new StreamReader(s)) return r.ReadToEnd();
            }
        }
    }

    public class RpgRuntime
    {
        public readonly List<string> Out = new List<string>();
        private static readonly string[] TabsBase = new string[4] { "Class", "Gear", "Shop", "Settings" };
        private static readonly string[] TabNames = TabsBase;
        private string _tab = "Class";
        private readonly Dictionary<string, int> _badge = new Dictionary<string, int>();
        private int _gold = 3;

        public void Setup()
        {
            Plugin.Settings.UiFont = Plugin.Cfg.Bind("General", "UiFont", "auto", "Font used by the menu. 'auto' picks the game's font.");
            string asked = Environment.GetEnvironmentVariable("FAKE_UIFONT");          // what the player typed into the mod's settings
            if (asked != null) Plugin.Settings.UiFont.Value = asked;
            Plugin.Cfg.Bind("HUD", "Scale", 1f, new ConfigDescription("Scale of the HUD.", null, "Advanced"));
            Plugin.Cfg.Bind("HUD", "Anchor", "Left", new ConfigDescription("Where the HUD sits.", null));
            _badge["Shop"] = 2;
        }

        // ---- display helpers (hook targets)
        private void Label(string text) { Out.Add("Label:" + text); }
        private bool Btn(string label) { Out.Add("Btn:" + label); return label.Length < 0; }
        private void Toast(string text) { Out.Add("Toast:" + text); }
        private float TextW(string text) { return text.Length * 7f; }
        private void Notify(string key, string text) { Out.Add("Notify[" + key + "]:" + text); }
        private bool AskConfirm(string title, string body, string okLabel) { Out.Add("Ask:" + title + "/" + body + "/" + okLabel); return true; }
        private static string Spaced(string key)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in key) { if (char.IsUpper(c) && sb.Length > 0) sb.Append(' '); sb.Append(c); }
            return sb.ToString();
        }
        private void DebugLog(string s) { Out.Add("log:" + s); }

        public void OnGUI(bool rich)
        {
            // 1. tabs: the same literals are ids (compare, dictionary key, array) and captions
            for (int i = 0; i < TabNames.Length; i++)
            {
                string name = TabNames[i];
                int badge;
                _badge.TryGetValue(name, out badge);
                if (Btn(name)) _tab = name;
                if (name == "Shop" && badge > 0) Label(badge.ToString());
            }
            Out.Add("tab-is-class=" + (_tab == "Class"));
            Out.Add("stat=" + Game.Get("Gear"));

            // 2. plain sentences
            Label("Choose your class");
            Label("Class");
            Label(rich ? "<b>Not enough gold</b>" : "Not enough gold");
            Toast(_gold > 5 ? "Purchased" : "Not enough gold");

            // 3. concatenation and formatting
            Label("Level " + 5);
            Label(string.Format("Level {0} - {1:0.#} XP", 3, 40.5));
            Label($"Gold: {_gold}");
            Toast("Gear: " + Spaced("IronPlate") + " equipped");
            Label(string.Concat("Unlocks at level ", 7.ToString(), "."));

            // 4. local variable that is both shown and compared
            string mode = "Gear";
            Label(mode);
            if (mode == "Gear") Out.Add("mode-ok");

            // 5. switch over strings
            foreach (string rarity in new[] { "Rare", "Epic" })
            {
                switch (rarity)
                {
                    case "Rare": Label("Rare item"); break;
                    case "Epic": Label("Epic item"); break;
                }
            }

            // 6. logs and network must stay as they are
            Plugin.Log.LogInfo("Not enough gold");
            DebugLog("Purchased");
            Net.SendNotice("p", 1, 0f, "Not enough gold");
            Notify("shop.fail", "Not enough gold");

            // 7. confirm dialog, closure, iterator
            AskConfirm("Reset class?", "This refunds all points.", "Reset");
            Action later = () => Toast("Class reset");
            later();
            foreach (object o in Lines()) Label((string)o);

            // 8. exceptions, parsing
            try { throw new InvalidOperationException("Not enough gold"); }
            catch (Exception e) { Out.Add("ex:" + e.Message); }
            Out.Add("enum=" + Enum.Parse(typeof(DayOfWeek), "Monday"));
            Out.Add("w=" + TextW("Settings"));
            Out.Add("sent=" + string.Join(",", Net.Sent.ToArray()));

            // 9. multi-word names kept in arrays: one of them is also searched for by its literal
            foreach (string s in Sections) Label(s);
            Out.Add("idx=" + Array.IndexOf(Sections, "Team Vote"));
            foreach (string h in Hints) Label(h);

            // 10. a word that is an id in one place and part of a composed caption in another
            string state = _gold > 0 ? "On" : "Off";
            if (state == "On") Out.Add("state-on");
            Label("Mode: " + (_gold > 0 ? "On" : "Off"));

            // 11. single word coming out of a method, and a line nobody translated
            Label(Header());
            Label("Untranslated line");
            // a counter: one text for the log of untranslated texts, whatever the numbers are
            for (int stage = 3; stage <= 5; stage++) Label("Stage " + stage + " of 7");

            // 12. texts the mod recognises by their wording (they come from somewhere else, as in the real mod)
            string why = Text(0);
            if (why.StartsWith("Load too big")) Out.Add("probe-prefix-ok");
            Label(why);
            string res = Text(1);
            if (res.Contains(" x")) Out.Add("probe-inside-ok");
            Label(res);
            if (!Text(6).Contains("fix")) Out.Add("never");
            Label(Text(2));
            string reason = Text(3);
            if (reason.IndexOf("Gift", StringComparison.Ordinal) >= 0) Out.Add("probe-word-ok");
            Label(reason);
            if (Text(7).Contains("late join")) Out.Add("plugin-seen");
            Label(Text(4));
            string summary = Text(5);
            int cut = summary.IndexOf(". ", StringComparison.Ordinal);
            Label(cut > 0 ? summary.Substring(0, cut) : summary);

            // 13. texts put together by the mod and finished elsewhere
            Label(Legend().ToUpperInvariant());
            Label("<b>" + Legend() + "</b>");
            Net.SendNotice("p", 2, 0f, string.Format("Provoked: {0} vs {1}", "Rex", "Bob"));
            Out.Add("sent2=" + Net.Sent[Net.Sent.Count - 1]);
            Toast(Net.Sent[Net.Sent.Count - 1]);                           // what the receiving player gets
            Toast(string.Join(" ", new string[] { "Taken", "by", "Alice" }));
            Toast(string.Join(" ", new string[] { "Equipped:", "Iron", "Plate" }));

            Measured();
            Sentences("Rune", "Blade", 3);
            Stats();

            // 14. the mod's own {named} placeholders
            Label("Kill {t} monster{s}".Replace("{t}", "3").Replace("{s}", "s"));
            Label("Carry {t} items".Replace("{t}", "2"));

            // 19. the font of the menus, picked the way the real mod picks it - and once more, as after a change of the UI style
            _font = LoadFont();
            Out.Add("font=" + _font.name + " " + Wide(_font));
            UnityEngine.Font again = LoadFont();
            Out.Add("font-again=" + ((UnityEngine.Object)again == (UnityEngine.Object)_font));
            // a level change unloads what nothing holds on to
            UnityEngine.Object.UnloadUnused(new UnityEngine.Object[] { _font });
            Out.Add("font-after-level=" + ((UnityEngine.Object)_font != (UnityEngine.Object)null));
            Out.Add("font-hud=" + new Hud().LoadFont().name);
            Out.Add("destroyed=" + string.Join(",", UnityEngine.Object.Destroyed.ToArray()));
        }

        private UnityEngine.Font _font;

        private UnityEngine.Font LoadFont()
        {
            string text = Plugin.Settings.UiFont.Value.Trim();
            if (!string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
            {
                try
                {
                    return UnityEngine.Font.CreateDynamicFontFromOSFont(text, 32);
                }
                catch
                {
                }
            }
            return UnityEngine.Font.CreateDynamicFontFromOSFont(new string[2] { "Bahnschrift", "Arial" }, 32);      // "the game's font"
        }

        /// <summary>Width of a Russian caption in pixels at size 20.</summary>
        private static int Wide(UnityEngine.Font f)
        {
            const string caption = "НАСТРОЙКИ";
            f.RequestCharactersInTexture(caption, 20, UnityEngine.FontStyle.Normal);
            float w = 0f;
            foreach (char c in caption)
            {
                UnityEngine.CharacterInfo ci;
                if (f.GetCharacterInfo(c, out ci, 20, UnityEngine.FontStyle.Normal)) w += ci.width;
            }
            return (int)Math.Round(w);
        }

        private string Legend() { return _gold > 0 ? "Legend" : "?"; }

        // 15. measured through a foreign setter, drawn later: translated at the call site
        private void Measured()
        {
            Content c = new Content();
            c.text = _gold > 0 ? Word() : "x";                              // both branches join at the setter call
            Out.Add("measure=" + c.text);
            try { c.text = _gold > 1000 ? "x" : Word(); }
            finally { Out.Add("measure2=" + c.text); }
        }

        private string Word() { return _gold >= 0 ? "Wide" : "Narrow"; }

        // 16. sentences glued together piece by piece: replaced whole or not at all
        private void Sentences(string a, string b, int n)
        {
            string one = "Slotted " + a + " into " + b;
            Out.Add("glue1=" + one);
            string two = "Load too big " + n + " carts wide";              // the first piece is recognised by its wording (case 12)
            Out.Add("glue2=" + two);
            Label(two);
        }

        // 17. stat lines the mod takes apart itself, and string tests that say how they compare
        private static string Describe(int stat, float v)
        {
            string n = "+" + v.ToString("0");
            switch (stat)
            {
                case 0: return n + "% max health";
                case 1: return n + "s Mark duration";
                default: return n + "s stun";
            }
        }

        private void Stats()
        {
            string line = Describe(1, 2f);
            int sp = line.IndexOf(' ');
            Out.Add("stat1=" + line.Substring(0, sp) + "|" + line.Substring(sp + 1));     // value | name, as SplitStat() does
            string label = Text(8);
            Out.Add("unit=" + (label.StartsWith("s ") ? label : " " + label));            // the test the stat lines never reach
            Out.Add("stat2=" + Describe(2, 3f));
            Label(Describe(2, 3f));
            string key = Text(9);
            if (key.StartsWith("gear", StringComparison.Ordinal)) Out.Add("probe-case-ok");
            if (key.EndsWith("R3", StringComparison.OrdinalIgnoreCase)) Out.Add("probe-nocase-ok");
            Out.Add("cap=" + Caption(4) + "/" + Caption(-4));
            Label(Caption(-4));
            Label(Describe(0, 5f));

            // 18. host-side logic: what it writes travels to the other players, so it stays as written - and is translated when drawn
            string refusal = HostRefuse(3);
            Out.Add("state=" + refusal);
            Label(refusal);
        }

        private string HostRefuse(int open)
        {
            Func<string> why = () => open > 2 ? "Too many trades are open" : "No answer in time";
            return why();
        }

        private string Caption(int n) { return n > 0 ? "Gear: " + n : "Layer3"; }

        private string Text(int i)
        {
            switch (i)
            {
                case 8: return "s stun";
                case 9: return "gear3";
                case 0: return "Load too big for this door";
                case 1: return "Bonus x2";
                case 2: return "A hidden affix appears.";
                case 3: return "Gift from a friend";
                case 4: return "Waiting for a late join";
                case 5: return "HUD: shows the level. Hidden in menus.";
                case 6: return "cartfix";
                default: return "some late join mod";
            }
        }

        private static readonly string[] Sections = new string[2] { "Quick Slots", "Team Vote" };
        private static readonly string[] Hints = new string[2] { "Hold to sprint", "Press to dash" };
        private string Header() { return "Overview"; }

        private IEnumerable Lines()
        {
            yield return "Choose your class";
            yield return "Purchased";
        }
    }
}
