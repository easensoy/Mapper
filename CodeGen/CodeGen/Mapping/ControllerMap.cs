using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

using CodeGen.Mapping;
namespace CodeGen.Mapping
{
    // Allocation questions answered from the target registry, which owns the facts. Kept as a named lens
    // because "is this the Feed controller?" reads better than a descriptor field at every call site.
    public static class ControllerMap
    {
        public static string ResourceForPlc(PlcAssignment plc) => TargetRegistry.Of(plc).ResourceName;

        public static bool IsFeedController(PlcAssignment plc) =>
            TargetRegistry.IsRegistered(plc) && TargetRegistry.Of(plc).HostsFeedStation;

        public static PlcAssignment FeedController => TargetRegistry.FeedTarget;
    }
}
