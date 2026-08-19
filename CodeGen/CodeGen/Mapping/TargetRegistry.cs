using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // One supported deployment target, fully described. Everything the generic planner needs to know
    // about a controller lives on its descriptor, so planning selects a target by key instead of running
    // a switch per question -- and a question nobody answered is a missing field here rather than a
    // silently-empty string somewhere downstream.
    //
    // Backend RENDERERS stay typed C#: an EAE device is not configuration, and its emitter cannot be
    // named in YAML. What the registry removes is the per-question switch, not the backend.
    public sealed record TargetDescriptor(
        PlcAssignment Plc,
        // EAE resource name. Load-bearing: the authored M580 .hcf symlinks read 'RES0.M580IO.*'.
        string ResourceName,
        // The sysdev Type. BX1 and the RevPi are both Soft_dPAC, so Type alone does not identify a device.
        string DeviceType,
        // Disambiguates two targets that share a DeviceType; null when the Type is unique.
        string? DeviceName,
        // Runs the Feed station, so components upstream of the assembly side land here.
        bool HostsFeedStation,
        // Its sysres canvas is device-local, so FBs translate to a local origin.
        bool DeviceLocalCanvas,
        // Receives components relocated off another target, so they must not be swept from its sysres.
        bool ReceivesRelocatedComponents,
        // Hands the cover detour out to another target; its ring closes across the seam, not locally.
        bool OpensCoverSeam,
        // Carries a chain another target commands, so it is open at both ends.
        bool CarriesDetouredChain)
    {
        // Every EAE device the Mapper emits lives in the same vendor namespace.
        public const string DeviceNamespace = "SE.DPAC";
    }

    public static class TargetRegistry
    {
        private static readonly TargetDescriptor[] Targets =
        {
            new(PlcAssignment.M262,  "M262_RES",  "M262_dPAC", null,
                HostsFeedStation: true,  DeviceLocalCanvas: false, ReceivesRelocatedComponents: false,
                OpensCoverSeam: false,   CarriesDetouredChain: false),
            new(PlcAssignment.M580,  "RES0",      "M580_dPAC", null,
                HostsFeedStation: false, DeviceLocalCanvas: true,  ReceivesRelocatedComponents: false,
                OpensCoverSeam: true,    CarriesDetouredChain: false),
            new(PlcAssignment.BX1,   "BX1_RES",   "Soft_dPAC", "BX1",
                HostsFeedStation: false, DeviceLocalCanvas: true,  ReceivesRelocatedComponents: false,
                OpensCoverSeam: false,   CarriesDetouredChain: true),
            new(PlcAssignment.RevPi, "RevPi_RES", "Soft_dPAC", "Revolution_Pi",
                HostsFeedStation: true,  DeviceLocalCanvas: false, ReceivesRelocatedComponents: true,
                OpensCoverSeam: false,   CarriesDetouredChain: true),
        };

        // Throws rather than returning a blank: an unregistered target used to yield an empty resource
        // name, which produced a device with no resource instead of a diagnostic.
        public static TargetDescriptor Of(PlcAssignment plc) =>
            Targets.FirstOrDefault(t => t.Plc == plc)
            ?? throw new InvalidOperationException(
                $"[Target] '{plc}' is not a supported deployment target. Registered: " +
                string.Join(", ", Targets.Select(t => t.Plc)) +
                ". A new controller needs a descriptor here and a backend emitter; it is not configuration.");

        public static bool IsRegistered(PlcAssignment plc) => Targets.Any(t => t.Plc == plc);

        public static IReadOnlyList<TargetDescriptor> All => Targets;

        // The controller that runs the Feed station when nothing has relocated it.
        public static PlcAssignment FeedTarget =>
            Targets.First(t => t is { HostsFeedStation: true, ReceivesRelocatedComponents: false }).Plc;
    }
}
