using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Hmi;
using Xunit;

namespace MapperTests
{
    // hmi.yml is the single source of truth for HMI DEPLOYMENT facts - the identities,
    // addresses, ports, filenames and library version the panel is deployed with.
    //
    // Two properties matter and are pinned here:
    //   1. The shipped file states the values the July 30 reference solution deploys with. A wrong
    //      logical port produces a panel that builds, deploys and never connects.
    //   2. The loader REFUSES a malformed file instead of substituting a default, and reports every
    //      problem at once - a validator that stops at the first fault hides the rest.
    public class HmiDeviceDefinitionTests
    {
        private static string ShippedYaml()
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "Config", "hmi.yml");
            if (File.Exists(beside)) return File.ReadAllText(beside);

            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "CodeGen", "CodeGen", "Config", "hmi.yml"));
            Assert.True(File.Exists(repo), $"hmi.yml not found beside the test binary or at {repo}");
            return File.ReadAllText(repo);
        }

        [Fact]
        public void ShippedDefinitionLoadsAndIsComplete()
        {
            var def = HmiDeviceLoader.Parse(ShippedYaml());

            Assert.True(Guid.TryParse(def.DeviceId, out _));
            Assert.NotEmpty(def.Artefacts);
            Assert.False(string.IsNullOrWhiteSpace(def.LibraryName));
            Assert.False(string.IsNullOrWhiteSpace(def.LogicalDeviceType));
            Assert.False(string.IsNullOrWhiteSpace(def.SwitchEquipmentFile));
        }

        // The defect this test exists for: the ports were 4840 (OPC UA), which is not what the
        // reference panel is deployed with. 61999/51443 come from the July 30 solution's
        // Simulation.Binding LogicalPort and the topology logicalPort/logicalPortSecured pair.
        [Fact]
        public void ShippedPortsMatchTheReferenceRuntime()
        {
            var def = HmiDeviceLoader.Parse(ShippedYaml());

            Assert.Equal(61999, def.LogicalPort);
            Assert.Equal(51443, def.SecurePort);
            Assert.NotEqual(def.LogicalPort, def.SecurePort);
        }

        [Fact]
        public void HostAndInternalRuntimeAddressesAreDistinctAndOnTheConfiguredSubnet()
        {
            var def = HmiDeviceLoader.Parse(ShippedYaml());

            Assert.NotEqual(def.HostIp, def.InternalRuntimeIp);

            // The NIC and the container must both sit on the subnet the broadcast domain is matched by,
            // otherwise the emitter resolves a network the panel is not actually attached to.
            string Prefix(string ip) => string.Join('.', ip.Split('.').Take(3));
            Assert.Equal(Prefix(def.Subnet), Prefix(def.HostIp));
            Assert.Equal(Prefix(def.Subnet), Prefix(def.InternalRuntimeIp));
        }

        // Every token a deployment template references must be supplied. The emitter fails a
        // generation on a surviving placeholder; this catches the same mistake at build time.
        [Fact]
        public void TokensCoverEveryPlaceholderTheShippedTemplatesUse()
        {
            var def = HmiDeviceLoader.Parse(ShippedYaml());
            var supplied = def.Tokens("solution-id", "switch-id").Keys
                .Concat(new[] { "NetworkId", "FirstCanvas" })   // derived from the generated project
                .ToHashSet(StringComparer.Ordinal);

            var templateDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Template Library", "HMI", "Deployment"));
            if (!Directory.Exists(templateDir)) return;   // template library not laid out beside the tests

            foreach (var spec in def.Artefacts)
            {
                var path = Path.Combine(templateDir, spec.Template);
                if (!File.Exists(path)) continue;

                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"\{\{(\w+)\}\}"))
                    Assert.True(supplied.Contains(m.Groups[1].Value),
                        $"{spec.Template} references {{{{{m.Groups[1].Value}}}}}, which hmi.yml does not supply.");
            }
        }

        [Fact]
        public void ArtefactDestinationsAreDistinctAndConfined()
        {
            var def = HmiDeviceLoader.Parse(ShippedYaml());

            var destinations = def.Artefacts.Select(a => a.Into + "/" + a.Name).ToList();
            Assert.Equal(destinations.Count, destinations.Distinct(StringComparer.Ordinal).Count());

            foreach (var a in def.Artefacts)
            {
                Assert.Contains(a.Into, new[] { "system", "device", "topology" });
                Assert.DoesNotContain("..", a.Name);
                Assert.DoesNotContain('/', a.Name);
                Assert.DoesNotContain('\\', a.Name);
            }
        }

        // The regression this exists for: substituting the single-brace {Token} form first consumed the
        // inner braces of a template's {{Token}}, so every deployed artefact carried a literal
        // '{192.168.1.2}'. It generated, deployed and could never connect.
        [Fact]
        public void DoubleBracePlaceholdersAreSubstitutedBeforeSingleBraceOnes()
        {
            var tokens = HmiDeviceLoader.Parse(ShippedYaml()).Tokens("solution-id", "switch-id");

            var rendered = HmiRuntimeEmitter.Substitute("port={{LogicalPort}} file={DeviceId}.sysdev", tokens);

            Assert.Equal("port=61999 file=a441cfb6-5523-4d2c-a152-aacecdcef78e.sysdev", rendered);
            Assert.DoesNotContain("{", rendered);
        }

        // Renders every shipped deployment template through the emitter's own substitution and proves
        // the configured runtime ports actually reach the artefacts EAE deploys.
        [Fact]
        public void RenderedDeploymentArtefactsCarryTheConfiguredPorts()
        {
            var def = HmiDeviceLoader.Parse(ShippedYaml());
            var tokens = def.Tokens("solution-id", "switch-id")
                .Concat(new[]
                {
                    new KeyValuePair<string, string>("NetworkId", "network-id"),
                    new KeyValuePair<string, string>("FirstCanvas", "MainScreen"),
                })
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

            var templateDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Template Library", "HMI", "Deployment"));
            if (!Directory.Exists(templateDir)) return;

            var sawLogical = false;
            var sawSecure = false;

            foreach (var spec in def.Artefacts)
            {
                var path = Path.Combine(templateDir, spec.Template);
                if (!File.Exists(path)) continue;

                var rendered = HmiRuntimeEmitter.Substitute(File.ReadAllText(path), tokens);

                Assert.DoesNotMatch(@"\{\{[A-Za-z]+\}\}", rendered);
                sawLogical |= rendered.Contains(def.LogicalPort.ToString(), StringComparison.Ordinal);
                sawSecure |= rendered.Contains(def.SecurePort.ToString(), StringComparison.Ordinal);
            }

            Assert.True(sawLogical, "no deployment artefact carries the configured logical port");
            Assert.True(sawSecure, "no deployment artefact carries the configured secure port");
        }

        [Fact]
        public void UnknownKeyIsRejected()
        {
            var ex = Assert.ThrowsAny<Exception>(
                () => HmiDeviceLoader.Parse(ShippedYaml() + "\nthisKeyDoesNotExist: 1\n"));
            Assert.Contains("hmi.yml", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("  logicalPort:       61999", "  logicalPort:       0", "logicalPort")]
        [InlineData("  logicalPort:       61999", "  logicalPort:       70000", "logicalPort")]
        [InlineData("  hostIp:            192.168.1.2", "  hostIp:            not-an-ip", "hostIp")]
        [InlineData("  device:                 a441cfb6-5523-4d2c-a152-aacecdcef78e",
                    "  device:                 not-a-guid", "device")]
        [InlineData("  name:    SE.Standard", "  name:    ''", "library.name")]
        [InlineData("  equipmentFile: Equipment_Switch_1.json",
                    "  equipmentFile: ../../escape.json", "equipmentFile")]
        public void MalformedValueIsRejectedNamingItsPath(string find, string replace, string expected)
        {
            var yaml = ShippedYaml();
            Assert.Contains(find, yaml);   // guards against a silently-ineffective mutation

            var ex = Assert.Throws<HmiConfigException>(
                () => HmiDeviceLoader.Parse(yaml.Replace(find, replace)));
            Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Two real identities colliding would silently bind the panel to the wrong object.
        [Fact]
        public void DuplicateIdentityIsRejected()
        {
            var yaml = ShippedYaml().Replace(
                "  panel:                  25e3cc7c-d98a-486a-963b-c5ab645331c9",
                "  panel:                  a441cfb6-5523-4d2c-a152-aacecdcef78e");

            var ex = Assert.Throws<HmiConfigException>(() => HmiDeviceLoader.Parse(yaml));
            Assert.Contains("identity", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // The ordering defect: fields validated after Throw() were reported only on a later run.
        // A file with faults in several sections must surface all of them at once.
        [Fact]
        public void EveryFaultIsReportedTogether()
        {
            var yaml = ShippedYaml()
                .Replace("  logicalPort:       61999", "  logicalPort:       0")
                .Replace("  name:    SE.Standard", "  name:    ''")
                .Replace("  type: HMI_NET", "  type: ''")
                .Replace("  equipmentFile: Equipment_Switch_1.json", "  equipmentFile: ''");

            var ex = Assert.Throws<HmiConfigException>(() => HmiDeviceLoader.Parse(yaml));

            foreach (var path in new[] { "logicalPort", "library.name", "logicalDevice.type", "equipmentFile" })
                Assert.Contains(path, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
