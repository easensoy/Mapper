using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    /// <summary>
    /// Materialises the EAE Physical Devices canvas (Workstation_1 + NIC_1 + Runtime_1
    /// + DeviceNetwork_1) and the EcoRT_0 logical-to-physical binding so the user does
    /// not have to drag a Workstation onto the canvas, configure the NIC, attach a
    /// Soft dPAC runtime, set IP/subnet/gateway, then bind EcoRT_0 to Runtime_1 — all
    /// of which currently disappears every time the Demonstrator is cleaned.
    ///
    /// Reverse-engineered from `C:\SMC_Rig_Expo_*.sln\Topology\` and
    /// `C:\EAE Projects\LibCustomization\IEC61499\System\*\*.Properties.xml`.
    ///
    /// Files written / mutated, all relative to the EAE project root:
    ///   Topology\Equipment_Workstation_1.json        (Workstation + NIC + Runtime + binding)
    ///   Topology\BroadcastDomain_{LogicalNetworkName}.json   (subnet/mask/gateway)
    ///   Topology\TopologyManager.topologyproj                (registers the two .json files)
    ///   IEC61499\System\{sys-guid}\{ecort-guid}\{plugin-guid}.Properties.xml
    ///       (UseEncryption + InsecureApplicationEnable)
    /// </summary>
    public static class PhysicalTopologyDeployer
    {
        // Stable UUIDs — keeping them constant means re-running the deployer overwrites
        // the same Topology entries instead of creating duplicates each time.
        const string DefaultWorkstationUuid = "11111111-2222-3333-4444-000000000001";
        const string DefaultNicUuid         = "11111111-2222-3333-4444-000000000002";
        const string DefaultRuntimeUuid     = "11111111-2222-3333-4444-000000000003";
        const string DefaultDomainUuid      = "11111111-2222-3333-4444-000000000004";
        // Plugin GUID for the SystemDeviceProperties — copied from
        // LibCustomization (Soft_dPAC's Security/DeployPlugin properties file uses this id).
        const string SoftDpacPropertiesPluginGuid = "F513CAE3-7194-4086-936C-02912EA0B352";

        public static TopologyDeploymentResult Deploy(MapperConfig cfg)
        {
            var result = new TopologyDeploymentResult();
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            var eaeRoot = DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null)
            {
                result.Warnings.Add("Cannot derive EAE project root from MapperConfig.SyslayPath2");
                return result;
            }

            var host = cfg.WindowsSoftDpacHost ?? new WindowsSoftDpacHostConfig();

            // 1. Discover the EcoRT_0 sysdev (its ID is what the Workstation runtime binds to).
            var (ecortSysdevPath, ecortDeviceId) = FindEcoRtSysdev(eaeRoot);
            if (ecortSysdevPath == null)
                result.Warnings.Add(
                    "EcoRT_0 sysdev not found — Workstation will be written without a logicalDeviceId binding.");

            // 2. Workstation + BroadcastDomain JSON in Topology/.
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            Directory.CreateDirectory(topologyDir);
            var workstationFile = Path.Combine(topologyDir, "Equipment_Workstation_1.json");
            var domainFile = Path.Combine(topologyDir,
                $"BroadcastDomain_{host.LogicalNetworkName}.json");

            File.WriteAllText(workstationFile, BuildWorkstationJson(host, ecortDeviceId));
            File.WriteAllText(domainFile, BuildBroadcastDomainJson(host));
            result.FilesWritten.Add(Path.GetFileName(workstationFile));
            result.FilesWritten.Add(Path.GetFileName(domainFile));

            // 3. Register both files in TopologyManager.topologyproj.
            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                int added = RegisterInTopologyProj(topologyProj,
                    new[] { Path.GetFileName(workstationFile), Path.GetFileName(domainFile) });
                if (added > 0) result.TopologyProjEntriesAdded = added;
            }
            else
            {
                result.Warnings.Add("TopologyManager.topologyproj missing — equipment not registered with build");
            }

            // 4. Properties.xml: UseEncryption + InsecureApplicationEnable on Soft_dPAC sysdev.
            if (ecortSysdevPath != null)
            {
                var deviceFolder = Path.Combine(
                    Path.GetDirectoryName(ecortSysdevPath)!,
                    Path.GetFileNameWithoutExtension(ecortSysdevPath));
                Directory.CreateDirectory(deviceFolder);
                var propsFile = Path.Combine(deviceFolder, $"{SoftDpacPropertiesPluginGuid}.Properties.xml");
                File.WriteAllText(propsFile, BuildPropertiesXml(host));
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, propsFile));
            }

            return result;
        }

        // ---- discovery ----

        public static string? DeriveEaeProjectRoot(MapperConfig cfg)
        {
            var path = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var dir = Path.GetDirectoryName(path);
            while (dir != null)
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dfbproj").Any())
                    return Path.GetDirectoryName(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        /// <summary>
        /// Walks the IEC61499/System tree for a Soft_dPAC sysdev named EcoRT_0 (case-insensitive).
        /// Returns (path, device-ID) or (null, null) if not found.
        /// </summary>
        public static (string? Path, string? DeviceId) FindEcoRtSysdev(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return (null, null);
            foreach (var sysdev in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                XDocument doc;
                try { doc = XDocument.Load(sysdev); }
                catch { continue; }
                var root = doc.Root;
                if (root == null) continue;
                var name = (string?)root.Attribute("Name") ?? "";
                var type = (string?)root.Attribute("Type") ?? "";
                if (string.Equals(name, "EcoRT_0", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(type, "Soft_dPAC", StringComparison.OrdinalIgnoreCase))
                {
                    return (sysdev, (string?)root.Attribute("ID"));
                }
            }
            return (null, null);
        }

        // ---- JSON builders ----
        // Hand-rolled JSON instead of System.Text.Json because the EAE schema requires
        // exact key order and exact value formatting (e.g. doubles for x/y, strings for
        // booleans wrapped as "True"/"False"). Letting JsonSerializer reorder properties
        // would diff against EAE's own writer and cause noise.

        static string BuildWorkstationJson(WindowsSoftDpacHostConfig h, string? logicalDeviceId)
        {
            var binding = string.IsNullOrEmpty(logicalDeviceId)
                ? "" : $",\n      \"logicalDeviceId\": \"{logicalDeviceId}\"";

            return $$"""
{
  "catalogReference": "Workstation_V01.00_01.00",
  "uuid": "{{DefaultWorkstationUuid}}",
  "identifier": "Workstation_1",
  "path": "Topology",
  "properties": [
    { "propertyName": "IsUnderConstruction", "propertyValue": "False" },
    { "propertyName": "CommCardReference",   "propertyValue": "PC Network Interface Card" },
    { "propertyName": "DomainTag",           "propertyValue": "{{DefaultDomainUuid}}" }
  ],
  "references": [
    { "diagramPath": "Physical Views", "x": -400.0, "y": -300.0 }
  ],
  "equipments": [
    {
      "catalogReference": "NIC_EAE_V01.00_01.00",
      "uuid": "{{DefaultNicUuid}}",
      "identifier": "NIC_1",
      "path": "Workstation_1\\NIC_1",
      "components": [
        {
          "interfaces": [
            {
              "identifier": "{{h.NicIdentifier}}",
              "disabled": false,
              "physicalAddress": "",
              "endpoints": [
                {
                  "identifier": "IP Address",
                  "isReadOnly": false,
                  "domainReadOnly": false,
                  "ipAddress": "{{h.WorkstationIP}}",
                  "domain": "{{DefaultDomainUuid}}"
                }
              ]
            }
          ],
          "ports": [
            { "identifier": "Port1", "side": "Default" }
          ],
          "componentType": "EthernetDEO"
        }
      ]
    }
  ],
  "components": [
    {
      "uuid": "{{DefaultRuntimeUuid}}",
      "typeId": "422ee926-a34a-4ab5-9e8f-dce0782579f0"{{binding}},
      "identifier": "Runtime_1",
      "runtimeServices": [
        {
          "identifier": "Deployment",
          "endpoint": "NIC_1\\{{h.NicIdentifier}}\\IP Address",
          "logicalPort": "{{h.RuntimePort}}",
          "logicalPortSecured": "{{h.ArchivePort}}"
        }
      ],
      "componentType": "RuntimeDEO"
    }
  ]
}
""";
        }

        static string BuildBroadcastDomainJson(WindowsSoftDpacHostConfig h) => $$"""
{
  "uuid": "{{DefaultDomainUuid}}",
  "identifier": "{{h.LogicalNetworkName}}",
  "ipV4Address": "{{h.SubnetAddress}}",
  "ipV4Mask": "{{h.SubnetMask}}",
  "ipV4Gateway": "{{h.GatewayAddress}}"
}
""";

        // ---- Properties.xml writer ----

        static string BuildPropertiesXml(WindowsSoftDpacHostConfig h)
        {
            // Schema reverse-engineered from
            // LibCustomization\IEC61499\System\<sys>\<ecort>\F513CAE3-...Properties.xml
            // (xmlns http://www.nxtControl.com/DeviceProperties).
            // InsecureApplicationEnable lives in a SecurityApp ComplexProperty group;
            // EAE's deploy step reads it and writes Configuration.SecurityApp.InsecureApplication.Enable
            // into the Soft_dPAC runtime.config baked into bin/Deploy at compile time.
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(XName.Get("SystemDeviceProperties", "http://www.nxtControl.com/DeviceProperties"),
                    new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                    new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                    PropertyGroup("Security",
                        Property("UseEncryption", h.UseEncryption ? "True" : "False")),
                    PropertyGroup("SecurityApp",
                        Property("InsecureApplicationEnable", h.InsecureApplicationEnable ? "True" : "False")),
                    PropertyGroup("DeployPlugin",
                        Property("ClearBeforeDeploy", "True"))));
            return doc.ToString(SaveOptions.None);
        }

        static XElement PropertyGroup(string name, params XElement[] children)
        {
            XNamespace ns = "http://www.nxtControl.com/DeviceProperties";
            var el = new XElement(ns + "ComplexProperty",
                new XAttribute("Name", name),
                new XAttribute("Expanded", "true"));
            foreach (var c in children) el.Add(c);
            return el;
        }

        static XElement Property(string name, string value)
        {
            XNamespace ns = "http://www.nxtControl.com/DeviceProperties";
            return new XElement(ns + "Property",
                new XAttribute("Name", name),
                new XAttribute("Value", value),
                new XAttribute("IsPassword", "false"));
        }

        // ---- TopologyManager.topologyproj registration ----

        public static int RegisterInTopologyProj(string topologyProjPath, IEnumerable<string> jsonFileNames)
        {
            var doc = XDocument.Load(topologyProjPath);
            var ns = doc.Root!.GetDefaultNamespace();
            // Use the ItemGroup that already holds None entries with .json includes,
            // creating one if none exists.
            var noneGroup = doc.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "None").Any())
                ?? new XElement(ns + "ItemGroup");
            if (noneGroup.Parent == null) doc.Root!.Add(noneGroup);

            int added = 0;
            foreach (var name in jsonFileNames)
            {
                bool exists = noneGroup.Elements(ns + "None").Any(e =>
                    string.Equals((string?)e.Attribute("Include"), name, StringComparison.OrdinalIgnoreCase));
                if (exists) continue;
                noneGroup.Add(new XElement(ns + "None", new XAttribute("Include", name)));
                added++;
            }
            if (added > 0) doc.Save(topologyProjPath);
            return added;
        }

        /// <summary>
        /// Deletes the JSON files this deployer would have written, plus the matching
        /// TopologyManager.topologyproj &lt;None&gt; entries. Called when AutoEmit is OFF
        /// so a previous Mapper run that wrote bad topology files leaves nothing
        /// behind for EAE to choke on. Idempotent — does nothing if files are absent.
        /// </summary>
        public static int RemoveEmittedTopology(MapperConfig cfg)
        {
            int removed = 0;
            var eaeRoot = DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null) return 0;
            var host = cfg.WindowsSoftDpacHost ?? new WindowsSoftDpacHostConfig();

            var topologyDir = Path.Combine(eaeRoot, "Topology");
            var workstation = Path.Combine(topologyDir, "Equipment_Workstation_1.json");
            var domain = Path.Combine(topologyDir, $"BroadcastDomain_{host.LogicalNetworkName}.json");

            foreach (var f in new[] { workstation, domain })
            {
                if (File.Exists(f))
                {
                    try { File.Delete(f); removed++; } catch { /* swallow */ }
                }
            }

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                try
                {
                    var doc = XDocument.Load(topologyProj);
                    var ns = doc.Root!.GetDefaultNamespace();
                    var stale = new[] { Path.GetFileName(workstation), Path.GetFileName(domain) };
                    var toRemove = doc.Descendants(ns + "None")
                        .Where(e => stale.Contains((string?)e.Attribute("Include")))
                        .ToList();
                    foreach (var r in toRemove) { r.Remove(); removed++; }
                    if (toRemove.Count > 0) doc.Save(topologyProj);
                }
                catch { /* swallow */ }
            }

            // Also nuke the per-device Properties.xml if we wrote it.
            var (sysdev, _) = FindEcoRtSysdev(eaeRoot);
            if (sysdev != null)
            {
                var deviceFolder = Path.Combine(
                    Path.GetDirectoryName(sysdev)!,
                    Path.GetFileNameWithoutExtension(sysdev));
                var props = Path.Combine(deviceFolder, $"{SoftDpacPropertiesPluginGuid}.Properties.xml");
                if (File.Exists(props))
                {
                    try { File.Delete(props); removed++; } catch { /* swallow */ }
                }
            }

            return removed;
        }
    }

    public class TopologyDeploymentResult
    {
        public List<string> FilesWritten { get; } = new();
        public List<string> Warnings { get; } = new();
        public int TopologyProjEntriesAdded { get; set; }
    }
}
