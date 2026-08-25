using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeGen.Hmi
{
    // Loads and validates Config/hmi.yml into an immutable definition. Reading, strict parsing,
    // caching and the path-addressed node reader all come from HmiYaml.
    internal static class HmiDefinitionLoader
    {
        internal const string FileName = "hmi.yml";

        private static readonly object Gate = new();
        private static HmiDefinition? _cached;
        private static DateTime _stampUtc;

        // Re-read only when the file itself changes, so repeated generations in one process parse once.
        // The stamp is captured INSIDE the lock and BEFORE the content: a file rewritten between the
        // two would otherwise be cached under the older timestamp and pin stale content.
        internal static HmiDefinition Load()
        {
            var path = HmiYaml.PathOf(FileName);
            DateTime Stamp() => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cached != null && Stamp() == _stampUtc) return _cached;
            lock (Gate)
            {
                var stamp = Stamp();
                if (_cached != null && stamp == _stampUtc) return _cached;
                _cached = Parse(HmiYaml.Read(FileName));
                _stampUtc = stamp;
                return _cached;
            }
        }

        internal static HmiDefinition Parse(string yaml)
        {
            var v = new HmiYaml.Validator(FileName);
            var root = v.Root(yaml);

            var schema = root.Int("schemaVersion");
            if (schema != HmiDefinition.SupportedSchemaVersion)
                v.Fail($"schemaVersion {schema} is not supported (this build understands " +
                       $"{HmiDefinition.SupportedSchemaVersion}).");

            var policy = root.Sec("policy");
            var geo = root.Sec("geometry");
            var style = root.Sec("style");
            var screens = root.Sec("screens");
            var proto = root.Sec("protocol");
            var caps = root.Sec("capabilities");
            var deploy = root.Sec("deployment");
            var rt = root.Sec("runtime");

            var unknown = policy.Text("unknownContract").ToLowerInvariant();
            if (unknown is not ("fail" or "skip")) v.Fail("policy.unknownContract must be 'fail' or 'skip'.");

            var geometry = new HmiGeometry(
                geo.Int("canvasWidth", 1, int.MaxValue),
                geo.Int("canvasHeight", 1, int.MaxValue),
                geo.Int("navigationBarHeight", 0, int.MaxValue),
                geo.Int("margin", 0, int.MaxValue),
                geo.Int("gap", 0, int.MaxValue),
                geo.Int("captionHeight", 0, int.MaxValue),
                geo.Int("titleY", 0, int.MaxValue),
                geo.Int("contentTop", 0, int.MaxValue),
                geo.Int("button.width", 1, int.MaxValue),
                geo.Int("button.height", 1, int.MaxValue),
                geo.Int("button.bottomInset", 0, int.MaxValue));

            if (geometry.WorkHeight <= geometry.ContentTop)
                v.Fail("geometry.navigationBarHeight leaves no work area below geometry.contentTop.");

            // A font is either a named theme font (family only) or an explicit triple. Anything in
            // between - a size without a weight, a weight without a size - is ambiguous and rejected
            // rather than silently completed with a default this file claims not to have.
            HmiFont Font(string rel)
            {
                var d = style.Sec(rel);
                var family = d.Text("family");
                bool hasSize = d.Has("size"), hasBold = d.Has("bold");
                if (!hasSize && !hasBold) return new HmiFont(family, 0, false);
                if (!hasSize) v.Fail($"'{d.Path}.size' is required when '{d.Path}.bold' is set.");
                if (!hasBold) v.Fail($"'{d.Path}.bold' is required when '{d.Path}.size' is set.");
                var size = hasSize ? d.Int("size") : 0;
                if (hasSize && size <= 0) v.Fail($"'{d.Path}.size' must be greater than zero.");
                return new HmiFont(family, size, hasBold && d.Flag("bold"));
            }

            HmiColor Color(string rel)
            {
                var d = style.Sec(rel);
                return new HmiColor(d.Int("r", 0, 255), d.Int("g", 0, 255), d.Int("b", 0, 255));
            }

            var hmiStyle = new HmiStyle(
                style.Text("canvasBrush"), style.Text("buttonBrush"),
                Font("buttonFont"), Font("captionFont"),
                Color("buttonTextColor"), Color("captionColor"), Color("emphasisColor"));

            // The screen families. Every role and capability name is validated here, so a typo in
            // hmi.yml fails the load rather than silently producing an empty screen.
            var families = new List<HmiScreenFamily>();
            foreach (var f in screens.Seq("families"))
            {
                var name = f.Identifier("name");
                if (families.Any(x => x.Name == name)) v.Fail($"duplicate screen family '{name}'.");

                var roles = new List<HmiRole>();
                foreach (var r in f.Strings("include"))
                    if (Enum.TryParse<HmiRole>(r, ignoreCase: true, out var role)) roles.Add(role);
                    else v.Fail($"'{f.Path}.include' names unknown role '{r}'.");
                if (roles.Count == 0) v.Fail($"'{f.Path}.include' must name at least one role.");

                var variant = new Dictionary<HmiRole, string>();
                foreach (var (k, sym) in f.Map("variant"))
                    if (Enum.TryParse<HmiRole>(k, ignoreCase: true, out var role)) variant[role] = sym.Trim();
                    else v.Fail($"'{f.Path}.variant' names unknown role '{k}'.");

                HmiCapabilityPurpose? requires = null;
                var wants = f.Opt("requires");
                if (wants != null)
                {
                    if (Enum.TryParse<HmiCapabilityPurpose>(wants, ignoreCase: false, out var p2)) requires = p2;
                    else v.Fail($"'{f.Path}.requires' names unknown capability '{wants}'.");
                }

                families.Add(new HmiScreenFamily(name, f.Text("title"), roles, variant,
                                                 requires, f.FlagOr("onlySupported", false)));
            }
            if (families.Count == 0) v.Fail("'screens.families' declares no screens.");

            var texts = screens.Sec("recipeText");
            var recipeText = new HmiRecipeTextPolicy(
                texts.Text("command"), texts.Text("wait"), texts.Text("phase"),
                texts.Text("end"), texts.Text("unresolved"));

            var screenPolicy = new HmiScreenPolicy(families, screens.Identifier("hubName"),
                                                 screens.Identifier("detailName"), screens.Text("detailTitle"),
                                                 screens.Identifier("processDetailName"),
                                                 screens.Text("processDetailTitle"), recipeText);

            var profiles = new List<HmiStatesProfile>();
            foreach (var p in proto.Seq("statesProfiles"))
            {
                var id = p.Identifier("id");
                if (profiles.Any(x => x.Id == id)) v.Fail($"duplicate state profile id '{id}'.");
                var carries = p.Strings("match.inputEventCarries");
                var labels = p.Strings("labels");
                if (labels.Count == 0) v.Fail($"state profile '{id}' declares no labels.");
                profiles.Add(new HmiStatesProfile(id, carries, labels));
            }

            var rules = new List<HmiCapabilityRule>();
            foreach (var c in caps.Seq("commands"))
            {
                var purposeText = c.Text("purpose");
                if (!Enum.TryParse<HmiCapabilityPurpose>(purposeText, ignoreCase: false, out var purpose))
                    v.Fail($"unknown capability purpose '{purposeText}'.");
                if (rules.Any(r => r.Purpose == purpose)) v.Fail($"duplicate capability purpose '{purposeText}'.");

                var data = c.StringLists("outputData");
                if (data.Count == 0)
                    v.Fail($"capability '{purposeText}' declares no outputData shape - a command that " +
                           "carries no data still declares one empty shape.");

                // Every command must declare how the CONTROLLER proves it honours the value. A rule
                // with no tokens would silently reduce to "the port exists", which is the exact
                // mistake this clause exists to prevent, so an empty token list is rejected.
                var spec = c.Sec("consumption");
                var tokens = spec.Strings("tokens");
                if (tokens.Count == 0)
                    v.Fail($"'{spec.Path}.tokens' must name at least one ECC token - without it the " +
                           "command would be offered on port existence alone.");

                rules.Add(new HmiCapabilityRule(
                    purpose, c.Text("outputEvent"), data,
                    c.Opt("alsoRequiresOutputEvent"), c.Strings("anyFeedback"), c.Flag("needsModeChain"),
                    c.OptStrings("chainPorts"),
                    new HmiConsumptionSpec(spec.Opt("inType"), tokens, spec.OptStrings("anyOf"))));
            }

            // Actions. Every one must name a capability the rule table declares, and must fire
            // exactly one way - a call or a tag - so the gate always knows how to suppress it.
            var actions = new List<HmiOperatorAction>();
            foreach (var a in caps.Seq("actions"))
            {
                var id = a.Identifier("id");
                if (actions.Any(x => x.Id == id)) v.Fail($"duplicate action id '{id}'.");

                var provedText = a.Text("provedBy");
                if (!Enum.TryParse<HmiCapabilityPurpose>(provedText, ignoreCase: false, out var proved))
                    v.Fail($"action '{id}' names unknown purpose '{provedText}'.");
                else if (rules.All(r => r.Purpose != proved))
                    v.Fail($"action '{id}' is proved by '{provedText}', which declares no command rule.");

                var fires = a.Opt("fires");
                var writes = a.Opt("writes");
                if ((fires == null) == (writes == null))
                    v.Fail($"action '{id}' must declare exactly one of 'fires' or 'writes'.");

                string? evt = null, payload = null;
                if (fires != null)
                {
                    var m = Regex.Match(fires, @"^(?<e>[A-Za-z_][A-Za-z0-9_]*)\((?<p>[^)]*)\)$");
                    if (!m.Success) v.Fail($"action '{id}' fires '{fires}', which is not EVENT(payload).");
                    else { evt = m.Groups["e"].Value; payload = m.Groups["p"].Value.Trim(); }
                }

                HmiActionProof? proof = null;
                if (a.Has("alsoRequires"))
                {
                    var r = a.Sec("alsoRequires");
                    proof = new HmiActionProof(r.Text("guard"), r.Opt("distinctFrom"), r.Opt("interlockedBy"));
                    if (proof.DistinctFrom == null && proof.InterlockedBy == null)
                        v.Fail($"action '{id}' declares alsoRequires with no distinctFrom or interlockedBy.");
                }

                actions.Add(new HmiOperatorAction(id, a.Text("label"), proved, evt, payload, writes, proof));
            }

            var deployment = new HmiDeploymentPolicy(
                deploy.Text("hmiFolderName"),
                deploy.Identifier("canvasNamespaceSuffix"),
                deploy.Identifier("symbolNamespaceSuffix"),
                deploy.Identifier("defaultLibraryNamespace"),
                deploy.Text("generatedBanner"),
                deploy.Text("ownershipManifest"),
                deploy.Identifier("primarySymbol"));

            HmiResolutionPolicy Resolution(string rel)
            {
                var r = rt.Sec(rel);
                return new HmiResolutionPolicy(
                    r.Text("name"),
                    r.Opt("template") ?? string.Empty,        // deliberately empty on the fallback entry
                    r.Text("resizeBehaviour"),
                    r.Int("canvasButtonHeight", 1, int.MaxValue),
                    r.Has("siblingButtonCount") ? r.Int("siblingButtonCount") : 0,
                    r.Has("childButtonCount") ? r.Int("childButtonCount") : 0);
            }

            var runtime = new HmiRuntimePolicy(
                rt.Text("schemaVersion"), rt.Identifier("startCanvas"),
                Resolution("resolution"), Resolution("fallbackResolution"),
                new HmiChromePolicy(
                    rt.Flag("chrome.logger"), rt.Flag("chrome.login"),
                    rt.Flag("chrome.currentUser"), rt.Flag("chrome.languageButton"),
                    rt.Flag("chrome.runtimeConnection"), rt.Flag("chrome.navigationBar"),
                    rt.Flag("chrome.newVersionDeployed"), rt.Int("chrome.navigationControl")));

            // Every remaining field is validated into a local BEFORE Throw(), so a fault here is
            // reported alongside the others instead of being accepted because the validator had
            // already decided the file was clean.
            var notice = policy.Text("unsupportedCommandNotice");
            var noContract = policy.Text("noContractNotice");
            var withheldMarker = policy.Text("withheldMarker");
            var withheldHeading = policy.Text("withheldHeading");
            var feedback = caps.Strings("interlockDiagnostics.requiredFeedback");
            // The device/deployment half of the same document - one file, one parse, one Throw.
            var device = HmiDeviceBinder.Bind(root, v);

            v.Unknown();
            v.Throw();

            // Constructed exclusively from already-validated locals.
            return new HmiDefinition(
                schema, unknown == "fail", notice, noContract, withheldMarker, withheldHeading,
                geometry, hmiStyle, screenPolicy,
                profiles, rules, actions, feedback,
                runtime, deployment, device);
        }

    }
}
