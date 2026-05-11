using System.Collections.Generic;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    public class InstanceNameResolverTests
    {
        static VueOneComponent MakeComp(string name, string type, string id = "C-test-id")
            => new VueOneComponent { Name = name, Type = type, ComponentID = id };

        [Fact]
        public void ProcessName_StripsTrailingProcessSuffix()
        {
            var c = MakeComp("Feed_Station_process", "Process");
            Assert.Equal("Feed_Station", InstanceNameResolver.Resolve(c));
        }

        [Fact]
        public void ProcessName_StripsTrailingProcessSuffix_CapitalCase()
        {
            var c = MakeComp("Assembly_Station_Process", "Process");
            Assert.Equal("Assembly_Station", InstanceNameResolver.Resolve(c));
        }

        [Fact]
        public void ProcessName_PassesThroughWhenNoSuffix()
        {
            var c = MakeComp("Disassembly", "Process");
            Assert.Equal("Disassembly", InstanceNameResolver.Resolve(c));
        }

        [Fact]
        public void ActuatorName_PassesThroughUnchanged()
        {
            var c = MakeComp("Feeder", "Actuator");
            Assert.Equal("Feeder", InstanceNameResolver.Resolve(c));
        }

        [Fact]
        public void SensorName_PassesThroughUnchanged()
        {
            var c = MakeComp("PartInHopper", "Sensor");
            Assert.Equal("PartInHopper", InstanceNameResolver.Resolve(c));
        }

        [Fact]
        public void OverrideByVueOneName_BeatsDefaultConvention()
        {
            var c = MakeComp("Feed_Station_process", "Process");
            var byName = new Dictionary<string, string> { ["Feed_Station_process"] = "Feeding" };
            Assert.Equal("Feeding", InstanceNameResolver.Resolve(c, byVueOneName: byName));
        }

        [Fact]
        public void OverrideByComponentId_BeatsOverrideByName()
        {
            var c = MakeComp("Feed_Station_process", "Process", id: "C-abc-123");
            var byName = new Dictionary<string, string> { ["Feed_Station_process"] = "ByName" };
            var byId   = new Dictionary<string, string> { ["C-abc-123"]            = "ById"   };
            Assert.Equal("ById", InstanceNameResolver.Resolve(c, byId, byName));
        }

        [Fact]
        public void EmptyOverrideValue_FallsThroughToDefault()
        {
            var c = MakeComp("Feed_Station_process", "Process", id: "C-abc-123");
            var byId = new Dictionary<string, string> { ["C-abc-123"] = "   " };
            Assert.Equal("Feed_Station", InstanceNameResolver.Resolve(c, byComponentId: byId));
        }

        [Fact]
        public void NullComponent_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, InstanceNameResolver.Resolve(null!));
        }

        [Fact]
        public void OverrideByComponentId_TrimsKeyWhitespace()
        {
            var c = MakeComp("X", "Process", id: "C-trim");
            var byId = new Dictionary<string, string> { ["C-trim"] = "Renamed" };
            Assert.Equal("Renamed", InstanceNameResolver.Resolve(c, byComponentId: byId));
        }
    }
}
