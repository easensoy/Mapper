using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Process;

namespace CodeGen.Hmi
{
    // Builds the normalised HMI model.
    //
    // Division of sources is deliberate and absolute:
    //   * SEMANTICS come from GenerationContext - the twin parsed once. Component identity, state
    //     names, recipe rows, slots and controller allocation are read from the model objects, never
    //     recovered by parsing a string the generator itself just serialised.
    //   * DEPLOYMENT BINDINGS come from the finished syslay - the emitted FB Id (the HMI TagName),
    //     the emitted CAT type and the exact emitted RuleTable. These are facts about what was
    //     actually generated and cannot be known any other way.
    internal static class HmiSemanticModelBuilder
    {
        internal static HmiPlant Build(
            GenerationContext ctx,
            string syslayPath,
            string eaeProjectDir,
            IReadOnlyDictionary<string, HmiContract> contracts,
            HmiModeReach reach,
            HmiEngineSupport engine,
            HmiDefinition def)
        {
            var diagnostics = new List<string>();
            var fbs = SyslayReader.Read(syslayPath);
            var byId = fbs.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            HmiContract Contract(string catType) =>
                contracts.TryGetValue(catType, out var c) ? c : HmiContract.None;

            // ---- components ------------------------------------------------------------------
            var components = new List<HmiComponent>();

            // Every twin component that is not a process is plant the operator monitors. Selecting by
            // "not a process" rather than by listing Actuator/Sensor is deliberate: the grippers and
            // the task arm carry Type="Robot", and an allow-list silently demoted them to
            // infrastructure - which is exactly the class of drop this model exists to prevent.
            foreach (var c in ctx.Components.Where(c =>
                         !ComponentType.IsProcess(c) && !ComponentType.Is(c, ComponentType.NonControl)))
            {
                if (string.IsNullOrWhiteSpace(c.ComponentID)) continue;

                var tag = FBIdGenerator.GenerateFBId(c.ComponentID);
                if (!byId.TryGetValue(tag, out var fb))
                {
                    // The twin declares it but this deployment did not emit it (a component the
                    // roster does not place). Recorded, never silently dropped.
                    diagnostics.Add($"'{c.Name}' is declared in the twin but no FB with id {tag} was emitted - not placed.");
                    continue;
                }

                var plc = ctx.Allocation.Of(fb.Name);

                components.Add(new HmiComponent(
                    ComponentId: c.ComponentID,
                    InstanceName: fb.Name,
                    DisplayName: HmiPlanner.Humanise(fb.Name),
                    SourceType: c.Type ?? string.Empty,
                    CatType: fb.Type,
                    TagName: fb.Id,
                    Controller: plc,
                    Resource: ControllerMap.ResourceForPlc(plc),
                    OwningProcess: null,                       // filled once processes are known
                    Slot: ctx.Slots.TryGetValue(fb.Name, out var slot) ? slot : -1,
                    States: StatesOf(c, Contract(fb.Type), def, diagnostics),
                    Interlocks: Array.Empty<HmiInterlockRule>(),   // filled once every slot is known
                    Capabilities: HmiCapabilityResolver.Resolve(
                        Contract(fb.Type), fb.Name, reach, engine, def)));
            }

            // Slots are resolved inside the REPORT RING that writes them, never by controller and
            // never globally. Cross-ring slot reuse is legal and normal, so any wider scope names the
            // wrong component - see HmiRingIndex.
            var rings = HmiRingIndex.Build(syslayPath, ctx.Slots);
            foreach (var c in rings.Conflicts) diagnostics.Add(c);

            var byInstance = components.ToDictionary(c => c.InstanceName, StringComparer.Ordinal);

            // ---- interlocks ------------------------------------------------------------------
            components = components.Select(c =>
            {
                var fb = byId[c.TagName];
                var rules = InterlockRules(fb, c, rings, byInstance);
                return c with { Interlocks = rules, Ring = rings.RingOf(c.InstanceName) };
            }).ToList();
            byInstance = components.ToDictionary(c => c.InstanceName, StringComparer.Ordinal);

            // ---- processes -------------------------------------------------------------------
            var processes = new List<HmiProcess>();
            var owner = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var p in ctx.Components.Where(ComponentType.IsProcess))
            {
                var key = p.Name?.Trim() ?? string.Empty;
                if (!ctx.Recipes.TryGetValue(key, out var arrays)) continue;

                var tag = FBIdGenerator.GenerateFBId(p.ComponentID ?? key);
                if (!byId.TryGetValue(tag, out var fb)) continue;

                var plc = ctx.Allocation.Of(fb.Name);
                var rows = Rows(arrays, components, rings, fb.Name);

                var ownedSet = new List<string>();
                var observedSet = new List<string>();
                foreach (var r in rows)
                {
                    if (r.StepType == StepType.Cmd)
                    {
                        var t = ResolveCommandTarget(r.CmdTarget, components);
                        if (t != null && !ownedSet.Contains(t.InstanceName, StringComparer.Ordinal))
                        {
                            ownedSet.Add(t.InstanceName);
                            owner.TryAdd(t.InstanceName, fb.Name);
                        }
                    }
                    else if (r.StepType == StepType.Wait)
                    {
                        // A recipe WAIT reads this process's own state_table, so the slot resolves
                        // inside the process's ring - the same scope the controller itself uses.
                        var inst = rings.Resolve(fb.Name, r.WaitSlot);
                        if (inst != null && !observedSet.Contains(inst, StringComparer.Ordinal))
                            observedSet.Add(inst);
                    }
                }

                processes.Add(new HmiProcess(
                    ComponentId: p.ComponentID ?? key,
                    InstanceName: fb.Name,
                    DisplayName: HmiPlanner.Humanise(fb.Name),
                    Controller: plc,
                    Resource: ControllerMap.ResourceForPlc(plc),
                    TagName: fb.Id,
                    CatType: fb.Type,
                    Slot: ctx.Slots.TryGetValue(fb.Name, out var s) ? s : -1,
                    Rows: rows,
                    Owned: ownedSet,
                    Observed: observedSet,
                    Phases: (arrays.ProcessPhaseNames ?? new Dictionary<int, string>())
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new HmiStateName(kv.Key, kv.Value))
                        .ToList(),
                    Capabilities: HmiCapabilityResolver.Resolve(
                        Contract(fb.Type), fb.Name, reach, engine, def),
                    Ring: rings.RingOf(fb.Name)));
            }

            components = components
                .Select(c => owner.TryGetValue(c.InstanceName, out var o) ? c with { OwningProcess = o } : c)
                .ToList();

            // ---- stations --------------------------------------------------------------------
            var processNames = new HashSet<string>(processes.Select(p => p.InstanceName), StringComparer.Ordinal);
            var componentNames = new HashSet<string>(components.Select(c => c.InstanceName), StringComparer.Ordinal);

            var stations = fbs
                .Where(f => Contract(f.Type).Exists &&
                            !processNames.Contains(f.Name) && !componentNames.Contains(f.Name))
                .Select(f =>
                {
                    var plc = ctx.Allocation.Of(f.Name);
                    return new HmiStation(
                        f.Name, f.Id, f.Type, plc, ControllerMap.ResourceForPlc(plc),
                        // Resolved through the core this faceplate drives - see HmiModeReach.
                        reach.ReachesThrough(f.Name),
                        HmiCapabilityResolver.Resolve(Contract(f.Type), f.Name, reach, engine, def));
                })
                .OrderBy(s => s.InstanceName, StringComparer.Ordinal)
                .ToList();

            foreach (var unreached in reach.Unreached(
                         components.Select(c => c.InstanceName).Concat(processes.Select(p => p.InstanceName))))
                diagnostics.Add($"'{unreached}' is not on the station mode chain - mode selection, setup jog " +
                                "and fault reset cannot reach it.");

            if (engine.Found && !engine.AutoGating)
                diagnostics.Add($"the deployed process engine's ECC ({engine.TransitionCount} transitions) never reads " +
                                "Mode or CycleType, so Auto gating and an operational Stop are not implemented by the controller.");
            if (engine.Found && !engine.ManualStepping)
                diagnostics.Add("the deployed process engine does not implement manual recipe stepping.");

            return new HmiPlant(
                ModelName: Path.GetFileName(eaeProjectDir),
                Stations: stations,
                Processes: processes.OrderBy(p => p.InstanceName, StringComparer.Ordinal).ToList(),
                Components: components.OrderBy(c => c.InstanceName, StringComparer.Ordinal).ToList(),
                Diagnostics: diagnostics);
        }

        // ---- state naming --------------------------------------------------------------------

        // The value an instance reports is the CAT's runtime encoding, not the twin's State_Number
        // (which is a broadcast slot and collides). The twin supplies the NAMES; the deployed contract
        // decides HOW MANY values exist, by selecting a state profile from hmi.yml on its own input
        // signature. No CAT name is tested and no vocabulary is written down in C#.
        private static IReadOnlyList<HmiStateName> StatesOf(
            VueOneComponent c, HmiContract contract, HmiDefinition def, List<string> diagnostics)
        {
            var declared = c.States ?? new List<VueOneState>();
            if (declared.Count == 0) return Array.Empty<HmiStateName>();

            var profile = def.ProfileFor(contract, out var ambiguity);
            if (ambiguity != null)
            {
                diagnostics.Add($"'{c.Name}': {ambiguity}; state labels omitted rather than guessed.");
                return Array.Empty<HmiStateName>();
            }
            if (profile == null)
            {
                // policy.unknownContract decides what an unrecognised contract means. 'fail' stops the
                // generation: the twin's State_Number is a BROADCAST SLOT, not the runtime encoding,
                // so falling back to it can label a live value with a stop the instance never reports.
                // 'skip' accepts that risk explicitly and says so on the screen.
                if (def.FailOnUnknownContract)
                    throw new InvalidOperationException(
                        $"[Hmi] '{c.Name}' ({c.Type}): no hmi.yml state profile matches the deployed HMI " +
                        "contract, and policy.unknownContract is 'fail'. Add a protocol.statesProfiles " +
                        "entry whose match.inputEventCarries describes this contract, or set " +
                        "policy.unknownContract: skip to accept unlabelled states.");

                diagnostics.Add($"'{c.Name}': no hmi.yml state profile matches the deployed contract; " +
                                "state labels fall back to the twin's own State_Number.");
                return declared.GroupBy(s => s.StateNumber).Where(g => g.Count() == 1)
                    .Select(g => new HmiStateName(g.Key, g.First().Name ?? string.Empty))
                    .OrderBy(s => s.Value).ToList();
            }

            // The twin's stop names map straight onto the runtime values when it declares exactly one
            // state per value - this is what makes _sw5 read "AtWork1 / ToWork2 / AtWork2" while a
            // clamp model reads its own words, from one generic faceplate.
            if (declared.Count == profile.Labels.Count)
                return declared.Select((s, i) => new HmiStateName(i, s.Name ?? string.Empty)).ToList();

            // A branched twin declares more stops than the contract can report, so a positional map
            // would mislabel live values. Use the profile's own vocabulary, which is complete for
            // every value the instance can actually publish, and say why.
            diagnostics.Add(
                $"'{c.Name}' declares {declared.Count} states but profile '{profile.Id}' reports " +
                $"{profile.Labels.Count} runtime values; labels come from the profile because the " +
                "twin's stops cannot be mapped one-to-one.");
            return profile.Labels.Select((n, i) => new HmiStateName(i, n)).ToList();
        }

        // ---- interlocks ----------------------------------------------------------------------

        private static readonly Regex RuleRow = new(
            @"FromState:=(-?\d+),\s*ToState:=(-?\d+),\s*SourceID:=(-?\d+),\s*BlockedState:=(-?\d+)",
            RegexOptions.Compiled);

        // The RuleTable is read back verbatim from the syslay because it IS the deployment fact -
        // the exact rows the evaluator will run. Nothing here recomputes or reorders a rule; the
        // numbers are only joined to names so the operator sees who is blocking and in what state.
        private static IReadOnlyList<HmiInterlockRule> InterlockRules(
            SyslayFb fb,
            HmiComponent owner,
            HmiRingIndex rings,
            IReadOnlyDictionary<string, HmiComponent> byInstance)
        {
            var raw = fb.Param("RuleTable");
            if (string.IsNullOrEmpty(raw)) return Array.Empty<HmiInterlockRule>();

            var rules = new List<HmiInterlockRule>();
            foreach (Match m in RuleRow.Matches(raw))
            {
                var from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var to = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var src = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                var blocked = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                if (from == 0 && to == 0 && src == 0 && blocked == 0) continue;   // unused array slot

                // Resolved inside the owner's own report ring. The evaluator reads state_table[src],
                // and that table is written only by this ring, so the ring is the exact scope. A slot
                // that does not resolve here is left unresolved and shown as a bare slot - naming the
                // wrong blocker is worse than naming none.
                var srcName = rings.Resolve(owner.InstanceName, src);
                HmiComponent? srcComp = srcName != null && byInstance.TryGetValue(srcName, out var sc) ? sc : null;

                rules.Add(new HmiInterlockRule(
                    from, to, src, blocked,
                    srcComp?.DisplayName,
                    srcComp?.StateName(blocked),
                    owner.StateName(from),
                    owner.StateName(to)));
            }
            return rules;
        }

        // ---- recipe rows ---------------------------------------------------------------------

        private static IReadOnlyList<HmiRecipeRow> Rows(
            RecipeArrays a, IReadOnlyList<HmiComponent> components,
            HmiRingIndex rings, string processInstance)
        {
            var rows = new List<HmiRecipeRow>(a.Count);
            for (var i = 0; i < a.Count; i++)
            {
                var type = a.StepType[i];
                var target = a.CmdTargetName[i] ?? string.Empty;
                var cmdState = a.CmdStateArr[i];
                var waitSlot = a.Wait1Id[i];
                var waitState = a.Wait1State[i];

                string text;
                if (type == StepType.Cmd)
                {
                    var t = ResolveCommandTarget(target, components);
                    text = t != null
                        ? $"Command {t.DisplayName} to {t.StateName(cmdState)}"
                        : $"Announce phase '{target}' = {cmdState}";
                }
                else if (type == StepType.Wait)
                {
                    var name = rings.Resolve(processInstance, waitSlot);
                    var w = name == null ? null
                        : components.FirstOrDefault(c => string.Equals(c.InstanceName, name, StringComparison.Ordinal));
                    text = w != null
                        ? $"Wait until {w.DisplayName} is {w.StateName(waitState)}"
                        : $"Wait until slot {waitSlot} reports {waitState}";
                }
                else
                {
                    text = "End of recipe";
                }

                rows.Add(new HmiRecipeRow(i, type, target, cmdState, waitSlot, waitState, a.NextStep[i], text));
            }
            return rows;
        }

        // CmdTargetName is one of three things and each is matched on its own terms: an actuator ring
        // key (lower-cased), a sensor refresh (verbatim name), or a process phase announcement (which
        // resolves to no component - correctly, not as a gap).
        private static HmiComponent? ResolveCommandTarget(string target, IReadOnlyList<HmiComponent> components)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            var t = target.Trim();

            return components.FirstOrDefault(c =>
                       string.Equals(TemplateMap.RingKey(c.InstanceName), t, StringComparison.Ordinal))
                ?? components.FirstOrDefault(c =>
                       string.Equals(c.InstanceName, t, StringComparison.OrdinalIgnoreCase));
        }
    }
}
