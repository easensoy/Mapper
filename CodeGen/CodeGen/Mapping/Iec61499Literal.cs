using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Mapping
{
    // IEC 61499 literal syntax: how a planned VALUE is spelled as an FB Parameter. This is protocol,
    // not emission - the plan decides the value, this decides its text, so a planning pass can produce
    // a parameter without reaching for the XML document builder.
    public static class Iec61499Literal
    {
        public static string FormatString(string value) => $"'{value}'";
        public static string FormatInt(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static string FormatBool(bool value) => value ? "TRUE" : "FALSE";
        public static string FormatTimeMs(int ms) => $"T#{ms.ToString(System.Globalization.CultureInfo.InvariantCulture)}ms";

        // Formats an INT array as an EAE square-bracket literal, e.g. [1, 2, 9] (empty list -> "[]").
        public static string FormatIntArray(IEnumerable<int> values)
        {
            var formatted = string.Join(", ",
                values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return $"[{formatted}]";
        }

        // InterlockRule array-of-struct literal, e.g. [(FromState:=2, ToState:=4, SourceID:=6,
        // BlockedState:=2, TermCount:=1), ...]. Emits every slot; RuleCount bounds the evaluator so
        // trailing zero-rows unread. TermCount>=1 heads an alternative, 0 continues the one above it.
        public static string FormatRuleTable(
            IReadOnlyList<int> from, IReadOnlyList<int> to,
            IReadOnlyList<int> src, IReadOnlyList<int> blk, IReadOnlyList<int> terms, int capacity)
        {
            if (from.Count > capacity)
                throw new ArgumentOutOfRangeException(nameof(capacity),
                    $"{from.Count} rules will not fit a table declared for {capacity}.");
            var elems = new List<string>();
            for (int i = 0; i < capacity; i++)
                elems.Add(i < from.Count
                    ? $"(FromState:={from[i]}, ToState:={to[i]}, SourceID:={src[i]}, " +
                      $"BlockedState:={blk[i]}, TermCount:={terms[i]})"
                    : "(FromState:=0, ToState:=0, SourceID:=0, BlockedState:=0, TermCount:=0)");
            return "[" + string.Join(", ", elems) + "]";
        }

        // InterlockTable nested-struct literal: (Count:=N, Rules:=[(FromState:=…, …), …]).
        public static string FormatInterlockTable(
            IReadOnlyList<int> from, IReadOnlyList<int> to,
            IReadOnlyList<int> src, IReadOnlyList<int> blk, IReadOnlyList<int> terms, int capacity)
            => $"(Count:={from.Count}, Rules:={FormatRuleTable(from, to, src, blk, terms, capacity)})";

        // TargetStates struct literal: (Work1:=N, Work2:=N, Home:=N).
        public static string FormatTargetStates(int work1, int work2, int home)
            => $"(Work1:={work1}, Work2:={work2}, Home:={home})";

        // TelemetryConfig STRUCT literal for a Telemetry_CAT Config input, e.g. (QI:=TRUE,
        // ConnectionID:='SMC', URL:='mqtt://...', ClientIdentifier:='SMC_M262', ValidateCert:=0, CACert:='').
        public static string FormatTelemetryConfig(bool qi, string connectionId, string url,
            string clientIdentifier, int validateCert, string caCert)
            => $"(QI:={FormatBool(qi)}, ConnectionID:={FormatString(connectionId)}, " +
               $"URL:={FormatString(url)}, ClientIdentifier:={FormatString(clientIdentifier)}, " +
               $"ValidateCert:={validateCert}, CACert:={FormatString(caCert)})";

        // STRING array as an EAE square-bracket literal of single-quoted entries, e.g.
        // ['Feeder', '', 'PartInHopper']. Internal quotes doubled (IEC 61131-3 STRING escaping).
        // RecipeStep array-of-struct literal (mixed INT + STRING), e.g. [(StepType:=2,
        // CmdTargetName:='feeder', CmdStateArr:=1, Wait1Id:=0, Wait1State:=0, NextStep:=1,
        // AltCount:=1, TermCount:=1), ...]. Emits
        // every row; STRING member single-quoted, internal quotes doubled (IEC 61131-3).
        public static string FormatRecipeTable(
            IReadOnlyList<int> stepType, IReadOnlyList<string> cmdTargetName,
            IReadOnlyList<int> cmdStateArr, IReadOnlyList<int> wait1Id,
            IReadOnlyList<int> wait1State, IReadOnlyList<int> nextStep,
            IReadOnlyList<int> altCount, IReadOnlyList<int> termCount)
        {
            int n = stepType.Count;
            var elems = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var name = "'" + (cmdTargetName[i] ?? string.Empty).Replace("'", "''") + "'";
                elems.Add(
                    $"(StepType:={stepType[i]}, CmdTargetName:={name}, " +
                    $"CmdStateArr:={cmdStateArr[i]}, Wait1Id:={wait1Id[i]}, " +
                    $"Wait1State:={wait1State[i]}, NextStep:={nextStep[i]}, " +
                    $"AltCount:={altCount[i]}, TermCount:={termCount[i]})");
            }
            return "[" + string.Join(", ", elems) + "]";
        }
    }
}
