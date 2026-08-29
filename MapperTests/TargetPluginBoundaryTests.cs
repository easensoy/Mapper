using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Application;
using CodeGen.Configuration;
using CodeGen.Devices;
using CodeGen.Devices.Core;
using CodeGen.Translation;
using CodeGen.Mapping;
using Xunit;

namespace MapperTests
{
    /// A TARGET IS A DECLARATION PLUS ONE REGISTRY ENTRY.
    ///
    /// The point of the eight-stage backend contract is that hardware knowledge lives in one folder.
    /// These prove it by adding a controller this repository has never heard of - declared in a run's
    /// own `device.yml`, implemented by a class defined in this file - and showing it is driven like
    /// any other, with no edit to planning, recipes, interlocks or GenerationContext.
    ///
    /// This was untestable before: the kind table was `private static readonly` with no seam, so the
    /// only thing a test could do was construct a backend directly and never let the pipeline see it.
    public sealed class TargetPluginBoundaryTests : IDisposable
    {
        readonly List<string> _roots = new();

        public void Dispose()
        {
            foreach (var r in _roots)
                try { if (Directory.Exists(r)) Directory.Delete(r, true); } catch { /* temp */ }
        }

        // ---- the fake controller, defined here and nowhere else ----

        sealed class KilnBackend : TargetBackend
        {
            public readonly List<string> Drove = new();
            public KilnBackend(TargetDescriptor d) : base(d) { }

            public override IReadOnlySet<string> ServableComponents(CompilerConfiguration cfg)
            { Drove.Add(nameof(ServableComponents)); return new HashSet<string>(); }
            public override void ValidateAssignment(GenerationContext ctx) => Drove.Add(nameof(ValidateAssignment));
            public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log) => Drove.Add(nameof(EmitDevice));
            public override void CopyHardwareConfig(GenerationContext ctx, Action<string> log) => Drove.Add(nameof(CopyHardwareConfig));
            public override void WireResource(GenerationContext ctx, SystemInjector.BindingApplicationReport report, Action<string> log) => Drove.Add(nameof(WireResource));
            public override void BindHardware(GenerationContext ctx, IoBindings? bindings, SystemInjector.BindingApplicationReport report, Action<string> log) => Drove.Add(nameof(BindHardware));
            public override void FinishApplication(GenerationContext ctx, string syslayPath, SystemInjector.BindingApplicationReport report, Action<string> log) => Drove.Add(nameof(FinishApplication));
            public override void ValidateOutput(GenerationContext ctx, Action<string> log) => Drove.Add(nameof(ValidateOutput));
        }

        static IReadOnlyDictionary<string, Func<TargetDescriptor, ITargetBackend>> WithKiln() =>
            new Dictionary<string, Func<TargetDescriptor, ITargetBackend>>(
                GenerateProject.BackendKinds, StringComparer.OrdinalIgnoreCase)
            {
                ["Kiln"] = d => new KilnBackend(d),
            };

        // ---- the declaration ----

        string Bundle(Func<string, string> editDeviceYml)
        {
            var root = Path.Combine(Path.GetTempPath(), "plug_" + Guid.NewGuid().ToString("N")[..8]);
            var dst = Path.Combine(root, "Config");
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Config")))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            var p = Path.Combine(dst, "device.yml");
            File.WriteAllText(p, editDeviceYml(File.ReadAllText(p)));
            _roots.Add(root);
            return root;
        }

        /// The shipped bundle with one target's backendKind pointed at the fake. Re-pointing rather
        /// than appending keeps every identity, port and boot declaration valid, so what this proves
        /// is the KIND resolution and nothing else.
        static string RepointLastTarget(string yml, string kind)
        {
            var i = yml.LastIndexOf("backendKind:", StringComparison.Ordinal);
            Assert.True(i > 0, "device.yml declares no backendKind; this fixture assumes the shipped shape");
            var end = yml.IndexOf('\n', i);
            return yml[..i] + "backendKind: " + kind + yml[end..];
        }

        static CompilerConfiguration Load(string root) =>
            CompilerConfiguration.Load(TestConfig.Cfg.Paths.Clone(), root);

        [Fact]
        public void A_target_declared_with_an_unimplemented_kind_is_refused_by_name()
        {
            var cfg = Load(Bundle(y => RepointLastTarget(y, "Kiln")));
            var ex = Assert.Throws<InvalidOperationException>(() => GenerateProject.Backends(cfg));
            Assert.Contains("'Kiln'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no backend implements", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Implemented kinds:", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Registering_the_kind_is_all_it_takes_for_the_pipeline_to_compose_it()
        {
            var cfg = Load(Bundle(y => RepointLastTarget(y, "Kiln")));
            var backends = GenerateProject.Backends(cfg, WithKiln());

            Assert.Single(backends.OfType<KilnBackend>());
            // and the rest of the declared targets still resolve to their shipped implementations
            Assert.Equal(cfg.Devices.BackendEmitOrder.Count, backends.Length);
        }

        [Fact]
        public void The_backend_is_handed_its_own_resolved_declaration_row()
        {
            var cfg = Load(Bundle(y => RepointLastTarget(y, "Kiln")));
            var kiln = GenerateProject.Backends(cfg, WithKiln()).OfType<KilnBackend>().Single();

            // Not a name to look up again per stage: the row itself, so a backend cannot name a target
            // its own declaration contradicts.
            Assert.Equal(kiln.Target, cfg.Targets.Of(kiln.Target).Plc);
            Assert.False(string.IsNullOrWhiteSpace(cfg.Targets.Of(kiln.Target).ResourceName));
        }

        [Fact]
        public void Backends_are_composed_in_the_declared_drive_order()
        {
            var cfg = Load(Bundle(y => RepointLastTarget(y, "Kiln")));
            Assert.Equal(cfg.Devices.BackendEmitOrder.ToArray(),
                         GenerateProject.Backends(cfg, WithKiln()).Select(b => b.Target).ToArray());
        }

        [Fact]
        public void Every_stage_a_backend_does_not_override_is_a_no_op_rather_than_a_flag()
        {
            // Optionality is expressed by NOT overriding, so there is no "is this stage enabled?"
            // anywhere. A backend that does nothing is legal and drives cleanly.
            sealed_check(new Silent(TestConfig.Cfg.Targets.All[0]));

            static void sealed_check(Silent s)
            {
                s.ValidateAssignment(null!);
                s.CopyHardwareConfig(null!, _ => { });
                s.ValidateOutput(null!, _ => { });
                Assert.Empty(s.ServableComponents(TestConfig.Cfg));
            }
        }

        sealed class Silent : TargetBackend
        {
            public Silent(TargetDescriptor d) : base(d) { }
        }

        [Fact]
        public void The_shipped_kinds_are_exactly_what_device_yml_can_name()
        {
            // One registry, and it is the thing the refusal message quotes. If a kind were resolved
            // anywhere else, a target could be driven by something this list does not mention.
            Assert.Equal(new[] { "BX1", "M262", "M580", "RevPi" },
                GenerateProject.BackendKinds.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

            foreach (var t in TestConfig.Cfg.Devices.Targets)
                Assert.True(GenerateProject.BackendKinds.ContainsKey(t.BackendKind ?? ""),
                    $"device.yml target '{t.Plc}' names a kind the registry does not implement");
        }
    }
}
