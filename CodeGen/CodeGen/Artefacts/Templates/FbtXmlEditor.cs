using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;

namespace CodeGen.Services
{
    // Shared .fbt/.xml load/save primitives for the deploy-time template patchers. They retry on a
    // transient EAE file lock and preserve byte-identical formatting. Consumed via `using static`.
    // WHERE the deployed types are and HOW LONG to keep trying one EAE is holding open - the two
    // facts every patch needs and neither should read for itself. Carried together so a patcher takes
    // one parameter, not two, and cannot pair a root with a budget from a different run.
    internal readonly record struct FbtEditScope(string Root, int Retries)
    {
        public static FbtEditScope For(string eaeProjectDir, Configuration.CompilerConfiguration cfg) =>
            new(eaeProjectDir, cfg.Generation.FileWriteRetries);
    }

    internal static class FbtXmlEditor
    {
        internal static XDocument LoadXmlWithRetry(string path, LoadOptions opts, int retries)
        {
            for (int attempt = 1, delay = 50; ; attempt++, delay = Math.Min(delay * 2, 800))
            {
                try { return XDocument.Load(path, opts); }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < retries)
                {
                    System.Threading.Thread.Sleep(delay);
                }
            }
        }

        // Plain doc.Save formatting, so the on-disk bytes stay byte-identical to XDocument.Save(path).
        internal static void SaveXmlWithRetry(XDocument doc, string path, int retries) =>
            Retry(retries, () => doc.Save(path));

        // Returns true if anything was removed.
        internal static bool RemoveElems(IEnumerable<XElement>? src, Func<XElement, bool> pred)
        {
            if (src == null) return false;
            var hits = src.Where(pred).ToList();
            foreach (var h in hits) h.Remove();
            return hits.Count > 0;
        }

        // EAE writes a connection group only when non-empty, so adding the first wire must create one.
        internal static ConnectionSet Connections(XElement network, XNamespace ns, string group)
        {
            var element = network.Element(ns + group);
            if (element == null) { element = new XElement(ns + group); network.Add(element); }
            return new ConnectionSet(element, ns);
        }

        // One connection group and the edits the patchers make to it. XAttribute constructor order IS the
        // serialised order, so building a Connection in one place keeps every patcher's output identical.
        internal readonly struct ConnectionSet
        {
            private readonly XElement _group;
            private readonly XNamespace _ns;

            internal ConnectionSet(XElement group, XNamespace ns) { _group = group; _ns = ns; }

            internal IEnumerable<XElement> All => _group.Elements(_ns + "Connection");

            internal XElement? Find(string source, string destination) =>
                All.FirstOrDefault(c =>
                    string.Equals((string?)c.Attribute("Source"), source, StringComparison.Ordinal) &&
                    string.Equals((string?)c.Attribute("Destination"), destination, StringComparison.Ordinal));

            internal bool Has(string source, string destination) => Find(source, destination) != null;

            // A data input takes ONE source, so guard on the source alone: a second wire would be two
            // drivers for one value.
            internal bool HasSource(string source) =>
                All.Any(c => string.Equals((string?)c.Attribute("Source"), source, StringComparison.Ordinal));

            // Appends, so a re-deploy is a no-op and the order established the first time is preserved.
            internal bool Add(string source, string destination)
            {
                if (Has(source, destination)) return false;
                _group.Add(new XElement(_ns + "Connection",
                    new XAttribute("Source", source),
                    new XAttribute("Destination", destination)));
                return true;
            }

            // Unguarded on purpose: adding a guard would change what a re-deploy over an existing tree produces.
            internal void Append(string source, string destination) =>
                _group.Add(new XElement(_ns + "Connection",
                    new XAttribute("Source", source),
                    new XAttribute("Destination", destination)));

            internal bool Remove(string source, string destination) =>
                RemoveElems(All, c =>
                    string.Equals((string?)c.Attribute("Source"), source, StringComparison.Ordinal) &&
                    string.Equals((string?)c.Attribute("Destination"), destination, StringComparison.Ordinal));

            internal bool RemoveTo(params string[] destinations)
            {
                var set = destinations.ToHashSet(StringComparer.Ordinal);
                return RemoveElems(All, c => set.Contains((string?)c.Attribute("Destination") ?? string.Empty));
            }

        }

        // A file that will not parse is skipped, not fatal: verifiers must report on the tree they were given.
        internal static IEnumerable<(string Path, XDocument Doc)> EachDeployedFbt(string eaeProjectDir)
        {
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec)) yield break;
            foreach (var fbt in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
            {
                XDocument doc;
                try { doc = XDocument.Load(fbt); }
                catch { continue; }
                yield return (fbt, doc);
            }
        }

        // The deployed CAT/type .fbt under IEC61499/ (excluding its _HMI faceplate); "" if absent.
        internal static string FindDeployedFbt(string eaeProjectDir, string fbtFileName)
            => Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"), fbtFileName, SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal)) ?? string.Empty;

        // Hands (doc, root, ns, path) to `edit`, which mutates, saves and logs.
        //
        // An ABSENT .fbt is a configuration fact - this run does not deploy that type - so it is skipped
        // with a note. A THROWING edit is not: it means the file is locked or the archive no longer has
        // the shape the patch expects, and an unapplied interface patch deploys cleanly while EAE
        // silently ignores every instance parameter naming a pin the type never gained. So it ABORTS.
        internal static void EditDeployedFbt(FbtEditScope scope, string fbtFileName, string failNote,
            DeployResult result, Action<XDocument, XElement, XNamespace, string> edit, string? notFoundNote = null)
        {
            var fbt = FindDeployedFbt(scope.Root, fbtFileName);
            if (string.IsNullOrEmpty(fbt))
            {
                if (notFoundNote != null) result.Warnings.Add(notFoundNote);
                return;
            }
            try
            {
                var doc = LoadXmlWithRetry(fbt, LoadOptions.PreserveWhitespace, scope.Retries);
                var root = doc.Root;
                if (root == null) return;
                edit(doc, root, root.GetDefaultNamespace(), fbt);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{failNote}: {ex.Message} — the deploy-time patch of '{fbtFileName}' could not be " +
                    "applied, so the deployed type does not have the interface the planner's parameters " +
                    "name. This is usually EAE holding the .fbt open during Generate: CLOSE EAE and " +
                    "Generate again. Generation ABORTED rather than shipping a tree EAE will not run.", ex);
            }
        }

        // A REQUIRED structural patch: a missing .fbt, an unreadable root or a throwing edit all abort.
        // A patch that reshapes a TYPE's interface is what makes the planner's instance parameters legal;
        // unapplied, EAE ignores every parameter naming an undeclared pin and the deploy looks correct.
        internal static void RequireDeployedFbt(FbtEditScope scope, string fbtFileName, string what,
            Action<XDocument, XElement, XNamespace, string> edit)
        {
            var fbt = FindDeployedFbt(scope.Root, fbtFileName);
            if (string.IsNullOrEmpty(fbt))
                throw new InvalidOperationException(
                    $"{what}: {fbtFileName} is not deployed under {scope.Root}\\IEC61499, so the patch " +
                    "cannot be applied and any instance parameter it enables would be a phantom.");
            var doc = LoadXmlWithRetry(fbt, LoadOptions.PreserveWhitespace, scope.Retries);
            var root = doc.Root
                ?? throw new InvalidOperationException($"{what}: {fbt} has no root element.");
            edit(doc, root, root.GetDefaultNamespace(), fbt);
        }

        // Copy-if-absent, then record in DataTypesDeployed so the dfbproj registers the type.
        internal static void DeployDatatype(FbtEditScope scope, string name, string dtXml,
            DeployResult result, string? patchNote = null)
        {
            try
            {
                var dtDir = Path.Combine(scope.Root, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, name + ".dt");
                // Written every deploy, never copy-if-absent: a datatype this plan SIZES would
                // otherwise keep the previous run's array length while the literal grew past it.
                File.WriteAllText(dtPath, dtXml);
                if (!result.DataTypesDeployed.Contains(name)) result.DataTypesDeployed.Add(name);
                result.PatchesApplied.Add($"{name}.dt deployed + registered{(patchNote is null ? "" : " " + patchNote)}");
                MapperLogger.Info($"[Deploy] {name}.dt written + registered");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{name}.dt deploy failed: {ex.Message} — the datatype every instance of this "
                    + "interface is declared in is missing, so EAE cannot resolve the type. Generation ABORTED.", ex);
            }
        }

        // The CALLER supplies the writer settings, because .hcf encodings differ deliberately (one exporter
        // emits a BOM, the other must not, one rewrites newlines) and collapsing them changes the bytes.
        public static int SaveXmlRetrying(int retries,
            string path, System.Xml.XmlWriterSettings settings, Action<System.Xml.XmlWriter> write) =>
            Retry(retries, () =>
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var w = System.Xml.XmlWriter.Create(fs, settings);
                write(w);
            });

        // The one place that decides how long to keep trying a locked file. Returns the succeeding
        // attempt. The budget is DECLARED (config.yaml fileWriteRetries) and handed in with the run, so
        // this layer holds no policy of its own and cannot read a different one than the run compiled with.
        private static int Retry(int attempts, Action write)
        {
            for (int attempt = 1, delay = 50; ; attempt++, delay = Math.Min(delay * 2, 800))
            {
                try { write(); return attempt; }
                catch (Exception e) when (attempt < attempts &&
                                          (e is IOException || e is UnauthorizedAccessException))
                {
                    System.Threading.Thread.Sleep(delay);
                }
            }
        }
    }
}
