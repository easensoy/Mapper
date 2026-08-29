using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using Xunit;

namespace MapperTests
{
    // A PRODUCER'S ENTRY PHASE IS READ PER EDGE.
    //
    // It used to be one plant-wide value, and that made a mixed deployment unrepresentable in the
    // worst direction: on a `readinessAssertion` plant, an edge that genuinely needed a runtime wait
    // compiled to NO WAIT AT ALL, was recorded as covered-by-declaration, and shipped a plant that did
    // not wait for something its own model named. The opposite setting failed loudly for every edge.
    public class HandoffPolicyTests
    {
        static PeerEntryPhaseRule Rule(PeerEntryPhaseMeaning m, string producer = "", string consumer = "",
                                       string producerState = "") =>
            new() { Meaning = m, Producer = producer, Consumer = consumer,
                    ProducerState = producerState, Because = "declared by this test" };

        [Fact]
        public void A_row_carrying_only_a_meaning_answers_every_edge()
        {
            var p = new HandoffPolicy { PeerEntryPhase = { Rule(PeerEntryPhaseMeaning.ReadinessAssertion) } };
            Assert.Equal(PeerEntryPhaseMeaning.ReadinessAssertion, p.MeaningFor("A", "B", "Entry"));
            Assert.Equal(PeerEntryPhaseMeaning.ReadinessAssertion, p.MeaningFor("X", "Y", "Start"));
        }

        [Fact]
        public void No_row_covering_an_edge_is_undeclared_and_therefore_refused()
        {
            var p = new HandoffPolicy { PeerEntryPhase = { Rule(PeerEntryPhaseMeaning.RuntimePhase, producer: "Feed") } };
            Assert.Equal(PeerEntryPhaseMeaning.RuntimePhase, p.MeaningFor("Feed", "Assembly", "Entry"));
            Assert.Equal(PeerEntryPhaseMeaning.Undeclared, p.MeaningFor("Other", "Assembly", "Entry"));
        }

        [Fact]
        public void One_deployment_can_hold_both_readings_at_once()
        {
            // THE CASE THE GLOBAL COULD NOT EXPRESS.
            var p = new HandoffPolicy
            {
                PeerEntryPhase =
                {
                    Rule(PeerEntryPhaseMeaning.RuntimePhase, producer: "Feed", consumer: "Assembly"),
                    Rule(PeerEntryPhaseMeaning.ReadinessAssertion),
                },
            };
            Assert.Equal(PeerEntryPhaseMeaning.RuntimePhase, p.MeaningFor("Feed", "Assembly", "Entry"));
            Assert.Equal(PeerEntryPhaseMeaning.ReadinessAssertion, p.MeaningFor("Feed", "Other", "Entry"));
            Assert.Equal(PeerEntryPhaseMeaning.ReadinessAssertion, p.MeaningFor("Other", "Assembly", "Entry"));
        }

        [Fact]
        public void The_most_specific_row_wins_regardless_of_the_order_it_is_written_in()
        {
            var specificFirst = new HandoffPolicy
            {
                PeerEntryPhase = { Rule(PeerEntryPhaseMeaning.RuntimePhase, producer: "Feed", consumer: "Assembly"),
                                   Rule(PeerEntryPhaseMeaning.ReadinessAssertion) },
            };
            var catchAllFirst = new HandoffPolicy
            {
                PeerEntryPhase = { Rule(PeerEntryPhaseMeaning.ReadinessAssertion),
                                   Rule(PeerEntryPhaseMeaning.RuntimePhase, producer: "Feed", consumer: "Assembly") },
            };
            Assert.Equal(specificFirst.MeaningFor("Feed", "Assembly", "Entry"),
                         catchAllFirst.MeaningFor("Feed", "Assembly", "Entry"));
            Assert.Equal(PeerEntryPhaseMeaning.RuntimePhase, catchAllFirst.MeaningFor("Feed", "Assembly", "Entry"));
        }

        [Fact]
        public void A_producer_state_pins_one_announcement_of_a_producer_that_has_several()
        {
            var p = new HandoffPolicy
            {
                PeerEntryPhase = { Rule(PeerEntryPhaseMeaning.RuntimePhase, producer: "Feed", producerState: "Restart"),
                                   Rule(PeerEntryPhaseMeaning.ReadinessAssertion, producer: "Feed") },
            };
            Assert.Equal(PeerEntryPhaseMeaning.RuntimePhase, p.MeaningFor("Feed", "Any", "Restart"));
            Assert.Equal(PeerEntryPhaseMeaning.ReadinessAssertion, p.MeaningFor("Feed", "Any", "Boot"));
        }

        [Fact]
        public void Matching_is_case_insensitive_like_every_other_component_reference() =>
            Assert.Equal(PeerEntryPhaseMeaning.RuntimePhase,
                new HandoffPolicy { PeerEntryPhase = { Rule(PeerEntryPhaseMeaning.RuntimePhase, producer: "FEED") } }
                    .MeaningFor("feed", "x", "y"));

        // ---- what the catalogue refuses ----

        static string Refuse(HandoffPolicy h)
        {
            var rig = new RigCatalog { Handoff = h };
            return Assert.Throws<InvalidOperationException>(() => RigCatalogValidator.Validate(rig)).Message;
        }

        [Fact]
        public void Two_equally_specific_rows_that_could_both_match_are_refused()
        {
            // Resolving this by file order would make whether the plant waits depend on where a line
            // was typed. Different specificity is fine - the more specific one wins, unambiguously.
            var m = Refuse(new HandoffPolicy
            {
                PeerEntryPhase = { Rule(PeerEntryPhaseMeaning.RuntimePhase, producer: "Feed"),
                                   Rule(PeerEntryPhaseMeaning.ReadinessAssertion, consumer: "Assembly") },
            });
            Assert.Contains("equally specific", m, StringComparison.Ordinal);
            Assert.Contains("depend on the order", m, StringComparison.Ordinal);
        }

        [Fact]
        public void A_row_with_no_meaning_or_no_reason_is_refused()
        {
            var noMeaning = Refuse(new HandoffPolicy
            {
                PeerEntryPhase = { new PeerEntryPhaseRule { Because = "x" } },
            });
            Assert.Contains("declares no meaning", noMeaning, StringComparison.Ordinal);

            var noWhy = Refuse(new HandoffPolicy
            {
                PeerEntryPhase = { new PeerEntryPhaseRule { Meaning = PeerEntryPhaseMeaning.RuntimePhase } },
            });
            Assert.Contains("no 'because'", noWhy, StringComparison.Ordinal);
        }

        [Fact]
        public void Two_carriers_covering_one_producer_are_refused()
        {
            // The carrier list had NO overlap check: a wildcard row written above a specific one
            // silently swallowed it, and a carrier decides whether a material level may stand in for
            // a producer's phase.
            var m = Refuse(new HandoffPolicy
            {
                Carriers =
                {
                    new CarrierSubstitution { Producer = "Feed", Carrier = "PartAt", Because = "a" },
                    new CarrierSubstitution { Producer = "Feed", ProducerState = "Done", Carrier = "Other", Because = "b" },
                },
            });
            Assert.Contains("both cover", m, StringComparison.Ordinal);
            Assert.Contains("depend on their order", m, StringComparison.Ordinal);
        }

        [Fact]
        public void A_carrier_with_no_reason_is_refused()
        {
            var m = Refuse(new HandoffPolicy
            {
                Carriers = { new CarrierSubstitution { Producer = "Feed", Carrier = "PartAt" } },
            });
            Assert.Contains("no 'because'", m, StringComparison.Ordinal);
            Assert.Contains("nobody checked", m, StringComparison.Ordinal);
        }

        [Fact]
        public void The_shipped_profile_declares_a_reading_for_every_edge_and_gives_its_reason()
        {
            var h = TestConfig.Cfg.Rig.Handoff;
            Assert.NotEmpty(h.PeerEntryPhase);
            Assert.All(h.PeerEntryPhase, r => Assert.NotEqual(PeerEntryPhaseMeaning.Undeclared, r.Meaning));
            Assert.All(h.PeerEntryPhase, r => Assert.False(string.IsNullOrWhiteSpace(r.Because)));
            Assert.False(string.IsNullOrWhiteSpace(h.ReasonFor("Anything", "Anything", "Entry")));
        }
    }
}
