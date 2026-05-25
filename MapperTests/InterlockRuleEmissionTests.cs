using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Process;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Devices.Shared;
using CodeGen.Services;
using Xunit;
using Xunit.Abstractions;

namespace MapperTests
{
    /// <summary>
    /// Defect 2: actuator interlocks defined in Control.xml's STATE-level
    /// &lt;Interlock_Condition&gt; blocks must be translated into the
    /// Five_State_Actuator_CAT InterlockManager Rule* arrays, using the same
    /// sensors-first state_table id scheme as the recipe Wait1Id. If a
    /// Control.xml interlock is in scope but RuleCount comes out 0 the build
    /// must abort (a silently-empty safety net is worse than none).
    /// </summary>
    public class InterlockRuleEmissionTests
    {
        readonly ITestOutputHelper _out;
        public InterlockRuleEmissionTests(ITestOutputHelper o) => _out = o;

        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        static (List<VueOneComponent> all, StationContents contents,
                IReadOnlyDictionary<string, int> scopedIds) Load()
        {
            var all = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = all.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, all);
            var scopedIds = ProcessRecipeArrayGenerator.BuildScopedComponentMap(
                contents.Sensors, contents.Actuators);
            return (all, contents, scopedIds);
        }

        [Fact]
        public void Fixture_EmitsNonZeroInterlockRules_ForAtLeastOneActuator()
        {
            var (all, contents, scopedIds) = Load();

            int totalRules = 0;
            foreach (var act in contents.Actuators)
            {
                var (count, from, to, src, blk) =
                    SystemInjector.BuildInterlockRules(act, all, scopedIds);
                _out.WriteLine(
                    $"{act.Name}: RuleCount={count} From=[{string.Join(",", from.Take(count))}] " +
                    $"To=[{string.Join(",", to.Take(count))}] Src=[{string.Join(",", src.Take(count))}] " +
                    $"Blocked=[{string.Join(",", blk.Take(count))}]");
                totalRules += count;
            }

            Assert.True(totalRules > 0,
                "SMC_Vue2VC defines Control.xml <Interlock_Condition> blocks — the " +
                "translator must emit at least one InterlockManager rule.");
        }

        [Fact]
        public void EveryInScopeInterlock_YieldsRules_NoSilentlyEmptySafetyNet()
        {
            // This is the exact deploy-time abort invariant from
            // SystemLayoutInjector: an actuator with in-scope Control.xml
            // interlock conditions must NOT emit RuleCount=0.
            var (all, contents, scopedIds) = Load();

            foreach (var act in contents.Actuators)
            {
                int inScope = SystemInjector.CountInScopeInterlockConds(act, scopedIds);
                var ap = SystemInjector.BuildActuatorParameters(
                    act, assignedId: 99, all, scopedIds);
                int ruleCount = int.Parse(ap["RuleCount"], CultureInfo.InvariantCulture);

                if (inScope > 0)
                    Assert.True(ruleCount > 0,
                        $"'{act.Name}' has {inScope} in-scope Control.xml interlock " +
                        $"condition(s) but emitted RuleCount={ruleCount} — this is the " +
                        "silently-empty safety net the build must abort on.");
            }
        }

        [Fact]
        public void RuleSourceID_UsesSensorsFirstScopedScheme()
        {
            // RuleSourceID must index the same sensors-first state_table slots
            // the recipe Wait1Id uses, so the runtime InterlockManager reads
            // the slot the blocking component actually publishes into.
            var (all, contents, scopedIds) = Load();
            var validIds = new HashSet<int>(scopedIds.Values);

            foreach (var act in contents.Actuators)
            {
                var (count, _, _, src, _) =
                    SystemInjector.BuildInterlockRules(act, all, scopedIds);
                for (int i = 0; i < count; i++)
                    Assert.Contains(src[i], validIds);
            }
        }

        [Fact]
        public void LegacyCaller_NoScopedMap_GetsPassThroughZeroRules()
        {
            // The null-scopedIds overload (legacy/test callers) must NOT invent
            // rules — it returns RuleCount=0 (pass-through) by design.
            var (all, contents, _) = Load();
            var act = contents.Actuators.First();
            var ap = SystemInjector.BuildActuatorParameters(
                act, assignedId: 1, all, scopedIds: null);
            Assert.Equal("0", ap["RuleCount"]);
        }
    }
}
