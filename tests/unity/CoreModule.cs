// Test double of UnityEngine.CoreModule: the few members of UnityEngine.Object the add-on's font code reaches by reflection,
// declared the way Unity 6 declares them (UnityCsReference, branch 6000.3). Never shipped.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    [Flags]
    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = 52,
        HideAndDontSave = 61
    }

    public static class Resources
    {
        internal static readonly List<Object> All = new List<Object>();

        public static Object[] FindObjectsOfTypeAll(Type type)
        {
            List<Object> found = new List<Object>();
            foreach (Object o in All) if (type.IsInstanceOfType(o) && o) found.Add(o);
            return found.ToArray();
        }
    }

    public class Object
    {
        private bool destroyed;
        private string objectName = "";

        public Object() { Resources.All.Add(this); }

        public string name { get { return objectName; } set { objectName = value; } }
        public HideFlags hideFlags { get; set; }

        public static void Destroy(Object obj) { if (!ReferenceEquals(obj, null)) { obj.destroyed = true; Destroyed.Add(obj.name); } }

        /// <summary>Not in Unity: for a double of an object the engine lets go by itself (an unloaded asset bundle).</summary>
        protected void Gone() { destroyed = true; }
        public static void Destroy(Object obj, float t) { Destroy(obj); }

        /// <summary>What a level change does: everything not marked to be kept goes away.</summary>
        public static void UnloadUnused(IEnumerable<Object> all)
        {
            foreach (Object o in all)
                if (!ReferenceEquals(o, null) && (o.hideFlags & HideFlags.DontUnloadUnusedAsset) == 0) o.destroyed = true;
        }

        public static readonly List<string> Destroyed = new List<string>();

        public static implicit operator bool(Object exists) { return !ReferenceEquals(exists, null) && !exists.destroyed; }
        public static bool operator ==(Object x, Object y)
        {
            bool nx = ReferenceEquals(x, null) || x.destroyed, ny = ReferenceEquals(y, null) || y.destroyed;
            if (nx || ny) return nx && ny;
            return ReferenceEquals(x, y);
        }
        public static bool operator !=(Object x, Object y) { return !(x == y); }
        public override bool Equals(object other) { return ReferenceEquals(this, other); }
        public override int GetHashCode() { return base.GetHashCode(); }
    }
}
