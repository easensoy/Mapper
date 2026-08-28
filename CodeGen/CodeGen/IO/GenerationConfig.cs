namespace CodeGen.Configuration
{
    public sealed class GenerationConfig
    {
        public int RecipeArraySize { get; set; }
        public int StateTableCapacity { get; set; }
        public int DefaultMotionMs { get; set; }
        public int CoverMotionMs { get; set; }
        public int BearingPnpHomeBrakeMs { get; set; }

        // Attempts for a generated-file write that EAE may be holding open.
        public int FileWriteRetries { get; set; }

        // Periods the plant runs at, in ms. Declared because they are commissioning facts about
        // this rig's hardware, not implementation constants of the generator.
        public int Bx1IoScanPeriodMs { get; set; }
        public int M262BusCycleMs { get; set; }
        public int M262BusCycleTolerance { get; set; }
        public int M262BusCycleActionWhenMissed { get; set; }
        public int ActuatorInputPollMs { get; set; }

        // The EAE namespace every emitted type and instance lives in.
        public string ProjectNamespace { get; set; } = string.Empty;

        // What the generated application and its one canvas layer are CALLED in EAE. Names an engineer
        // reads in the device tree, so they are declared rather than compiled in - the layer's name was
        // spelled at the one place that emits it and could only ever be the name of one rig's project.
        public string ApplicationName { get; set; } = string.Empty;
        public string LayerName { get; set; } = string.Empty;

        // The IEC 61499 spelling of a duration. Grammar, so it stays in code; the NUMBER is declared.
        public static string Duration(int ms) => $"T#{ms}ms";

        // The one spelling of the project namespace, so the two dozen places that stamp a Namespace
        // attribute cannot disagree about which project they are emitting into.
        public static string Namespace => Current.ProjectNamespace;

        // The one spelling of each, for the same reason: an emitter that writes one of these names and
        // a wiper that blanks it are describing the same artefact.
        public static string Application => Current.ApplicationName;
        public static string Layer       => Current.LayerName;

        // Refused at load: a value this file does not declare is not a zero, it is a missing fact,
        // and every one of these decides something about the emitted project.
        private static void Validate(GenerationConfig c)
        {
            var errors = new System.Collections.Generic.List<string>();
            void Positive(string key, int v)
            {
                if (v <= 0) errors.Add($"{key} must be a positive number (declared: {v})");
            }
            Positive("recipeArraySize", c.RecipeArraySize);
            Positive("stateTableCapacity", c.StateTableCapacity);
            Positive("defaultMotionMs", c.DefaultMotionMs);
            Positive("coverMotionMs", c.CoverMotionMs);
            Positive("bearingPnpHomeBrakeMs", c.BearingPnpHomeBrakeMs);
            Positive("fileWriteRetries", c.FileWriteRetries);
            Positive("bx1IoScanPeriodMs", c.Bx1IoScanPeriodMs);
            Positive("m262BusCycleMs", c.M262BusCycleMs);
            Positive("m262BusCycleTolerance", c.M262BusCycleTolerance);
            Positive("actuatorInputPollMs", c.ActuatorInputPollMs);
            if (c.M262BusCycleActionWhenMissed < 0)
                errors.Add("m262BusCycleActionWhenMissed must not be negative");
            if (string.IsNullOrWhiteSpace(c.ProjectNamespace))
                errors.Add("projectNamespace is not declared, so every emitted FB would carry an " +
                           "empty Namespace and resolve to no type");
            // Blank is what a WIPED project carries, and it is how a Clean is told from a generation.
            // Declaring it blank would make every run look like a Clean in the EAE tree.
            foreach (var (key, value) in new[]
                     { ("applicationName", c.ApplicationName), ("layerName", c.LayerName) })
                if (string.IsNullOrWhiteSpace(value))
                    errors.Add($"{key} is not declared; a blank name is what a wiped project carries, " +
                               "so the generated project would be indistinguishable from a cleaned one");
            if (errors.Count > 0)
                throw new System.InvalidOperationException(
                    "Config/config.yaml is invalid: " + string.Join("; ", errors));
        }

        private static readonly YamlConfigFile<GenerationConfig> _file =
            new("Config", "config.yaml") { OnLoaded = Validate };

        public static GenerationConfig Current => _file.Load();

        /// The same declaration read from a run's OWN profile bundle. A root of null is the
        /// bundle shipped beside CodeGen.dll, which is what a normal run reads.
        public static GenerationConfig LoadFrom(string? root) => _file.Load(root);
    }
}
