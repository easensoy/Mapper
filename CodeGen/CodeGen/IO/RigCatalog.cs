using System.Collections.Generic;

namespace CodeGen.Configuration
{
    public sealed class RigCatalog
    {
        // Control.xml process name -> its state_table slot. Allocation, so it is data: a twin that renames
        // or adds a process needs a row here, not a branch in the planner.
        public Dictionary<string, int> ProcessSlots { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public int RobotActuatorId { get; set; }
        public List<SynthSensor> SynthSensors { get; set; } = new();
        public List<string> CrossRingSegment { get; set; } = new();
        public List<DischargeChannel> DischargeChannels { get; set; } = new();

        // Part-presence gates the Assembly recipe inserts before each pick (block -> sensor + the
        // runtime state that means "present"; active-low sensors = 0, active-high = 1). The top-cover
        // sensor's state_table slot is NOT stored here -- StateTableAllocation computes it per ring
        // topology, so the cover interlock is model-independent.
        public List<SensorInterlock> SensorInterlocks { get; set; } = new();

        public static RigCatalog Current => RigCatalogLoader.Catalog;

        // The slot a process publishes its own phase on. Unallocated fails loudly and names the file to
        // edit: a defaulted slot would silently collide with a component id and repoint every WAIT on it.
        public int SlotOfProcess(string? processName) =>
            ProcessSlots.TryGetValue((processName ?? string.Empty).Trim(), out int slot)
                ? slot
                : throw new System.InvalidOperationException(
                    $"Process '{processName}' has no state_table slot. Add it to processSlots in " +
                    "Config/smc-rig.yml, above the component id space and distinct from every other slot.");
    }

    public sealed class SensorInterlock
    {
        public string Block { get; set; } = string.Empty;   // sensor-interlock block key (bearing/shaft/coverPlace)
        public string Sensor { get; set; } = string.Empty;  // Control.xml sensor instance name
        public int PresentState { get; set; }               // runtime state that means "part present"
    }

    public sealed class DischargeChannel
    {
        public string Channel { get; set; } = string.Empty;
        public string Meaning { get; set; } = string.Empty;
    }

    public sealed class SynthSensor
    {
        public string Name { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
        public int Id { get; set; }
    }
}
