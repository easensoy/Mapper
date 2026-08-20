using System;
using System.Collections.Generic;
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
    // The ring itself is the plan's answer (ReportGraph.Domain); these tests supply it directly.
    public class HmiSlotIndexTests
    {
        private const string Feed = "feed";
        private const string Assembly = "assembly";

        // The two real rings and their slots, as the plan and the generated syslay carry them.
        private static (Func<string, string> RingOf, Dictionary<string, int> Slots) Smc()
        {
            var ring = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PartInHopper"] = Feed, ["Feeder"] = Feed, ["Checker"] = Feed,
                ["Transfer"] = Feed, ["Feed_Station"] = Feed,
                ["BearingSensor"] = Assembly, ["ShaftSensor"] = Assembly, ["Bearing_PnP"] = Assembly,
                ["Clamp"] = Assembly, ["TopCoverSensor"] = Assembly, ["CoverPNP_Hr"] = Assembly,
                ["Assembly_Station"] = Assembly, ["Disassembly"] = Assembly,
                ["Ejector"] = Assembly, ["Robot"] = Assembly, ["PartAtAssembly"] = Assembly,
            };

            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["PartInHopper"] = 0, ["Feeder"] = 4, ["Checker"] = 5, ["Transfer"] = 6, ["Feed_Station"] = 10,
                ["BearingSensor"] = 1, ["ShaftSensor"] = 2, ["Bearing_PnP"] = 8, ["Clamp"] = 13,
                ["TopCoverSensor"] = 6,          // <- the same numeric slot, on the other ring
                ["CoverPNP_Hr"] = 14, ["Assembly_Station"] = 17, ["Disassembly"] = 18,
                ["Ejector"] = 7, ["Robot"] = 19, ["PartAtAssembly"] = 3,
            };

            return (n => ring.TryGetValue(n, out var r) ? r : string.Empty, slots);
        }

        [Fact]
        public void EjectorSlotSixResolvesToTopCoverSensorAndNeverToTransfer()
        {
            var (ringOf, slots) = Smc();
            var index = HmiSlotIndex.Build(ringOf, slots);
            var blocker = index.Resolve("Ejector", 6);

            Assert.Equal("TopCoverSensor", blocker);
            Assert.NotEqual("Transfer", blocker);

            // ... and the same number on the Feed ring is still Transfer, which is the whole point:
            // the slot is not globally unique and must not be resolved as if it were.
            Assert.Equal("Transfer", index.Resolve("Feeder", 6));
            Assert.Empty(index.Conflicts);
        }

        [Fact]
        public void RecipeWaitResolvesInsideTheProcessOwnRing()
        {
            var (ringOf, slots) = Smc();
            var index = HmiSlotIndex.Build(ringOf, slots);

            // Assembly_Station waits on slot 6 -> its own ring's owner, TopCoverSensor.
            Assert.Equal("TopCoverSensor", index.Resolve("Assembly_Station", 6));
            // Feed_Station waits on slot 6 -> Transfer.
            Assert.Equal("Transfer", index.Resolve("Feed_Station", 6));
        }

        [Fact]
        public void DuplicateSlotInsideOneRingIsReportedAsAConflict()
        {
            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = 1, ["B"] = 2, ["C"] = 2,     // B and C both claim slot 2 on one ring
            };
            var index = HmiSlotIndex.Build(_ => Feed, slots);

            Assert.NotEmpty(index.Conflicts);
            Assert.Contains(index.Conflicts, c => c.Contains("slot 2"));
        }

        [Fact]
        public void SlotOutsideTheObserverRingDoesNotResolve()
        {
            var (ringOf, slots) = Smc();
            var index = HmiSlotIndex.Build(ringOf, slots);

            // Slot 10 (Feed_Station) exists, but not on the assembly ring.
            Assert.Null(index.Resolve("Ejector", 10));
            // An instance on no ring at all resolves nothing rather than guessing.
            Assert.Null(index.Resolve("NotOnAnyRing", 6));
        }
    }
}
