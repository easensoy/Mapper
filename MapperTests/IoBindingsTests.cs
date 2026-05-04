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
        public void PusherSyslayDoesNotEmitNestedFBs()
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
            var pusher = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB")
                .First(fb => fb.Attribute("Name")!.Value == "Pusher");
            Assert.Empty(pusher.Elements(ns + "FB"));
            Assert.True(report.Bound.Count > 0 || report.Missing.Count > 0);
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
