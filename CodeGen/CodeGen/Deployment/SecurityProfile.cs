using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.IO;

namespace CodeGen.Configuration
{
    /// The controller's declared security payload: which certificates it trusts and which accounts and
    /// roles it accepts.
    ///
    /// These are CREDENTIALS. They are one-way hashes rather than plaintext, but they are still the
    /// material an offline attack works against, so:
    ///   - nothing here is ever written to a log, a warning or an exception message;
    ///   - the values are declared in Config/security.yml so a site replaces them without a rebuild;
    ///   - the JSON SHAPE they are written into stays in the M262 backend, because that is EAE's own
    ///     document grammar rather than configuration.
    ///
    /// A missing or empty value is REFUSED at load. A silently blank password hash or an empty trust
    /// chain would produce a project that deploys and then rejects every login, or worse, trusts
    /// nothing and is diagnosed as a network fault.
    public sealed class SecurityProfile
    {
        public string CsConfHash { get; set; } = string.Empty;
        public string AnonCsConfHash { get; set; } = string.Empty;

        /// In declaration order: EAE joins these with ';' and the order is part of the artefact.
        public List<string> CertThumbprintChain { get; set; } = new();

        public SecurityPrincipal Principal { get; set; } = new();
        public SecurityPrincipal Anonymous { get; set; } = new();

        /// The chain as EAE writes it: joined with ';' and terminated with one.
        public string ChainLiteral =>
            string.Concat(CertThumbprintChain.Select(t => t.Trim() + ";"));

        static readonly YamlConfigFile<SecurityProfile> File =
            new("Config", "security.yml") { OnLoaded = Validate };

        public static SecurityProfile Current => File.Load();

        // Refuses by NAMING THE FIELD, never by quoting its value: a diagnostic that echoed a hash
        // would put the credential in every log that captured the failure.
        static void Validate(SecurityProfile s)
        {
            var missing = new List<string>();
            void Need(string name, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) missing.Add(name);
            }

            Need("csConfHash", s.CsConfHash);
            Need("anonCsConfHash", s.AnonCsConfHash);
            foreach (var (who, p) in new[] { ("principal", s.Principal), ("anonymous", s.Anonymous) })
            {
                Need($"{who}.userName", p.UserName);
                Need($"{who}.passwordHash", p.PasswordHash);
                Need($"{who}.state", p.State);
                Need($"{who}.roleName", p.RoleName);
                if (p.Permissions.Count == 0) missing.Add($"{who}.permissions");
                if (p.Permissions.Any(string.IsNullOrWhiteSpace)) missing.Add($"{who}.permissions (blank entry)");
            }

            if (s.CertThumbprintChain.Count == 0) missing.Add("certThumbprintChain");
            if (s.CertThumbprintChain.Any(string.IsNullOrWhiteSpace))
                missing.Add("certThumbprintChain (blank entry)");

            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "[Security] Config/security.yml does not declare: " + string.Join(", ", missing) +
                    ". The controller would be emitted with an incomplete trust chain or account set, " +
                    "which deploys cleanly and then rejects every login. Generation ABORTED.");
        }
    }

    /// One account and the role it is granted. Declared, so a site's own credentials replace these.
    public sealed class SecurityPrincipal
    {
        public string UserName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public List<string> Permissions { get; set; } = new();

        /// Deliberately does NOT include the hash: a principal that lands in a log, a debugger watch or
        /// an exception message must not carry the credential with it.
        public override string ToString() => $"{UserName} ({RoleName})";
    }
}
