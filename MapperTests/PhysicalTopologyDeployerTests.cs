using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using CodeGen.Configuration;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    /// <summary>
    /// PhysicalTopologyDeployer materialises Topology/Equipment_Workstation_1.json,
    /// Topology/BroadcastDomain_*.json, registers both in TopologyManager.topologyproj,
    /// and writes a Soft_dPAC Properties.xml so the user does not have to drag a
    /// Workstation onto the Physical Devices canvas after every Clean.
    /// </summary>
    public class PhysicalTopologyDeployerTests
    {
        const string EmptyDfbproj = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003""><ItemGroup /></Project>";

        const string EmptyTopologyProj = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup>
    <None Include="".solutionData"" />
  </ItemGroup>
</Project>";

        static MapperConfig MakeFakeProject(out string eaeRoot, string ecortName = "EcoRT_0")
        {
            var root = Path.Combine(Path.GetTempPath(), "PTD_" + Path.GetRandomFileName());
            eaeRoot = Path.Combine(root, "Fake");
            var sysGuid = "00000000-0000-0000-0000-000000000000";
            var devGuid = "00000000-0000-0000-0000-000000000002";
            var sys = Path.Combine(eaeRoot, "IEC61499", "System", sysGuid);
            var sysFolder1 = Path.Combine(sys, "00000000-0000-0000-0000-000000000001");
            Directory.CreateDirectory(sysFolder1);
            Directory.CreateDirectory(Path.Combine(eaeRoot, "Topology"));

            File.WriteAllText(Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj"), EmptyDfbproj);

            var sysdev = Path.Combine(sys, $"{devGuid}.sysdev");
            File.WriteAllText(sysdev,
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                $"<Device xmlns=\"https://www.se.com/LibraryElements\" " +
                $"ID=\"{devGuid}\" Name=\"{ecortName}\" Type=\"Soft_dPAC\" Namespace=\"SE.DPAC\" />");

            var syslay = Path.Combine(sysFolder1, "00000000-0000-0000-0000-000000000000.syslay");
            File.WriteAllText(syslay,
                "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");

            File.WriteAllText(Path.Combine(eaeRoot, "Topology", "TopologyManager.topologyproj"),
                EmptyTopologyProj);

            return new MapperConfig { SyslayPath2 = syslay };
        }

        [Fact]
        public void DeriveEaeProjectRoot_FindsDfbprojParent()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            Assert.Equal(eaeRoot, PhysicalTopologyDeployer.DeriveEaeProjectRoot(cfg));
        }

        [Fact]
        public void FindEcoRtSysdev_ReturnsPathAndId()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            var (path, id) = PhysicalTopologyDeployer.FindEcoRtSysdev(eaeRoot);
            Assert.NotNull(path);
            Assert.NotNull(id);
            Assert.EndsWith(".sysdev", path);
        }

        [Fact]
        public void Deploy_WritesWorkstationJsonWithConfigIp()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            cfg.WindowsSoftDpacHost.WorkstationIP = "10.20.30.40";
            cfg.WindowsSoftDpacHost.NicIdentifier = "eth0";

            var result = PhysicalTopologyDeployer.Deploy(cfg);

            Assert.Contains("Equipment_Workstation_1.json", result.FilesWritten);
            var json = File.ReadAllText(Path.Combine(eaeRoot, "Topology", "Equipment_Workstation_1.json"));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Workstation_1", doc.RootElement.GetProperty("identifier").GetString());

            var ip = doc.RootElement.GetProperty("equipments")[0]
                .GetProperty("components")[0]
                .GetProperty("interfaces")[0]
                .GetProperty("endpoints")[0]
                .GetProperty("ipAddress").GetString();
            Assert.Equal("10.20.30.40", ip);

            var endpoint = doc.RootElement.GetProperty("components")[0]
                .GetProperty("runtimeServices")[0]
                .GetProperty("endpoint").GetString();
            Assert.Equal(@"NIC_1\eth0\IP Address", endpoint);
        }

        [Fact]
        public void Deploy_WritesBroadcastDomainWithSubnetTrio()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            cfg.WindowsSoftDpacHost.LogicalNetworkName = "TestNet";
            cfg.WindowsSoftDpacHost.SubnetAddress = "192.168.5.0";
            cfg.WindowsSoftDpacHost.SubnetMask = "255.255.255.0";
            cfg.WindowsSoftDpacHost.GatewayAddress = "192.168.5.1";

            PhysicalTopologyDeployer.Deploy(cfg);

            var path = Path.Combine(eaeRoot, "Topology", "BroadcastDomain_TestNet.json");
            Assert.True(File.Exists(path));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("TestNet", doc.RootElement.GetProperty("identifier").GetString());
            Assert.Equal("192.168.5.0", doc.RootElement.GetProperty("ipV4Address").GetString());
            Assert.Equal("255.255.255.0", doc.RootElement.GetProperty("ipV4Mask").GetString());
            Assert.Equal("192.168.5.1", doc.RootElement.GetProperty("ipV4Gateway").GetString());
        }

        [Fact]
        public void Deploy_BindsRuntimeToEcoRtSysdevId()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            var (_, ecortId) = PhysicalTopologyDeployer.FindEcoRtSysdev(eaeRoot);

            PhysicalTopologyDeployer.Deploy(cfg);

            var json = File.ReadAllText(Path.Combine(eaeRoot, "Topology", "Equipment_Workstation_1.json"));
            using var doc = JsonDocument.Parse(json);
            var bound = doc.RootElement.GetProperty("components")[0]
                .GetProperty("logicalDeviceId").GetString();
            Assert.Equal(ecortId, bound);
        }

        [Fact]
        public void Deploy_RegistersBothFilesInTopologyProj()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            cfg.WindowsSoftDpacHost.LogicalNetworkName = "DeviceNetwork_1";

            PhysicalTopologyDeployer.Deploy(cfg);

            var topologyProj = Path.Combine(eaeRoot, "Topology", "TopologyManager.topologyproj");
            var doc = XDocument.Load(topologyProj);
            var ns = doc.Root!.GetDefaultNamespace();
            var includes = doc.Descendants(ns + "None")
                .Select(e => (string?)e.Attribute("Include"))
                .ToHashSet();
            Assert.Contains("Equipment_Workstation_1.json", includes);
            Assert.Contains("BroadcastDomain_DeviceNetwork_1.json", includes);
        }

        [Fact]
        public void Deploy_IsIdempotent()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            var first = PhysicalTopologyDeployer.Deploy(cfg);
            var second = PhysicalTopologyDeployer.Deploy(cfg);

            // Both runs write the equipment files; second run shouldn't grow topologyproj.
            Assert.NotEmpty(first.FilesWritten);
            Assert.NotEmpty(second.FilesWritten);
            Assert.Equal(0, second.TopologyProjEntriesAdded);
        }

        [Fact]
        public void Deploy_WritesPropertiesXmlWithUseEncryptionFalseAndInsecureTrue()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            cfg.WindowsSoftDpacHost.UseEncryption = false;
            cfg.WindowsSoftDpacHost.InsecureApplicationEnable = true;

            PhysicalTopologyDeployer.Deploy(cfg);

            var ecortFolder = Path.Combine(eaeRoot, "IEC61499", "System",
                "00000000-0000-0000-0000-000000000000",
                "00000000-0000-0000-0000-000000000002");
            var props = Directory.GetFiles(ecortFolder, "*.Properties.xml").FirstOrDefault();
            Assert.NotNull(props);

            var doc = XDocument.Load(props);
            var ns = (XNamespace)"http://www.nxtControl.com/DeviceProperties";
            var groups = doc.Descendants(ns + "ComplexProperty")
                .ToDictionary(g => (string?)g.Attribute("Name") ?? "", g => g);

            Assert.True(groups.ContainsKey("Security"));
            var encryption = groups["Security"].Elements(ns + "Property")
                .First(p => (string?)p.Attribute("Name") == "UseEncryption");
            Assert.Equal("False", (string?)encryption.Attribute("Value"));

            Assert.True(groups.ContainsKey("SecurityApp"));
            var insecure = groups["SecurityApp"].Elements(ns + "Property")
                .First(p => (string?)p.Attribute("Name") == "InsecureApplicationEnable");
            Assert.Equal("True", (string?)insecure.Attribute("Value"));
        }

        [Fact]
        public void Deploy_NoEcoRtSysdev_StillWritesTopologyButLogsWarning()
        {
            var cfg = MakeFakeProject(out var eaeRoot, ecortName: "SomethingElse");

            var result = PhysicalTopologyDeployer.Deploy(cfg);

            Assert.Contains(result.Warnings, w => w.Contains("EcoRT_0"));
            // Workstation JSON still gets written but without logicalDeviceId.
            var json = File.ReadAllText(Path.Combine(eaeRoot, "Topology", "Equipment_Workstation_1.json"));
            Assert.DoesNotContain("logicalDeviceId", json);
        }

        [Fact]
        public void RuntimePort_AndArchivePort_FlowFromConfig()
        {
            var cfg = MakeFakeProject(out var eaeRoot);
            cfg.WindowsSoftDpacHost.RuntimePort = 51499;
            cfg.WindowsSoftDpacHost.ArchivePort = 51496;

            PhysicalTopologyDeployer.Deploy(cfg);

            var json = File.ReadAllText(Path.Combine(eaeRoot, "Topology", "Equipment_Workstation_1.json"));
            using var doc = JsonDocument.Parse(json);
            var svc = doc.RootElement.GetProperty("components")[0]
                .GetProperty("runtimeServices")[0];
            Assert.Equal("51499", svc.GetProperty("logicalPort").GetString());
            Assert.Equal("51496", svc.GetProperty("logicalPortSecured").GetString());
        }
    }
}
