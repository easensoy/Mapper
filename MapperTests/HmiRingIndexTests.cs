using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Hmi;
using Xunit;

namespace MapperTests
{
    // The regression these tests exist for:
    //
    // The SMC model reuses state_table slot 6 on two different report rings - Transfer on the Feed
    // ring, TopCoverSensor on the assembly/discharge ring. Ejector's generated RuleTable blocks its
    // advance while slot 6 reports 2, and Ejector sits on the SAME ring as TopCoverSensor. Resolving
    // that slot by controller, or by a global lookup, named Transfer: a real component, on the wrong
    // ring, presented to an operator as the thing blocking the ejector.
    //
    // Slots are only meaningful inside the ring that writes them, so that is the only legal scope.
    public class HmiRingIndexTests
    {
        // A syslay reduced to what ring identity depends on: the stateRprtCmd adapter edges.
        private static string Syslay(params (string From, string To)[] edges)
        {
            var body = string.Join(Environment.NewLine, edges.Select(e =>
                $"      <Connection Source=\"{e.From}.stateRprtCmd_out\" Destination=\"{e.To}.stateRprtCmd_in\" />"));
            var path = Path.Combine(Path.GetTempPath(), "hmiring_" + Guid.NewGuid().ToString("N") + ".syslay");
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
                "<System><Application><SubAppNetwork>" + Environment.NewLine +
                "    <AdapterConnections>" + Environment.NewLine + body + Environment.NewLine +
                "    </AdapterConnections>" + Environment.NewLine +
                "</SubAppNetwork></Application></System>");
            return path;
        }

        // The two real rings, in the shape the generated syslay actually emits them.
        private static (string Path, Dictionary<string, int> Slots) SmcRings()
        {
            var path = Syslay(
                // Feed ring
                ("PartInHopper", "Feeder"), ("Feeder", "Checker"), ("Checker", "Transfer"),
                ("Transfer", "Feed_Station"), ("Feed_Station", "PartInHopper"),
                // Assembly / discharge ring
                ("BearingSensor", "ShaftSensor"), ("ShaftSensor", "Bearing_PnP"),
                ("Bearing_PnP", "Clamp"), ("Clamp", "TopCoverSensor"),
                ("TopCoverSensor", "CoverPNP_Hr"), ("CoverPNP_Hr", "Assembly_Station"),
                ("Assembly_Station", "Disassembly"), ("Disassembly", "Ejector"),
                ("Ejector", "Robot"), ("Robot", "PartAtAssembly"), ("PartAtAssembly", "BearingSensor"));

            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["PartInHopper"] = 0, ["Feeder"] = 4, ["Checker"] = 5, ["Transfer"] = 6, ["Feed_Station"] = 10,
                ["BearingSensor"] = 1, ["ShaftSensor"] = 2, ["Bearing_PnP"] = 8, ["Clamp"] = 13,
                ["TopCoverSensor"] = 6,          // <- the same numeric slot, on the other ring
                ["CoverPNP_Hr"] = 14, ["Assembly_Station"] = 17, ["Disassembly"] = 18,
                ["Ejector"] = 7, ["Robot"] = 19, ["PartAtAssembly"] = 3,
            };
            return (path, slots);
        }

        [Fact]
        public void RingsAreDiscoveredFromStateRprtCmdConnections()
        {
            var (path, slots) = SmcRings();
            try
            {
                var index = HmiRingIndex.Build(path, slots);

                Assert.Equal(2, index.Rings.Count);
                Assert.True(index.SameRing("Ejector", "TopCoverSensor"));
                Assert.True(index.SameRing("Transfer", "Feeder"));
                Assert.False(index.SameRing("Ejector", "Transfer"));
                Assert.Empty(index.Conflicts);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void EjectorSlotSixResolvesToTopCoverSensorAndNeverToTransfer()
        {
            var (path, slots) = SmcRings();
            try
            {
                var index = HmiRingIndex.Build(path, slots);
                var blocker = index.Resolve("Ejector", 6);

                Assert.Equal("TopCoverSensor", blocker);
                Assert.NotEqual("Transfer", blocker);

                // ... and the same number on the Feed ring is still Transfer, which is the whole point:
                // the slot is not globally unique and must not be resolved as if it were.
                Assert.Equal("Transfer", index.Resolve("Feeder", 6));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RecipeWaitResolvesInsideTheProcessOwnRing()
        {
            var (path, slots) = SmcRings();
            try
            {
                var index = HmiRingIndex.Build(path, slots);

                // Assembly_Station waits on slot 6 -> its own ring's owner, TopCoverSensor.
                Assert.Equal("TopCoverSensor", index.Resolve("Assembly_Station", 6));
                // Feed_Station waits on slot 6 -> Transfer.
                Assert.Equal("Transfer", index.Resolve("Feed_Station", 6));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DuplicateSlotInsideOneRingIsReportedAsAConflict()
        {
            var path = Syslay(("A", "B"), ("B", "C"), ("C", "A"));
            try
            {
                var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = 1, ["B"] = 2, ["C"] = 2,     // B and C both claim slot 2 on one ring
                };
                var index = HmiRingIndex.Build(path, slots);

                Assert.NotEmpty(index.Conflicts);
                Assert.Contains(index.Conflicts, c => c.Contains("slot 2"));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SlotOutsideTheObserverRingDoesNotResolve()
        {
            var (path, slots) = SmcRings();
            try
            {
                var index = HmiRingIndex.Build(path, slots);
                // Slot 10 (Feed_Station) exists, but not on the assembly ring.
                Assert.Null(index.Resolve("Ejector", 10));
                // An instance on no ring at all resolves nothing rather than guessing.
                Assert.Null(index.Resolve("NotOnAnyRing", 6));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RingIdentityIsDeterministic()
        {
            var (path, slots) = SmcRings();
            try
            {
                var a = HmiRingIndex.Build(path, slots);
                var b = HmiRingIndex.Build(path, slots);
                Assert.Equal(a.Rings, b.Rings);
                Assert.Equal(a.RingOf("Ejector"), b.RingOf("Ejector"));
            }
            finally { File.Delete(path); }
        }
    }
}
