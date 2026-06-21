using System.Collections.Generic;

namespace CodeGen.Configuration
{
    public sealed class RigCatalog
    {
        public ProcessIds ProcessIds { get; set; } = new();
        public int RobotActuatorId { get; set; }
        public Dictionary<string, int> CoverActuatorIds { get; set; } = new();
        public List<SynthSensor> SynthSensors { get; set; } = new();
        public List<string> CrossRingSegment { get; set; } = new();
        public List<DischargeChannel> DischargeChannels { get; set; } = new();

        public static RigCatalog Current => RigCatalogLoader.Catalog;
    }

    public sealed class DischargeChannel
    {
        public string Channel { get; set; } = string.Empty;
        public string Meaning { get; set; } = string.Empty;
    }

    public sealed class ProcessIds
    {
        public int FeedStation { get; set; }
        public int Assembly { get; set; }
        public int Disassembly { get; set; }
    }

    public sealed class SynthSensor
    {
        public string Name { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
        public int Id { get; set; }
    }
}
