using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Domain.Twin;
using CodeGen.IO;
using CodeGen.Models;
using Xunit;

namespace MapperTests
{
    // THE FRONTEND IS FAIL-CLOSED, and these are what say so.
    //
    // Every case here used to produce an EMPTY OR PARTIAL model that the pipeline compiled and
    // published as a success: the reader accumulated a LastError string nothing outside it read, and
    // returned whatever had parsed. A file that is not a twin, a twin whose root is another schema,
    // a component the compiler cannot classify and a component nothing could reference all looked
    // exactly like a small plant.
    public class FrontendRefusalTests
    {
        static string Twin(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "twins", name);

        static SystemXmlReader Reader() => new(TestConfig.Cfg.Twin);

        static string Refusal(string fixture) =>
            Assert.Throws<InvalidOperationException>(
                () => Reader().ReadAllComponents(Twin(fixture))).Message;

        // ---- the file itself ----

        [Fact]
        public void Malformed_xml_is_refused_and_names_the_file()
        {
            var m = Refusal("malformed.xml");
            Assert.Contains("[Twin]", m, StringComparison.Ordinal);
            Assert.Contains("not readable as XML", m, StringComparison.Ordinal);
            Assert.Contains("malformed.xml", m, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_file_is_refused_rather_than_read_as_a_plant_with_no_components()
        {
            var m = Refusal("empty.xml");
            Assert.Contains("[Twin]", m, StringComparison.Ordinal);
            Assert.Contains("empty.xml", m, StringComparison.Ordinal);
        }

        [Fact]
        public void A_root_of_an_unsupported_type_is_refused_and_says_what_it_reads()
        {
            var m = Refusal("unknown-root.xml");
            Assert.Contains("Layout", m, StringComparison.Ordinal);
            Assert.Contains("'System'", m, StringComparison.Ordinal);
            Assert.Contains("'Component'", m, StringComparison.Ordinal);
            Assert.Contains("will not guess", m, StringComparison.Ordinal);
        }

        [Fact]
        public void A_system_file_with_no_system_element_is_refused_and_lists_what_it_found()
        {
            var m = Refusal("no-system-element.xml");
            Assert.Contains("no <s> or <System> element", m, StringComparison.Ordinal);
            Assert.Contains("Other", m, StringComparison.Ordinal);   // the child it did find
        }

        [Fact]
        public void A_missing_file_is_refused_before_anything_is_parsed() =>
            Assert.Throws<FileNotFoundException>(
                () => Reader().ReadAllComponents(Twin("no-such-twin.xml")));

        // ---- the components ----

        [Fact]
        public void An_unmapped_component_type_is_refused_and_names_the_token_and_the_declaration()
        {
            var m = Refusal("unknown-kind.xml");
            Assert.Contains("Kiln_Damper", m, StringComparison.Ordinal);
            Assert.Contains("Servo", m, StringComparison.Ordinal);
            Assert.Contains("twin-schema.yml", m, StringComparison.Ordinal);
            // and it says what a legal answer looks like
            Assert.Contains("actuator", m, StringComparison.Ordinal);
            Assert.Contains("sensor", m, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unmapped_type_does_not_default_to_actuator()
        {
            // The predecessor classified anything that was not a Process or a Sensor as an actuator,
            // so a token the compiler had never seen became something the planner would try to drive.
            Assert.Null(TestConfig.Cfg.Twin.TryKind("Servo"));
            Assert.Equal(ComponentKind.Actuator, TestConfig.Cfg.Twin.TryKind("Actuator"));
            Assert.Equal(ComponentKind.Actuator, TestConfig.Cfg.Twin.TryKind("Robot"));
            Assert.Equal(ComponentKind.Sensor,   TestConfig.Cfg.Twin.TryKind("Sensor"));
            Assert.Equal(ComponentKind.Process,  TestConfig.Cfg.Twin.TryKind("Process"));
            Assert.Equal(ComponentKind.Excluded, TestConfig.Cfg.Twin.TryKind("NonControl"));
        }

        [Fact]
        public void Component_parse_failures_are_aggregated_and_none_of_the_file_is_returned()
        {
            var m = Refusal("partial-corruption.xml");

            // BOTH bad rows are reported from ONE run, so the engineer does not fix them one
            // generation at a time...
            Assert.Contains("No_Id_Here", m, StringComparison.Ordinal);
            Assert.Contains("<ComponentID>", m, StringComparison.Ordinal);
            Assert.Contains("<n>, <Name> or <VcID>", m, StringComparison.Ordinal);
            Assert.Contains("2 component(s)", m, StringComparison.Ordinal);

            // ...and the one component that DID parse is not returned as a plant.
            Assert.Contains("rather than compiling the components that did parse", m, StringComparison.Ordinal);
        }

        [Fact]
        public void A_nameless_component_is_refused_rather_than_given_a_machine_made_name()
        {
            // It used to be named `Component_<first 8 of its id>`, which matches no roster row,
            // no ring key and no channel binding - a plant with a name nothing declared.
            var m = Refusal("partial-corruption.xml");
            Assert.DoesNotContain("Component_C-namele", m, StringComparison.Ordinal);
        }

        // ---- the model ----

        [Fact]
        public void A_twin_whose_every_component_is_excluded_is_refused_by_the_model()
        {
            // The reader is happy: NonControl is a declared kind and is dropped by design. What is
            // refused is publishing a project for a plant with nothing in it.
            var read = Reader().ReadAllComponents(Twin("all-excluded.xml"));
            Assert.Empty(read);

            var m = Assert.Throws<InvalidOperationException>(
                () => TwinModel.Build(read, TestConfig.Cfg.Twin)).Message;
            Assert.Contains("declares no control components", m, StringComparison.Ordinal);
            Assert.Contains("twin-schema.yml", m, StringComparison.Ordinal);
        }

        [Fact]
        public void Duplicate_names_and_duplicate_ids_are_both_refused_together()
        {
            var read = Reader().ReadAllComponents(Twin("duplicates.xml"));
            var m = Assert.Throws<InvalidOperationException>(
                () => TwinModel.Build(read, TestConfig.Cfg.Twin)).Message;

            Assert.Contains("share ComponentID 'C-ram'", m, StringComparison.Ordinal);
            Assert.Contains("share the name 'Charge_Ram'", m, StringComparison.Ordinal);
            Assert.Contains("ambiguous", m, StringComparison.Ordinal);
        }

        [Fact]
        public void A_component_built_in_code_is_classified_by_the_same_declaration()
        {
            // Two entry points - the reader and TwinModel.Build - one owner. A twin built in code
            // cannot carry a kind no twin FILE could, and cannot skip the refusal.
            var unmapped = new List<VueOneComponent>
            {
                new() { ComponentID = "C-x", Name = "Kiln_Damper", Type = "Servo" }
            };
            var m = Assert.Throws<InvalidOperationException>(
                () => TwinModel.Build(unmapped, TestConfig.Cfg.Twin)).Message;
            Assert.Contains("Servo", m, StringComparison.Ordinal);
            Assert.Contains("twin-schema.yml", m, StringComparison.Ordinal);
        }

        // ---- what it stays tolerant of ----

        [Fact]
        public void The_shipped_twins_still_read_and_carry_the_kinds_the_declaration_maps()
        {
            foreach (var suffix in new[] { "_se", "_vc", "_sw5", "_sw5_noclamp" })
            {
                var read = Reader().ReadAllComponents(TestTwin.CompilableFixturePath(suffix));
                Assert.NotEmpty(read);

                // NonControl is dropped by the reader, so nothing excluded reaches a plan...
                Assert.All(read, c => Assert.NotEqual(ComponentKind.Excluded, c.Kind));
                // ...and every component that survives carries a resolved kind.
                Assert.All(read, c => Assert.NotNull(c.Kind));

                // A Robot is an ACTUATOR here. The predecessor answered this two ways: the IR said
                // actuator (`!IsProcess && !IsSensor`) while the interlock encoder said no
                // (`Type == "Actuator"`), so a Robot used as an interlock source had its blocked
                // state written in a vocabulary its CAT never publishes.
                foreach (var robot in read.Where(c => string.Equals(c.Type, "Robot", StringComparison.Ordinal)))
                    Assert.Equal(ComponentKind.Actuator, robot.Kind);
            }
        }
    }
}
