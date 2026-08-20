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
    // The HMI is a pure consumer: FB Id -> TagName, FB Type -> faceplate. It adds no control logic
    // and it never writes a physical output - a command control fires an EXISTING CAT command event
    // and nothing else.
    //
    // There is no global read-only switch. Monitoring is always generated; each operator ACTION is
    // judged on its own - the controller gates in HmiCapabilityResolver, then the payload's own
    // proof in HmiActionResolver - and an action that fails is made NON-FIREABLE, not merely
    // reported: its call is deleted or its control unbound in the staged faceplate, its control is
    // disabled, and the screen states the reason. Where every action on a symbol is withheld the
    // monitoring variant is placed instead, so the operator keeps the live values either way.
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

            var shell = HmiTemplateLibrary.ShellDir(config.TemplateLibraryPath);
            if (!Directory.Exists(shell))
            {
                MapperLogger.Warn($"[Hmi] Template Library\\HMI\\Shell not found - HMI generation skipped ({shell}).");
                return;
            }

            var templates = HmiTemplateLibrary.Load(config.TemplateLibraryPath);
            var hmiDir = Path.Combine(eaeRoot!, def.Deployment.HmiFolderName);
            var libraryNs = LibraryNamespace(eaeRoot!, def);
            var projectName = new DirectoryInfo(eaeRoot!).Name;

            // ---- read + normalise ----------------------------------------------------------
            var syslay = HmiSyslay.Load(syslayPath);
            var types = HmiDeployedTypes.Read(eaeRoot!);
            // A station's HMI adapter is only a mode SOURCE if a symbol the panel actually places can
            // raise the mode event. Wired-but-undrivable would otherwise report Setup as reachable on
            // actuators whose mode can never leave the value their CAT initialises to.
            var modeRule = def.Capabilities.FirstOrDefault(c => c.Purpose == HmiCapabilityPurpose.ModeSelection);
            var typeOfFb = syslay.Fbs.GroupBy(f => f.Name, StringComparer.Ordinal)
                                     .ToDictionary(g => g.Key, g => g.First().Type, StringComparer.Ordinal);
            bool CanDriveMode(string faceplateFb)
            {
                if (modeRule == null || !typeOfFb.TryGetValue(faceplateFb, out var catType)) return false;
                var sym = templates.FirstOrDefault(t =>
                    string.Equals(t.CatType, catType, StringComparison.OrdinalIgnoreCase))
                    ?.Primary(def.Deployment.PrimarySymbol);
                return sym != null && modeRule.OutputData.Any(d => sym.CanSend(modeRule.OutputEvent, d));
            }

            var reach = HmiModeReach.From(syslay, CanDriveMode,
                                          modeRule?.ChainPorts ?? Array.Empty<string>());
            var plant = HmiSemanticModelBuilder.Build(
                ctx, syslay, eaeRoot!, types, reach, def);

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

                var plan = HmiPlanner.Plan(plant, templates, types.Ecc, def);
                foreach (var d in plan.Diagnostics.Except(plant.Diagnostics)) MapperLogger.Warn("[Hmi] " + d);

                // Only the faceplates the plan places are staged, and only the symbols it selected.
                var selectedByCat = plan.SelectedSymbols
                    .GroupBy(s => s.CatType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key,
                                  g => (IReadOnlyCollection<string>)new HashSet<string>(
                                      g.Select(x => x.Symbol), StringComparer.OrdinalIgnoreCase),
                                  StringComparer.OrdinalIgnoreCase);

                var deployed = templates.Where(t => selectedByCat.ContainsKey(t.CatType)).ToList();
                foreach (var tpl in deployed)
                    foreach (var f in HmiTemplateLibrary.CopySelected(
                                 tpl, Path.Combine(staging, tpl.CatType), selectedByCat[tpl.CatType]))
                        MapperLogger.Info($"[Hmi] not deployed (no screen uses it): {f}");

                // Make every withheld action non-fireable in the STAGED faceplate before anything is
                // validated or committed. A reported-disabled control that still raises the event is
                // the defect this closes; the validator below proves the outcome on the staged source.
                foreach (var note in HmiFaceplatePatcher.Suppress(staging, deployed, plan.AllVerdicts, def))
                    MapperLogger.Warn("[Hmi] " + note);

                var owned = new List<string>();
                var screenResx = HmiTemplateLibrary.ScreenResxTemplate(config.TemplateLibraryPath);
                foreach (var screen in plan.Screens)
                    owned.AddRange(HmiCanvasEmitter.Emit(screen, staging, libraryNs, screenResx, def));

                // The capability evidence file, owned by the manifest like every other generated file.
                owned.Add(HmiCapabilityReportEmitter.Emit(staging, plant, plan));

                var screenNames = plan.Screens.Select(s => s.Name).ToList();
                HmiProjectEmitter.EmitCanvasList(staging, projectName, libraryNs, screenNames, plan.FirstCanvas, def);
                HmiProjectEmitter.EmitCsproj(staging, screenNames);

                // The CAT .cfg files belong to the IEC61499 tree, so they are rendered into their own
                // staging area and only copied across with the rest of the commit.
                foreach (var tpl in deployed)
                    HmiCatCfgEmitter.EmitTo(cfgStaging, tpl, def.Deployment, selectedByCat[tpl.CatType]);

                // ---- validate every staged artefact ----------------------------------------
                var problems = HmiPlanValidator.Validate(staging, eaeRoot!, cfgStaging, syslay,
                                                         plan with { UsedTemplates = deployed }, def)
                    .Concat(HmiPlanValidator.ValidateModel(plant, plan))
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

                foreach (var f in PruneAbandonedFaceplates(hmiDir, eaeRoot!, templates))
                    MapperLogger.Info($"[Hmi] removed abandoned faceplate folder: {f}");

                // Re-stated over the COMMITTED tree, which is a superset of staging: it also holds the
                // device faceplates another emitter placed here (the BX1 coupler's, whose own .cfg
                // already names its canvases). Written from staging alone they were shipped but never
                // compiled, so the .cfg pointed at a type the HMI assembly did not contain.
                HmiProjectEmitter.EmitCsproj(hmiDir, screenNames);

                foreach (var f in Directory.EnumerateFiles(cfgStaging))
                {
                    var cat = Path.GetFileNameWithoutExtension(f);
                    var target = Path.Combine(eaeRoot!, "IEC61499", cat, cat + ".cfg");
                    if (Directory.Exists(Path.GetDirectoryName(target)!)) File.Copy(f, target, overwrite: true);
                }

                // The project file and the canvas list are written by this generator too, so they
                // belong to it: without them the manifest would leave a stale csproj behind when a
                // model stops needing a screen the previous one had.
                HmiOwnership.Write(hmiDir, def, owned
                    .Concat(screenNames.Select(n => n + ".cnv.resx"))
                    .Concat(new[] { "HMI.csproj", HmiProjectEmitter.CanvasListFileName })
                    .Distinct());

                var placed = plan.Screens.Sum(s => s.Items.Count);
                var acts = plan.Screens.SelectMany(s => s.Items).SelectMany(i => i.Actions).ToList();
                MapperLogger.Info($"[Hmi] {acts.Count(a => a.Effective)}/{acts.Count} operator action(s) effective: " +
                                  $"{plan.Screens.Count} canvas(es), {placed} instance(s), " +
                                  $"{plan.UsedTemplates.Count} faceplate type(s), {plant.Components.Count} component(s), " +
                                  $"{plant.Processes.Count} process(es).");
                LogCapabilities(plant, def);
            }
            finally
            {
                foreach (var dir in new[] { staging, cfgStaging })
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        // A faceplate folder this generator no longer has any reason to keep.
        //
        // Deliberately narrow, because the HMI folder is shared: a folder survives if the template
        // library still ships it OR the deployed CAT still declares a .cfg for it - which is what
        // keeps the BX1 coupler's device faceplate, placed here by another emitter. Only a folder
        // that is CAT-SHAPED (it carries the per-CAT <name>.def.cs) and satisfies neither is removed,
        // so a shell folder or a vendor resource can never be caught by this.
        private static IReadOnlyList<string> PruneAbandonedFaceplates(
            string hmiDir, string eaeProjectDir, IReadOnlyList<HmiCatTemplate> library)
        {
            var removed = new List<string>();
            if (!Directory.Exists(hmiDir)) return removed;

            foreach (var dir in Directory.EnumerateDirectories(hmiDir))
            {
                var name = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, name + ".def.cs"))) continue;   // not a CAT folder
                if (library.Any(t => string.Equals(t.CatType, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (File.Exists(Path.Combine(eaeProjectDir, "IEC61499", name, name + ".cfg"))) continue;

                Directory.Delete(dir, recursive: true);
                removed.Add(name);
            }
            return removed;
        }

        // Phase two: the HMI logical device, its runtime properties and its topology emitter.
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

            var runtime = HmiRuntimeEmitter.Emit(eaeRoot!, ctx, firstCanvas, def.Device);
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
        private static void LogCapabilities(HmiPlant plant, HmiDefinition def)
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
                var withheld = holders.FirstOrDefault(h => !h.Cap!.Supported).Cap;
                MapperLogger.Info($"[Hmi][capability] {purpose}: ENABLED on {ok.Count}/{holders.Count} instance(s)" +
                                  (withheld == null ? string.Empty : $" - withheld because {withheld.Detail}"));
            }
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
