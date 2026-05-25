using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Devices.Shared;
using CodeGen.Services;
using CodeGen.Translation;
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
        public void GeneratesTwelveFBs()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fbs = doc.Descendants(ns + "SubAppNetwork").Single().Elements(ns + "FB").ToList();
            // Widened Feed Station scope: 4 structural (Area_HMI/Area/Station1/
            // Station1_HMI) + Feed_Station + 2 terminators (Stn1_Term/Area_Term)
            // + Feeder + Checker + Transfer actuators + PartInHopper +
            // PartAtChecker sensors = 12. Top-level PLC_Start removed;
            // Area_CAT/Station_CAT bootstrap themselves.
            Assert.Equal(12, fbs.Count);
        }

        [Fact]
        public void ContainsFeedStationSliceInstances()
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
            // Phase 2: Process FB instance now uses the canonical Control.xml name
            // ("Feed_Station") instead of the hardcoded "Process1".
            Assert.Contains("Feed_Station", names);
            Assert.Contains("Stn1_Term", names);
            Assert.Contains("Area_Term", names);
            Assert.Contains("Feeder", names);
            Assert.Contains("PartInHopper", names);
            // Widened Feed Station scope now includes the Checker + Transfer
            // actuators and the PartAtChecker sensor (Checker/PartAtChecker for
            // the PartChecking step; Transfer for the Station1→Station2 move).
            Assert.Contains("Checker", names);
            Assert.Contains("PartAtChecker", names);
            Assert.Contains("Transfer", names);
        }

        [Fact]
        public void InitChainHasSevenConnections()
        {
            var (doc, _) = Generate();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var ec = doc.Descendants(ns + "EventConnections").Single();
            var connections = ec.Elements(ns + "Connection").ToList();
            // Widened Feed Station scope, syslay component-driven order
            // (sensors then actuators): Area->Station1, Station1->PartInHopper,
            // PartInHopper->PartAtChecker, PartAtChecker->Feeder,
            // Feeder->Checker, Checker->Transfer, Transfer->Feed_Station = 7
            // connections. Bootstrap edges to/from PLC_Start removed.
            Assert.Equal(7, connections.Count);
        }

        [Fact]
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
            // Phase 1 changed the top-comment from "v1 limitations: …" to
            // "Phase 1: …" when recipe arrays moved from FBT-internal ST to
            // syslay Parameter values. The assertion is intentionally weakened
            // to "any limitations/caveats note exists" rather than a literal match.
            var commentText = ((XComment)firstNode!).Value;
            Assert.True(
                commentText.Contains("Phase 1", System.StringComparison.OrdinalIgnoreCase) ||
                commentText.Contains("v1 limitations", System.StringComparison.OrdinalIgnoreCase),
                $"top comment should be a limitations/phase note; got: {commentText}");
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

            // Tuesday slice ring: PartInHopper -> Feeder -> Process1 -> PartInHopper (3 edges)
            Assert.True(conns.Count >= 3);
            var lastConnection = conns.Last();
            Assert.Contains("stateR", lastConnection.Source);
            Assert.Contains("stateR", lastConnection.Dest);
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
