using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Services;

namespace CodeGen.Hmi
{
    // Generates the native EAE HMI project from the finished syslay.
    //
    // The HMI is a pure consumer and a MONITORING panel: FB Id -> TagName, FB Type -> faceplate.
    // It adds no control logic, and it must not present a control the controller cannot honour --
    // the Station/Area STOP button fires CycleType=0, but ProcessRuntime_Generic_v1's ECC never reads
    // CycleType, so STOP cannot stop recipe execution.
    //
    // The faceplates in Template Library\HMI\Faceplates are therefore ALREADY read-only: their
    // command controls were removed once, reviewably, in the library rather than by mutating a copy
    // during every generation. Generation only copies and places them, and the planner still REFUSES
    // to place any symbol whose .cnv.xml contract declares controller outputs - so a faceplate that
    // regains a command control fails loudly instead of shipping one the controller cannot honour.
    public static class HmiGenerator
    {
        public static void Emit(string syslayPath, CodeGen.Translation.GenerationContext ctx)
        {
            var config = ctx.Config;
            if (config == null || string.IsNullOrWhiteSpace(config.TemplateLibraryPath)) return;

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
            if (string.IsNullOrWhiteSpace(eaeRoot) || !Directory.Exists(eaeRoot)) return;

            // A schema or validation error in hmi.yml aborts before anything is written. The
            // deployment half is validated here too, even though only EmitDeployment uses it, so a
            // malformed value fails before the first canvas rather than after the whole HMI is built.
            var def = HmiDefinitionLoader.Load();
            HmiDeviceLoader.Load();

            var shell = HmiTemplateLibrary.ShellDir(config.TemplateLibraryPath);
            if (!Directory.Exists(shell))
            {
                MapperLogger.Warn($"[Hmi] Template Library\\HMI\\Shell not found - HMI generation skipped ({shell}).");
                return;
            }

            if (!def.ReadOnly)
                throw new InvalidOperationException(
                    "[Hmi] policy.readOnly=false is not supported. " + def.UnsupportedCommandNotice);

            var templates = HmiTemplateLibrary.Load(config.TemplateLibraryPath);
            var hmiDir = Path.Combine(eaeRoot!, def.Deployment.HmiFolderName);
            var libraryNs = LibraryNamespace(eaeRoot!, def);
            var projectName = new DirectoryInfo(eaeRoot!).Name;

            // ---- read + normalise ----------------------------------------------------------
            var contracts = HmiContractReader.ReadAll(eaeRoot!);
            var engine = HmiEngineProbe.Probe(eaeRoot!, def.EngineProbe);
            var reach = HmiModeReach.FromSyslay(syslayPath);
            var plant = HmiSemanticModelBuilder.Build(
                ctx, syslayPath, eaeRoot!, contracts, reach, engine, def);

            foreach (var d in plant.Diagnostics) MapperLogger.Warn("[Hmi] " + d);

            // ---- render to staging ---------------------------------------------------------
            // NOTHING outside staging is touched until every validator has passed, so a rejected
            // generation leaves the previously deployed HMI and its registrations exactly as they were.
            var staging = hmiDir + ".staging";
            var cfgStaging = Path.Combine(Path.GetDirectoryName(staging)!, ".hmi-cfg.staging");
            foreach (var dir in new[] { staging, cfgStaging })
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(dir);
            }

            try
            {
                HmiTemplateLibrary.CopyDirectory(shell, staging);
                foreach (var tpl in templates)
                    HmiTemplateLibrary.CopyDirectory(tpl.SourceDir, Path.Combine(staging, tpl.CatType));

                var deployed = HmiTemplateLibrary.LoadFrom(staging)
                    .Where(t => templates.Any(l => string.Equals(l.CatType, t.CatType, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var plan = HmiPlanner.Plan(plant, deployed, def);
                foreach (var d in plan.Diagnostics.Except(plant.Diagnostics)) MapperLogger.Warn("[Hmi] " + d);

                var owned = new List<string>();
                var screenResx = HmiTemplateLibrary.ScreenResxTemplate(config.TemplateLibraryPath);
                foreach (var screen in plan.Screens)
                    owned.AddRange(HmiCanvasEmitter.Emit(screen, staging, libraryNs, screenResx, def));

                var screenNames = plan.Screens.Select(s => s.Name).ToList();
                HmiProjectEmitter.EmitCanvasList(staging, projectName, libraryNs, screenNames, plan.FirstCanvas, def);
                HmiProjectEmitter.EmitCsproj(staging, screenNames);

                // The CAT .cfg files belong to the IEC61499 tree, so they are rendered into their own
                // staging area and only copied across with the rest of the commit.
                foreach (var tpl in deployed) HmiCatCfgEmitter.EmitTo(cfgStaging, tpl, def.Deployment);

                // ---- validate every staged artefact ----------------------------------------
                var problems = HmiPlanValidator.Validate(staging, eaeRoot!, syslayPath,
                                                         plan with { UsedTemplates = deployed }, def)
                    .Concat(HmiModelValidator.Validate(plant, plan))
                    .ToList();
                foreach (var p in problems) MapperLogger.Error("[Hmi] " + p);

                if (problems.Count > 0)
                    throw new InvalidOperationException(
                        "[Hmi] the generated HMI failed validation and was NOT deployed:" +
                        Environment.NewLine + string.Join(Environment.NewLine, problems));

                // ---- commit ----------------------------------------------------------------
                HmiOwnership.RemovePreviouslyGenerated(hmiDir, def);
                ResetGeneratedCanvases(hmiDir, templates);
                HmiTemplateLibrary.CopyDirectory(staging, hmiDir);

                foreach (var f in Directory.EnumerateFiles(cfgStaging))
                {
                    var cat = Path.GetFileNameWithoutExtension(f);
                    var target = Path.Combine(eaeRoot!, "IEC61499", cat, cat + ".cfg");
                    if (Directory.Exists(Path.GetDirectoryName(target)!)) File.Copy(f, target, overwrite: true);
                }

                HmiOwnership.Write(hmiDir, def, owned.Concat(screenNames.Select(n => n + ".cnv.resx")).Distinct());

                var placed = plan.Screens.Sum(s => s.Items.Count);
                MapperLogger.Info($"[Hmi] MONITORING-ONLY HMI: {plan.Screens.Count} canvas(es), {placed} instance(s), " +
                                  $"{plan.UsedTemplates.Count} faceplate type(s), {plant.Components.Count} component(s), " +
                                  $"{plant.Processes.Count} process(es).");
                LogCapabilities(plant, engine, def);
            }
            finally
            {
                foreach (var dir in new[] { staging, cfgStaging })
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        // Phase two: the HMI logical device, its runtime properties and its topology equipment.
        //
        // Deliberately separate from Emit. The HMI PROJECT is derived from the syslay, so it is built
        // with the syslay; the HMI DEPLOYMENT attaches to the physical network, so it can only be
        // built once the topology exists - the broadcast domain and the switch it binds to are
        // emitted later in the pipeline. Resolving them from the finished topology is what stops the
        // panel silently attaching to a domain or switch that no longer exists.
        //
        // The link between the phases is the generated CanvasesResolutionList.xml, not a field passed
        // between them: the runtime must start on the canvas the project actually declares.
        public static void EmitDeployment(CodeGen.Translation.GenerationContext ctx)
        {
            var config = ctx.Config;
            if (config == null || string.IsNullOrWhiteSpace(config.TemplateLibraryPath)) return;

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
            if (string.IsNullOrWhiteSpace(eaeRoot) || !Directory.Exists(eaeRoot)) return;

            var def = HmiDefinitionLoader.Load();
            var hmiDir = Path.Combine(eaeRoot!, def.Deployment.HmiFolderName);
            if (!Directory.Exists(hmiDir)) return;   // phase one did not run (no template library)

            var firstCanvas = FirstCanvasOf(hmiDir);
            if (firstCanvas == null)
                throw new InvalidOperationException(
                    "[Hmi] the generated CanvasesResolutionList.xml declares no FirstCanvas; " +
                    "the HMI runtime has no screen to start on.");

            var runtime = HmiRuntimeEmitter.Emit(eaeRoot!, ctx, firstCanvas, HmiDeviceLoader.Load());
            foreach (var p in runtime.Problems) MapperLogger.Error("[Hmi] " + p);

            if (runtime.Problems.Count > 0)
                throw new InvalidOperationException(
                    "[Hmi] HMI deployment emission failed:" + Environment.NewLine +
                    string.Join(Environment.NewLine, runtime.Problems));

            MapperLogger.Info($"[Hmi] deployment: {runtime.FilesWritten.Count} file(s), " +
                              $"{runtime.ProjectEntriesAdded} project entry/entries, first canvas '{firstCanvas}'.");
        }

        private static string? FirstCanvasOf(string hmiDir)
        {
            var list = Path.Combine(hmiDir, "CanvasesResolutionList.xml");
            if (!File.Exists(list)) return null;
            try
            {
                return XDocument.Load(list).Descendants()
                    .Where(e => e.Name.LocalName == "Topology")
                    .Select(e => (string?)e.Attribute("FirstCanvas"))
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            }
            catch (Exception ex)
            {
                MapperLogger.Warn($"[Hmi] Could not read the generated canvas list: {ex.Message}");
                return null;
            }
        }

        // States, per controller, exactly which operator capabilities the deployed contracts support.
        // Reported every run because "the button is missing" must always have a stated reason.
        private static void LogCapabilities(HmiPlant plant, HmiEngineSupport engine, HmiDefinition def)
        {
            foreach (var purpose in new[]
                     {
                         HmiCapabilityPurpose.ModeSelection, HmiCapabilityPurpose.CycleControl,
                         HmiCapabilityPurpose.ManualStep, HmiCapabilityPurpose.SetupJog,
                         HmiCapabilityPurpose.FaultReset,
                     })
            {
                var holders = plant.Stations.Select(s => (s.InstanceName, s.Capabilities))
                    .Concat(plant.Processes.Select(p => (p.InstanceName, p.Capabilities)))
                    .Concat(plant.Components.Select(c => (c.InstanceName, c.Capabilities)))
                    .Select(x => (x.Item1, Cap: x.Item2.FirstOrDefault(c => c.Purpose == purpose)))
                    .Where(x => x.Cap != null)
                    .ToList();
                if (holders.Count == 0) continue;

                var ok = holders.Where(h => h.Cap!.Supported).Select(h => h.Item1).ToList();
                var reason = def.Explain(holders.First(h => !h.Cap!.Supported).Cap!.Reason);
                MapperLogger.Info($"[Hmi][capability] {purpose}: supported on {ok.Count}/{holders.Count} instance(s)" +
                                  (ok.Count == 0 ? $" - unavailable because {reason}" : string.Empty));
            }

            if (engine.Found && !engine.AutoGating)
                MapperLogger.Warn("[Hmi] " + def.UnsupportedCommandNotice);
        }

        // Removes only what this generator owns: the root canvases and the faceplate folders it ships.
        // Device faceplates copied by other emitters (e.g. the BX1 EtherNet/IP coupler) are left alone.
        private static void ResetGeneratedCanvases(string hmiDir, IReadOnlyList<HmiCatTemplate> templates)
        {
            if (!Directory.Exists(hmiDir)) return;

            foreach (var cache in new[] { "bin", "obj" })
            {
                var dir = Path.Combine(hmiDir, cache);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }

            foreach (var f in Directory.EnumerateFiles(hmiDir, "*.cnv.*", SearchOption.TopDirectoryOnly).ToList())
                File.Delete(f);

            foreach (var tpl in templates)
            {
                var dir = Path.Combine(hmiDir, tpl.CatType);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        // The canvas namespace segment is the IEC 61499 library name (HMI.<Library>.Canvases).
        private static string LibraryNamespace(string eaeRoot, HmiDefinition def)
        {
            var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
            if (File.Exists(dfbproj))
            {
                try
                {
                    var root = XDocument.Load(dfbproj).Root;
                    var name = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "LibraryName")?.Value
                               ?? root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value;
                    if (!string.IsNullOrWhiteSpace(name)) return name!.Trim();
                }
                catch (Exception ex) { MapperLogger.Warn($"[Hmi] Could not read the IEC61499 library name: {ex.Message}"); }
            }
            return def.Deployment.DefaultLibraryNamespace;
        }
    }
}
