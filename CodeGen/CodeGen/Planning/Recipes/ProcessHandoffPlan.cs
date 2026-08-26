using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;
using CodeGen.Mapping;

namespace CodeGen.Translation.Process.Recipes
{
    // How a producer's phase announcement reaches a consumer's state_table. Derived per generation from
    // the twin plus controller allocation, never from a component name, model name or configuration flag.
    [Flags]
    public enum HandoffTransport
    {
        None = 0,           // no route: the consumer falls back to the material bridge, or generation fails
        Ring = 1,           // producer and consumer share a report ring: the producer's self-named CMD carries it
        CrossController = 2 // separate rings: the phase travels the process-phase cross-reference path
    }

    // One model-derived handoff: a consumer transition condition that names a state of another process.
    public sealed record ProcessHandoff(
        string ProducerName,
        int ProducerProcessId,
        string ProducerEntryStateId,
        int ProducerEntryStateNumber,
        string ProducerCompletionStateId,
        int ProducerCompletionStateNumber,
        string ConsumerName,
        int ConsumerProcessId,
        string ConsumingStateId,
        string ConditionName,
        HandoffTransport Transport);

    // Every process-to-process handoff the twin declares, transport resolved and capacity checked. The
    // recipe rows, the syslay wiring and the receiver slot all read this one answer, so they cannot disagree.
    public sealed class ProcessHandoffPlan
    {
        // A producer/consumer pair, compared the way process names are compared everywhere else. A
        // typed key rather than two names glued together: a separator is a character a name could
        // contain, so the pair ('a b','c') and ('a','b c') would be one entry.
        private readonly record struct Pair(string Producer, string Consumer)
        {
            public static Pair Of(string? producer, string? consumer) =>
                new((producer ?? string.Empty).Trim(), (consumer ?? string.Empty).Trim());

            public bool Equals(Pair other) =>
                string.Equals(Producer, other.Producer, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Consumer, other.Consumer, StringComparison.OrdinalIgnoreCase);

            public override int GetHashCode() => HashCode.Combine(
                Producer.ToLowerInvariant(), Consumer.ToLowerInvariant());
        }

        private readonly List<ProcessHandoff> _handoffs = new();
        private readonly Dictionary<string, Dictionary<string, HandoffTransport>> _announce =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Pair, HandoffTransport> _transportByPair = new();
        private readonly Dictionary<string, int> _receiverSlot = new(StringComparer.OrdinalIgnoreCase);
        // The process type's own announcement ports, taken from the run's index at construction.
        private readonly CatPhaseHandoff _transport;

        private readonly string _processTypeName;

        private ProcessHandoffPlan(Mapping.TemplateIndex manifest)
        {
            _transport = manifest.PhaseTransport;
            _processTypeName = manifest.ProcessType.Name;
        }

        // The producer is the process a condition names; the consumer is the one whose transition carries it.
        public static ProcessHandoffPlan Derive(
            CodeGen.Domain.Twin.TwinModel twin,
            IReadOnlyDictionary<string, int> processIdByName,
            IReadOnlyDictionary<string, CodeGen.Domain.Twin.ProcessGraph> graphs,
            Func<VueOneComponent, VueOneComponent, bool> sameRing, Mapping.TemplateIndex manifest)
        {
            var plan = new ProcessHandoffPlan(manifest);
            foreach (var consumer in twin.Processes.Select(p => p.Source))
            {
                foreach (var st in consumer.States)
                foreach (var cond in st.Transitions.SelectMany(t => t.Guard?.References()
                    ?? (IReadOnlyList<VueOneCondition>)Array.Empty<VueOneCondition>()))
                {
                    var producer = twin.ComponentOf(cond);
                    if (producer == null || !ComponentType.IsProcess(producer)) continue;
                    if (string.Equals(producer.Name?.Trim(), consumer.Name?.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                    var refState = twin.StateOf(producer, cond);
                    // A reference to the producer's ENTRY phase is answered by the declared handoff
                    // policy rather than by a phase announcement, so nothing is announced for it here.
                    if (refState == null || refState.InitialState) continue;

                    if (!processIdByName.TryGetValue(producer.Name?.Trim() ?? string.Empty, out int producerId)) continue;
                    processIdByName.TryGetValue(consumer.Name?.Trim() ?? string.Empty, out int consumerId);

                    var entry = graphs.TryGetValue(producer.Name?.Trim() ?? string.Empty, out var g)
                        ? g.Entry : null;
                    plan._handoffs.Add(new ProcessHandoff(
                        producer.Name?.Trim() ?? string.Empty, producerId,
                        entry?.StateID ?? string.Empty, entry?.StateNumber ?? 0,
                        refState.StateID, refState.StateNumber,
                        consumer.Name?.Trim() ?? string.Empty, consumerId,
                        st.StateID, cond.Name ?? string.Empty,
                        sameRing(producer, consumer) ? HandoffTransport.Ring : HandoffTransport.CrossController));
                }
            }

            plan.RejectUnsupportedFanIn();
            plan.Index();
            return plan;
        }

        // A consumer reads its transported phase from ONE input group, so a second cross-ring producer
        // cannot be represented. Fail here rather than deploy a machine that ignores a declared handoff.
        private void RejectUnsupportedFanIn()
        {
            foreach (var group in _handoffs
                .Where(h => h.Transport == HandoffTransport.CrossController)
                .GroupBy(h => h.ConsumerName, StringComparer.OrdinalIgnoreCase))
            {
                var producers = group.Select(h => h.ProducerName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                if (producers.Count <= _transport.ProducersPerConsumer) continue;
                throw new InvalidOperationException(
                    $"[Handoff] '{group.Key}' consumes phases from {producers.Count} processes on other " +
                    $"controllers ({string.Join(", ", producers)}), but the process-phase transport carries " +
                    $"{_transport.ProducersPerConsumer} producer per consumer: " +
                    $"{_processTypeName} declares one " +
                    $"{_transport.EventIn}/{_transport.DataIn} " +
                    $"input group and one {_transport.ReceiverSlotParam} slot. Conditions involved: " +
                    string.Join("; ", group.Select(h => $"'{h.ConditionName}' on state '{h.ConsumingStateId}'")) + ".");
            }
        }

        private void Index()
        {
            foreach (var h in _handoffs)
            {
                if (h.Transport == HandoffTransport.None) continue;

                // A phase carried by several transports announces on each.
                var byState = _announce.TryGetValue(h.ProducerName, out var m)
                    ? m : _announce[h.ProducerName] = new Dictionary<string, HandoffTransport>(StringComparer.Ordinal);
                void Add(string id) { if (id.Length > 0) byState[id] = byState.GetValueOrDefault(id) | h.Transport; }
                Add(h.ProducerCompletionStateId);
                Add(h.ProducerEntryStateId);

                var key = Pair.Of(h.ProducerName, h.ConsumerName);
                _transportByPair[key] = _transportByPair.GetValueOrDefault(key) | h.Transport;

                // The receiver slot is the producer's own process id, so the recipe's WAIT reads that slot.
                if (h.Transport == HandoffTransport.CrossController)
                    _receiverSlot[h.ConsumerName] = h.ProducerProcessId;
            }
        }

        public IReadOnlyDictionary<string, HandoffTransport> AnnouncementsOf(string producerName) =>
            _announce.TryGetValue(producerName ?? string.Empty, out var m)
                ? m
                : (IReadOnlyDictionary<string, HandoffTransport>)new Dictionary<string, HandoffTransport>();

        // None = no route; the caller falls back to the material bridge or fails.
        public HandoffTransport TransportFor(string producerName, string consumerName) =>
            _transportByPair.GetValueOrDefault(Pair.Of(producerName, consumerName));

        public int? ReceiverSlotOf(string consumerName) =>
            _receiverSlot.TryGetValue(consumerName ?? string.Empty, out var slot) ? slot : null;

        // One per (producer, consumer) pair, for the backend to render transport for.
        public IEnumerable<ProcessHandoff> CrossControllerLinks() =>
            _handoffs.Where(h => h.Transport == HandoffTransport.CrossController)
                     .GroupBy(h => Pair.Of(h.ProducerName, h.ConsumerName))
                     .Select(g => g.First());
    }
}
