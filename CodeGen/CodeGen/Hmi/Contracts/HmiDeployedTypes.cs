using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // One event on a deployed <CAT>_HMI.fbt, with the data it carries.
    //
    // Direction is from the HMI's point of view and is fixed by the file: an <EventOutputs> event is
    // the HMI reaching the controller, an <EventInputs> event is the controller reporting back. That
    // is the authoritative capability test - a button is not a capability, an output event is.
    internal sealed record HmiContractEvent(string Name, IReadOnlyList<string> With);

    internal sealed record HmiContract(
        string CatType,
        IReadOnlyList<HmiContractEvent> Outputs,   // HMI -> controller
        IReadOnlyList<HmiContractEvent> Inputs,    // controller -> HMI
        IReadOnlyList<string> InputVars,
        IReadOnlyList<string> OutputVars)
    {
        internal static readonly HmiContract None =
            new(string.Empty, Array.Empty<HmiContractEvent>(), Array.Empty<HmiContractEvent>(),
                Array.Empty<string>(), Array.Empty<string>());

        internal bool Exists => Outputs.Count + Inputs.Count + InputVars.Count + OutputVars.Count > 0;

        internal HmiContractEvent? Output(string name) =>
            Outputs.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // A datum the controller pushes to the HMI - i.e. something we can display, and therefore
        // something a command can be gated on.
        internal bool HasFeedback(string var) =>
            InputVars.Any(v => v.Equals(var, StringComparison.OrdinalIgnoreCase)) ||
            Inputs.Any(e => e.With.Any(w => w.Equals(var, StringComparison.OrdinalIgnoreCase)));

        internal bool OutputCarries(string eventName, params string[] data)
        {
            var e = Output(eventName);
            return e != null && data.All(d => e.With.Any(w => w.Equals(d, StringComparison.OrdinalIgnoreCase)));
        }
    }

    // Reads the deployed IEC61499 tree ONCE and answers both questions the capability gates ask of
    // it: what each CAT's HMI companion DECLARES, and what each deployed FB's ECC actually ACTS ON.
    //
    // Deployed, not templated: what the CAT in the generated project declares is what the operator
    // can reach. One walk of the tree answers both, rather than two walks of the same folders over
    // the same files.
    internal sealed record HmiDeployedTypes(
        IReadOnlyDictionary<string, HmiContract> Contracts,
        HmiEccIndex Ecc)
    {
        internal static HmiDeployedTypes Read(string eaeProjectDir)
        {
            var contracts = new Dictionary<string, HmiContract>(StringComparer.OrdinalIgnoreCase);
            var conditions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec)) return new HmiDeployedTypes(contracts, new HmiEccIndex(conditions));

            foreach (var path in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
            {
                XDocument doc;
                try { doc = XDocument.Load(path); }
                catch { continue; }   // a malformed artefact is reported by the artefact validators

                var file = Path.GetFileNameWithoutExtension(path);
                var cat = Path.GetFileName(Path.GetDirectoryName(path));
                // The companion belongs to the CAT folder it sits in; a stray copy elsewhere is not
                // the deployed contract for anything.
                if (cat != null && string.Equals(file, cat + "_HMI", StringComparison.OrdinalIgnoreCase) &&
                    Contract(cat, doc) is { } c)
                    contracts[cat] = c;

                var type = doc.Root?.Attribute("Name")?.Value;
                if (string.IsNullOrWhiteSpace(type)) type = file;

                var guards = doc.Descendants()
                    .Where(e => e.Name.LocalName == "ECTransition")
                    .Select(e => (string?)e.Attribute("Condition") ?? string.Empty)
                    .Where(x => x.Length > 0)
                    .ToList();
                if (guards.Count == 0) continue;

                // A type can legitimately appear twice (a CAT and its companion); union the
                // conditions rather than letting whichever file is enumerated last win.
                conditions[type!] = conditions.TryGetValue(type!, out var existing)
                    ? existing.Concat(guards).Distinct(StringComparer.Ordinal).ToList()
                    : guards;
            }

            return new HmiDeployedTypes(contracts, new HmiEccIndex(conditions));
        }

        private static HmiContract? Contract(string catType, XDocument doc)
        {
            var iface = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "InterfaceList");
            if (iface == null) return null;

            return new HmiContract(
                catType,
                Events(iface, "EventOutputs"),
                Events(iface, "EventInputs"),
                Vars(iface, "InputVars"),
                Vars(iface, "OutputVars"));
        }

        private static IReadOnlyList<HmiContractEvent> Events(XElement iface, string section) =>
            iface.Elements().Where(e => e.Name.LocalName == section)
                .SelectMany(s => s.Elements().Where(e => e.Name.LocalName == "Event"))
                .Select(e => new HmiContractEvent(
                    (string?)e.Attribute("Name") ?? string.Empty,
                    e.Elements().Where(w => w.Name.LocalName == "With")
                        .Select(w => (string?)w.Attribute("Var") ?? string.Empty)
                        .Where(v => v.Length > 0).ToList()))
                .Where(e => e.Name.Length > 0)
                .ToList();

        private static IReadOnlyList<string> Vars(XElement iface, string section) =>
            iface.Elements().Where(e => e.Name.LocalName == section)
                .SelectMany(s => s.Elements().Where(e => e.Name.LocalName == "VarDeclaration"))
                .Select(v => (string?)v.Attribute("Name") ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToList();
    }

    // What the DEPLOYED controller actually acts on.
    //
    // Every CAT declares more interface than its logic uses. Area_CAT declares Mode, CycleType and
    // Fault_Reset; ProcessRuntime declares Mode, MREQ and NSREQ. A contract check alone would
    // therefore conclude that mode selection, STOP and manual stepping are all available, and three
    // of those buttons would be inert.
    //
    // The only honest test is whether some deployed ECC transition GUARDS on the value: a port no
    // transition reads is a port the controller ignores. This index reads every deployed .fbt once
    // and answers that question, so the verdict tracks whatever the template library actually ships
    // rather than what a comment claims.
    internal sealed class HmiEccIndex
    {
        // FB type name -> every ECTransition condition in that type.
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _conditions;

        internal HmiEccIndex(IReadOnlyDictionary<string, IReadOnlyList<string>> conditions) =>
            _conditions = conditions;

        internal bool KnowsType(string type) => _conditions.ContainsKey(type);

        // True when SOME transition guards on the token. `inType` restricts the search to one FB type,
        // which is what distinguishes "the station tracks the cycle" from "the recipe engine stops".
        internal bool Consumes(string token, string? inType)
        {
            if (inType == null)
                return _conditions.Values.Any(cs => cs.Any(c => Guards(c, token)));

            return _conditions.TryGetValue(inType, out var list) && list.Any(c => Guards(c, token));
        }

        // The types whose ECC guards on the token - reported so a generated reason can name them.
        internal IReadOnlyList<string> ConsumersOf(string token) =>
            _conditions.Where(kv => kv.Value.Any(c => Guards(c, token)))
                       .Select(kv => kv.Key).OrderBy(t => t, StringComparer.Ordinal).ToList();

        // Why one PAYLOAD may still be refused although its event is honoured, judged over the
        // transitions that guard on it. Returns null when the proof passes.
        //
        //   DistinctFrom  - every transition accepting this payload also accepts another, so the
        //                   payload selects nothing the plant behaves differently for.
        //   InterlockedBy - some transition accepting it omits the interlock term, so the payload
        //                   can move plant with the interlock bypassed.
        internal string? Refuses(HmiActionProof proof)
        {
            var guarded = _conditions.Values.SelectMany(cs => cs).Select(Normalise)
                .Where(c => Mentions(c, proof.Guard)).Distinct(StringComparer.Ordinal).ToList();

            if (guarded.Count == 0)
                return $"no deployed ECC transition is guarded on '{proof.Guard}'";

            if (proof.DistinctFrom is { } other && guarded.All(c => Mentions(c, other)))
                return $"every transition guarded on '{proof.Guard}' also accepts '{other}', " +
                       "so selecting it changes nothing the controller does";

            if (proof.InterlockedBy is { } term && guarded.Any(c => !Mentions(c, term)))
                return $"a transition guarded on '{proof.Guard}' carries no '{term}' term, " +
                       "so the command could move plant with the interlock bypassed";

            return null;
        }

        // ECC conditions carry encoded newlines; compare them as one flat line.
        private static string Normalise(string condition) =>
            System.Text.RegularExpressions.Regex.Replace(condition, @"\s+", " ").Trim();

        // A phrase match with word boundaries, so 'mode = 1' is never satisfied by 'mode = 10'.
        private static bool Mentions(string condition, string phrase)
        {
            for (var i = condition.IndexOf(phrase, StringComparison.Ordinal); i >= 0;
                 i = condition.IndexOf(phrase, i + 1, StringComparison.Ordinal))
            {
                var before = i == 0 || !IsWord(condition[i - 1]);
                var after = i + phrase.Length >= condition.Length || !IsWord(condition[i + phrase.Length]);
                if (before && after) return true;
            }
            return false;
        }

        // Whole-identifier match. 'Mode' must not be satisfied by 'CurrentStepType', and 'toWork'
        // must not be satisfied by 'toWork2' - a substring hit would report a capability the
        // controller does not have.
        private static bool Guards(string condition, string token)
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
