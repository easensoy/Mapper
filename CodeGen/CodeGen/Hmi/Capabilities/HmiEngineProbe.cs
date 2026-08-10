using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // What the DEPLOYED process engine actually implements.
    //
    // The engine declares Mode, CycleType, MREQ and NSREQ as interface ports, so a name check would
    // wrongly conclude that Auto gating, Stop and Manual stepping are available. The only honest test
    // is whether the ECC ever reads them: a port no transition guards is a port the controller
    // ignores, and a button wired to it would be inert. This is read, not assumed, so the answer
    // tracks whatever engine the template library actually ships.
    internal sealed record HmiEngineSupport(
        bool Found, bool AutoGating, bool ManualStepping, int TransitionCount)
    {
        internal static readonly HmiEngineSupport Absent = new(false, false, false, 0);
    }

    internal static class HmiEngineProbe
    {
        internal static HmiEngineSupport Probe(string eaeProjectDir, HmiEngineProbeSpec spec)
        {
            var path = Locate(eaeProjectDir, spec.TypeName);
            if (path == null) return HmiEngineSupport.Absent;

            XDocument doc;
            try { doc = XDocument.Load(path); }
            catch { return HmiEngineSupport.Absent; }

            var conditions = doc.Descendants()
                .Where(e => e.Name.LocalName == "ECTransition")
                .Select(e => (string?)e.Attribute("Condition") ?? string.Empty)
                .ToList();

            bool Guards(string token) => conditions.Any(c => Mentions(c, token));

            // What counts as "honoured" is declared in hmi.yml, not hardcoded here: a port the ECC
            // never reads is a port the controller ignores, whatever it is called.
            return new HmiEngineSupport(
                Found: true,
                AutoGating: spec.AutoGatingTokens.Count > 0 && spec.AutoGatingTokens.All(Guards),
                ManualStepping: spec.ManualSteppingTokens.Count > 0 && spec.ManualSteppingTokens.All(Guards),
                TransitionCount: conditions.Count);
        }

        private static string? Locate(string eaeProjectDir, string engineType)
        {
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec)) return null;

            var direct = Path.Combine(iec, engineType + ".fbt");
            if (File.Exists(direct)) return direct;

            return Directory.EnumerateFiles(iec, engineType + ".fbt", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        // Whole-identifier match: "Mode" must not be satisfied by "CurrentStepType" or by a longer
        // name that merely contains it.
        private static bool Mentions(string condition, string token)
        {
            for (var i = condition.IndexOf(token, StringComparison.Ordinal); i >= 0;
                 i = condition.IndexOf(token, i + 1, StringComparison.Ordinal))
            {
                var before = i == 0 || !IsWord(condition[i - 1]);
                var after = i + token.Length >= condition.Length || !IsWord(condition[i + token.Length]);
                if (before && after) return true;
            }
            return false;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
