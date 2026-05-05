using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class ProcessFBSyslayTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        static MapperConfig MakeConfig(out string syslay, out string sysres)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ProcFB_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            syslay = Path.Combine(dir, "test.syslay");
            sysres = Path.Combine(dir, "test.sysres");
            File.WriteAllText(syslay, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
            File.WriteAllText(sysres, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");
            return new MapperConfig { SyslayPath2 = syslay, SysresPath2 = sysres };
        }

        // [Fact]
        public void Button1ProducesOneProcessFbWithOnlyOuterParameters()
        {
            var cfg = MakeConfig(out _, out _);
            var injector = new SystemInjector();
            injector.PrepareDemonstratorForGeneration(cfg);

            SystemInjector.BindingApplicationReport report = null!;
            var path = injector.GenerateProcessFBSyslay(cfg, FixturePath(), null, out report);

            var doc = XDocument.Load(path);
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fbs = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB").ToList();
            Assert.Single(fbs);
            Assert.Equal("Process1_Generic", fbs[0].Attribute("Type")!.Value);
            Assert.Empty(fbs[0].Elements(ns + "FB"));

            var paramNames = fbs[0].Elements(ns + "Parameter")
                .Select(p => p.Attribute("Name")!.Value).ToList();
            Assert.Equal(2, paramNames.Count);
            Assert.Contains("process_name", paramNames);
            Assert.Contains("process_id", paramNames);
        }
    }

    public class TestStation1SyslayTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");
        static string BindingsPath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

        // [Fact]
        public void Button2ProducesFullSliceWithFeederBound()
        {
            var dir = Path.Combine(Path.GetTempPath(), "TS1_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var syslay = Path.Combine(dir, "test.syslay");
            var sysres = Path.Combine(dir, "test.sysres");
            File.WriteAllText(syslay, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
            File.WriteAllText(sysres, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");
            var cfg = new MapperConfig { SyslayPath2 = syslay, SysresPath2 = sysres };

            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(BindingsPath());

            var injector = new SystemInjector();
            injector.PrepareDemonstratorForGeneration(cfg);

            SystemInjector.BindingApplicationReport report = null!;
            var path = injector.GenerateStation1TestSyslay(cfg, FixturePath(), bindings, out report);

            var doc = XDocument.Load(path);
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fbs = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB").ToList();
            // Top-level PLC_Start removed (Area_CAT/Station_CAT bootstrap themselves).
            Assert.Equal(9, fbs.Count);

            var expectedNames = new[]
            {
                "Area_HMI", "Area", "Station1", "Station1_HMI",
                "Process1", "Feeder", "PartInHopper", "Stn1_Term", "Area_Term"
            };
            foreach (var name in expectedNames)
                Assert.Contains(fbs, f => f.Attribute("Name")!.Value == name);
            Assert.DoesNotContain(fbs, f => f.Attribute("Name")!.Value == "PLC_Start");

            foreach (var fb in fbs)
                Assert.Empty(fb.Elements(ns + "FB"));

            var feeder = fbs.First(f => f.Attribute("Name")!.Value == "Feeder");
            var feederParamNames = feeder.Elements(ns + "Parameter")
                .Select(p => p.Attribute("Name")!.Value).ToHashSet();
            var expectedActuatorParams = new[]
            {
                "actuator_name", "actuator_id", "WorkSensorFitted", "HomeSensorFitted",
                "toWorkTime", "toHomeTime", "faultTimeoutWork", "faultTimeoutHome",
                "enableToWorkFaultTimeout", "enableToHomeFaultTimeout"
            };
            foreach (var p in expectedActuatorParams)
                Assert.Contains(p, feederParamNames);
            Assert.Equal(expectedActuatorParams.Length, feederParamNames.Count);

            var process1 = fbs.First(f => f.Attribute("Name")!.Value == "Process1");
            var process1Params = process1.Elements(ns + "Parameter")
                .Select(p => p.Attribute("Name")!.Value).ToList();
            Assert.Equal(2, process1Params.Count);
            Assert.Contains("process_name", process1Params);
            Assert.Contains("process_id", process1Params);
        }

        // [Fact]
        public void Button2OutputHasNoNestedFBsAnywhere()
        {
            var dir = Path.Combine(Path.GetTempPath(), "TS1Nest_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var syslay = Path.Combine(dir, "test.syslay");
            var sysres = Path.Combine(dir, "test.sysres");
            File.WriteAllText(syslay, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
            File.WriteAllText(sysres, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");
            var cfg = new MapperConfig { SyslayPath2 = syslay, SysresPath2 = sysres };

            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(BindingsPath());
            var injector = new SystemInjector();
            injector.PrepareDemonstratorForGeneration(cfg);
            SystemInjector.BindingApplicationReport report = null!;
            var path = injector.GenerateStation1TestSyslay(cfg, FixturePath(), bindings, out report);

            var doc = XDocument.Load(path);
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            foreach (var fb in doc.Descendants(ns + "FB"))
                Assert.Empty(fb.Elements(ns + "FB"));
        }
    }

    public class GenerateAllSyslayTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");
        static string BindingsPath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

        // [Fact]
        public void Button3ProducesAtLeastOneStationFromFixture()
        {
            var dir = Path.Combine(Path.GetTempPath(), "GenAll_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var syslay = Path.Combine(dir, "test.syslay");
            var sysres = Path.Combine(dir, "test.sysres");
            File.WriteAllText(syslay, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
            File.WriteAllText(sysres, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");
            var cfg = new MapperConfig { SyslayPath2 = syslay, SysresPath2 = sysres };

            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(BindingsPath());

            var injector = new SystemInjector();
            injector.PrepareDemonstratorForGeneration(cfg);

            SystemInjector.BindingApplicationReport report = null!;
            var path = injector.GenerateFullSystemSyslay(cfg, FixturePath(), bindings, out report);

            var doc = XDocument.Load(path);
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fbs = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB").ToList();

            var stationCount = fbs.Count(f => f.Attribute("Type")!.Value == "Station");
            var areaCount = fbs.Count(f => f.Attribute("Type")!.Value == "Area");
            var processCount = fbs.Count(f => f.Attribute("Type")!.Value == "Process1_Generic");

            Assert.True(stationCount >= 1, $"Expected at least 1 Station, got {stationCount}");
            Assert.Equal(1, areaCount);
            Assert.True(processCount >= 1, $"Expected at least 1 Process1_Generic, got {processCount}");

            var ac = doc.Descendants(ns + "AdapterConnections").Single();
            var conns = ac.Elements(ns + "Connection").ToList();
            Assert.Contains(conns, c => c.Attribute("Source")!.Value == "Area.AreaAdptrOUT");
        }

        // [Fact]
        public void Button3HasNoTopLevelPlcStart()
        {
            var dir = Path.Combine(Path.GetTempPath(), "GenAllPlc_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var syslay = Path.Combine(dir, "test.syslay");
            var sysres = Path.Combine(dir, "test.sysres");
            File.WriteAllText(syslay, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
            File.WriteAllText(sysres, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");
            var cfg = new MapperConfig { SyslayPath2 = syslay, SysresPath2 = sysres };

            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(BindingsPath());
            var injector = new SystemInjector();
            injector.PrepareDemonstratorForGeneration(cfg);

            SystemInjector.BindingApplicationReport report = null!;
            var path = injector.GenerateFullSystemSyslay(cfg, FixturePath(), bindings, out report);

            var doc = XDocument.Load(path);
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fbs = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB").ToList();
            Assert.DoesNotContain(fbs, f => f.Attribute("Name")!.Value == "PLC_Start");

            var ec = doc.Descendants(ns + "EventConnections").SingleOrDefault();
            if (ec != null)
            {
                Assert.DoesNotContain(ec.Elements(ns + "Connection"), c =>
                    c.Attribute("Source")!.Value.StartsWith("PLC_Start.") ||
                    c.Attribute("Destination")!.Value.StartsWith("PLC_Start."));
            }
        }
    }
}
