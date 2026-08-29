using System;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Services;

namespace CodeGen.Devices.Core
{
    // THE M580 dPAC, rendered. Its sysres is NAME-scoped, so the resource keeps its declared name.
    //
    // Only what is specific to this device is here; the shell every device shares - sysdev, sysres,
    // .hcf, properties, simulation binding, topology registration - is EaeDeviceWriter's.
    public static class M580DeviceRenderer
    {
        public static EaeDeviceWriter.EmitResult EmitM580(Configuration.CompilerConfiguration cfg,
            Translation.PlcAssignment target, DeviceScope scope)
        {
            var self = cfg.Targets.Of(target);
            var result = new EaeDeviceWriter.EmitResult();
            // Two Equipment JSONs declaring the SAME uuid make EAE reject the whole topology.
            for (int n = 2; n <= 9; n++)
                EaeDeviceWriter.CleanupStaleTopologyJson(scope.EaeRoot, $"Equipment_M580dPAC_{n}.json", result);

            EaeDeviceWriter.EmitOnePlc(cfg, self, scope.EaeRoot, scope.SystemGuidDir, result,
                sysdevId: self.Identity.Sysdev,
                deviceName: EaeDeviceWriter.DeviceNameOf(cfg.Targets, target),
                deviceType: self.DeviceType,
                resourceId: self.Identity.Resource,
                resourceName: self.ResourceName,
                hcfTemplatePath: cfg.Paths.M580HcfTemplatePath,
                equipmentJsonName: "Equipment_M580dPAC_1.json",
                equipmentBuilder: () => BuildM580EquipmentJson(cfg, self, self.Identity.Sysdev, scope.SolutionId,
                                          cfg.Devices.NetworkOf(target.Name).TargetIp,
                                          cfg.Devices.DefaultNetwork.DomainUuid),
                deployPluginPropertiesXml: EaeDeviceWriter.BuildDeployPluginPropertiesXml(cfg, bootProject: false,
                    cfg.Telemetry.PublishEnabled && !cfg.Telemetry.SecureTls),
                simulationBindingDeployPort: self.SimulationDeployPort,
                simulationBindingArchivePort: self.SimulationArchivePort);
            return result;
        }

        // M580 dPAC equipment JSON (X80 rack + PSU + CPU); catalog refs must match EAE 24.1 names or they render as unknown boxes.
        static string BuildM580EquipmentJson(Configuration.CompilerConfiguration cfg,
                                             Mapping.TargetDescriptor self, string sysdevId, string solutionId,
                                             string targetIp, string broadcastDomainUuid)
        {
            return TemplateDocument.Load(cfg, @"Topology\Equipment_M580dPAC.json",
                new Dictionary<string, string>
                {
                    ["M580CpsUuid"] = self.Identity.Cps,
                    ["M580CpuUuid"] = self.Identity.Cpu,
                    ["M580EquipmentUuid"] = self.Identity.Equipment,
                    ["M580RackUuid"] = self.Identity.Rack,
                    ["M580RuntimeTypeId"] = self.Identity.RuntimeType,
                    ["M580RuntimeUuid"] = self.Identity.Runtime,
                    ["broadcastDomainUuid"] = broadcastDomainUuid,
                    ["solutionId"] = solutionId,
                    ["sysdevId"] = sysdevId,
                    ["targetIp"] = targetIp,
                });
        }
    }
}
