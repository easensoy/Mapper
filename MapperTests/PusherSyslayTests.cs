using System.IO;
using System.Linq;
using System.Xml.Linq;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class PusherSyslayTests
    {
        [Fact]
        public void GeneratesSingleFBWithAllTenParameters()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MapperTests_Pusher_" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            try
            {
                var injector = new SystemInjector();
                var path = injector.GeneratePusherTestSyslay(folder);

                Assert.True(File.Exists(path));
                Assert.EndsWith("Pusher_Test.syslay", path);

                var doc = XDocument.Load(path);
                var ns = (XNamespace)"https://www.se.com/LibraryElements";
                var fbs = doc.Descendants(ns + "FB").ToList();

                Assert.Single(fbs);
                Assert.Equal("Pusher", fbs[0].Attribute("Name")!.Value);
                Assert.Equal("Five_State_Actuator_CAT", fbs[0].Attribute("Type")!.Value);

                var parameters = fbs[0].Elements(ns + "Parameter")
                    .ToDictionary(p => p.Attribute("Name")!.Value, p => p.Attribute("Value")!.Value);

                Assert.Equal(10, parameters.Count);
                Assert.Equal("'pusher'", parameters["actuator_name"]);
                Assert.Equal("0", parameters["actuator_id"]);
                Assert.Equal("FALSE", parameters["WorkSensorFitted"]);
                Assert.Equal("FALSE", parameters["HomeSensorFitted"]);
                Assert.Equal("T#2000ms", parameters["toWorkTime"]);
                Assert.Equal("T#2000ms", parameters["toHomeTime"]);
                Assert.Equal("FALSE", parameters["enableToWorkFaultTimeout"]);
                Assert.Equal("FALSE", parameters["enableToHomeFaultTimeout"]);
                Assert.Equal("T#4000ms", parameters["faultTimeoutWork"]);
                Assert.Equal("T#4000ms", parameters["faultTimeoutHome"]);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void ThrowsWhenOutputFolderDoesNotExist()
        {
            var folder = Path.Combine(Path.GetTempPath(), "DoesNotExist_" + Path.GetRandomFileName());
            var injector = new SystemInjector();
            Assert.Throws<DirectoryNotFoundException>(() => injector.GeneratePusherTestSyslay(folder));
        }
    }
}
