using System;
using System.Collections.Generic;
using System.IO;
using CodeGen.Configuration;
using CodeGen.Devices;
using CodeGen.Devices.Core;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// A partial device deploys. So a required backend stage that fails must STOP the run and say which
    /// target and which stage, rather than log a line and let the next stage write over it.
    public sealed class TargetBackendFailureTests
    {
        // A backend that fails on demand, exercising the contract every real backend inherits.
        private sealed class Failing : TargetBackend
        {
            private readonly string _stage;
            public Failing(string stage) => _stage = stage;
            public readonly List<string> Log = new();
            public override PlcAssignment Target => PlcAssignment.Named("M262");

            public override void EmitDevice(GenerationContext ctx, DeviceScope scope, Action<string> log) =>
                Run("device emit", log);

            public override void CopyHardwareConfig(GenerationContext ctx, Action<string> log) =>
                Run("hardware config", log);

            public override void WireResource(GenerationContext ctx,
                SystemInjector.BindingApplicationReport report, Action<string> log) =>
                Run("resource wire", log);

            public override void BindHardware(GenerationContext ctx, IoBindings? bindings,
                SystemInjector.BindingApplicationReport report, Action<string> log) =>
                Run("hardware bind", log);

            private void Run(string stage, Action<string> log) =>
                Stage(stage, log, () =>
                {
                    if (stage == _stage) throw new IOException("the authored file was not there");
                });
        }

        public static IEnumerable<object[]> RequiredStages() => new[]
        {
            new object[] { "device emit" },
            new object[] { "hardware config" },
            new object[] { "resource wire" },
            new object[] { "hardware bind" },
        };

        [Theory]
        [MemberData(nameof(RequiredStages))]
        public void A_failed_required_stage_aborts_and_names_the_target_the_stage_and_the_cause(string stage)
        {
            var backend = new Failing(stage);
            var log = new List<string>();
            void Drive()
            {
                backend.EmitDevice(null!, null!, log.Add);
                backend.CopyHardwareConfig(null!, log.Add);
                backend.WireResource(null!, null!, log.Add);
                backend.BindHardware(null!, null, null!, log.Add);
            }

            var failure = Assert.Throws<TargetStageException>(Drive);

            Assert.Equal(PlcAssignment.Named("M262"), failure.Target);
            Assert.Equal(stage, failure.Stage);
            Assert.Contains("the authored file was not there", failure.Message, StringComparison.Ordinal);
            Assert.IsType<IOException>(failure.InnerException);
        }

        [Fact]
        public void A_stage_that_succeeds_is_not_turned_into_a_failure()
        {
            var backend = new Failing("nothing fails");
            var log = new List<string>();
            backend.EmitDevice(null!, null!, log.Add);
            backend.CopyHardwareConfig(null!, log.Add);
            backend.WireResource(null!, null!, log.Add);
            backend.BindHardware(null!, null, null!, log.Add);
            Assert.Empty(log);
        }

        [Fact]
        public void Every_registered_backend_inherits_the_fail_closed_stage_contract()
        {
            Assert.NotEmpty(TargetBackends.All);
            foreach (var backend in TargetBackends.All)
                Assert.IsAssignableFrom<TargetBackend>(backend);
        }

        [Fact]
        public void A_project_no_device_can_be_written_into_stops_before_any_device_is_emitted()
        {
            var empty = Path.Combine(Path.GetTempPath(), "mapper-no-project-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            try
            {
                var cfg = CompilerConfiguration.Load(
                    new MapperConfig { SyslayPath2 = Path.Combine(empty, "nothing.syslay") });
                var failure = Assert.Throws<InvalidOperationException>(() => DeviceScope.Open(cfg));
                Assert.Contains("no device can be emitted", failure.Message, StringComparison.Ordinal);
            }
            finally { Directory.Delete(empty, true); }
        }
    }
}
