using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Application;
using CodeGen.Configuration;
using CodeGen.Devices;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// Which target a component runs on is an ASSIGNMENT: a component name and a target. Planning works
    /// in those terms only - never "is this the RevPi run" - so adding a target is a backend plus a
    /// device.yml row, and moving a component is a roster decision. These pin that, and pin the one
    /// boundary where the shape the UI and the prebuilt runner send is converted into assignments.
    public sealed class TargetAssignmentTests
    {
        private static LayoutCatalog Layout => LayoutCatalog.Load();

        [Fact]
        public void Nothing_assigned_means_every_component_keeps_its_layout_row()
        {
            var profile = DeploymentProfile.AsPlaced(TestConfig.Cfg);
            Assert.False(profile.HasAssignments);
            Assert.Empty(profile.Assignments);

            var roster = new DeploymentRoster(profile);
            foreach (var row in Layout.Components)
                Assert.Equal(row.Plc, roster.Get(row.Name)?.Plc);
        }

        [Fact]
        public void An_assignment_moves_where_a_component_RUNS_and_not_where_it_is_drawn()
        {
            // The canvas is the plant; the assignment is the wiring. A relocated component keeps its
            // cell so the layout still reads as the machine.
            var moved = Layout.Components.First(c => c.Name == "Feeder");
            var placed = new DeploymentRoster(DeploymentProfile.AsPlaced(TestConfig.Cfg)).Get(moved.Name)!;
            var profile = DeploymentProfile.Relocating(new[] { moved.Name }, TestConfig.Cfg);
            var relocated = new DeploymentRoster(profile).Get(moved.Name)!;

            Assert.NotEqual(placed.Plc, relocated.Plc);
            Assert.Equal(placed.X, relocated.X);
            Assert.Equal(placed.Y, relocated.Y);
        }

        [Fact]
        public void An_assignment_to_a_target_no_backend_implements_is_refused()
        {
            var ex = Assert.Throws<ArgumentException>(() => new DeploymentProfile(
                new Dictionary<string, PlcAssignment> { ["Feeder"] = PlcAssignment.Unknown },
                TestConfig.Cfg));
            Assert.Contains("no backend implements", ex.Message);
        }

        [Fact]
        public void A_target_that_declares_components_its_own_hardware_must_read_takes_them_along()
        {
            // A hardware contract of the TARGET, declared beside its addresses - so assigning one
            // component can legitimately bring others with it, and the profile says which.
            var target = TargetRegistry.All.First(t => t.ReceivesRelocatedComponents).Plc;
            var required = DeviceConfig.Current.AlwaysHostedBy(target);
            var profile = DeploymentProfile.Relocating(new[] { "Feeder" }, TestConfig.Cfg);

            foreach (var name in required)
                Assert.Equal(target, profile.AssignedTarget(name));
        }

        [Fact]
        public void The_boundary_is_the_only_place_that_turns_a_selection_into_assignments()
        {
            // GenerationRequest carries a SET of names because that is the binary shape MapperUI and the
            // prebuilt VueOne runner have always sent. It is converted once, here.
            var request = typeof(GenerationRequest);
            var property = request.GetProperty("RevPiComponents");
            Assert.NotNull(property);
            Assert.Equal(typeof(IReadOnlySet<string>), property!.PropertyType);

            var profile = DeploymentProfile.Relocating(new[] { "Feeder", "Checker" }, TestConfig.Cfg);
            var target = TargetRegistry.All.First(t => t.ReceivesRelocatedComponents).Plc;
            Assert.Equal(target, profile.AssignedTarget("Feeder"));
            Assert.Equal(target, profile.AssignedTarget("Checker"));
            Assert.Null(profile.AssignedTarget("Clamp"));
        }

        // ---- the backend contract ---------------------------------------------------------------

        [Fact]
        public void Registration_is_an_explicit_composition_root_of_CLR_backends()
        {
            // What a target IS stays data; what it DOES is typed code. So the backend list is C# and
            // every registered backend answers for exactly one declared target.
            Assert.NotEmpty(TargetRegistry.Backends);
            foreach (var backend in TargetRegistry.Backends)
            {
                Assert.IsAssignableFrom<ITargetBackend>(backend);
                Assert.True(TargetRegistry.IsRegistered(backend.Target));
            }
            var targets = TargetRegistry.Backends.Select(b => b.Target).ToList();
            Assert.Equal(targets.Count, targets.Distinct().Count());
        }

        [Fact]
        public void Every_target_stage_is_answerable_by_every_backend()
        {
            // The pipeline drives the run by asking each backend in turn, so a stage a backend cannot
            // answer would be a stage the pipeline has to special-case.
            foreach (var stage in new[]
                     {
                         nameof(ITargetBackend.ValidateAssignment),
                         nameof(ITargetBackend.EmitDevice),
                         nameof(ITargetBackend.CopyHardwareConfig),
                         nameof(ITargetBackend.WireResource),
                         nameof(ITargetBackend.BindHardware),
                         nameof(ITargetBackend.FinishApplication),
                         nameof(ITargetBackend.ValidateOutput),
                     })
                foreach (var backend in TargetRegistry.Backends)
                    Assert.NotNull(backend.GetType().GetMethod(stage));
        }

        [Fact]
        public void A_required_stage_failure_aborts_and_names_the_target_and_the_stage()
        {
            // A half-emitted device still deploys, so a stage that fails must stop the run rather than
            // let a later stage write over it and the pipeline report success.
            var backend = new ThrowingBackend();
            var ex = Assert.Throws<TargetStageException>(
                () => backend.Run("device emit", _ => { }, () => throw new InvalidOperationException("disk full")));

            Assert.Equal(PlcAssignment.Named("BX1"), ex.Target);
            Assert.Equal("device emit", ex.Stage);
            Assert.Contains("disk full", ex.Message);
        }

        private sealed class ThrowingBackend : TargetBackend
        {
            public override PlcAssignment Target => PlcAssignment.Named("BX1");
            public void Run(string stage, Action<string> log, Action work) => Stage(stage, log, work);
        }

        // ---- the pipeline names no controller ----------------------------------------------------

        [Fact]
        public void The_pipeline_drives_every_target_through_the_registry()
        {
            // Every stage in GenerateProject is a loop over the registered backends. A per-controller
            // step in the pipeline is what makes adding a target a change to the pipeline.
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "CodeGen", "CodeGen", "Application", "GenerateProject.cs"));
            foreach (var stage in new[]
                     {
                         "ValidateAssignment", "EmitDevice", "CopyHardwareConfig",
                         "WireResource", "BindHardware", "FinishApplication", "ValidateOutput",
                     })
                Assert.Contains($"backend.{stage}", source);
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "CodeGen")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir ?? AppContext.BaseDirectory;
        }
    }
}
