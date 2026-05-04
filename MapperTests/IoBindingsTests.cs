using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class IoBindingsTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

        [Fact]
        public void FeederBindingResolvesCorrectly()
        {
            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(FixturePath());

            Assert.True(bindings.Actuators.ContainsKey("Feeder"));
            var feeder = bindings.Actuators["Feeder"];
            Assert.Equal("PusherAtHome", feeder.AthomeTag);
            Assert.Equal("PusherAtWork", feeder.AtworkTag);
            Assert.Equal("ExtendPusher", feeder.OutputToWorkTag);
            Assert.Null(feeder.OutputToHomeTag);
        }

        [Fact]
        public void PartInHopperBindingResolvesCorrectly()
        {
            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(FixturePath());

            Assert.True(bindings.Sensors.ContainsKey("PartInHopper"));
            var hopper = bindings.Sensors["PartInHopper"];
            Assert.Equal("Hopper", hopper.InputTag);
        }

        [Fact]
        public void BearingSensorHasNullTagWithoutThrowing()
        {
            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(FixturePath());

            Assert.True(bindings.Sensors.ContainsKey("BearingSensor"));
            Assert.Null(bindings.Sensors["BearingSensor"].InputTag);
        }

        [Fact]
        public void ThrowsOnMissingFile()
        {
            IoBindingsLoader.InvalidateCache();
            Assert.Throws<FileNotFoundException>(() =>
                IoBindingsLoader.LoadBindings("C:/nonexistent/file.xlsx"));
        }
    }

    public class SymlinkOverrideTests
    {
        static string BindingsFixture() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

        [Fact]
        public void PusherSyslayContainsNestedNAME1OverrideForFeeder()
        {
            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(BindingsFixture());

            var dir = Path.Combine(Path.GetTempPath(), "PusherBind_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "Pusher_Test.syslay");

            var injector = new SystemInjector();
            SystemInjector.BindingApplicationReport report = null!;
            injector.GeneratePusherTestSyslayToPath(target, bindings, out report);

            var doc = XDocument.Load(target);
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var pusher = doc.Descendants(ns + "FB")
                .First(fb => fb.Attribute("Name")!.Value == "Pusher");
            var inputs = pusher.Elements(ns + "FB")
                .First(fb => fb.Attribute("Name")!.Value == "Inputs");
            var name1 = inputs.Elements(ns + "Parameter")
                .First(p => p.Attribute("Name")!.Value == "NAME1");
            Assert.Equal("'PusherAtHome'", name1.Attribute("Value")!.Value);

            var name2 = inputs.Elements(ns + "Parameter")
                .First(p => p.Attribute("Name")!.Value == "NAME2");
            Assert.Equal("'PusherAtWork'", name2.Attribute("Value")!.Value);

            var output = pusher.Elements(ns + "FB")
                .First(fb => fb.Attribute("Name")!.Value == "Output");
            var oName2 = output.Elements(ns + "Parameter")
                .First(p => p.Attribute("Name")!.Value == "NAME2");
            Assert.Equal("'ExtendPusher'", oName2.Attribute("Value")!.Value);
        }

        [Fact]
        public void MissingBindingsAreLoggedNotFatal()
        {
            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(BindingsFixture());

            var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");
            var dir = Path.Combine(Path.GetTempPath(), "FSBind_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "FS.syslay");

            var injector = new SystemInjector();
            SystemInjector.BindingApplicationReport report = null!;
            var path = injector.GenerateFeedStationSyslayToPath(fixturePath, target, bindings, out report);

            Assert.True(File.Exists(path));
            Assert.Contains(report.Bound, b => b.Component == "Feeder");
            Assert.Contains(report.Bound, b => b.Component == "PartInHopper");
        }
    }
}
