using System.Collections.Generic;
using System.Xml.Linq;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    public class SyslayBuilderTests
    {
        // [Fact]
        public void Build_RootIsLayerWithCorrectNamespace()
        {
            var builder = new SyslayBuilder("ABC123");
            var doc = builder.Build();

            Assert.NotNull(doc.Root);
            Assert.Equal("Layer", doc.Root!.Name.LocalName);
            Assert.Equal("https://www.se.com/LibraryElements", doc.Root.Name.NamespaceName);
            Assert.Equal("ABC123", doc.Root.Attribute("ID")!.Value);
        }

        // [Fact]
        public void AddFB_WithParameters_ProducesCorrectChildElements()
        {
            var builder = new SyslayBuilder("LayerId");
            var parms = new Dictionary<string, string>
            {
                ["actuator_name"] = "'pusher'",
                ["actuator_id"] = "0"
            };
            builder.AddFB("FBID01", "Pusher", "Five_State_Actuator_CAT", "Main", 100, 200, parms);

            var doc = builder.Build();
            var ns = (XNamespace)"https://www.se.com/LibraryElements";
            var fb = doc.Descendants(ns + "FB").Single();
            var paramElems = fb.Elements(ns + "Parameter").ToList();

            Assert.Equal(2, paramElems.Count);
            Assert.Contains(paramElems, p => p.Attribute("Name")!.Value == "actuator_name");
            Assert.Contains(paramElems, p => p.Attribute("Value")!.Value == "'pusher'");
        }

        // [Fact]
        public void Build_RoundTripsThroughXDocumentParse()
        {
            var builder = new SyslayBuilder("LayerId");
            builder.AddFB("FBID01", "Pusher", "Five_State_Actuator_CAT", "Main", 100, 200);
            var doc = builder.Build();

            var serialised = doc.ToString();
            var reparsed = XDocument.Parse(serialised);

            Assert.Equal(doc.Root!.Name, reparsed.Root!.Name);
            Assert.Equal(doc.Root.Attribute("ID")!.Value, reparsed.Root.Attribute("ID")!.Value);
        }
    }
}
