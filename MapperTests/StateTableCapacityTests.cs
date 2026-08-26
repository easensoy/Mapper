using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeGen.Services;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// The state table used to be a fixed 24 slots with nothing checking the twin against it: a rig that
    /// outgrew it produced a plan whose highest ids indexed past the array, which the FBs accept and then
    /// read as whatever sits beyond. Capacity is now derived from the plan and patched into every FB that
    /// declares the array. These fail if it stops growing, grows inconsistently, or moves an existing id.
    public sealed class StateTableCapacityTests
    {
        private static string ModelPath(string suffix) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Documents", "VueOne", "system",
            "SMC_Vue2VC_With_Processes" + suffix, "Control.xml");

        private static string Require(string suffix)
        {
            var path = ModelPath(suffix);
            if (!File.Exists(path))
                throw new FileNotFoundException($"VueOne source model '{suffix}' not found at {path}.", path);
            return path;
        }

        // The rig model with N extra sensors bolted on: a plant that has outgrown the declared capacity,
        // written as a real Control.xml so it takes the same path a rig model does.
        private static string TwinWithExtraSensors(int count)
        {
            string xml = File.ReadAllText(Require("_se"));
            var template = Regex.Match(
                xml, @"<Component>(?:(?!</Component>).)*?<Type>Sensor</Type>.*?</Component>", RegexOptions.Singleline);
            Assert.True(template.Success, "the _se model no longer contains a Sensor component to copy");

            var added = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                string one = template.Value;
                one = Regex.Replace(one, @"<ComponentID>[^<]*</ComponentID>",
                    $"<ComponentID>C-extra-{i:D4}-0000-0000-0000-000000000000</ComponentID>", RegexOptions.Singleline);
                one = Regex.Replace(one, @"<Name>[^<]*</Name>", $"<Name>ExtraSensor{i:D2}</Name>",
                    RegexOptions.Singleline);
                // Fresh state ids too: two components sharing a StateID would not be a bigger plant.
                int n = 0;
                one = Regex.Replace(one, @"<StateID>[^<]*</StateID>",
                    _ => $"<StateID>S-extra-{i:D4}-{n++:D4}-0000-000000000000</StateID>", RegexOptions.Singleline);
                added.Append(one).AppendLine();
            }

            int at = xml.LastIndexOf("</Component>", StringComparison.Ordinal) + "</Component>".Length;
            string grown = xml.Insert(at, Environment.NewLine + added);

            string path = Path.Combine(Path.GetTempPath(),
                "mapper-capacity-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, grown);
            return path;
        }

        [Fact]
        public void A_twin_with_more_reporters_than_the_declared_capacity_expands_it()
        {
            int declared = GenerationConfig.Current.StateTableCapacity;
            string twin = TwinWithExtraSensors(declared);   // guarantees the table must grow
            try
            {
                var ctx = GenerationContext.Plan(
                    TestConfig.Cfg, twin, DeploymentProfile.AsPlaced(TestConfig.Cfg));

                Assert.True(ctx.Slots.Count > declared,
                    $"the fixture must exceed the declared capacity; it planned {ctx.Slots.Count} reporters");
                // Exactly one past the highest slot: big enough to index, never padded.
                Assert.Equal(ctx.Slots.Values.Max() + 1, ctx.StateTableCapacity);
                // The point of growing it: nothing indexes past the array. A slot beyond the declared
                // size is accepted by the FBs and then read as whatever sits after the table.
                Assert.All(ctx.Slots, kv => Assert.InRange(kv.Value, 0, ctx.StateTableCapacity - 1));
            }
            finally { File.Delete(twin); }
        }

        [Fact]
        public void Expanding_the_table_does_not_move_an_existing_id()
        {
            var profile = DeploymentProfile.AsPlaced(TestConfig.Cfg);
            var before = GenerationContext.Plan(TestConfig.Cfg, Require("_se"), profile).Slots;

            string twin = TwinWithExtraSensors(GenerationConfig.Current.StateTableCapacity);
            try
            {
                var after = GenerationContext.Plan(TestConfig.Cfg, twin, profile).Slots;
                var moved = before.Where(kv => after[kv.Key] != kv.Value)
                    .Select(kv => $"{kv.Key} {kv.Value}->{after[kv.Key]}").ToList();
                Assert.True(moved.Count == 0, "ids moved: " + string.Join(", ", moved));
            }
            finally { File.Delete(twin); }
        }

        [Fact]
        public void Every_fb_that_declares_the_table_is_patched_to_the_same_size()
        {
            string root = Path.Combine(Path.GetTempPath(), "mapper-fbt-" + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(root, "IEC61499");
            Directory.CreateDirectory(dir);
            string[] owners = { "ProcessRuntime_Generic_v1.fbt", "ProcessStateBusHandler.fbt", "updateComponentState.fbt" };
            foreach (var f in owners)
                File.WriteAllText(Path.Combine(dir, f),
                    "<FBType><InterfaceList><InputVars><VarDeclaration Name=\"state_table\" " +
                    "Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"24\" /></InputVars>" +
                    "</InterfaceList></FBType>");
            try
            {
                var result = new DeployResult();
                ProcessRuntimeTemplatePatcher.PatchStateTableCapacity(new FbtEditScope(root, TestConfig.Cfg.Generation.FileWriteRetries), 40, result);

                foreach (var f in owners)
                    Assert.Contains("ArraySize=\"40\"", File.ReadAllText(Path.Combine(dir, f)), StringComparison.Ordinal);
                Assert.Equal(owners.Length, result.PatchesApplied.Count(p => p.Contains("state_table", StringComparison.Ordinal)));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
