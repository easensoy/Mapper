using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class M262SysdevEmitterTests
    {
        const string LibElNs = "https://www.se.com/LibraryElements";

        // Builds a throwaway EAE-shaped project on disk so the emitter has the
        // dfbproj-anchored path it needs to derive the project root.
        static MapperConfig BuildFakeProject(out string eaeRoot,
            string sysdevType = "Soft_dPAC", bool emptyResources = true,
            string? systemBody = null)
        {
            eaeRoot = Path.Combine(Path.GetTempPath(), "M262Sysdev_" + Path.GetRandomFileName());
            var iec = Path.Combine(eaeRoot, "IEC61499");
            var sysGuid = "00000000-0000-0000-0000-000000000000";
            var sysdevGuid = "00000000-0000-0000-0000-000000000002";
            var sysappGuid = "00000000-0000-0000-0000-000000000001";
            var sys = Path.Combine(iec, "System", sysGuid);
            Directory.CreateDirectory(sys);
            File.WriteAllText(Path.Combine(iec, "IEC61499.dfbproj"),
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"/>");

            var resourcesXml = emptyResources ? "" :
                $@"  <Resources>
    <Resource ID=""00000000-0000-0000-0000-000000000000"" Name=""RES0"" Type=""EMB_RES_ECO"" Namespace=""Runtime.Management"" />
  </Resources>
";
            File.WriteAllText(Path.Combine(sys, sysdevGuid + ".sysdev"),
                $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Device xmlns=""{LibElNs}"" ID=""{sysdevGuid}"" Name=""EcoRT_0"" Type=""{sysdevType}"" Namespace=""SE.DPAC"">
{resourcesXml}</Device>");

            File.WriteAllText(Path.Combine(sys, sysappGuid + ".sysapp"),
                $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Application xmlns=""{LibElNs}"" Name=""APP1"" ID=""{sysappGuid}""/>");

            File.WriteAllText(Path.Combine(iec, "System", sysGuid + ".system"),
                systemBody ??
                $@"<?xml version=""1.0"" encoding=""utf-8""?>
<System xmlns=""{LibElNs}"" Name=""System"" ID=""{sysGuid}""/>");

            var syslayDir = Path.Combine(sys, sysappGuid);
            Directory.CreateDirectory(syslayDir);
            var syslayPath = Path.Combine(syslayDir, "00000000-0000-0000-0000-000000000000.syslay");
            File.WriteAllText(syslayPath, "<Layer/>");

            return new MapperConfig
            {
                SyslayPath2 = syslayPath,
                SyslayPath = syslayPath,
            };
        }

        [Fact]
        public void Emit_RewritesSysdevToM262_dPAC()
        {
            var cfg = BuildFakeProject(out _);
            var result = M262SysdevEmitter.Emit(cfg);

            var doc = XDocument.Load(result.SysdevPath);
            var root = doc.Root!;
            Assert.Equal("EcoRT_0",  (string?)root.Attribute("Name"));
            Assert.Equal("M262_dPAC", (string?)root.Attribute("Type"));
            Assert.Equal("SE.DPAC",   (string?)root.Attribute("Namespace"));
        }

        [Fact]
        public void Emit_AddsRes0EmbResEcoWhenAbsent()
        {
            var cfg = BuildFakeProject(out _, emptyResources: true);
            var result = M262SysdevEmitter.Emit(cfg);

            var doc = XDocument.Load(result.SysdevPath);
            var ns = (XNamespace)LibElNs;
            var res0 = doc.Descendants(ns + "Resource").Single();
            Assert.Equal("RES0",                (string?)res0.Attribute("Name"));
            Assert.Equal("EMB_RES_ECO",         (string?)res0.Attribute("Type"));
            Assert.Equal("Runtime.Management",  (string?)res0.Attribute("Namespace"));
        }

        [Fact]
        public void Emit_PreservesRes0WhenAlreadyCorrect()
        {
            var cfg = BuildFakeProject(out _, emptyResources: false);
            var result = M262SysdevEmitter.Emit(cfg);

            var doc = XDocument.Load(result.SysdevPath);
            var ns = (XNamespace)LibElNs;
            var resCount = doc.Descendants(ns + "Resource").Count();
            Assert.Equal(1, resCount);
        }

        [Fact]
        public void Emit_AddsBothMappingsToSystemRoot()
        {
            var cfg = BuildFakeProject(out _);
            var result = M262SysdevEmitter.Emit(cfg);

            Assert.Equal(2, result.MappingsAdded);

            var doc = XDocument.Load(result.SystemFilePath);
            var ns = (XNamespace)LibElNs;
            var mappings = doc.Descendants(ns + "Mapping").ToList();
            Assert.Contains(mappings, m =>
                (string?)m.Attribute("From") == "APP1.Feeder" &&
                (string?)m.Attribute("To")   == "EcoRT_0.RES0");
            Assert.Contains(mappings, m =>
                (string?)m.Attribute("From") == "APP1.Feed_Station" &&
                (string?)m.Attribute("To")   == "EcoRT_0.RES0");
        }

        [Fact]
        public void Emit_IsIdempotent()
        {
            var cfg = BuildFakeProject(out _);
            var first  = M262SysdevEmitter.Emit(cfg);
            var second = M262SysdevEmitter.Emit(cfg);

            Assert.Equal(2, first.MappingsAdded);
            Assert.Equal(0, second.MappingsAdded);

            var doc = XDocument.Load(second.SystemFilePath);
            var ns = (XNamespace)LibElNs;
            Assert.Equal(2, doc.Descendants(ns + "Mapping").Count());
        }

        [Fact]
        public void Emit_HandlesPreexistingMappings()
        {
            var systemBody = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<System xmlns=""{LibElNs}"" Name=""System"" ID=""00000000-0000-0000-0000-000000000000"">
  <Mappings>
    <Mapping From=""APP1.Feeder"" To=""EcoRT_0.RES0"" />
  </Mappings>
</System>";
            var cfg = BuildFakeProject(out _, systemBody: systemBody);
            var result = M262SysdevEmitter.Emit(cfg);

            // Only the missing Feed_Station mapping should be added.
            Assert.Equal(1, result.MappingsAdded);

            var doc = XDocument.Load(result.SystemFilePath);
            var ns = (XNamespace)LibElNs;
            Assert.Equal(2, doc.Descendants(ns + "Mapping").Count());
        }
    }
}
