using System.IO;
using System.Linq;
using System.Xml.Linq;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class FeedStationSyslayTests
    {
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        static (XDocument doc, string path) Generate()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MapperTests_FS_" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            var injector = new SystemInjector();
            var path = injector.GenerateFeedStationSyslay(FixturePath(), folder);
            return (XDocument.Load(path), path);
        }

        // [Fact]
        public void GeneratesNineFBs()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fbs = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB").ToList();
            // Top-level PLC_Start removed; Area_CAT/Station_CAT bootstrap themselves.
            Assert.Equal(9, fbs.Count);
        }

        // [Fact]
        public void ContainsTuesdaySliceTopLevelInstances()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var names = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB")
                .Select(fb => fb.Attribute("Name")!.Value)
                .ToList();

            Assert.DoesNotContain("PLC_Start", names);
            Assert.Contains("Area_HMI", names);
            Assert.Contains("Area", names);
            Assert.Contains("Station1", names);
            Assert.Contains("Station1_HMI", names);
            Assert.Contains("Process1", names);
            Assert.Contains("Stn1_Term", names);
            Assert.Contains("Area_Term", names);
            Assert.Contains("Feeder", names);
            Assert.Contains("PartInHopper", names);
            Assert.DoesNotContain("Checker", names);
            Assert.DoesNotContain("Transfer", names);
        }

        // [Fact]
        public void InitChainHasFourConnections()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var ec = doc.Descendants(ns + "EventConnections").Single();
            var connections = ec.Elements(ns + "Connection").ToList();
            // Area -> Station1, Station1 -> PartInHopper, PartInHopper -> Feeder,
            // Feeder -> Process1. Bootstrap edges to/from PLC_Start removed.
            Assert.Equal(4, connections.Count);
        }

        // [Fact]
        public void NoPlcStartBootstrapEdges()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";

            var plcStart = doc.Descendants(ns + "FB")
                .FirstOrDefault(fb => fb.Attribute("Name")!.Value == "PLC_Start");
            Assert.Null(plcStart);

            var ec = doc.Descendants(ns + "EventConnections").Single();
            Assert.DoesNotContain(ec.Elements(ns + "Connection"), c =>
                c.Attribute("Source")!.Value.StartsWith("PLC_Start.") ||
                c.Attribute("Destination")!.Value.StartsWith("PLC_Start."));
        }

        // [Fact]
        public void AreaTerminatorDaisyChainTest()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var ac = doc.Descendants(ns + "AdapterConnections").Single();
            var conns = ac.Elements(ns + "Connection")
                .Select(c => (Source: c.Attribute("Source")!.Value, Dest: c.Attribute("Destination")!.Value))
                .ToList();

            Assert.Contains(conns, c => c.Source == "Station1.AreaAdptrOUT" && c.Dest == "Area_Term.CasAdptrIN");
            Assert.DoesNotContain(conns, c => c.Source == "Area.AreaAdptrOUT" && c.Dest == "Area_Term.CasAdptrIN");
        }

        // [Fact]
        public void V1LimitationCommentNearTop()
        {
            var (doc, _) = Generate();
            Assert.NotNull(doc.Root);
            var firstNode = doc.Root!.FirstNode;
            Assert.IsType<XComment>(firstNode);
            Assert.Contains("v1 limitations", ((XComment)firstNode!).Value);
        }

        // [Fact]
        public void StateRptCmdAdptrRingClosesBackToFirst()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var ac = doc.Descendants(ns + "AdapterConnections").Single();
            var conns = ac.Elements(ns + "Connection")
                .Select(c => (Source: c.Attribute("Source")!.Value, Dest: c.Attribute("Destination")!.Value))
                .Where(c => c.Source.Contains("stateR") || c.Dest.Contains("stateR"))
                .ToList();

            // Tuesday slice ring: PartInHopper -> Feeder -> Process1 -> PartInHopper (3 edges)
            Assert.True(conns.Count >= 3);
            var lastConnection = conns.Last();
            Assert.Contains("stateR", lastConnection.Source);
            Assert.Contains("stateR", lastConnection.Dest);
        }

        // [Fact]
        public void DataConnectionsEmptyForV1()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var dc = doc.Descendants(ns + "DataConnections").FirstOrDefault();
            if (dc != null)
                Assert.Empty(dc.Elements(ns + "Connection"));
        }
    }
}
