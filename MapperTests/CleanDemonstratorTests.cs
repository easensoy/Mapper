using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class CleanDemonstratorTests
    {
        static (string syslayPath, string sysresPath) MakeFixtureFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "MapperClean_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var syslay = Path.Combine(dir, "test.syslay");
            var sysres = Path.Combine(dir, "test.sysres");

            var ns = (XNamespace)"https://www.se.com/LibraryElements";

            var syslayDoc = new XDocument(
                new XElement(ns + "Layer",
                    new XAttribute("ID", "AAA"),
                    new XAttribute("Name", "Default"),
                    new XAttribute("IsDefault", "true"),
                    new XElement(ns + "SubAppNetwork",
                        new XElement(ns + "FB", new XAttribute("Name", "Pusher"),
                            new XAttribute("Type", "Five_State_Actuator_CAT"), new XAttribute("Namespace", "Main")),
                        new XElement(ns + "FB", new XAttribute("Name", "Process1"),
                            new XAttribute("Type", "Process1_Generic"), new XAttribute("Namespace", "Main")),
                        new XElement(ns + "FB", new XAttribute("Name", "Station1"),
                            new XAttribute("Type", "Station"), new XAttribute("Namespace", "Main")),
                        new XElement(ns + "FB", new XAttribute("Name", "M262IO"),
                            new XAttribute("Type", "PLC_RW_M262"), new XAttribute("Namespace", "Main")),
                        new XElement(ns + "FB", new XAttribute("Name", "Robot1"),
                            new XAttribute("Type", "Robot_Task_CAT"), new XAttribute("Namespace", "Main")),
                        new XElement(ns + "EventConnections",
                            new XElement(ns + "Connection",
                                new XAttribute("Source", "Pusher.pst_out"),
                                new XAttribute("Destination", "Process1.state_change")),
                            new XElement(ns + "Connection",
                                new XAttribute("Source", "M262IO.INITO"),
                                new XAttribute("Destination", "Robot1.INIT"))))));

            syslayDoc.Save(syslay);

            var sysresDoc = new XDocument(
                new XElement(ns + "Layer",
                    new XAttribute("ID", "BBB"),
                    new XAttribute("Name", "Default"),
                    new XAttribute("IsDefault", "true"),
                    new XElement(ns + "FBNetwork",
                        new XElement(ns + "FB", new XAttribute("Name", "Pusher"),
                            new XAttribute("Type", "Five_State_Actuator_CAT"), new XAttribute("Namespace", "Main")),
                        new XElement(ns + "FB", new XAttribute("Name", "M262IO"),
                            new XAttribute("Type", "PLC_RW_M262"), new XAttribute("Namespace", "Main")))));

            sysresDoc.Save(sysres);

            return (syslay, sysres);
        }

        // [Fact]
        public void RemovesUniversalFbsAndPreservesNonUniversal()
        {
            var (syslay, sysres) = MakeFixtureFiles();
            var cfg = new MapperConfig { SyslayPath2 = syslay, SysresPath2 = sysres };

            var injector = new SystemInjector();
            var report = injector.PrepareDemonstratorForGeneration(cfg);

            Assert.Contains(report.RemovedFbs, n => n.StartsWith("Pusher"));
            Assert.Contains(report.RemovedFbs, n => n.StartsWith("Process1"));
            Assert.Contains(report.RemovedFbs, n => n.StartsWith("Station1"));
            Assert.Contains(report.PreservedFbs, n => n.StartsWith("M262IO"));
            Assert.Contains(report.PreservedFbs, n => n.StartsWith("Robot1"));
            Assert.True(report.RemovedConnections >= 1);

            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var doc = XDocument.Load(syslay);
            var fbNames = doc.Descendants(ns + "FB").Select(fb => fb.Attribute("Name")!.Value).ToList();
            Assert.DoesNotContain("Pusher", fbNames);
            Assert.DoesNotContain("Process1", fbNames);
            Assert.DoesNotContain("Station1", fbNames);
            Assert.Contains("M262IO", fbNames);
            Assert.Contains("Robot1", fbNames);

            var conns = doc.Descendants(ns + "Connection").ToList();
            Assert.DoesNotContain(conns, c => c.Attribute("Source")!.Value.StartsWith("Pusher."));
            Assert.Contains(conns, c => c.Attribute("Source")!.Value.StartsWith("M262IO."));
        }

        // [Fact]
        public void ThrowsWhenSyslayPath2IsMissing()
        {
            var cfg = new MapperConfig { SyslayPath2 = "C:/nonexistent/file.syslay", SysresPath2 = "" };
            var injector = new SystemInjector();
            Assert.Throws<FileNotFoundException>(() => injector.PrepareDemonstratorForGeneration(cfg));
        }

        // [Fact]
        public void ThrowsWhenSyslayPath2IsEmpty()
        {
            var cfg = new MapperConfig { SyslayPath2 = string.Empty, SysresPath2 = string.Empty };
            var injector = new SystemInjector();
            Assert.Throws<FileNotFoundException>(() => injector.PrepareDemonstratorForGeneration(cfg));
        }

        // [Fact]
        public void GeneratePusherTestSyslayToPathWritesToExactFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "PusherDirect_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "MyCustom.syslay");

            var injector = new SystemInjector();
            var path = injector.GeneratePusherTestSyslayToPath(target);

            Assert.Equal(target, path);
            Assert.True(File.Exists(target));
        }
    }
}
