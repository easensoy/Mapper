using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // Loads and validates Config/hmi.yml into an immutable definition. Reading, strict parsing,
    // caching and the shared validators all come from HmiYaml.
    internal static class HmiDefinitionLoader
    {
        internal const string FileName = "hmi.yml";

        private static readonly HmiYaml.Cache<HmiDefinition> Cached = new(FileName, Parse);

        internal static HmiDefinition Load() => Cached.Load();

        internal static HmiDefinition Parse(string yaml)
        {
            var dto = HmiYaml.Deserialize<HmiDefinitionDto>(FileName, yaml);

            var v = new Validator();

            var schema = v.Required(dto.SchemaVersion, "schemaVersion");
            if (schema != HmiDefinition.SupportedSchemaVersion)
                v.Fail($"schemaVersion {schema} is not supported (this build understands " +
                       $"{HmiDefinition.SupportedSchemaVersion}).");

            var policy = v.Section(dto.Policy, "policy");
            var geo = v.Section(dto.Geometry, "geometry");
            var style = v.Section(dto.Style, "style");
            var screens = v.Section(dto.Screens, "screens");
            var proto = v.Section(dto.Protocol, "protocol");
            var caps = v.Section(dto.Capabilities, "capabilities");
            var deploy = v.Section(dto.Deployment, "deployment");

            var unknown = (policy.UnknownContract ?? string.Empty).Trim().ToLowerInvariant();
            if (unknown is not ("fail" or "skip")) v.Fail("policy.unknownContract must be 'fail' or 'skip'.");

            var btn = v.Section(geo.Button, "geometry.button");
            var fallback = v.Section(geo.SymbolFallback, "geometry.symbolFallback");

            var geometry = new HmiGeometry(
                v.Positive(geo.CanvasWidth, "geometry.canvasWidth"),
                v.Positive(geo.CanvasHeight, "geometry.canvasHeight"),
                v.NonNegative(geo.NavigationBarHeight, "geometry.navigationBarHeight"),
                v.NonNegative(geo.Margin, "geometry.margin"),
                v.NonNegative(geo.Gap, "geometry.gap"),
                v.NonNegative(geo.CaptionHeight, "geometry.captionHeight"),
                v.NonNegative(geo.BannerY, "geometry.bannerY"),
                v.NonNegative(geo.TitleY, "geometry.titleY"),
                v.NonNegative(geo.ContentTop, "geometry.contentTop"),
                v.Positive(btn.Width, "geometry.button.width"),
                v.Positive(btn.Height, "geometry.button.height"),
                v.NonNegative(btn.BottomInset, "geometry.button.bottomInset"),
                new HmiSize(v.Positive(fallback.Width, "geometry.symbolFallback.width"),
                            v.Positive(fallback.Height, "geometry.symbolFallback.height")));

            if (geometry.WorkHeight <= geometry.ContentTop)
                v.Fail("geometry.navigationBarHeight leaves no work area below geometry.contentTop.");

            var hmiStyle = new HmiStyle(
                v.Text(style.CanvasBrush, "style.canvasBrush"),
                v.Text(style.ButtonBrush, "style.buttonBrush"),
                v.Font(style.ButtonFont, "style.buttonFont"),
                v.Font(style.CaptionFont, "style.captionFont"),
                v.Font(style.DetailFont, "style.detailFont"),
                v.Color(style.ButtonTextColor, "style.buttonTextColor"),
                v.Color(style.CaptionColor, "style.captionColor"),
                v.Color(style.EmphasisColor, "style.emphasisColor"),
                v.Color(style.DetailColor, "style.detailColor"));

            var detail = v.Section(screens.Detail, "screens.detail");
            var screenPolicy = new HmiScreenPolicy(
                v.Identifier(screens.HubName, "screens.hubName"),
                v.Text(screens.HubTitle, "screens.hubTitle"),
                v.Identifier(screens.ResidualName, "screens.residualName"),
                v.Text(screens.ResidualTitle, "screens.residualTitle"),
                v.Identifier(screens.Suffix, "screens.suffix"),
                v.Text(screens.HubButtonText, "screens.hubButtonText"),
                v.Text(screens.PreviousText, "screens.previousText"),
                v.Text(screens.NextText, "screens.nextText"),
                v.Required(detail.ShowStates, "screens.detail.showStates"),
                v.Required(detail.ShowInterlocks, "screens.detail.showInterlocks"),
                v.Required(detail.ShowAllocation, "screens.detail.showAllocation"),
                v.Required(detail.ShowProtocolLegend, "screens.detail.showProtocolLegend"),
                v.Positive(detail.MaxStateChars, "screens.detail.maxStateChars"),
                v.Positive(detail.MaxInterlockChars, "screens.detail.maxInterlockChars"),
                v.Positive(detail.MaxProtocolChars, "screens.detail.maxProtocolChars"));

            var modes = v.ValueLabels(proto.Modes, "protocol.modes");
            var cycles = v.ValueLabels(proto.CycleTypes, "protocol.cycleTypes");

            var profiles = new List<HmiStatesProfile>();
            foreach (var p in v.List(proto.StatesProfiles, "protocol.statesProfiles"))
            {
                var id = v.Identifier(p.Id, "protocol.statesProfiles[].id");
                if (profiles.Any(x => x.Id == id)) v.Fail($"duplicate state profile id '{id}'.");
                var match = v.Section(p.Match, $"protocol.statesProfiles[{id}].match");
                var carries = v.Strings(match.InputEventCarries, $"protocol.statesProfiles[{id}].match.inputEventCarries");
                var labels = v.Strings(p.Labels, $"protocol.statesProfiles[{id}].labels");
                if (labels.Count == 0) v.Fail($"state profile '{id}' declares no labels.");
                profiles.Add(new HmiStatesProfile(id, carries, labels));
            }

            var rules = new List<HmiCapabilityRule>();
            foreach (var c in v.List(caps.Commands, "capabilities.commands"))
            {
                var purposeText = v.Text(c.Purpose, "capabilities.commands[].purpose");
                if (!Enum.TryParse<HmiCapabilityPurpose>(purposeText, ignoreCase: false, out var purpose))
                    v.Fail($"unknown capability purpose '{purposeText}'.");
                if (rules.Any(r => r.Purpose == purpose)) v.Fail($"duplicate capability purpose '{purposeText}'.");

                var variants = (c.OutputDataVariants ?? new List<List<string>>())
                    .Select(x => (IReadOnlyList<string>)(x ?? new List<string>())).ToList();
                var data = (IReadOnlyList<string>)(c.OutputData ?? new List<string>());
                if (variants.Count == 0 && c.OutputData == null)
                    v.Fail($"capability '{purposeText}' declares neither outputData nor outputDataVariants.");

                rules.Add(new HmiCapabilityRule(
                    purpose,
                    v.Text(c.OutputEvent, $"capabilities.commands[{purposeText}].outputEvent"),
                    data, variants,
                    string.IsNullOrWhiteSpace(c.AlsoRequiresOutputEvent) ? null : c.AlsoRequiresOutputEvent!.Trim(),
                    v.Strings(c.AnyFeedback, $"capabilities.commands[{purposeText}].anyFeedback"),
                    v.Required(c.NeedsModeChain, $"capabilities.commands[{purposeText}].needsModeChain"),
                    v.Required(c.NeedsRecipeEngine, $"capabilities.commands[{purposeText}].needsRecipeEngine")));
            }

            var interlock = v.Section(caps.InterlockDiagnostics, "capabilities.interlockDiagnostics");
            var probe = v.Section(caps.EngineProbe, "capabilities.engineProbe");

            var deployment = new HmiDeploymentPolicy(
                v.Text(deploy.HmiFolderName, "deployment.hmiFolderName"),
                v.Identifier(deploy.CanvasNamespaceSuffix, "deployment.canvasNamespaceSuffix"),
                v.Identifier(deploy.SymbolNamespaceSuffix, "deployment.symbolNamespaceSuffix"),
                v.Identifier(deploy.DefaultLibraryNamespace, "deployment.defaultLibraryNamespace"),
                v.Text(deploy.GeneratedBanner, "deployment.generatedBanner"),
                v.Text(deploy.OwnershipManifest, "deployment.ownershipManifest"),
                v.Identifier(deploy.PrimarySymbol, "deployment.primarySymbol"),
                v.Strings(deploy.CommandSymbols, "deployment.commandSymbols"));

            var rt = v.Section(dto.Runtime, "runtime");
            var chrome = v.Section(rt.Chrome, "runtime.chrome");

            HmiResolutionPolicy Resolution(HmiDefinitionDto.RuntimeDto.ResolutionDto? spec, string path)
            {
                var r = v.Section(spec, path);
                return new HmiResolutionPolicy(
                    v.Text(r.Name, path + ".name"),
                    r.Template ?? string.Empty,               // deliberately empty on the fallback entry
                    v.Text(r.ResizeBehaviour, path + ".resizeBehaviour"),
                    v.Positive(r.CanvasButtonHeight, path + ".canvasButtonHeight"),
                    r.SiblingButtonCount ?? 0,
                    r.ChildButtonCount ?? 0);
            }

            var runtime = new HmiRuntimePolicy(
                v.Text(rt.SchemaVersion, "runtime.schemaVersion"),
                v.Identifier(rt.StartCanvas, "runtime.startCanvas"),
                Resolution(rt.Resolution, "runtime.resolution"),
                Resolution(rt.FallbackResolution, "runtime.fallbackResolution"),
                new HmiChromePolicy(
                    v.Required(chrome.Logger, "runtime.chrome.logger"),
                    v.Required(chrome.Login, "runtime.chrome.login"),
                    v.Required(chrome.CurrentUser, "runtime.chrome.currentUser"),
                    v.Required(chrome.LanguageButton, "runtime.chrome.languageButton"),
                    v.Required(chrome.RuntimeConnection, "runtime.chrome.runtimeConnection"),
                    v.Required(chrome.NavigationBar, "runtime.chrome.navigationBar"),
                    v.Required(chrome.NewVersionDeployed, "runtime.chrome.newVersionDeployed"),
                    v.Required(chrome.NavigationControl, "runtime.chrome.navigationControl")));

            // Every remaining field is validated into a local BEFORE Throw(), so a fault here is
            // reported alongside the others instead of being accepted because the validator had
            // already decided the file was clean.
            var readOnly = v.Required(policy.ReadOnly, "policy.readOnly");
            var banner = v.Text(policy.BannerText, "policy.bannerText");
            var notice = v.Text(policy.UnsupportedCommandNotice, "policy.unsupportedCommandNotice");
            var reasons = v.Reasons(policy.UnavailableReasons, "policy.unavailableReasons");
            var feedback = v.Strings(interlock.RequiredFeedback, "capabilities.interlockDiagnostics.requiredFeedback");
            var engineProbe = new HmiEngineProbeSpec(
                v.Text(probe.TypeName, "capabilities.engineProbe.typeName"),
                v.Strings(probe.AutoGatingTokens, "capabilities.engineProbe.autoGatingTokens"),
                v.Strings(probe.ManualSteppingTokens, "capabilities.engineProbe.manualSteppingTokens"));

            v.Throw();

            // Constructed exclusively from already-validated locals.
            return new HmiDefinition(
                schema, readOnly, unknown == "fail", banner, notice, reasons,
                geometry, hmiStyle, screenPolicy,
                modes, cycles, profiles, rules, feedback,
                engineProbe, runtime, deployment);
        }

        // hmi.yml adds two checks the shared validator does not need: a font is either a named
        // theme font or a complete triple, and every withheld-capability reason must have text.
        private sealed class Validator : HmiYaml.Validator
        {
            internal Validator() : base(HmiDefinitionLoader.FileName) { }

            // A font is either a named theme font (family only) or an explicit triple. Anything in
            // between - a size without a weight, a weight without a size - is ambiguous and rejected
            // rather than silently completed with a default this file claims not to have.
            internal HmiFont Font(HmiDefinitionDto.FontDto? dto, string path)
            {
                var d = Section(dto, path);
                var family = Text(d.Family, path + ".family");
                if (d.Size == null && d.Bold == null) return new HmiFont(family, 0, false);
                if (d.Size == null) _problems.Add($"'{path}.size' is required when '{path}.bold' is set.");
                if (d.Bold == null) _problems.Add($"'{path}.bold' is required when '{path}.size' is set.");
                if (d.Size is <= 0) _problems.Add($"'{path}.size' must be greater than zero.");
                return new HmiFont(family, d.Size ?? 0, d.Bold ?? false);
            }

            internal IReadOnlyDictionary<HmiUnavailableReason, string> Reasons(
                Dictionary<string, string>? dto, string path)
            {
                var map = new Dictionary<HmiUnavailableReason, string>();
                if (dto == null) { _problems.Add($"missing required section '{path}'."); return map; }

                foreach (var kv in dto)
                {
                    if (!Enum.TryParse<HmiUnavailableReason>(kv.Key, ignoreCase: false, out var reason) ||
                        reason == HmiUnavailableReason.None)
                    { _problems.Add($"'{path}' declares unknown reason '{kv.Key}'."); continue; }
                    if (string.IsNullOrWhiteSpace(kv.Value))
                    { _problems.Add($"'{path}.{kv.Key}' is empty."); continue; }
                    map[reason] = kv.Value.Trim();
                }

                foreach (var r in Enum.GetValues<HmiUnavailableReason>())
                    if (r != HmiUnavailableReason.None && !map.ContainsKey(r))
                        _problems.Add($"'{path}' is missing '{r}'.");
                return map;
            }

            internal HmiColor Color(HmiDefinitionDto.ColorDto? dto, string path)
            {
                var d = Section(dto, path);
                int Channel(int? c, string n)
                {
                    var v = Required(c, $"{path}.{n}");
                    if (v is < 0 or > 255) _problems.Add($"'{path}.{n}' must be 0-255 (got {v}).");
                    return v;
                }
                return new HmiColor(Channel(d.R, "r"), Channel(d.G, "g"), Channel(d.B, "b"));
            }

            internal IReadOnlyDictionary<int, string> ValueLabels(
                List<HmiDefinitionDto.ValueLabelDto>? list, string path)
            {
                var map = new Dictionary<int, string>();
                foreach (var e in List(list, path))
                {
                    var value = Required(e.Value, path + "[].value");
                    var label = Text(e.Label, path + "[].label");
                    if (!map.TryAdd(value, label)) _problems.Add($"'{path}' repeats value {value}.");
                }
                return map;
            }
        }
    }
}
