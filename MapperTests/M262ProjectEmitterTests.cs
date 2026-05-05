using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    /// <summary>
    /// Spec-level acceptance tests for the M262 project artefacts the Mapper emits
    /// (sysdev with IPV4Address Parameter + per-FB Mappings, populated .hcf with
    /// RES0 symlinks, .dfbproj registration). These assertions encode the
    /// "Demonstrator/IEC61499/System ... fix these blockers" spec verbatim.
    ///
    /// They run on a synthetic in-temp EAE project layout so they are repeatable
    /// without touching the real Demonstrator.
    /// </summary>
    public class M262ProjectEmitterTests
    {
        const string LibElNs = "https://www.se.com/LibraryElements";
        const string SysGuid = "00000000-0000-0000-0000-000000000000";
        const string SysappGuid = "00000000-0000-0000-0000-000000000001";
        const string SysdevGuid = "00000000-0000-0000-0000-000000000002";

        const string SyntheticHcfTemplate = @"<?xml version=""1.0"" encoding=""utf-8""?>
<DeviceHwConfigurationItems xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <DeviceHwConfigurationItem ResourceId=""54EB0B3D5D16444D"">
    <ConfigurationBaseItem>
      <Name>BMTM3</Name>
      <ID>9510AF594EA1EDD1</ID>
      <ItemProperties />
      <ParameterValues>
        <ParameterValue Name=""busCycleTime"" Value=""T#80ms"" />
      </ParameterValues>
      <Items>
        <ConfigurationBaseItem>
          <Name>TM262L01MDESE8T</Name>
          <ID>E2B036F9B0A5B0A4</ID>
          <ItemProperties />
          <ParameterValues />
          <Items>
            <ConfigurationBaseItem>
              <Name>TM3DI16_G</Name>
              <ID>52DB1E4920A80F90</ID>
              <ItemProperties />
              <ParameterValues>
                <ParameterValue Name=""DI00"" Value=""''"" />
                <ParameterValue Name=""DI01"" Value=""''"" />
              </ParameterValues>
            </ConfigurationBaseItem>
            <ConfigurationBaseItem>
              <Name>TM3DQ16T_G</Name>
              <ID>F46B871E4D88E59A</ID>
              <ItemProperties />
              <ParameterValues>
                <ParameterValue Name=""DO00"" Value=""''"" />
              </ParameterValues>
            </ConfigurationBaseItem>
          </Items>
        </ConfigurationBaseItem>
      </Items>
    </ConfigurationBaseItem>
  </DeviceHwConfigurationItem>
</DeviceHwConfigurationItems>";

        // Builds a synthetic EAE project layout matching the Feed_Station + Feeder
        // fixture: syslay has Feeder + Feed_Station as top-level FBs, sysdev exists
        // for EcoRT_0, .system file is empty (no Mappings yet), dfbproj is empty,
        // baseline .hcf folder is on the side. Returns a wired MapperConfig.
        static MapperConfig BuildScenario(out string eaeRoot, out string baselineRoot,
            string targetIp = "172.24.61.92")
        {
            eaeRoot      = Path.Combine(Path.GetTempPath(), "M262Emit_"  + Path.GetRandomFileName());
            baselineRoot = Path.Combine(Path.GetTempPath(), "M262Base_"  + Path.GetRandomFileName());

            // Target EAE project tree.
            var iec = Path.Combine(eaeRoot, "IEC61499");
            var sys = Path.Combine(iec, "System", SysGuid);
            Directory.CreateDirectory(sys);
            File.WriteAllText(Path.Combine(iec, "IEC61499.dfbproj"),
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup /></Project>");
            File.WriteAllText(Path.Combine(sys, $"{SysdevGuid}.sysdev"),
                $"<Device xmlns=\"{LibElNs}\" ID=\"{SysdevGuid}\" Name=\"EcoRT_0\" Type=\"Soft_dPAC\" Namespace=\"SE.DPAC\" />");
            File.WriteAllText(Path.Combine(sys, $"{SysappGuid}.sysapp"),
                $"<Application xmlns=\"{LibElNs}\" Name=\"APP1\" ID=\"{SysappGuid}\" />");
            File.WriteAllText(Path.Combine(iec, "System", $"{SysGuid}.system"),
                $"<System xmlns=\"{LibElNs}\" Name=\"System\" ID=\"{SysGuid}\" />");

            // syslay with Feeder + Feed_Station FBs (the canonical fixture instance set).
            var syslayDir = Path.Combine(sys, SysappGuid);
            Directory.CreateDirectory(syslayDir);
            var syslayPath = Path.Combine(syslayDir, "00000000-0000-0000-0000-000000000000.syslay");
            File.WriteAllText(syslayPath, $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Layer xmlns=""{LibElNs}"">
  <SubAppNetwork>
    <FB ID=""1"" Name=""Feeder""       Type=""Five_State_Actuator_CAT"" Namespace=""Main"" />
    <FB ID=""2"" Name=""Feed_Station"" Type=""Process1_Generic""        Namespace=""Main"" />
  </SubAppNetwork>
</Layer>");

            // Baseline .hcf folder (verbatim copy source for HwConfigCopier).
            var bSys = Path.Combine(baselineRoot, "IEC61499", "System", SysGuid, SysdevGuid);
            Directory.CreateDirectory(bSys);
            File.WriteAllText(Path.Combine(bSys, $"{SysdevGuid}.hcf"), SyntheticHcfTemplate);
            Directory.CreateDirectory(Path.Combine(baselineRoot, "HwConfiguration"));

            return new MapperConfig
            {
                SyslayPath2 = syslayPath,
                SyslayPath = syslayPath,
                M262HardwareConfigBaselinePath = baselineRoot,
                M262TargetIp = targetIp,
            };
        }

        static IoBindings FeederPinBindings() => new IoBindings
        {
            Actuators =
            {
                ["Feeder"] = new ActuatorBinding("Feeder",
                    AthomeTag: "PusherAtHome", AtworkTag: "PusherAtWork",
                    OutputToWorkTag: "ExtendPusher", OutputToHomeTag: null),
            },
            PinAssignments =
            {
                ["DI00"] = new PinAssignment("DI00", "Feeder", "athome"),
                ["DI01"] = new PinAssignment("DI01", "Feeder", "atwork"),
                ["DO00"] = new PinAssignment("DO00", "Feeder", "OutputToWork"),
            },
        };

        // ---------------- sysdev assertions ----------------

        // [Fact]
        public void Sysdev_HasTypeM262_dPAC()
        {
            var cfg = BuildScenario(out _, out _);
            var result = M262SysdevEmitter.Emit(cfg);

            var doc = XDocument.Load(result.SysdevPath);
            Assert.Equal("M262_dPAC", (string?)doc.Root!.Attribute("Type"));
            Assert.Equal("SE.DPAC",   (string?)doc.Root!.Attribute("Namespace"));
        }

        // [Fact]
        public void Sysdev_HasIPV4AddressParameterFromConfig()
        {
            var cfg = BuildScenario(out _, out _, targetIp: "10.42.7.5");
            var result = M262SysdevEmitter.Emit(cfg);

            var doc = XDocument.Load(result.SysdevPath);
            var ns = (XNamespace)LibElNs;
            var ip = doc.Root!.Elements(ns + "Parameter")
                .FirstOrDefault(p => (string?)p.Attribute("Name") == "IPV4Address");
            Assert.NotNull(ip);
            Assert.Equal("10.42.7.5", (string?)ip!.Attribute("Value"));
        }

        // [Fact]
        public void Sysdev_HasResourceRes0EmbResEcoRuntimeManagement()
        {
            var cfg = BuildScenario(out _, out _);
            var result = M262SysdevEmitter.Emit(cfg);

            var doc = XDocument.Load(result.SysdevPath);
            var ns = (XNamespace)LibElNs;
            var res0 = doc.Descendants(ns + "Resource").Single();
            Assert.Equal("RES0",                (string?)res0.Attribute("Name"));
            Assert.Equal("EMB_RES_ECO",         (string?)res0.Attribute("Type"));
            Assert.Equal("Runtime.Management",  (string?)res0.Attribute("Namespace"));
        }

        // ---------------- Mappings (per-FB walk) ----------------

        // [Fact]
        public void System_HasOneMappingPerSyslayFb_ToEcoRT_RES0()
        {
            var cfg = BuildScenario(out _, out _);
            var result = M262SysdevEmitter.Emit(cfg);

            // Two FBs in the syslay -> two mappings.
            Assert.Equal(2, result.MappingsAdded);

            var doc = XDocument.Load(result.SystemFilePath);
            var ns = (XNamespace)LibElNs;
            var mappings = doc.Descendants(ns + "Mapping").ToList();
            Assert.Equal(2, mappings.Count);
            Assert.Contains(mappings, m =>
                (string?)m.Attribute("From") == "APP1.Feeder" &&
                (string?)m.Attribute("To")   == "EcoRT_0.RES0");
            Assert.Contains(mappings, m =>
                (string?)m.Attribute("From") == "APP1.Feed_Station" &&
                (string?)m.Attribute("To")   == "EcoRT_0.RES0");
        }

        // [Fact]
        public void System_MappingsScaleWithSyslayFbCount()
        {
            // Add 5 more FBs to the syslay; expect 7 total mappings.
            var cfg = BuildScenario(out var eaeRoot, out _);
            var syslay = cfg.ActiveSyslayPath;
            var doc = XDocument.Load(syslay);
            var ns = (XNamespace)LibElNs;
            var net = doc.Root!.Element(ns + "SubAppNetwork")!;
            foreach (var name in new[] { "Area", "Station1", "Process1", "PartInHopper", "Stn1_Term" })
                net.Add(new XElement(ns + "FB",
                    new XAttribute("ID", name),
                    new XAttribute("Name", name),
                    new XAttribute("Type", "X")));
            doc.Save(syslay);

            var result = M262SysdevEmitter.Emit(cfg);
            Assert.Equal(7, result.MappingsAdded);
        }

        // [Fact]
        public void System_MappingEmissionIsIdempotent()
        {
            var cfg = BuildScenario(out _, out _);
            var first  = M262SysdevEmitter.Emit(cfg);
            var second = M262SysdevEmitter.Emit(cfg);

            Assert.Equal(2, first.MappingsAdded);
            Assert.Equal(0, second.MappingsAdded);
        }

        // ---------------- .hcf symbol substitution ----------------

        // [Fact]
        public void Hcf_ParameterValueSymbolsMatchRES0SyslayInstanceFormat()
        {
            var cfg = BuildScenario(out _, out _);
            // Run sysdev first so a sysdev exists for HwConfigCopier to find.
            M262SysdevEmitter.Emit(cfg);

            var hcfResult = M262HwConfigCopier.Copy(cfg, FeederPinBindings());

            var doc = XDocument.Load(hcfResult.HcfPath!);
            string Get(string pin) => doc.Descendants("ParameterValue")
                .First(e => (string?)e.Attribute("Name") == pin)
                .Attribute("Value")!.Value;

            // Spec format: 'RES0.<syslayInstanceName>.<portName>' with literal single quotes.
            Assert.Equal("'RES0.Feeder.athome'",       Get("DI00"));
            Assert.Equal("'RES0.Feeder.atwork'",       Get("DI01"));
            Assert.Equal("'RES0.Feeder.OutputToWork'", Get("DO00"));
        }

        // [Fact]
        public void Hcf_OnlyParameterValuesOnTM3IoModulesAreModified()
        {
            var cfg = BuildScenario(out _, out _);
            M262SysdevEmitter.Emit(cfg);
            var hcfResult = M262HwConfigCopier.Copy(cfg, FeederPinBindings());

            var doc = XDocument.Load(hcfResult.HcfPath!);
            // The BMTM3 root has a busCycleTime ParameterValue â€” must NOT be touched.
            var bmtm3 = doc.Descendants("ConfigurationBaseItem")
                .First(e => e.Element("Name")?.Value == "BMTM3");
            var busCycle = bmtm3.Element("ParameterValues")!
                .Elements("ParameterValue")
                .First(e => (string?)e.Attribute("Name") == "busCycleTime");
            Assert.Equal("T#80ms", (string?)busCycle.Attribute("Value"));
        }

        // ---------------- .dfbproj registration ----------------

        // [Fact]
        public void Dfbproj_RegistersSysdevAsSystemDeviceCompileEntry()
        {
            var cfg = BuildScenario(out var eaeRoot, out _);
            M262SysdevEmitter.Emit(cfg);

            var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
            var doc = XDocument.Load(dfbproj);
            var ns = doc.Root!.GetDefaultNamespace();
            var sysdevEntry = doc.Descendants(ns + "Compile")
                .FirstOrDefault(e => ((string?)e.Attribute("Include") ?? "").EndsWith(".sysdev"));
            Assert.NotNull(sysdevEntry);
            Assert.Equal("SystemDevice", (string?)sysdevEntry!.Element(ns + "IEC61499Type"));
        }

        // [Fact]
        public void Dfbproj_RegistersHcfAsNoneWithDependentUponSysdev()
        {
            var cfg = BuildScenario(out var eaeRoot, out _);
            M262SysdevEmitter.Emit(cfg);
            M262HwConfigCopier.Copy(cfg, FeederPinBindings());

            // Re-run sysdev emit so the dfbproj sweep picks up the freshly-copied .hcf
            // sibling file. (RegisterSystemDevice walks the sysdev folder for siblings.)
            M262SysdevEmitter.Emit(cfg);

            var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
            var doc = XDocument.Load(dfbproj);
            var ns = doc.Root!.GetDefaultNamespace();
            var hcfEntry = doc.Descendants(ns + "None")
                .FirstOrDefault(e => ((string?)e.Attribute("Include") ?? "").EndsWith(".hcf"));
            Assert.NotNull(hcfEntry);
            Assert.Equal("SystemDevice", (string?)hcfEntry!.Element(ns + "IEC61499Type"));
            Assert.Equal($"{SysdevGuid}.sysdev", (string?)hcfEntry.Element(ns + "DependentUpon"));
        }

        // [Fact]
        public void Dfbproj_DeduplicatesPriorBrokenIEC61499TypeAndDependentUponDuplicates()
        {
            // Simulate a broken prior deploy: an existing <None> with FOUR copies of
            // <IEC61499Type> and <DependentUpon> children. Re-running emit must
            // collapse duplicates back to one each.
            var cfg = BuildScenario(out var eaeRoot, out _);
            var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
            var brokenInclude = $@"System\{SysGuid}\{SysdevGuid}\{SysdevGuid}.hcf";
            File.WriteAllText(dfbproj, $@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup>
    <None Include=""{brokenInclude}"">
      <IEC61499Type>SystemDevice</IEC61499Type>
      <DependentUpon>{SysdevGuid}.sysdev</DependentUpon>
      <IEC61499Type>SystemDevice</IEC61499Type>
      <DependentUpon>{SysdevGuid}.sysdev</DependentUpon>
      <IEC61499Type>SystemDevice</IEC61499Type>
      <DependentUpon>{SysdevGuid}.sysdev</DependentUpon>
      <IEC61499Type>SystemDevice</IEC61499Type>
      <DependentUpon>{SysdevGuid}.sysdev</DependentUpon>
    </None>
  </ItemGroup>
</Project>");

            // Need the .hcf to actually exist so RegisterSystemDevice's sibling sweep
            // sees it (this is what the deploy run will see in production).
            var sysdevFolder = Path.Combine(eaeRoot, "IEC61499", "System", SysGuid, SysdevGuid);
            Directory.CreateDirectory(sysdevFolder);
            File.WriteAllText(Path.Combine(sysdevFolder, $"{SysdevGuid}.hcf"), "<x/>");

            M262SysdevEmitter.Emit(cfg);

            var doc = XDocument.Load(dfbproj);
            var ns = doc.Root!.GetDefaultNamespace();
            var entry = doc.Descendants(ns + "None")
                .First(e => (string?)e.Attribute("Include") == brokenInclude);
            Assert.Single(entry.Elements(ns + "IEC61499Type"));
            Assert.Single(entry.Elements(ns + "DependentUpon"));
        }
    }
}
