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
    // CycleType, so STOP cannot stop recipe execution. Command controls are therefore stripped from
    // the deployed faceplates rather than merely hidden.
    public static class HmiGenerator
    {
        public static void Emit(string syslayPath, CodeGen.Translation.GenerationContext ctx)
        {
            var config = ctx.Config;
            if (config == null || string.IsNullOrWhiteSpace(config.TemplateLibraryPath)) return;

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
            if (string.IsNullOrWhiteSpace(eaeRoot) || !Directory.Exists(eaeRoot)) return;

            var shell = HmiTemplateLibrary.ShellDir(config.TemplateLibraryPath);
            if (!Directory.Exists(shell))
            {
                MapperLogger.Warn($"[Hmi] Template Library\\HMI\\Shell not found - HMI generation skipped ({shell}).");
                return;
            }

            if (!config.HmiReadOnly)
                throw new InvalidOperationException(
                    "[Hmi] HmiReadOnly=false is not supported. " + HmiNames.CommandContractDiagnostic);

            var templates = HmiTemplateLibrary.Load(config.TemplateLibraryPath);
            var hmiDir = Path.Combine(eaeRoot!, "HMI");
            var libraryNs = LibraryNamespace(eaeRoot!);
            var projectName = new DirectoryInfo(eaeRoot!).Name;

            // Rebuild from templates every run so a removed component cannot leave a stale canvas.
            // Every faceplate type is shipped, not just the placed ones, so the project stays
            // self-consistent regardless of which CATs a given model instantiates.
            ResetGeneratedCanvases(hmiDir, templates);
            HmiTemplateLibrary.CopyDirectory(shell, hmiDir);
            foreach (var tpl in templates)
                HmiTemplateLibrary.CopyDirectory(tpl.SourceDir, Path.Combine(hmiDir, tpl.CatType));

            // Strip command controls from the DEPLOYED copies, then re-read them so the plan sees
            // what was actually written rather than what the library ships.
            var stripped = HmiCommandStripper.StripAll(hmiDir, templates);
            foreach (var r in stripped.Where(r => r.Failure != null))
                MapperLogger.Error($"[Hmi] {r.CatType}.{r.Symbol}: command strip FAILED - {r.Failure}");
            foreach (var r in stripped.Where(r => r.Failure == null && r.Changed))
                MapperLogger.Info($"[Hmi] {r.CatType}.{r.Symbol}: removed {r.RemovedControls.Count} command control(s)" +
                                  (r.RemovedOutputs.Count > 0 ? $", cleared outputs [{string.Join(", ", r.RemovedOutputs)}]" : string.Empty) +
                                  (r.CodeBehindReplaced ? ", replaced code-behind" : string.Empty));

            var deployed = HmiTemplateLibrary.LoadFrom(hmiDir)
                .Where(t => templates.Any(l => string.Equals(l.CatType, t.CatType, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var plan = HmiPlanner.Plan(syslayPath, eaeRoot!, deployed, config.HmiReadOnly);
            foreach (var d in plan.Diagnostics) MapperLogger.Warn("[Hmi] " + d);

            var screenResx = HmiTemplateLibrary.ScreenResxTemplate(config.TemplateLibraryPath);
            foreach (var screen in plan.Screens)
                HmiCanvasEmitter.Emit(screen, hmiDir, libraryNs, screenResx);

            var screenNames = plan.Screens.Select(s => s.Name).ToList();
            HmiProjectEmitter.EmitCanvasList(hmiDir, projectName, libraryNs, screenNames, plan.FirstCanvas);
            HmiProjectEmitter.EmitCsproj(hmiDir, screenNames);

            // The .cfg must describe the STRIPPED symbols, not the library originals.
            foreach (var tpl in deployed) HmiCatCfgEmitter.Emit(eaeRoot!, tpl);

            var runtime = HmiRuntimeEmitter.Emit(eaeRoot!, ctx, plan.FirstCanvas);
            foreach (var p in runtime.Problems) MapperLogger.Error("[Hmi] " + p);

            var problems = HmiPlanValidator.Validate(hmiDir, eaeRoot!, syslayPath, plan with { UsedTemplates = deployed })
                .Concat(stripped.Where(r => r.Failure != null)
                    .Select(r => $"command strip failed for {r.CatType}.{r.Symbol}: {r.Failure}"))
                .ToList();
            foreach (var p in problems) MapperLogger.Error("[Hmi] " + p);

            var fatal = problems.Where(p => p.StartsWith("READ-ONLY VIOLATION", StringComparison.Ordinal) ||
                                            p.StartsWith("command strip failed", StringComparison.Ordinal)).ToList();
            if (fatal.Count > 0)
                throw new InvalidOperationException(
                    "[Hmi] the generated HMI is not read-only and was rejected:" +
                    Environment.NewLine + string.Join(Environment.NewLine, fatal));

            var placed = plan.Screens.Sum(s => s.Items.Count);
            MapperLogger.Info($"[Hmi] MONITORING-ONLY HMI: {plan.Screens.Count} canvas(es), {placed} faceplate instance(s), " +
                              $"{plan.UsedTemplates.Count} CAT faceplate type(s), " +
                              $"{runtime.FilesWritten.Count} runtime/topology file(s) updated.");
            MapperLogger.Warn("[Hmi] " + HmiNames.CommandContractDiagnostic);
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
        private static string LibraryNamespace(string eaeRoot)
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
            return "Main";
        }
    }
}
