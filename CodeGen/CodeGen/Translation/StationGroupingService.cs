using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public record StationContents(
        VueOneComponent Process,
        List<VueOneComponent> Actuators,
        List<VueOneComponent> Sensors);

    public class StationGroupingService
    {
        public StationContents GroupStationContents(VueOneComponent process,
            IReadOnlyList<VueOneComponent> allComponents)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            if (allComponents == null) throw new ArgumentNullException(nameof(allComponents));

            if (!string.Equals(process.Type, "Process", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Component '{process.Name}' has Type='{process.Type}', expected 'Process'.",
                    nameof(process));

            var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in process.States)
            {
                foreach (var trans in state.Transitions)
                {
                    foreach (var cond in trans.Conditions)
                    {
                        if (!string.IsNullOrEmpty(cond.ComponentID))
                            referencedIds.Add(cond.ComponentID);
                    }
                }
            }

            var byId = allComponents
                .Where(c => !string.IsNullOrEmpty(c.ComponentID))
                .ToDictionary(c => c.ComponentID, c => c, StringComparer.OrdinalIgnoreCase);

            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < allComponents.Count; i++)
            {
                var id = allComponents[i].ComponentID;
                if (!string.IsNullOrEmpty(id) && !orderIndex.ContainsKey(id))
                    orderIndex[id] = i;
            }

            var actuators = new List<VueOneComponent>();
            var sensors = new List<VueOneComponent>();

            foreach (var id in referencedIds)
            {
                if (!byId.TryGetValue(id, out var comp)) continue;
                if (string.Equals(comp.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                    actuators.Add(comp);
                else if (string.Equals(comp.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                    sensors.Add(comp);
            }

            actuators = actuators.OrderBy(c => orderIndex.TryGetValue(c.ComponentID, out var i) ? i : int.MaxValue).ToList();
            sensors = sensors.OrderBy(c => orderIndex.TryGetValue(c.ComponentID, out var i) ? i : int.MaxValue).ToList();

            return new StationContents(process, actuators, sensors);
        }
    }
}
