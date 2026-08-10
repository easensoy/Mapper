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

        internal HmiContractEvent? Input(string name) =>
            Inputs.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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

    // Reads the deployed contracts. Deployed, not templated: what the CAT in the generated project
    // actually declares is what the operator can reach.
    internal static class HmiContractReader
    {
        internal static IReadOnlyDictionary<string, HmiContract> ReadAll(string eaeProjectDir)
        {
            var map = new Dictionary<string, HmiContract>(StringComparer.OrdinalIgnoreCase);
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec)) return map;

            foreach (var dir in Directory.EnumerateDirectories(iec))
            {
                var catType = Path.GetFileName(dir);
                var fbt = Path.Combine(dir, catType + "_HMI.fbt");
                if (!File.Exists(fbt)) continue;
                var c = Read(catType, fbt);
                if (c != null) map[catType] = c;
            }
            return map;
        }

        internal static HmiContract? Read(string catType, string fbtPath)
        {
            XDocument doc;
            try { doc = XDocument.Load(fbtPath); }
            catch { return null; }

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
}
