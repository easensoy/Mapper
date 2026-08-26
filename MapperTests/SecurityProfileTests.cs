using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using Xunit;

namespace MapperTests
{
    /// The controller's trust chain and account set.
    ///
    /// This document is written ONLY into a project that has none — an existing one is preserved
    /// byte-for-byte, because overwriting it invalidates the trust binding the controller was deployed
    /// with. That means no gate run reaches it, and an unexercised path that emits credentials is
    /// exactly the one worth pinning: these assert the document the compiler WOULD write.
    public sealed class SecurityProfileTests
    {
        // EAE escapes a quote inside .solutionData as the six characters \u0022.
        const string Q = @"\u0022";

        static string Doc() => M262TopologyEmitter.BuildSolutionDataJson(TestConfig.Cfg, "SOLUTION-ID");

        [Fact]
        public void Every_declared_certificate_reaches_the_trust_chain_in_declaration_order()
        {
            var declared = TestConfig.Cfg.Security.CertThumbprintChain;
            Assert.NotEmpty(declared);

            // Joined with ';' and terminated with one, which is the form EAE reads.
            var expected = string.Concat(declared.Select(t => t + ";"));
            Assert.Equal(expected, TestConfig.Cfg.Security.ChainLiteral);
            Assert.Contains(expected, Doc());
        }

        [Fact]
        public void The_account_documents_carry_the_declared_users_roles_and_permissions()
        {
            var s = TestConfig.Cfg.Security;
            var doc = Doc();

            foreach (var p in new[] { s.Principal, s.Anonymous })
            {
                Assert.Contains($"{Q}user_name{Q}:{Q}{p.UserName}{Q}", doc);
                Assert.Contains($"{Q}password{Q}:{Q}{p.PasswordHash}{Q}", doc);
                Assert.Contains($"{Q}state{Q}:{Q}{p.State}{Q}", doc);
                Assert.Contains($"{Q}assigned_role{Q}:[{Q}{p.RoleName}{Q}]", doc);
                Assert.Contains(
                    $"{Q}permission_name{Q}:[" + string.Join(",", p.Permissions.Select(x => $"{Q}{x}{Q}")) + "]",
                    doc);
            }
        }

        [Fact]
        public void The_two_account_documents_keep_the_field_order_and_null_form_EAE_writes()
        {
            // Not cosmetic: the named account writes an EMPTY start date and puts version first, the
            // anonymous one writes a JSON null and puts version last. EAE emits them that way, so a
            // document that "tidied" them would differ from one EAE produced itself.
            var s = TestConfig.Cfg.Security;
            var doc = Doc();

            Assert.Contains($"{{{Q}version{Q}:{Q}1{Q},{Q}users_list{Q}:[{{{Q}user_name{Q}:{Q}{s.Principal.UserName}{Q}", doc);
            Assert.Contains($"{Q}AccountStartDate{Q}:{Q}{Q}", doc);          // named: empty string
            Assert.Contains($"{Q}AccountStartDate{Q}:null", doc);            // anonymous: JSON null
            Assert.Contains($"]}}],{Q}version{Q}:{Q}1{Q}}}", doc);           // anonymous: version last
        }

        [Fact]
        public void A_missing_credential_is_refused_by_name_and_never_by_value()
        {
            // The refusal has to be actionable without leaking what it was checking: a diagnostic that
            // echoed a hash would put the credential into every log that captured the failure.
            var blank = new SecurityProfile
            {
                CsConfHash = "present",
                AnonCsConfHash = string.Empty,
                CertThumbprintChain = new List<string>(),
                Principal = new SecurityPrincipal
                {
                    UserName = "u", PasswordHash = string.Empty, State = "Active",
                    RoleName = "r", Permissions = new List<string> { "p" },
                },
                Anonymous = new SecurityPrincipal
                {
                    UserName = "a", PasswordHash = "h", State = "Active",
                    RoleName = "ar", Permissions = new List<string>(),
                },
            };

            var boom = Assert.Throws<InvalidOperationException>(() => Validate(blank));
            foreach (var field in new[] { "anonCsConfHash", "certThumbprintChain",
                                          "principal.passwordHash", "anonymous.permissions" })
                Assert.Contains(field, boom.Message);
            Assert.DoesNotContain("present", boom.Message);   // no declared VALUE is echoed
            Assert.DoesNotContain("h\"", boom.Message);
        }

        // The loader's own validator, reached the way the loader reaches it.
        static void Validate(SecurityProfile p)
        {
            try
            {
                typeof(SecurityProfile)
                    .GetMethod("Validate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .Invoke(null, new object[] { p });
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;   // the loader sees the real refusal, so the test must too
            }
        }

        [Fact]
        public void A_principal_never_carries_its_hash_into_a_log_line()
        {
            // ToString is what lands in a log, a watch window or an aggregate diagnostic.
            var text = TestConfig.Cfg.Security.Principal.ToString();
            Assert.Contains(TestConfig.Cfg.Security.Principal.UserName, text);
            Assert.DoesNotContain(TestConfig.Cfg.Security.Principal.PasswordHash, text);
        }
    }
}
