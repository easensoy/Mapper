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

        [Fact]
        public void Button1ProducesOneProcessFbWithSixRecipeArrays()
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

            var inner = fbs[0].Elements(ns + "FB")
                .First(f => f.Attribute("Name")!.Value == "ProcessRuntime_Generic_v1");
            var paramNames = inner.Elements(ns + "Parameter")
                .Select(p => p.Attribute("Name")!.Value).ToList();

            Assert.Contains("StepType", paramNames);
            Assert.Contains("CmdTargetName", paramNames);
            Assert.Contains("CmdStateArr", paramNames);
            Assert.Contains("Wait1Id", paramNames);
            Assert.Contains("Wait1State", paramNames);
            Assert.Contains("NextStep", paramNames);
        }
    }

    public class TestStation1SyslayTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");
        static string BindingsPath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

        [Fact]
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
            Assert.True(fbs.Count >= 10, $"Expected at least 10 top-level FBs, got {fbs.Count}");

            var feeder = fbs.First(f => f.Attribute("Name")!.Value == "Feeder");
            var inputs = feeder.Elements(ns + "FB").First(f => f.Attribute("Name")!.Value == "Inputs");
            var name1 = inputs.Elements(ns + "Parameter").First(p => p.Attribute("Name")!.Value == "NAME1");
            Assert.Equal("'PusherAtHome'", name1.Attribute("Value")!.Value);
        }
    }

    public class GenerateAllSyslayTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");
        static string BindingsPath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

        [Fact]
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
    }
}
