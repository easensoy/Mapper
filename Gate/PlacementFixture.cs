using System.Xml.Linq;

namespace VueOneMapper.Gate;

// A plant small enough to place anywhere: one process, the actuator it drives and the sensor it waits
// on, with no dependency that crosses a controller. A stop count is what selects a CAT, so the actuator
// has the five the ordinary single-work-stop CAT declares. It is written here rather than checked in as
// a twin because the point is that the SAME plant compiles onto every target with only a roster row
// moving, so the plant has to be one thing, generated once and reused per target.
internal static class PlacementFixture
{
    public const string ProcessName = "Fixture_Line";
    public const string ActuatorName = "Fixture_Ram";
    public const string SensorName = "Fixture_Eye";

    // A target may declare which components its hardware can serve, and then only those can be hosted
    // there. The plant is the same either way; only what its two components are CALLED changes.
    public static string[] Write(string path, string? actuator = null, string? sensor = null)
    {
        var names = new[] { ProcessName, actuator ?? ActuatorName, sensor ?? SensorName };
        Compose(path, names[1], names[2]);
        return names;
    }

    private const string Line = "C-fixture-line";
    private const string Ram = "C-fixture-ram";
    private const string Eye = "C-fixture-eye";

    private static void Compose(string path, string actuatorName, string sensorName)
    {
        var ram = Component(Ram, actuatorName, "Actuator",
            Stop(Ram, 0, "Back", initial: true, leadsTo: 1, on: (Line, State(Line, 1))),
            Moving(Ram, 1, "Advancing", leadsTo: 2),
            Stop(Ram, 2, "Out", leadsTo: 3, on: (Line, State(Line, 2))),
            Moving(Ram, 3, "Returning", leadsTo: 4),
            Stop(Ram, 4, "Returned", leadsTo: 0));

        var eye = Component(Eye, sensorName, "Sensor",
            Stop(Eye, 0, "Clear", initial: true),
            Stop(Eye, 1, "Made"));

        var line = Component(Line, ProcessName, "Process",
            Stop(Line, 0, "Entry", initial: true, leadsTo: 1, on: (Eye, State(Eye, 1))),
            Stop(Line, 1, "Advance", leadsTo: 2, on: (Ram, State(Ram, 2))),
            Stop(Line, 2, "Retract", leadsTo: 0, on: (Ram, State(Ram, 0))));

        new XDocument(
            new XElement("vueOne_SystemDefinition",
                new XAttribute("Version", "1.0.0"), new XAttribute("Type", "System"),
                new XElement("System",
                    new XElement("SystemID", "SYS-gate-placement-fixture"),
                    new XElement("Name", "Gate_Placement_Fixture"),
                    line, ram, eye)))
            .Save(path);
    }

    private static XElement Component(string id, string name, string type, params XElement[] states) =>
        new("Component",
            new XElement("ComponentID", id),
            new XElement("Name", name),
            new XElement("VcID", string.Empty),
            new XElement("Description", string.Empty),
            new XElement("Type", type),
            states);

    private static string State(string component, int number) => $"{component}-s{number}";

    private static XElement Stop(string component, int number, string name,
        bool initial = false, int? leadsTo = null, (string Component, string State)? on = null) =>
        StateElement(component, number, name, statik: true, initial, leadsTo, on);

    private static XElement Moving(string component, int number, string name, int leadsTo) =>
        StateElement(component, number, name, statik: false, initial: false, leadsTo, on: null);

    private static XElement StateElement(string component, int number, string name,
        bool statik, bool initial, int? leadsTo, (string Component, string State)? on)
    {
        var state = new XElement("State",
            new XElement("StateID", State(component, number)),
            new XElement("Name", name),
            new XElement("State_Number", number),
            new XElement("Initial_State", initial ? "True" : "False"),
            new XElement("Time", 1000),
            new XElement("Speed", 100),
            new XElement("Position", number * 10),
            new XElement("Counter", 1),
            new XElement("StaticState", statik ? "True" : "False"));

        if (leadsTo is not { } destination) return state;

        var transition = new XElement("Transition",
            new XElement("TransitionID", $"T-{component}-{number}"),
            new XElement("Type", "SINGLE"),
            new XElement("Origin_State", State(component, number)),
            new XElement("Destination_State", State(component, destination)),
            new XElement("Priority", 0));

        if (on is { } guard)
            transition.Add(new XElement("Sequence_Condition",
                new XElement("ConditionValue",
                    new XElement("ConditionGroup",
                        new XAttribute("Operator", string.Empty),
                        new XAttribute("GroupName", "Group_1"),
                        new XElement("Condition",
                            new XAttribute("Operator", string.Empty),
                            new XAttribute("ID", guard.State),
                            new XAttribute("Name", $"{guard.Component}/{guard.State}"),
                            new XAttribute("ComponentID", guard.Component))))));

        state.Add(transition);
        return state;
    }
}
