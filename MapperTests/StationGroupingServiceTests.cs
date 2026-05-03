using System.IO;
using System.Linq;
using CodeGen.IO;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    public class StationGroupingServiceTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        [Fact]
        public void GroupsFeedStationCorrectly()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var feedStation = components.First(c => c.Type == "Process" && c.Name == "Feed_Station");

            var service = new StationGroupingService();
            var contents = service.GroupStationContents(feedStation, components);

            Assert.Equal(3, contents.Actuators.Count);
            Assert.Equal(2, contents.Sensors.Count);
            Assert.Contains(contents.Actuators, c => c.Name == "Feeder");
            Assert.Contains(contents.Actuators, c => c.Name == "Checker");
            Assert.Contains(contents.Actuators, c => c.Name == "Transfer");
            Assert.Contains(contents.Sensors, c => c.Name == "PartInHopper");
            Assert.Contains(contents.Sensors, c => c.Name == "PartAtChecker");
        }

        [Fact]
        public void ExcludesProcessAndRobotReferences()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var feedStation = components.First(c => c.Type == "Process" && c.Name == "Feed_Station");

            var service = new StationGroupingService();
            var contents = service.GroupStationContents(feedStation, components);

            Assert.DoesNotContain(contents.Actuators, c => c.Type == "Process");
            Assert.DoesNotContain(contents.Sensors, c => c.Type == "Process");
            Assert.DoesNotContain(contents.Actuators, c => c.Type == "Robot");
        }

        [Fact]
        public void ThrowsOnNonProcessComponent()
        {
            var reader = new SystemXmlReader();
            var components = reader.ReadAllComponents(FixturePath());
            var actuator = components.First(c => c.Type == "Actuator");

            var service = new StationGroupingService();
            Assert.Throws<System.ArgumentException>(() =>
                service.GroupStationContents(actuator, components));
        }
    }
}
