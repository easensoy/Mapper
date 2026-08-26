using System;

namespace CodeGen.Translation
{
    // A deployment target's identity.
    //
    // NOT a closed set. The targets a project has are the rows device.yml declares, so adding another
    // controller of a kind that already has a backend is a YAML edit and nothing here changes. It was
    // an enum, which meant every new target needed a C# member before it could even be named - and a
    // roster row naming a target the enum did not know silently became Unknown.
    //
    // Equality and hashing are the name's, so it keys a dictionary exactly as the enum did, and
    // ToString() answers the declared name (the default answers "Unknown") so anything that prints a
    // target - a report, an HMI attribute, a diagnostic - reads the same as before.
    public readonly record struct PlcAssignment : IComparable<PlcAssignment>
    {
        private readonly string? _name;

        private PlcAssignment(string? name) => _name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        public string Name => _name ?? string.Empty;

        // No target. A component placed nowhere, a symbol no .hcf claims, a lookup that found nothing.
        public static PlcAssignment Unknown => default;

        public bool IsKnown => _name != null;

        public static PlcAssignment Named(string? name) => new(name);

        // Ordered by name so a caller can sort targets deterministically without knowing them.
        public int CompareTo(PlcAssignment other) =>
            string.Compare(Name, other.Name, StringComparison.Ordinal);

        public override string ToString() => _name ?? "Unknown";
    }
}
