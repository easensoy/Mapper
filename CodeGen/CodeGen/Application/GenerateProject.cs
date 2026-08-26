using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Artefacts;
using CodeGen.Devices.BX1;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Devices.RevPi;
using CodeGen.Validation.Plan;
using System.Diagnostics;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Devices.Core;
using CodeGen.Devices;
using CodeGen.Services;
using CodeGen.Translation;
using CodeGen.Validation.Output;

namespace CodeGen.Application
{
    // Immutable: the orchestration never writes back, so a generation step cannot mutate a caller's inputs.
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

    // The one generation path from Control.xml to EAE artefacts. Synchronous: `log` runs on the caller's thread.
    public static class GenerateProject
    {
        // THE COMPOSITION ROOT for target backends: the one place in the generator that names a
        // concrete device family. Everything else asks TargetRegistry for the SET, so no planner,
        // emitter or validator has to know which controllers exist.
        //
        // A KIND is implemented once here. WHICH targets exist, which kind emits each, and the order a
        // run drives them are all declared in device.yml - so a second controller of a kind that
        // already has a backend is a row in that file and no change here at all. Genuinely new
        // hardware earns one entry.
        private static readonly IReadOnlyDictionary<string, Func<PlcAssignment, CodeGen.Devices.ITargetBackend>>
            BackendKinds = new Dictionary<string, Func<PlcAssignment, CodeGen.Devices.ITargetBackend>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["M262"] = t => new CodeGen.Devices.M262.M262Backend(t),
                ["M580"] = t => new CodeGen.Devices.M580.M580Backend(t),
                ["BX1"] = t => new CodeGen.Devices.BX1.Bx1Backend(t),
                ["RevPi"] = t => new CodeGen.Devices.RevPi.RevPiBackend(t),
            };

        // One backend per DECLARED target, in the declared drive order. A kind nothing implements and a
        // drive order that does not name every target both stop the run here, before anything is
        // planned - a target silently left undriven emits no device and the run still reports success.
        public static CodeGen.Devices.ITargetBackend[] Backends()
        {
            var declared = Configuration.DeviceConfig.Current;
            var byPlc = declared.Targets.ToDictionary(t => t.Plc);
            var order = declared.BackendEmitOrder;

            var errors = new List<string>();
            foreach (var t in declared.Targets)
                if (!BackendKinds.ContainsKey(t.BackendKind ?? string.Empty))
                    errors.Add($"target '{t.Plc}' declares backendKind '{t.BackendKind}', which no backend " +
                               $"implements. Implemented kinds: {string.Join(", ", BackendKinds.Keys)}");
            foreach (var plc in order)
                if (!byPlc.ContainsKey(plc))
                    errors.Add($"backendEmitOrder names '{plc}', which device.yml declares no target for");
            foreach (var t in declared.Targets)
                if (!order.Contains(t.Plc))
                    errors.Add($"backendEmitOrder omits target '{t.Plc}', so nothing would emit its device");
            if (order.Count != order.Distinct().Count())
                errors.Add("backendEmitOrder names a target twice, so its device would be emitted twice");
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "device.yml backend declarations are inconsistent:" + Environment.NewLine +
                    "  - " + string.Join(Environment.NewLine + "  - ", errors));

            return order.Select(plc => BackendKinds[byPlc[plc].BackendKind](plc)).ToArray();
        }

        public static GenerationResult Execute(GenerationRequest request, Action<string> log)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (log is null) throw new ArgumentNullException(nameof(log));

            // ONE SESSION for this run: the declarations, the plan and the backends that will emit
            // it, decided here and handed down. Nothing below reaches a global for any of them.
            var session = CompilerSession.Begin(
                Configuration.CompilerConfiguration.Load(request.Config.Clone()), Backends());

            // Work on a copy: MapperUI keeps one config for the process lifetime, so mutation leaks between runs.
            // THE CONFIGURATION COMPOSITION ROOT. Every declaration file is read ONCE, here, and one
            // snapshot travels through planning, validation and rendering. No stage below re-reads a
            // declaration, so a file saved while a run is in flight cannot make two stages of that run
            // compile against different configurations.

            // THE TRANSACTION BOUNDARY. Everything below writes into a staging copy of the project on
            // the same volume; the live tree is replaced only after every output validator has passed.
            // A throw anywhere - a model the compiler cannot express, a failed patch, a validator that
            // refuses - unwinds here and leaves the previous project exactly as it was.
            using var txn = ProjectTransaction.Begin(session.Cfg, log);
            session = session.With(txn.Configuration);
            var cfg = session.Cfg;

            // THE BOUNDARY. The request carries the selection the UI and the VueOne runner have always
            // sent - a set of component names bound for the relocation target. It becomes a generic
            // component -> target assignment here, and nothing below this line names a controller.
            var profile = DeploymentProfile.Relocating(request.RevPiComponents, cfg);

            // Plan before the first artefact is written, so an inexpressible model fails with a diagnostic.
            var ctx = GenerationContext.Plan(cfg, request.ControlXmlPath, profile);

            // Whether a target can actually serve what this run assigned to it is that TARGET's answer:
            // its own hardware decides. Still before anything is written - planning touches no file.
            foreach (var backend in session.Backends) backend.ValidateAssignment(ctx);

            // A dropped message and a connection nothing opens both look exactly like success, so the
            // telemetry declaration is proved against the planned resources here rather than on the rig.
            TelemetryPlanValidator.Validate(ctx);

            // And the template cfg are proved against the archives that ship the types. Still
            // before anything is written: a drifted declaration must not cost the previous project.
            PortNameValidator.AssertContractMatchesArchives(cfg.Paths.TemplateLibraryPath);

            DeepClean(cfg, log);
            LogFeedSysdevState(cfg, log);

            var injector = new SystemInjector();
            foreach (var finding in ctx.SemanticFindings) log($"[Semantics] {finding}");
            LogCleanup(Artefacts.DemonstratorPreparer.PrepareDemonstratorForGeneration(cfg), log);

            // AFTER cleanup: cleanup deletes flat Basic FB files, so deploying first would drop the patched core.
            DeployTemplates(ctx, log);

            var bindings = LoadBindings(cfg, log);
            var path = injector.EmitApplicationLayer(ctx, bindings, out var report);
            LogBindings(report, log);

            FinalizeDeviceStack(session, ctx, log);
            WireResources(session, ctx, report, log);
            // Anything a target must do to the canvas once every resource is wired.
            foreach (var backend in session.Backends)
                backend.FinishApplication(ctx, path, report, log);
            BindHardware(session, ctx, bindings, report, log);

            ValidateHcfReferences(cfg, log);
            SyncSysresParameters(cfg, path, log);
            SweepOrphans(cfg, log);
            ValidateParity(ctx, path, log);
            ValidateConnections(cfg, log);
            ValidateAddresses(ctx, log);
            ValidateMqtt(cfg, log);
            foreach (var backend in session.Backends) backend.ValidateOutput(ctx, log);

            // LAST, against the finished staged tree: everything EAE would reject on import that can be
            // answered without EAE. A required stage, so a project that would not load never publishes.
            try
            {
                var (registrations, types) = Validation.Output.ProjectIntegrityValidator.Validate(cfg);
                log($"[Integrity] {registrations} project registration(s) resolve, {types} referenced type(s) deployed");
            }
            catch (Exception ex) { StageFailed("project integrity", StageKind.Required, ex, log); }

            TouchDfbproj(cfg, log);

            // Every validator passed against the staged tree, so it is fit to become the project.
            var published = txn.Commit(path);
            log($"Generated: {published}");
            return new GenerationResult(published, report);
        }


        // WHETHER A STAGE MAY FAIL IS DECLARED, NOT INFERRED FROM HOW ITS MESSAGE READS.
        //
        // A REQUIRED stage produces part of the project itself: topology, a resource, a sysres, an HCF
        // binding, a connection, MQTT, or a registration that makes EAE load what was written. When one
        // of those fails the tree is incomplete, and logging "[Error]" and carrying on to print
        // "Generated:" is how a partial project reaches a controller. It aborts, and the transaction
        // leaves the previous project in place.
        //
        // An OPTIONAL stage produces something EAE regenerates for itself or that only affects
        // convenience. Its failure is reported and the run continues.
        internal enum StageKind { Required, Optional }

        // Thrown so the stage that failed is part of the diagnostic rather than buried in a message.
        internal sealed class GenerationStageException : Exception
        {
            public string Stage { get; }
            public GenerationStageException(string stage, Exception cause)
                : base($"[Generate] the required stage '{stage}' failed: {cause.Message} — the project " +
                       "would be incomplete, so generation ABORTED and the previous project is unchanged.",
                       cause)
                => Stage = stage;
        }

        static void StageFailed(string stage, StageKind kind, Exception ex, Action<string> log)
        {
            if (kind == StageKind.Required) throw new GenerationStageException(stage, ex);
            log($"[Generate][Optional] '{stage}' did not run: {ex.Message}");
        }

        // The root is DERIVED from the configured project, so a retargeted output root cleans itself.
        static void DeepClean(Configuration.CompilerConfiguration cfg, Action<string> log)
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

        // An existing .sysdev is preserved to keep its trust binding, an absent one is bootstrapped.
        static void LogFeedSysdevState(Configuration.CompilerConfiguration cfg, Action<string> log)
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
                StageFailed("template deploy", StageKind.Required, ex, log);
                throw;
            }
        }

        // A missing workbook leaves every actuator channel unbound, so self-heal or fail loudly.
        static IoBindings? LoadBindings(Configuration.CompilerConfiguration cfg, Action<string> log)
        {
            try
            {
                var path = ResolveBindingsPath(cfg.Paths.IoBindingsPath);
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
                        // Not a warning: without these the actuator athome/atwork/coil channels bind
                        // to nothing, the HCF is emitted blank, and the project deploys and then does
                        // not move. That is indistinguishable from a wiring fault on the rig, so the
                        // run stops here instead of shipping it.
                        throw new FileNotFoundException(
                            $"[IoBindings] '{fileName}' was not found at '{path}', and no project " +
                            "copy exists under any parent Input folder. Every actuator channel would " +
                            "bind to nothing and the generated HCF would be blank, so the project " +
                            "would deploy and then do nothing. Restore the workbook under Input and " +
                            "generate again. Generation ABORTED.", path);
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
                StageFailed("IO bindings load", StageKind.Required, ex, log);
                return null;
            }
        }

        // First existing wins, so every host resolves a relative path to the same file.
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

        static void LogCleanup(Artefacts.DemonstratorPreparer.CleanupReport report, Action<string> log)
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

        // MANDATORY after the syslay is written: Prepare wiped the FB mirror, so skipping this leaves the
        // .sysres empty and every FB unmapped on the EAE canvas.
        static void FinalizeDeviceStack(CompilerSession session, GenerationContext ctx, Action<string> log)
        {
            var cfg = ctx.Cfg;

            // Every registered backend, in registration order: a device whose System folder another one
            // creates has to come after it, and that order is declared there rather than written here.
            var scope = DeviceScope.Open(cfg);
            foreach (var backend in session.Backends) backend.EmitDevice(ctx, scope, log);
            foreach (var w in Station2DeviceEmitter.StripStaleSysresEntries(scope.EaeRoot).Warnings)
                log($"[Device][Warn] {w}");

            // EAE's own Clean does not flush its per-device cache; run early so later emitters write clean.
            try
            {
                var purge = CompileCachePurger.Purge(cfg);
                if (purge.FoldersRemoved > 0 || purge.SnapshotReset)
                    log($"[Topology] compile cache purged: {purge.FoldersRemoved} folder(s), snapshot reset={purge.SnapshotReset}");
                foreach (var w in purge.Warnings) log($"[Topology][Warn] cache purge: {w}");
            }
            catch (Exception ex) { StageFailed("EAE compile-cache purge", StageKind.Optional, ex, log); }

            // A sysdev missing from Folders.xml is silently dropped from EAE's Solution Explorer and Deploy.
            try
            {
                var fx = FoldersXmlEmitter.Register(cfg, partialRevPi: ctx.Profile.HasAssignments);
                if (fx.ItemsAdded > 0) log($"[Topology] Folders.xml: registered {fx.ItemsAdded} sysdev GUID(s)");
                if (fx.ItemsRemoved > 0) log($"[Topology] Folders.xml: removed {fx.ItemsRemoved} sysdev GUID(s) this run does not emit");
                foreach (var w in fx.Warnings) log($"[Topology][Warn] Folders.xml: {w}");
            }
            catch (Exception ex) { StageFailed("Folders.xml sysdev registration", StageKind.Required, ex, log); }

            try
            {
                var bd = BroadcastDomainEmitter.Emit(cfg);
                foreach (var f in bd.FilesWritten) log($"[Topology]   {f}");
                foreach (var w in bd.Warnings) log($"[Topology][Warn] {w}");
            }
            catch (Exception ex) { StageFailed("topology: broadcast domains", StageKind.Required, ex, log); }

            // BX1 binds a non-default domain; without its BroadcastDomain JSON EAE rejects the whole topology.
            try
            {
                var dom = BroadcastDomainEmitter.EnsureReferencedDomains(cfg);
                foreach (var f in dom.FilesWritten) log($"[Topology]   {f}");
                foreach (var w in dom.Warnings) log($"[Topology] {w}");
            }
            catch (Exception ex) { StageFailed("topology: referenced-domain consistency", StageKind.Required, ex, log); }

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
            catch (Exception ex) { StageFailed("dfbproj: stale sysres-stem sweep", StageKind.Required, ex, log); }

            // Runs AFTER Station2DeviceEmitter so the Equipment UUIDs the wires reference are on disk.
            try
            {
                var net = TopologyNetworkEmitter.Emit(ctx);
                log($"[Topology] {net.FilesWritten.Count} network file(s) written, {net.TopologyProjEntriesAdded} topologyproj entries");
                foreach (var f in net.FilesWritten) log($"[Topology]   {f}");
                foreach (var w in net.Warnings) log($"[Topology][Warn] {w}");
            }
            catch (Exception ex) { StageFailed("topology: network and wires", StageKind.Required, ex, log); }

            // Deployment waits until here: the panel binds to the domain and switch just written above.
            CodeGen.Hmi.HmiGenerator.EmitDeployment(ctx);

            // Runs AFTER the sysdev/sysres shells exist, else they stay empty and EAE reports "Repair Instances".
            try
            {
                var s2 = Station2SysresMirror.EmitStation2Sysres(ctx);
                log("[Stn2] mirrored FBs → " + string.Join(" ", s2.Select(r => $"{r.Plc}:{r.Count}")));
            }
            catch (Exception ex) { StageFailed("sysres: FB mirror", StageKind.Required, ex, log); }

            try
            {
                var eae = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                if (!string.IsNullOrEmpty(eae))
                {
                    var n = OpcuaCompanionEmitter.EnsureOpcuaInAllResourceFolders(eae);
                    log($"[Artefacts] opcua.xml companions ensured: {n} created");
                }
            }
            catch (Exception ex) { StageFailed("opcua.xml companions", StageKind.Optional, ex, log); }

            // The authored hardware configuration for each target, carried in by its own backend.
            foreach (var backend in session.Backends) backend.CopyHardwareConfig(ctx, log);

            // A wipe can leave dfbproj refs to EAE-owned compile artefacts; EAE regenerates them on Build.
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
            catch (Exception ex) { StageFailed("dfbproj: dangling artefact references", StageKind.Required, ex, log); }
        }

        // The mirror MUST precede the wiring: it creates the FBs each resource is about to connect, and
        // re-syncs every CAT type (a stale Type trips "Found References to Missing Instances").
        // Without these wires EAE deploys a resource but nothing inits.
        static void WireResources(CompilerSession session, GenerationContext ctx,
            SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            // The wiring about to run connects the FBs this creates, so a failure here leaves every
            // resource half-built - and a half-built resource still deploys. It ABORTS.
            var mirrored = Station2SysresMirror.EmitStation2Sysres(ctx);
            log("[Resource] re-mirrored FBs -> " +
                string.Join(" ", mirrored.Select(r => $"{r.Plc}:{r.Count}")) +
                " (CAT types synced to the canvas)");

            var before = report.Missing.Count;
            foreach (var backend in session.Backends) backend.WireResource(ctx, report, log);
            LogNew(report, before, _ => true, log);
        }

        // Physical channels bound to the FBs each target hosts.
        static void BindHardware(CompilerSession session, GenerationContext ctx, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report, Action<string> log)
        {
            var before = report.Missing.Count;
            foreach (var backend in session.Backends)
                backend.BindHardware(ctx, bindings, report, log);
            LogNew(report, before, l => l.StartsWith("[Hcf") || l.StartsWith("[HcfBind"), log);
        }

        // Every DI/DO binding must resolve to an FB and pin on the SAME resource's sysres.
        static void ValidateHcfReferences(Configuration.CompilerConfiguration cfg, Action<string> log)
        {
            // FATAL: the project still imports, deploys and reports success while the channel is dead.
            List<HcfReferenceValidator.Violation> violations;
            try
            {
                violations = HcfReferenceValidator.Validate(EaeProjectLayout.DeriveEaeProjectRoot(cfg));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[Hcf][Validate] the generated .hcf bindings could not be checked: {ex.Message}", ex);
            }
            if (violations.Count == 0)
            {
                log("[Hcf][Validate] PASS — every DI/DO binding resolves to a sysres FB + pin (no split-brain).");
                return;
            }
            foreach (var v in violations) log($"  [Hcf][SPLIT-BRAIN] {v}");
            throw new InvalidOperationException(
                $"[Hcf][Validate] {violations.Count} split-brain HCF binding(s) reference an FB/pin that is " +
                "not on the resource; the first is: " + violations[0]);
        }

        // Both failures are silent in EAE: an unresolvable wire is dropped and a double-driven input resolves
        // by evaluation order, so they surface here or not at all.
        static void ValidateConnections(Configuration.CompilerConfiguration cfg, Action<string> log)
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

        static void SyncSysresParameters(Configuration.CompilerConfiguration cfg, string path, Action<string> log)
        {
            try
            {
                var synced = RuntimeArtifactVerifier.SyncMappedSysresParametersFromSyslay(path, cfg, log);
                if (synced > 0) log($"[Test Runtime] final sysres parameter sync: {synced} mapped FB(s).");
            }
            catch (Exception ex) { StageFailed("sysres: final parameter sync", StageKind.Required, ex, log); }
        }

        // .hcf-id realignment can leave an empty sysres under the old id; runs LATE so each device folder
        // ends with exactly its live resource.
        static void SweepOrphans(Configuration.CompilerConfiguration cfg, Action<string> log)
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
            catch (Exception ex) { StageFailed("sysres: orphan sweep", StageKind.Required, ex, log); }

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
            catch (Exception ex) { StageFailed("dfbproj: post-sweep strip", StageKind.Required, ex, log); }

            // The centre-home CAT's work-to-home timers feed only events the No_Sensor ECC ignores.
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var timerStripped = EaeProjectLayout.StripStaleHomeTimerParams(eaeRoot, log);
                if (timerStripped > 0)
                    log($"[Sysres][TimerStrip] removed {timerStripped} stale work1/work2ToHomeTime param(s) " +
                        "from the centre-home swivel sysres (dead timers; values no longer emitted).");
            }
            catch (Exception ex) { StageFailed("sysres: stale home-timer parameters", StageKind.Required, ex, log); }
        }

        // HARD guard: a sysres that is not a faithful projection of the syslay makes EAE deploy the OLD
        // logic silently. A FAIL usually means EAE held a sysres locked during the sync.
        static void ValidateParity(GenerationContext ctx, string path, Action<string> log)
        {
            var cfg = ctx.Cfg;
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
                StageFailed("syslay/sysres parity", StageKind.Required, ex, log);
                throw;
            }
        }

        // An IP collision is only visible ACROSS emitters writing into one broadcast domain, so no single-file
        // check can catch it. A duplicated container address is fatal; duplicated host NICs are tolerated.
        static void ValidateAddresses(GenerationContext ctx, Action<string> log)
        {
            var cfg = ctx.Cfg;
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
                StageFailed("topology address validation", StageKind.Required, ex, log);
                throw;
            }
        }

        static void ValidateMqtt(Configuration.CompilerConfiguration cfg, Action<string> log)
        {
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                var (rows, findings) = MqttConnectionValidator.Inspect(cfg, eaeRoot);
                foreach (var r in rows)
                    log($"[MQTT] {r.Resource}.{r.Fb}: URL={r.Url} ConnectionID={r.ConnectionID} " +
                        $"ClientIdentifier={r.ClientIdentifier} ValidateCert={r.ValidateCert}");
                foreach (var f in findings) log($"[MQTT] {f}");
                if (findings.Any(f => f.Impossible))
                    log("[MQTT] IMPOSSIBLE config flagged — it will NOT reach ReturnCode 0. Fix the URL/mode.");
            }
            catch (Exception ex) { StageFailed("MQTT connection validation", StageKind.Required, ex, log); }
        }

        static void TouchDfbproj(Configuration.CompilerConfiguration cfg, Action<string> log)
        {
            try
            {
                var dfbproj = EaeProjectLayout.FindDfbproj(EaeProjectLayout.DeriveEaeProjectRoot(cfg) ?? string.Empty);
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
            catch (Exception ex) { StageFailed("dfbproj timestamp touch", StageKind.Optional, ex, log); }
        }

    }
}
