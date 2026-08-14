using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Artefacts;
using CodeGen.Configuration;
using CodeGen.Devices.BX1;
using CodeGen.Mapping;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Devices.RevPi;
using CodeGen.Services;
using CodeGen.Translation;
using CodeGen.Validation.Output;
using CodeGen.Validation.Plan;

namespace CodeGen.Application
{
    // What to generate. Immutable: the orchestration reads it and never writes back, so a caller cannot
    // have its inputs mutated underneath it by a generation step.
    public sealed record GenerationRequest(
        string ControlXmlPath,
        MapperConfig Config,
        IReadOnlySet<string> RevPiComponents);

    public sealed record GenerationResult(
        string SyslayPath,
        SystemInjector.BindingApplicationReport Report)
    {
        public int BoundCount => Report.Bound.Count;
    }

    // The one generation path from Control.xml to EAE artefacts.
    //
    // Synchronous by design: callers that need responsiveness wrap the single call, rather than each step
    // deciding its own threading. `log` is invoked on the calling thread.
    public static class GenerateProject
    {
        public static GenerationResult Execute(GenerationRequest request, Action<string> log)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (log is null) throw new ArgumentNullException(nameof(log));

            // Work on a copy so a run cannot write back into the caller's config. MapperUI keeps one
            // instance for the life of the process, so in-place mutation leaked between runs.
            var cfg = request.Config.Clone();
            cfg.UseRecipeStruct = true;

            // The Feed station stays on M262 and the RevPi COEXISTS with it: PLC_RW_REVPI carries IO for
            // only Feeder/Checker/PartInHopper, so a whole-station swap would strand Transfer/Ejector/
            // Robot/PartAtAssembly with no physical IO. RevPiSelectionValidator rejects the full swap.
            var profile = new DeploymentProfile(request.RevPiComponents, LayoutCatalog.Load());
            RevPiSelectionValidator.ThrowIfInvalid(profile);

            // Parse once, plan once, render once: everything the run decides is settled here, before the
            // first artefact is written, so a model the backend cannot express fails with a diagnostic
            // instead of a half-generated project.
            var ctx = GenerationContext.Plan(cfg, request.ControlXmlPath, profile);

            DeepClean(cfg, log);
            LogM262SysdevState(cfg, log);

            var injector = new SystemInjector();
            LogCleanup(injector.PrepareDemonstratorForGeneration(cfg), log);

            // AFTER cleanup: cleanup deletes flat root-level Basic FB files, so deploying first would drop
            // the patched core.
            DeployTemplates(ctx, log);

            var bindings = LoadBindings(cfg, log);
            var path = injector.GenerateStation1TestSyslay(ctx, bindings, out var report);
            LogBindings(report, log);

            FinalizeDeviceStack(ctx, log);
            WireFeedResource(ctx, report, log);
            MirrorAndWireStation2(ctx, report, log);
            InjectBx1Broker(ctx, path, report, log);
            PatchAndBindHcf(ctx, path, bindings, report, log);

            ValidateHcfReferences(cfg, log);
            SyncSysresParameters(cfg, path, log);
            SweepOrphans(cfg, log);
            ValidateParity(ctx, path, log);
            ValidateConnections(cfg, log);
            ValidateAddresses(ctx, log);
            ValidateMqtt(cfg, log);
            ValidateBx1Scanner(cfg, log);

            TouchDfbproj(cfg, log);
            log($"Generated: {path}");
            return new GenerationResult(path, report);
        }

        // git reset/clean where the Demonstrator is a repo, then the deep FB/canvas/device wipe. The root
        // is DERIVED from the configured project so a retargeted output root cleans itself rather than
        // C:\Demonstrator.
        static void DeepClean(MapperConfig cfg, Action<string> log)
        {
            var eae = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var demoRepo = string.IsNullOrEmpty(eae) ? @"C:\Demonstrator" : Path.GetDirectoryName(eae);
            if (string.IsNullOrEmpty(demoRepo)) demoRepo = @"C:\Demonstrator";

            // EAE is intentionally NOT killed; files it holds open surface as sharing-violation warnings.
            if (Directory.Exists(Path.Combine(demoRepo, ".git")))
            {
                var (resetCode, resetOut) = RunGit(demoRepo, "reset --hard");
                log($"[Generate] git reset --hard -> exit {resetCode}");
                if (!string.IsNullOrWhiteSpace(resetOut)) log(resetOut.Trim());

                var (cleanCode, cleanOut) = RunGit(demoRepo, "clean -fd -e *.lock_sln");
                log($"[Generate] git clean -fd -e *.lock_sln -> exit {cleanCode}");
                if (!string.IsNullOrWhiteSpace(cleanOut)) log(cleanOut.Trim());
            }
            else
            {
                log($"[Generate] {demoRepo} is not a git repo — skipping git reset/clean. Wiper still runs.");
            }

            var report = DemonstratorWiper.Wipe(cfg, demoRepo);
            foreach (var step in report.Steps) log($"[Generate] {step}");
            foreach (var w in report.Warnings) log($"[Generate][!] {w}");
            log($"[Generate] wipe summary: {report.FilesEmptied} canvas(es) emptied, " +
                $"{report.FilesDeleted} FB-type file(s) deleted, " +
                $"{report.FoldersDeleted} type folder(s) removed, " +
                $"{report.DfbprojEntriesRemoved} dfbproj entry/entries stripped, " +
                $"{report.HwConfigFilesDeleted} HwConfiguration file(s) cleared.");
        }

        static (int exitCode, string output) RunGit(string workingDir, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, string.IsNullOrEmpty(stderr) ? stdout : stdout + stderr);
        }

        // The Mapper owns the M262 logical device: an existing .sysdev is preserved (trust kept), an
        // absent one is bootstrapped. Diagnostic only — never aborts.
        static void LogM262SysdevState(MapperConfig cfg, Action<string> log)
        {
            var exists = false;
            try { exists = M262SysdevEmitter.M262SysdevAlreadyExists(cfg); } catch { }
            log(exists
                ? "[Device] M262 sysdev present — preserved (trust binding intact)."
                : "[Device] M262 sysdev absent — Mapper will bootstrap the M262 logical device from scratch.");
        }

        static void DeployTemplates(GenerationContext ctx, Action<string> log)
        {
            try
            {
                var deploy = TemplateLibraryDeployer.DeployUniversalArchitecture(ctx);
                log($"[Deploy] Registered {deploy.CATsDeployed.Count} CAT(s), " +
                    $"{deploy.BasicFBsDeployed.Count} Basic(s), " +
                    $"{deploy.CompositesDeployed.Count} Composite(s), " +
                    $"{deploy.AdaptersDeployed.Count} Adapter(s) into Demonstrator/IEC61499 " +
                    $"({deploy.FilesExtracted} new, {deploy.FilesSkipped} skipped).");
                foreach (var w in deploy.Warnings) log($"[Deploy][Warn] {w}");
            }
            catch (Exception ex)
            {
                log($"[Deploy][Error] {ex.Message}");
                throw;
            }
        }

        // A missing IO-bindings xlsx means NO actuator athome/atwork/coil bindings — every Five_State
        // sensor unbound and nothing triggers. Self-heal from the project source if present, else fail
        // loudly rather than emit a blank HCF.
        static IoBindings? LoadBindings(MapperConfig cfg, Action<string> log)
        {
            try
            {
                var path = ResolveBindingsPath(cfg.IoBindingsPath);
                if (!File.Exists(path))
                {
                    var fileName = Path.GetFileName(path);
                    string? recovered = null;
                    foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                    {
                        var probe = new DirectoryInfo(origin);
                        for (var up = 0; up < 6 && probe != null && recovered == null; up++, probe = probe.Parent)
                        {
                            var cand = Path.Combine(probe.FullName, "Input", fileName);
                            if (File.Exists(cand)) recovered = cand;
                        }
                        if (recovered != null) break;
                    }
                    if (recovered == null)
                    {
                        log($"[IoBindings][ERROR] IO-bindings file '{fileName}' NOT FOUND at {path} and no project copy under any parent Input\\ folder. " +
                            "Actuator athome/atwork/coil channels will NOT bind — the generated HCF will be blank for Feeder/Checker/Transfer and NOTHING WILL TRIGGER. " +
                            "Restore Input\\" + fileName + " (from MapperUI\\MapperUI\\Input or MapperTests\\TestData) and regenerate.");
                        return null;
                    }
                    try
                    {
                        File.Copy(recovered, path, overwrite: true);
                        log($"[IoBindings] bin copy was missing — self-healed from {recovered}");
                    }
                    catch (Exception cx)
                    {
                        log($"[IoBindings] using project copy {recovered} (could not restore bin copy: {cx.Message})");
                        path = recovered;
                    }
                }
                var bindings = IoBindingsLoader.LoadBindings(path);
                log($"[IoBindings] Loaded {bindings.Actuators.Count} actuator + {bindings.Sensors.Count} sensor binding(s) from {path}");
                return bindings;
            }
            catch (Exception ex)
            {
                log($"[IoBindings] Failed to load: {ex.Message}");
                return null;
            }
        }

        // A relative IoBindingsPath used to be resolved against whatever the host happened to expose:
        // the UI used AppContext.BaseDirectory, the runner Environment.CurrentDirectory. Hosting the same
        // generation anywhere else then produced a blank Feed HCF and a rig where nothing triggers, with
        // no error. First existing wins, so every host resolves the same file.
        static string ResolveBindingsPath(string configured)
        {
            if (Path.IsPathRooted(configured)) return configured;
            foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                var candidate = Path.Combine(origin, configured);
                if (File.Exists(candidate)) return candidate;
            }
            return Path.Combine(AppContext.BaseDirectory, configured);
        }

        static void LogCleanup(SystemInjector.CleanupReport report, Action<string> log)
        {
            log($"[Cleanup] Removed {report.RemovedFbs.Count} universal FB(s), {report.RemovedConnections} connection(s)");
            foreach (var name in report.RemovedFbs) log($"  - {name}");
            log($"[Cleanup] Preserved {report.PreservedFbs.Count} non-universal FB(s)");
            foreach (var name in report.PreservedFbs) log($"  + {name}");
            foreach (var line in report.DeviceCleanupLog) log(line);
        }

        // report.Missing is the binders' general diagnostic channel, not a list of unbound components.
        static void LogBindings(SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            foreach (var (comp, detail) in report.Bound) log($"[IoBindings] {comp} bound: {detail}");
            foreach (var line in report.Missing) log($"[IoBindings] {line}");
            if (report.Bound.Count > 0)
            {
                log("[IoBindings] Symlink override via nested FB is invalid IEC 61499; PLC_RW_M262 variables must be renamed to match $${PATH} expansion: " +
                    "PusherAtHome to Pusher.athome, PusherAtWork to Pusher.atwork, ExtendPusher to Pusher.OutputToWork, Hopper to PartInHopper.Input. " +
                    "This is a one-time manual edit in PLC_RW_M262.fbt and is not Mapper's job.");
            }
        }

        static void LogNew(SystemInjector.BindingApplicationReport report, int fromIndex,
            Func<string, bool> keep, Action<string> log)
        {
            for (var i = fromIndex; i < report.Missing.Count; i++)
                if (keep(report.Missing[i])) log(report.Missing[i]);
        }

        // MANDATORY after the syslay is (re)written: Prepare cleans the .sysres, wiping the earlier FB
        // mirror, so without this the .sysres ends up empty and every FB is unmapped on the EAE canvas.
        static void FinalizeDeviceStack(GenerationContext ctx, Action<string> log)
        {
            var cfg = ctx.Config;
            var m262DeviceExists = false;
            try { m262DeviceExists = M262SysdevEmitter.M262SysdevAlreadyExists(cfg); } catch { }

            var sysdevId = string.Empty;
            try
            {
                var sysdev = M262SysdevEmitter.Emit(ctx);
                if (sysdev.DevicePreserved)
                {
                    log("[Device] M262 sysdev exists, skipping device creation and config writes to preserve trust binding");
                    log($"[M262] .sysres mirrored {sysdev.SysresFbsMirrored} FB(s) to {sysdev.SysresPath} (application-layer only)");
                }
                else
                {
                    log($"[M262] sysdev re-emitted; .sysres mirrored {sysdev.SysresFbsMirrored} FBs to {sysdev.SysresPath}");
                }
                sysdevId = ReadSysdevId(sysdev.SysdevPath);
            }
            catch (Exception ex) { log($"[M262][Error] sysdev emit: {ex.Message}"); }

            // Topology always runs: solutionData (trust) is preserved, but Equipment JSON (placement)
            // MUST be re-written every run or the M262dPAC never re-appears after a wipe.
            try
            {
                if (!string.IsNullOrEmpty(sysdevId))
                {
                    var topo = M262TopologyEmitter.Emit(cfg, sysdevId);
                    log($"[M262] topology emitted: {topo.FilesWritten.Count} JSON file(s), {topo.TopologyProjEntriesAdded} topologyproj entries added");
                    if (m262DeviceExists) log("[Device] solutionData preserved (existing trust binding kept intact)");
                    foreach (var w in topo.Warnings) log($"[M262][Warn] topology: {w}");
                }
                else
                {
                    log("[M262][Warn] topology emit skipped — sysdevId was empty");
                }
            }
            catch (Exception ex) { log($"[M262][Error] topology emit: {ex.Message}"); }

            if (m262DeviceExists) log("[Device] M262 sysdev preserved (trust binding intact)");
        
            // Partial swap: the RevPi coexists WITH M262 — emit its Soft_dPAC device AFTER the M262 device
            // so the System GUID folder exists. M262 is not swept in partial mode.
            if (ctx.Profile.PartialRevPi)
            {
                try
                {
                    var rr = new SystemInjector.BindingApplicationReport();
                    RevPiDeviceEmitter.EmitDevice(ctx, rr);
                    foreach (var m in rr.Missing) log(m);
                }
                catch (Exception ex) { log($"[RevPi][Error] partial device emit: {ex.Message}"); }
            }

            try
            {
                var stn2 = Station2DeviceEmitter.EmitAll(cfg);
                log($"[Stn2] {stn2.FilesWritten.Count} file(s) written, " +
                    $"{stn2.TopologyProjEntriesAdded} topologyproj entries, " +
                    $"{stn2.DfbprojEntriesAdded} dfbproj entries");
                foreach (var f in stn2.FilesWritten) log($"[Stn2]   {f}");
                foreach (var w in stn2.Warnings) log($"[Stn2][Warn] {w}");
            }
            catch (Exception ex) { log($"[Stn2][Error] Station 2 emit: {ex.Message}"); }

            // EAE caches the OLD layout per device and surfaces stale "2 instances of EMB_RES_ECO"-class
            // errors its own Clean does not flush. Runs early so later emitters write into a clean cache.
            try
            {
                var purge = CompileCachePurger.Purge(cfg);
                if (purge.FoldersRemoved > 0 || purge.SnapshotReset)
                    log($"[Topology] compile cache purged: {purge.FoldersRemoved} folder(s), snapshot reset={purge.SnapshotReset}");
                foreach (var w in purge.Warnings) log($"[Topology][Warn] cache purge: {w}");
            }
            catch (Exception ex) { log($"[Topology][Error] cache purge: {ex.Message}"); }

            // A sysdev not listed in Folders.xml is silently dropped from EAE's Solution Explorer and
            // Deploy & Diagnostic.
            try
            {
                var fx = FoldersXmlEmitter.Register(cfg, partialRevPi: ctx.Profile.PartialRevPi);
                if (fx.ItemsAdded > 0) log($"[Topology] Folders.xml: registered {fx.ItemsAdded} sysdev GUID(s)");
                foreach (var w in fx.Warnings) log($"[Topology][Warn] Folders.xml: {w}");
            }
            catch (Exception ex) { log($"[Topology][Error] Folders.xml register: {ex.Message}"); }

            try
            {
                var bd = BroadcastDomainEmitter.Emit(cfg);
                foreach (var f in bd.FilesWritten) log($"[Topology]   {f}");
                foreach (var w in bd.Warnings) log($"[Topology][Warn] {w}");
            }
            catch (Exception ex) { log($"[Topology][Error] BroadcastDomain emit: {ex.Message}"); }

            // The BX1 Equipment binds a non-default domain; without its BroadcastDomain JSON on disk EAE
            // rejects the whole topology import.
            try
            {
                var dom = BroadcastDomainEmitter.EnsureReferencedDomains(cfg);
                foreach (var f in dom.FilesWritten) log($"[Topology]   {f}");
                foreach (var w in dom.Warnings) log($"[Topology] {w}");
            }
            catch (Exception ex) { log($"[Topology][Error] domain consistency: {ex.Message}"); }

            try
            {
                var eae = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                if (!string.IsNullOrEmpty(eae))
                {
                    var dfb = Path.Combine(eae, "IEC61499", "IEC61499.dfbproj");
                    var n = DfbprojRegistrar.StripStaleSysresStemEntries(dfb, eae);
                    log($"[Topology] stripped {n} stale dfbproj sysres-stem entries");
                }
            }
            catch (Exception ex) { log($"[Topology][Error] dfbproj stem sweep: {ex.Message}"); }

            // Runs AFTER Station2DeviceEmitter so the Equipment UUIDs the wires reference are on disk.
            try
            {
                var net = TopologyNetworkEmitter.Emit(ctx);
                log($"[Topology] {net.FilesWritten.Count} network file(s) written, {net.TopologyProjEntriesAdded} topologyproj entries");
                foreach (var f in net.FilesWritten) log($"[Topology]   {f}");
                foreach (var w in net.Warnings) log($"[Topology][Warn] {w}");
            }
            catch (Exception ex) { log($"[Topology][Error] network emit: {ex.Message}"); }

            // The HMI PROJECT is generated with the syslay; its DEPLOYMENT must wait until here,
            // because the panel binds to the broadcast domain and the switch the topology emitters
            // have only just written. Both are resolved from those files rather than restated.
            CodeGen.Hmi.HmiGenerator.EmitDeployment(ctx);

            // Runs AFTER the sysdev/sysres shells exist, else the .sysres stay empty and EAE reports
            // "Repair Instances" / "Missing Project Files".
            try
            {
                var s2 = Station2SysresMirror.EmitStation2Sysres(ctx);
                log($"[Stn2] mirrored FBs → M580:{s2.M580} BX1:{s2.BX1}");
            }
            catch (Exception ex) { log($"[Stn2][Error] sysres mirror: {ex.Message}"); }

            try
            {
                var eae = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                if (!string.IsNullOrEmpty(eae))
                {
                    var n = OpcuaCompanionEmitter.EnsureOpcuaInAllResourceFolders(eae);
                    log($"[Artefacts] opcua.xml companions ensured: {n} created");
                }
            }
            catch (Exception ex) { log($"[Artefacts][Error] opcua sweep: {ex.Message}"); }

            {
                try
                {
                    var hcf = M262HwConfigCopier.Copy(cfg);
                    log($"[M262] hcf re-patched; {hcf.ParametersOverwritten.Count} channel symlink(s) written");
                    foreach (var w in hcf.Warnings) log($"[M262][Warn] {w}");
                }
                catch (Exception ex) { log($"[M262][Error] hcf patch: {ex.Message}"); }
            }

            // Authoritative final pass: copies the authored .hcf verbatim (re-rooted with the resource ID)
            // so it refills even after a wipe. Channel symlinks preserved byte-for-byte.
            try
            {
                var hcf = M580HwConfigCopier.Copy(cfg);
                log($"[M580] hcf deployed; {hcf.FilesCopied} file(s) copied → {hcf.HcfPath}");
                foreach (var w in hcf.Warnings) log($"[M580][Warn] {w}");
            }
            catch (Exception ex) { log($"[M580][Error] hcf deploy: {ex.Message}"); }

            try
            {
                var hcf = BX1HwConfigCopier.Copy(cfg);
                log($"[BX1] hcf deployed; {hcf.FilesCopied} file(s) copied → {hcf.HcfPath}");
                foreach (var w in hcf.Warnings) log($"[BX1][Warn] {w}");
            }
            catch (Exception ex) { log($"[BX1][Error] hcf deploy: {ex.Message}"); }

            // After a wipe the dfbproj can still reference EAE-owned compile artifacts deleted with the
            // device folders. EAE regenerates them on Build; never touches the device files.
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                if (!string.IsNullOrEmpty(eaeRoot))
                {
                    var stripped = DfbprojRegistrar.StripDanglingResourceArtifactEntries(eaeRoot);
                    if (stripped > 0)
                        log($"[Device] stripped {stripped} dangling compile-artifact ref(s) from the dfbproj " +
                            "(EAE regenerates them on Build) — Solution Integrity clean");
                }
            }
            catch (Exception ex) { log($"[Device][Warn] dfbproj artifact-ref cleanup: {ex.Message}"); }
        }

        static string ReadSysdevId(string sysdevPath)
        {
            try { return (string?)XDocument.Load(sysdevPath).Root?.Attribute("ID") ?? string.Empty; }
            catch { return string.Empty; }
        }

        // Event + data wires into the Feed sysres FBNetwork (init chain, adapter wires, I/O bindings);
        // without these EAE deploys but nothing inits.
        static void WireFeedResource(GenerationContext ctx, SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            var before = report.Missing.Count;
            M262SysresWireEmitter.Emit(ctx, report);
            if (ctx.Profile.PartialRevPi) RevPiDeviceEmitter.WireResource(ctx, report);
            LogNew(report, before, l => l.StartsWith("[Wire]") || l.StartsWith("[Sysres"), log);
        }

        // The mirror MUST run before the wiring: it creates the FBs the wiring connects. Re-mirroring also
        // re-syncs each component's CAT type every run; a stale Type trips "Found References to Missing
        // Instances".
        static void MirrorAndWireStation2(GenerationContext ctx, SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            try
            {
                var s2m = Station2SysresMirror.EmitStation2Sysres(ctx);
                log($"[Stn2] re-mirrored FBs -> M580:{s2m.M580} BX1:{s2m.BX1} (CAT types synced to syslay)");
            }
            catch (Exception ex) { log($"[Stn2][Error] sysres mirror: {ex.Message}"); }

            try
            {
                var before = report.Missing.Count;
                Station2WireEmitter.EmitStation2Resources(ctx, report);
                LogNew(report, before, l =>
                    l.StartsWith("[Wire][M580]") || l.StartsWith("[M580]") ||
                    l.StartsWith("[Wire][BX1]") || l.StartsWith("[BX1]") ||
                    l.StartsWith("[Wire][Stn2]"), log);
            }
            catch (Exception ex) { log($"[Wire][Stn2][Error] {ex.Message}"); }
        }

        // Instantiates BX1_IO (PLC_RW_BX1) so the .hcf's EIP_Input/Output_Word_1 symlinks resolve. Runs
        // after the Station-2 mirror/wire so the cover FBs exist.
        static void InjectBx1Broker(GenerationContext ctx, string syslayPath,
            SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            if (!ctx.Config.DeployBx1IoBroker) return;
            try
            {
                var n = Bx1IoBrokerInjector.InjectBx1IoBroker(ctx.Config, syslayPath, report);
                log($"[BX1][Broker] BX1_IO injected into {n} artefact(s).");
                ResourceWireEmitter.ApplyLayoutToSyslay(ctx, syslayPath, report);
            }
            catch (Exception ex) { log($"[BX1][Broker][Error] {ex.Message}"); }
        }

        static void PatchAndBindHcf(GenerationContext ctx, string path, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            var cfg = ctx.Config;
            var hcfBefore = report.Missing.Count;
            HcfPatchService.PatchDeployed(ctx, path, bindings, report);
            LogNew(report, hcfBefore, l => l.StartsWith("[Hcf]"), log);

            // Station 2 symbol binding only: the IO-bindings xlsx has no Station-2 pin rows, so each
            // symlink is unquoted and its resource head re-aligned to the deployed sysres ID.
            try
            {
                var bindBefore = report.Missing.Count;
                M580SymbolBinder.BindM580(cfg, report);
                BX1SymbolBinder.BindBX1(cfg, report);
                LogNew(report, bindBefore, l => l.StartsWith("[HcfBind]"), log);
            }
            catch (Exception ex) { log($"[HcfBind][Error] {ex.Message}"); }
        }

        // Every deployed .hcf DI/DO binding MUST resolve to an FB + pin on the SAME resource's sysres; a
        // legacy symbolic name or a GUID triple pointing at an absent FB never resolves.
        static void ValidateHcfReferences(MapperConfig cfg, Action<string> log)
        {
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var violations = HcfReferenceValidator.Validate(eaeRoot);
                if (violations.Count == 0)
                {
                    log("[Hcf][Validate] PASS — every DI/DO binding resolves to a sysres FB + pin (no split-brain).");
                }
                else
                {
                    log($"[Hcf][Validate] FAIL — {violations.Count} split-brain HCF binding(s) reference an FB/pin NOT on the resource:");
                    foreach (var v in violations) log($"  [Hcf][SPLIT-BRAIN] {v}");
                }
            }
            catch (Exception ex) { log($"[Hcf][Validate][Error] {ex.Message}"); }
        }

        // Every generated connection names endpoints that exist and leaves each input driven once. Both
        // failures are silent in EAE — an unresolvable wire is dropped, a double-driven input resolves by
        // evaluation order — so they surface here or not at all.
        static void ValidateConnections(MapperConfig cfg, Action<string> log)
        {
            var violations = ConnectionIntegrityValidator.Validate(EaeProjectLayout.DeriveEaeProjectRoot(cfg));
            if (violations.Count == 0)
            {
                log("[Connections] PASS — every endpoint resolves and no input carries two sources.");
                return;
            }
            foreach (var v in violations) log($"  [Connections][FAIL] {v}");
            throw new InvalidOperationException(
                $"[Connections] {violations.Count} connection defect(s) in the generated project; " +
                "the first is: " + violations[0]);
        }

        static void SyncSysresParameters(MapperConfig cfg, string path, Action<string> log)
        {
            try
            {
                var synced = RuntimeArtifactVerifier.SyncMappedSysresParametersFromSyslay(path, cfg, log);
                if (synced > 0) log($"[Test Runtime] final sysres parameter sync: {synced} mapped FB(s).");
            }
            catch (Exception ex) { log($"[Test Runtime][Sync][Warn] final sysres parameter sync failed: {ex.Message}"); }
        }

        // The device-create + .hcf-id-realignment path can leave an empty "RES0" sysres under the OLD id.
        // Runs LATE so each device folder ends with exactly its live resource.
        static void SweepOrphans(MapperConfig cfg, Action<string> log)
        {
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var swept = EaeProjectLayout.SweepOrphanSysres(eaeRoot, log);
                if (swept > 0)
                    log($"[Sysres][Sweep] removed {swept} orphan .sysres shell(s); EAE refreshes obj/System.hash on the next Build.");
                var deduped = EaeProjectLayout.DedupeSysdevResources(eaeRoot, log);
                if (deduped > 0)
                    log($"[Sysdev][Dedupe] removed {deduped} extra <Resource> entry(ies); each device now declares exactly one resource.");
            }
            catch (Exception ex) { log($"[Sysres][Sweep][Warn] orphan sysres sweep failed: {ex.Message}"); }

            // The sweep deletes a stale .sysres FILE but not its dfbproj entry, which then dangles.
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                if (string.IsNullOrEmpty(eaeRoot)) return;
                var dfbStrip = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
                var strippedLate = DfbprojRegistrar.StripStaleSysresStemEntries(dfbStrip, eaeRoot);
                if (strippedLate > 0)
                    log($"[Sysres][Strip] removed {strippedLate} dangling .dfbproj sysres reference(s) after the orphan sweep " +
                        "(the realigned-away device default id, e.g. BX1 117867…).");
            }
            catch (Exception ex) { log($"[Sysres][Strip][Warn] post-sweep dfbproj strip failed: {ex.Message}"); }

            // The centre-home CAT's two work-to-home E_DELAY timers feed only ReturnToHomeHandler events
            // the No_Sensor ECC ignores.
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var timerStripped = EaeProjectLayout.StripStaleHomeTimerParams(eaeRoot, log);
                if (timerStripped > 0)
                    log($"[Sysres][TimerStrip] removed {timerStripped} stale work1/work2ToHomeTime param(s) " +
                        "from the centre-home swivel sysres (dead timers; values no longer emitted).");
            }
            catch (Exception ex) { log($"[Sysres][TimerStrip][Warn] home-timer param strip failed: {ex.Message}"); }
        }

        // HARD guard: the deployable sysres MUST be a faithful projection of the syslay, else EAE deploys
        // the OLD logic silently. A FAIL usually means EAE held a sysres locked during the sync.
        static void ValidateParity(GenerationContext ctx, string path, Action<string> log)
        {
            var cfg = ctx.Config;
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var parity = SyslaySysresParityValidator.Validate(ctx, eaeRoot, path);
                if (parity.Count == 0)
                {
                    log("[Parity] PASS — every device sysres mirrors the syslay (FBs + recipes + discharge hcf).");
                    return;
                }

                log($"[Parity] FAIL — {parity.Count} sysres/hcf divergence(s) from the syslay (the deployable LAGS the design):");
                foreach (var v in parity) log($"  [Parity][DIVERGENCE] {v}");

                var resynced = RuntimeArtifactVerifier.SyncMappedSysresParametersFromSyslay(path, cfg, log);
                if (resynced > 0)
                    log($"[Parity] retry sync updated {resynced} mapped sysres FB parameter set(s); re-validating.");

                parity = SyslaySysresParityValidator.Validate(ctx, eaeRoot, path);
                if (parity.Count == 0)
                {
                    log("[Parity] PASS after retry sync — deployable sysres now mirrors the syslay.");
                }
                else
                {
                    foreach (var v in parity) log($"  [Parity][STILL-DIVERGED] {v}");
                    throw new InvalidOperationException(
                        "Generated deployable sysres still lags the syslay. Close EAE, rerun Generate IEC61499 Code, and do not deploy this stale tree.");
                }
            }
            catch (Exception ex)
            {
                log($"[Parity][Error] {ex.Message}");
                throw;
            }
        }

        // An IP collision is only visible ACROSS emitters — the RevPi device and the HMI panel are written
        // by unrelated paths into the SAME broadcast domain — so no single-file check can catch it. A
        // duplicated Soft dPAC CONTAINER address is fatal (it is EAE's deploy/login target); duplicated
        // host NICs are reported but tolerated, because the shipped tree and the reference both carry one.
        static void ValidateAddresses(GenerationContext ctx, Action<string> log)
        {
            var cfg = ctx.Config;
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var addr = TopologyAddressValidator.Validate(eaeRoot)
                    .Concat(TopologyAddressValidator.ValidateRevPiRoles(cfg, ctx.Profile)).ToList();
                var errors = addr.Where(v => v.IsError).ToList();
                foreach (var v in addr) log($"  [Addr] {v}");
                if (addr.Count == 0)
                    log("[Addr] PASS — every topology endpoint owns its address within its broadcast domain.");
                if (errors.Count > 0)
                    throw new InvalidOperationException(
                        $"Generated topology has {errors.Count} fatal address collision(s). Two endpoints " +
                        "cannot share one address in a broadcast domain — fix Config/device.yml and re-generate.");
            }
            catch (Exception ex)
            {
                log($"[Addr][Error] {ex.Message}");
                throw;
            }
        }

        static void ValidateMqtt(MapperConfig cfg, Action<string> log)
        {
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var (rows, findings) = MqttConnectionValidator.Inspect(eaeRoot);
                foreach (var r in rows)
                    log($"[MQTT] {r.Resource}.{r.Fb}: URL={r.Url} ConnectionID={r.ConnectionID} " +
                        $"ClientIdentifier={r.ClientIdentifier} ValidateCert={r.ValidateCert}");
                foreach (var f in findings) log($"[MQTT] {f}");
                if (findings.Any(f => f.Impossible))
                    log("[MQTT] IMPOSSIBLE config flagged — it will NOT reach ReturnCode 0. Fix the URL/mode.");
            }
            catch (Exception ex) { log($"[MQTT][Error] {ex.Message}"); }
        }

        // SAFETY: the BX1 cover safe-start reaches the TM3BC coupler only if the scanner carries the .210
        // device; warn loudly if it is missing or EIPSCANNER2.xml is empty.
        static void ValidateBx1Scanner(MapperConfig cfg, Action<string> log)
        {
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                if (string.IsNullOrEmpty(eaeRoot)) return;
                var scan = Bx1ScannerValidator.Validate(eaeRoot);
                foreach (var l in scan.Lines) log(l);
                if (scan.Fatal)
                    log("[BX1][Scanner] FATAL — the BX1 EtherNet/IP scanner would compile " +
                        "EMPTY; the cover I/O and the CoverPNP_Hr safe-start cannot reach the coupler. " +
                        "DO NOT deploy until fixed.");
            }
            catch (Exception ex) { log($"[BX1][Scanner][Error] {ex.Message}"); }
        }

        static void TouchDfbproj(MapperConfig cfg, Action<string> log)
        {
            try
            {
                var dfbproj = FindDfbproj(cfg.SyslayPath2);
                if (dfbproj != null && File.Exists(dfbproj))
                {
                    File.SetLastWriteTime(dfbproj, DateTime.Now);
                    log($"[EAE] Touched {Path.GetFileName(dfbproj)} to trigger Reload Solution prompt.");
                }
                else
                {
                    log("[EAE] .dfbproj not found; EAE will not auto-detect external changes. Use File > Reload Solution.");
                }
            }
            catch (Exception ex) { log($"[EAE] Failed to touch .dfbproj: {ex.Message}"); }
        }

        static string? FindDfbproj(string startPath)
        {
            var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
            while (dir != null)
            {
                var file = Directory.GetFiles(dir, "*.dfbproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (file != null) return file;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }
    }
}
