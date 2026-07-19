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

        // Part-presence gates the Assembly recipe inserts before each pick (block -> sensor + the
        // runtime state that means "present"; active-low sensors = 0, active-high = 1). TopCoverSenosr's
        // state_table slot is NOT stored here -- it is computed per ring topology (SystemLayoutInjector
        // -> MapperConfig.TopCoverSensorId) so the cover interlock is model-independent.
        public List<SensorInterlock> SensorInterlocks { get; set; } = new();

        public static RigCatalog Current => RigCatalogLoader.Catalog;
    }

    public sealed class SensorInterlock
    {
        public string Block { get; set; } = string.Empty;   // recipes.yml block name (bearing/shaft/coverPlace)
        public string Sensor { get; set; } = string.Empty;  // Control.xml sensor instance name
        public int PresentState { get; set; }               // runtime state that means "part present"
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
