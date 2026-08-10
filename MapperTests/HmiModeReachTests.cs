using System.IO;
using CodeGen.Hmi;
using Xunit;

namespace MapperTests
{
    // Mode reachability is read out of the generated syslay's adapter graph, never assumed.
    //
    // The subtlety this class exists for: only the *_CAT companion carries an HMI contract, so the
    // tile the operator sees is the FACEPLATE (Station1_HMI) while the node on the mode chain is the
    // CORE (Station1). The graph walk deliberately excludes the faceplate, so asking about it
    // directly always answers "no" - which reported every station as unreachable and suppressed the
    // mode/cycle legend on a plant where the chain was fully wired.
    public class HmiModeReachTests
    {
        private const string Syslay = """
            <SubAppNetwork>
              <FB Name="Station1_HMI" Type="Station_CAT" ID="AAA" />
              <FB Name="Station1" Type="Station" ID="BBB" />
              <FB Name="Feeder" Type="Five_State_Actuator_CAT" ID="CCC" />
              <FB Name="Orphan" Type="Five_State_Actuator_CAT" ID="DDD" />
              <AdapterConnections>
                <Connection Source="Station1_HMI.StationHMIAdptrOUT" Destination="Station1.StationHMIAdptrIN" />
                <Connection Source="Station1.stationAdptr_out" Destination="Feeder.stationAdptr_in" />
              </AdapterConnections>
            </SubAppNetwork>
            """;

        private static HmiModeReach Load()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".syslay");
            File.WriteAllText(path, Syslay);
            try { return HmiModeReach.FromSyslay(path); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void CoreOnTheChainIsReachedAndDownstreamFollows()
        {
            var reach = Load();

            Assert.True(reach.Reaches("Station1"));
            Assert.True(reach.Reaches("Feeder"));
            Assert.False(reach.Reaches("Orphan"));
        }

        [Fact]
        public void FaceplateIsNotItselfAPlantNode()
        {
            // Intentional: the faceplate is the operator's entry point, not a node the broadcast
            // travels through. Direct reachability must stay false so the graph walk is honest.
            Assert.False(Load().Reaches("Station1_HMI"));
        }

        // The regression: the station tile is the faceplate, so its reachability must resolve through
        // the core it drives - otherwise a fully-wired chain reports "no mode broadcast".
        [Fact]
        public void FaceplateReachabilityResolvesThroughTheCoreItDrives()
        {
            var reach = Load();

            Assert.Equal("Station1", reach.CoreDrivenBy("Station1_HMI"));
            Assert.True(reach.ReachesThrough("Station1_HMI"));
        }

        [Fact]
        public void AnInstanceThatDrivesNoCoreIsJudgedOnItself()
        {
            var reach = Load();

            Assert.Null(reach.CoreDrivenBy("Feeder"));
            Assert.True(reach.ReachesThrough("Feeder"));
            Assert.False(reach.ReachesThrough("Orphan"));
        }
    }
}
