using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            HmiSyslay syslay,
            string eaeProjectDir,
            HmiDeployedTypes types,
            HmiModeReach reach,
            HmiDefinition def)
        {
            var diagnostics = new List<string>();
            var fbs = syslay.Fbs;
            var byId = fbs.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var ecc = types.Ecc;
            HmiContract Contract(string catType) =>
                types.Contracts.TryGetValue(catType, out var c) ? c : HmiContract.None;

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
                    CatType: fb.Type,
                    TagName: fb.Id,
                    Controller: plc,
                    Resource: ControllerMap.ResourceForPlc(plc),
                    Slot: ctx.Slots.TryGetValue(fb.Name, out var slot) ? slot : -1,
                    States: StatesOf(c, Contract(fb.Type), def, diagnostics),
                    Interlocks: Array.Empty<HmiInterlockRule>(),   // filled once every slot is known
                    Capabilities: HmiCapabilityResolver.Resolve(
                        Contract(fb.Type), fb.Name, reach, ecc, def)));
            }

            // Slots are resolved inside the REPORT RING that writes them, never by controller and
            // never globally. Cross-ring slot reuse is legal and normal, so any wider scope names the
            // wrong component - see HmiSlotIndex. The ring itself comes from the plan, not from XML.
            var rings = HmiSlotIndex.Build(ctx.Rings.Domain, ctx.Slots);
            foreach (var c in rings.Conflicts) diagnostics.Add(c);

            var byInstance = components.ToDictionary(c => c.InstanceName, StringComparer.Ordinal);

            // ---- interlocks ------------------------------------------------------------------
            components = components.Select(c =>
            {
                var rules = InterlockRules(ctx, c, rings, byInstance);
                return c with { Interlocks = rules, Ring = rings.RingOf(c.InstanceName) };
            }).ToList();
            byInstance = components.ToDictionary(c => c.InstanceName, StringComparer.Ordinal);

            // ---- processes -------------------------------------------------------------------
            var processes = new List<HmiProcess>();
            var owner = new Dictionary<string, string>(StringComparer.Ordinal);

            // TWO PASSES, and the reason is a real correctness point: a WAIT row can name ANOTHER
            // process's phase (the cross-process handshake), and that number is only meaningful in
            // the OWNING process's phase table. Naming it from the observer's table would label a
            // handshake confidently and wrongly, so every process's phases are collected first.
            var found = new List<(VueOneComponent P, string Key, SyslayFb Fb, RecipeArrays Arrays)>();
            foreach (var p in ctx.Components.Where(ComponentType.IsProcess))
            {
                var key = p.Name?.Trim() ?? string.Empty;
                if (!ctx.Recipes.TryGetValue(key, out var arrays)) continue;
                var tag = FBIdGenerator.GenerateFBId(p.ComponentID ?? key);
                if (byId.TryGetValue(tag, out var fb)) found.Add((p, key, fb, arrays));
            }

            var phasesOf = found.ToDictionary(
                x => x.Fb.Name,
                x => (IReadOnlyDictionary<int, string>)x.Arrays.ProcessPhaseNames,
                StringComparer.OrdinalIgnoreCase);

            foreach (var (p, key, fb, arrays) in found)
            {
                var plc = ctx.Allocation.Of(fb.Name);

                // The compiled recipe, once, as rows. Roles are then DERIVED FROM THE ROWS, so the
                // panel's "this process commands that actuator" and the row an operator reads can
                // never disagree - they are the same objects.
                var rows = HmiRecipePresenter.Rows(
                    arrays, fb.Name, components, phasesOf, rings, def, diagnostics);

                processes.Add(new HmiProcess(
                    ComponentId: p.ComponentID ?? key,
                    InstanceName: fb.Name,
                    DisplayName: HmiPlanner.Humanise(fb.Name),
                    Controller: plc,
                    Resource: ControllerMap.ResourceForPlc(plc),
                    TagName: fb.Id,
                    CatType: fb.Type,
                    Slot: ctx.Slots.TryGetValue(fb.Name, out var s) ? s : -1,
                    Owned: HmiRecipePresenter.Owned(rows),
                    Capabilities: HmiCapabilityResolver.Resolve(
                        Contract(fb.Type), fb.Name, reach, ecc, def),
                    Rows: rows,
                    Observed: HmiRecipePresenter.Observed(rows, components),
                    Phases: HmiRecipePresenter.Phases(rows),
                    Ring: rings.RingOf(fb.Name)));
            }

            components = components
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
                        HmiCapabilityResolver.Resolve(Contract(f.Type), f.Name, reach, ecc, def));
                })
                .OrderBy(s => s.InstanceName, StringComparer.Ordinal)
                .ToList();

            foreach (var unreached in reach.Unreached(
                         components.Select(c => c.InstanceName).Concat(processes.Select(p => p.InstanceName))))
                diagnostics.Add($"'{unreached}' is not on the station mode chain - mode selection, setup jog " +
                                "and fault reset cannot reach it.");

            // Every withheld command reports its OWN generated reason (see HmiCapabilityResolver),
            // so nothing is restated here - a second summary would be a parallel source of truth.

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
            // state per value, so each twin labels its own stops through one generic faceplate.
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

        // The rules the evaluator will actually run, taken from the plan that emitted them.
        //
        // ctx.Interlocks is the SAME InterlockPlan the RuleTable parameter was written from, including
        // the centre-home range filter and mirrored crossings - so the panel explains the deployed
        // behaviour rather than a second interpretation of it. Nothing here recomputes or reorders a
        // rule; the numbers are only joined to names so the operator sees who is blocking and in what
        // state.
        private static IReadOnlyList<HmiInterlockRule> InterlockRules(
            GenerationContext ctx,
            HmiComponent owner,
            HmiSlotIndex rings,
            IReadOnlyDictionary<string, HmiComponent> byInstance)
        {
            if (!ctx.Interlocks.TryGetValue(owner.InstanceName, out var plan) || plan.Count == 0)
                return Array.Empty<HmiInterlockRule>();

            var rules = new List<HmiInterlockRule>(plan.Count);
            for (var i = 0; i < plan.Count; i++)
            {
                // Resolved inside the owner's own report ring. The evaluator reads state_table[src],
                // and that table is written only by this ring, so the ring is the exact scope. A slot
                // that does not resolve here is left unresolved and shown as a bare slot - naming the
                // wrong blocker is worse than naming none.
                var src = plan.Src[i];
                var blocked = plan.Blocked[i];
                var srcName = rings.Resolve(owner.InstanceName, src);
                HmiComponent? srcComp = srcName != null && byInstance.TryGetValue(srcName, out var sc) ? sc : null;

                rules.Add(new HmiInterlockRule(
                    plan.From[i], plan.To[i], src, blocked,
                    srcComp?.DisplayName,
                    srcComp?.StateName(blocked),
                    owner.StateName(plan.From[i]),
                    owner.StateName(plan.To[i])));
            }
            return rules;
        }

    }
}
