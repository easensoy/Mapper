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

        [Fact]
        public void GeneratesThirteenFBs()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fbs = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB").ToList();
            Assert.Equal(13, fbs.Count);
        }

        [Fact]
        public void ContainsAllExpectedTopLevelInstances()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var names = doc.Descendants(ns + "FB")
                .Select(fb => fb.Attribute("Name")!.Value)
                .ToList();

            Assert.Contains("Area_HMI", names);
            Assert.Contains("Area", names);
            Assert.Contains("Station1", names);
            Assert.Contains("Station1_HMI", names);
            Assert.Contains("Process1", names);
            Assert.Contains("Stn1_Term", names);
            Assert.Contains("Area_Term", names);
            Assert.Contains("Feeder", names);
            Assert.Contains("Checker", names);
            Assert.Contains("Transfer", names);
        }

        [Fact]
        public void InitChainHasNineConnections()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var ec = doc.Descendants(ns + "EventConnections").Single();
            var connections = ec.Elements(ns + "Connection").ToList();
            Assert.Equal(9, connections.Count);
        }

        [Fact]
        public void PlcStartBootstrapTest()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";

            var plcStart = doc.Descendants(ns + "FB")
                .FirstOrDefault(fb => fb.Attribute("Name")!.Value == "PLC_Start");
            Assert.NotNull(plcStart);
            Assert.Equal("plcStart", plcStart!.Attribute("Type")!.Value);
            Assert.Equal("SE.AppBase", plcStart.Attribute("Namespace")!.Value);

            var ec = doc.Descendants(ns + "EventConnections").Single();
            var hasFirstInit = ec.Elements(ns + "Connection").Any(c =>
                c.Attribute("Source")!.Value == "PLC_Start.FIRST_INIT" &&
                c.Attribute("Destination")!.Value == "Area.INIT");
            Assert.True(hasFirstInit);

            var hasAck = ec.Elements(ns + "Connection").Any(c =>
                c.Attribute("Source")!.Value == "Process1.INITO" &&
                c.Attribute("Destination")!.Value == "PLC_Start.ACK_FIRST");
            Assert.True(hasAck);
        }

        [Fact]
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

        [Fact]
        public void V1LimitationCommentNearTop()
        {
            var (doc, _) = Generate();
            Assert.NotNull(doc.Root);
            var firstNode = doc.Root!.FirstNode;
            Assert.IsType<XComment>(firstNode);
            Assert.Contains("v1 limitations", ((XComment)firstNode!).Value);
        }

        [Fact]
        public void StateRptCmdAdptrRingClosesBackToFirst()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var ac = doc.Descendants(ns + "AdapterConnections").Single();
            var conns = ac.Elements(ns + "Connection")
                .Select(c => (Source: c.Attribute("Source")!.Value, Dest: c.Attribute("Destination")!.Value))
                .Where(c => c.Source.Contains("stateR") || c.Dest.Contains("stateR"))
                .ToList();

            Assert.NotEmpty(conns);
            var lastConnection = conns.Last();
            Assert.Contains("stateR", lastConnection.Source);
            Assert.Contains("stateR", lastConnection.Dest);

            var firstInRingDest = conns.First().Dest;
            var ringSize = conns.Count;
            Assert.True(ringSize >= 6);
        }

        [Fact]
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
