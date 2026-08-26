using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Domain.Twin;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;

namespace CodeGen.Translation
{
    /// WHY a model cannot be compiled, told apart from WHETHER it can.
    ///
    /// The compiler already refuses what it cannot represent, one refusal at a time, at the point the
    /// question is asked. That is right for correctness and poor for an engineer: the first refusal
    /// stops the run, so a model with three separate problems takes three attempts to learn about, and
    /// each message says what failed without saying what KIND of thing failed. "No CAT serves a 9-state
    /// graph" and "device.yml declares a target no backend implements" both read as errors, but one is
    /// a model to change and the other is a compiler to extend.
    ///
    /// So this asks every question that can be answered before a plan exists, classifies each answer,
    /// and reports all of them together. It decides NOTHING of its own: each answer comes from the same
    /// owner that would refuse later, so the report and the run cannot disagree.
    public enum CapabilityKind
    {
        /// The compiler can render this today.
        Supported,

        /// The MODEL says something the target runtime cannot express. Changing the compiler will not
        /// help; the twin has to say something the deployed FBs can carry.
        UnsupportedModelSemantics,

        /// The model is fine and the compiler could render it, but a declaration it needs is absent.
        /// A YAML edit, not a code change.
        MissingProfileDeclaration,

        /// Everything is declared and the model is fine, but nothing implements the target. A backend
        /// has to be written.
        BackendUnavailable,
    }

    public sealed record CapabilityFinding(CapabilityKind Kind, string Subject, string Detail)
    {
        public override string ToString() => $"[{Kind}] {Subject}: {Detail}";
    }

    public sealed class CompilerCapabilityReport
    {
        public IReadOnlyList<CapabilityFinding> Findings { get; }

        private CompilerCapabilityReport(IReadOnlyList<CapabilityFinding> findings) => Findings = findings;

        public IEnumerable<CapabilityFinding> Blocking =>
            Findings.Where(f => f.Kind != CapabilityKind.Supported);

        public bool CanCompile => !Blocking.Any();

        /// Stops the run listing EVERY blocking finding, grouped by what would fix it. The individual
        /// refusals downstream still stand and still fire; this exists so an engineer sees all of them
        /// at once instead of one per attempt.
        public void AssertCompilable()
        {
            if (CanCompile) return;
            throw new InvalidOperationException(
                "This model cannot be compiled as declared. Generation ABORTED before anything was written." +
                Environment.NewLine +
                string.Join(Environment.NewLine, Blocking
                    .GroupBy(f => f.Kind)
                    .OrderBy(g => g.Key)
                    .Select(g => $"  {Explain(g.Key)}" + Environment.NewLine +
                                 "    - " + string.Join(Environment.NewLine + "    - ",
                                     g.Select(f => f.Subject + ": " + f.Detail)))));
        }

        static string Explain(CapabilityKind kind) => kind switch
        {
            CapabilityKind.UnsupportedModelSemantics =>
                "THE MODEL says something the deployed runtime cannot carry — change the twin:",
            CapabilityKind.MissingProfileDeclaration =>
                "A DECLARATION IS MISSING — add it to the profile (templates.yml / smc-rig.yml / device.yml):",
            CapabilityKind.BackendUnavailable =>
                "NO BACKEND IMPLEMENTS THIS TARGET — a device emitter has to be written:",
            _ => "Blocking:",
        };

        /// One line per kind, for the generation log. A run that can compile says so in one line rather
        /// than listing every component it is happy with.
        public IEnumerable<string> Summary()
        {
            foreach (var g in Findings.GroupBy(f => f.Kind).OrderBy(g => g.Key))
                yield return g.Key == CapabilityKind.Supported
                    ? $"[Capability] {g.Count()} supported"
                    : $"[Capability] {g.Count()} {g.Key}:" + Environment.NewLine +
                      "  - " + string.Join(Environment.NewLine + "  - ", g.Select(f => f.Subject + ": " + f.Detail));
        }

        /// Everything answerable before a plan exists: which CAT serves each actuator, whether each
        /// process has a control flow the engine can run, and whether every target the roster can place
        /// work on is both declared and implemented.
        public static CompilerCapabilityReport For(
            TwinModel twin, CompilerConfiguration cfg, IEnumerable<PlcAssignment> implemented)
        {
            var findings = new List<CapabilityFinding>();
            var backends = implemented.ToHashSet();

            foreach (var c in twin.Components.Select(t => t.Source))
            {
                if (ComponentType.IsProcess(c)) continue;
                if (!ComponentType.IsActuator(c) && !ComponentType.Is(c, ComponentType.Robot)) continue;
                findings.Add(ActuatorCapability(c, cfg.Manifest));
            }

            foreach (var p in twin.Processes.Select(t => t.Source))
                findings.Add(ProcessCapability(p));

            foreach (var t in cfg.Targets.All)
                findings.Add(backends.Contains(t.Plc)
                    ? new CapabilityFinding(CapabilityKind.Supported, t.Plc.ToString(),
                        $"declared and implemented ({t.DeviceType} on {t.ResourceName})")
                    : new CapabilityFinding(CapabilityKind.BackendUnavailable, t.Plc.ToString(),
                        "device.yml declares this target but no backend renders an EAE device for it"));

            return new CompilerCapabilityReport(findings);
        }

        // WHICH CAT, asked of the manifest that will answer it again during planning — and asked in its
        // TWO parts, because they fail for different reasons and send the engineer to different files.
        //
        // A component filling a declared semantic ROLE (the task arm) resolves through infraRoles: no
        // template serving that role is a DECLARATION that has not been written. Everything else
        // resolves through its state GRAPH: no protocol serving that shape is the MODEL saying
        // something no deployed type can carry. Collapsing the two - which reading one exception would
        // do - files half of them under the wrong fix.
        static CapabilityFinding ActuatorCapability(VueOneComponent actuator, TemplateIndex manifest)
        {
            var name = (actuator.Name ?? string.Empty).Trim();
            string cat;
            if (manifest.IsRobotTaskArm(actuator))
            {
                try { cat = manifest.ForInfraRole("taskArm").Name; }
                catch (InvalidOperationException ex)
                {
                    return new CapabilityFinding(CapabilityKind.MissingProfileDeclaration, name,
                        "fills the declared 'taskArm' role, but " + ex.Message);
                }
            }
            else
            {
                try
                {
                    cat = manifest.ResolveActuatorCatType(
                        name, actuator.States?.Count ?? 0, TemplateMap.IsBranchedSevenState(actuator));
                }
                catch (InvalidOperationException ex)
                {
                    return new CapabilityFinding(CapabilityKind.UnsupportedModelSemantics, name, ex.Message);
                }
            }

            return new CapabilityFinding(CapabilityKind.Supported, name, $"renders as {cat}");
        }

        // WHETHER THE ENGINE CAN RUN IT. ProcessGraph is the owner of that question and refuses by name;
        // asking it here reports the same refusal without stopping at the first one.
        static CapabilityFinding ProcessCapability(VueOneComponent process)
        {
            var name = (process.Name ?? string.Empty).Trim();
            try
            {
                var graph = ProcessGraph.Build(process);
                return new CapabilityFinding(CapabilityKind.Supported, name,
                    $"{graph.Ordered.Count} step(s) from '{graph.Entry.Name}'");
            }
            catch (InvalidOperationException ex)
            {
                return new CapabilityFinding(CapabilityKind.UnsupportedModelSemantics, name, ex.Message);
            }
        }
    }
}
