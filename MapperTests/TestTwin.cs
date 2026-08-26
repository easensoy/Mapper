using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace MapperTests
{
    /// The authored VueOne models, as the compiler is willing to accept them.
    ///
    /// Two of the shipped twins (_se and _vc) interlock Shaft_Hr/TurningWork on Bearing_PnP at two
    /// different stops inside ONE ConditionGroup. VueOne writes a group as a conjunction, so that rule
    /// can never fire, and the compiler REFUSES it rather than reinterpreting the AND — see
    /// UnsatisfiableInterlockException.
    ///
    /// A test that wants to exercise the rest of the compiler against those plants needs a model that
    /// compiles. So this applies the correction the refusal itself asks the model owner to make — each
    /// clashing condition in its OWN ConditionGroup, which VueOne reads as alternatives — to a COPY in
    /// the temp directory. The authored file is never written to, and the compiler still refuses the
    /// model as authored: that refusal is proved by the Gate's negative fixtures and by
    /// InterlockRefusalTests.
    internal static class TestTwin
    {
        static readonly Dictionary<string, string> Corrected = new(StringComparer.Ordinal);
        static readonly object Gate = new();

        public static string AuthoredPath(string suffix) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Documents", "VueOne", "system",
            "SMC_Vue2VC_With_Processes" + suffix, "Control.xml");

        /// The authored model if it already compiles, or a corrected copy if it does not.
        public static string CompilablePath(string suffix)
        {
            var authored = AuthoredPath(suffix);
            if (!File.Exists(authored))
                throw new FileNotFoundException($"VueOne source model '{suffix}' not found at {authored}.", authored);

            return Compile(authored, "authored" + suffix);
        }


        /// The same, for the twins checked into Gate/fixtures/models.
        public static string CompilableFixturePath(string suffix)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Gate", "fixtures", "models")))
                dir = dir.Parent;
            if (dir == null) throw new DirectoryNotFoundException("Gate/fixtures/models not found from " + AppContext.BaseDirectory);
            var authored = Path.Combine(dir.FullName, "Gate", "fixtures", "models",
                "SMC_Vue2VC_With_Processes" + suffix, "Control.xml");
            return Compile(authored, "fixture" + suffix);
        }

        static string Compile(string authored, string key)
        {
            if (!File.Exists(authored)) throw new FileNotFoundException("twin not found: " + authored, authored);
            lock (Gate)
            {
                if (Corrected.TryGetValue(key, out var cached) && File.Exists(cached)) return cached;
                var doc = XDocument.Load(authored, LoadOptions.PreserveWhitespace);
                if (!SplitClashingGroups(doc)) { Corrected[key] = authored; return authored; }
                var dir = Path.Combine(Path.GetTempPath(), "vueone_corrected_twins");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, key + ".Control.xml");
                doc.Save(path);
                Corrected[key] = path;
                return path;
            }
        }

        /// THE CORRECTION, exactly as the refusal states it: where one ConditionGroup names a component
        /// at more than one stop, every condition after the first for that component moves into its own
        /// new group. Separate groups are alternatives, so the guard then means "block while the source
        /// is at either stop" — which is what a rule naming two ends of one axis is written to mean.
        /// Returns false when the model needed no correction.
        static bool SplitClashingGroups(XDocument doc)
        {
            var changed = false;
            foreach (var value in doc.Descendants("Interlock_Condition")
                                     .SelectMany(i => i.Elements("ConditionValue")).ToList())
                foreach (var group in value.Elements("ConditionGroup").ToList())
                {
                    var byComponent = group.Elements("Condition")
                        .GroupBy(c => (string?)c.Attribute("ComponentID") ?? string.Empty)
                        .Where(g => g.Count() > 1)
                        .ToList();

                    foreach (var clash in byComponent)
                        foreach (var extra in clash.Skip(1).ToList())
                        {
                            extra.Remove();
                            var fresh = new XElement("ConditionGroup",
                                new XAttribute("Operator", (string?)group.Attribute("Operator") ?? string.Empty),
                                new XAttribute("GroupName", "Group_split_" + Guid.NewGuid().ToString("N")[..6]),
                                extra);
                            group.AddAfterSelf(fresh);
                            changed = true;
                        }
                }
            return changed;
        }
    }
}
