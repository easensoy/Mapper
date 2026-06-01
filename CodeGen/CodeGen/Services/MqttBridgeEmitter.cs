using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Services
{
    /// <summary>
    /// Emits the BX1-side MQTT bridge that carries M262 + M580 component state
    /// to the broker. Architecture (see <c>Docs/ARCHITECTURE.md</c> §4 / Mapping
    /// folder):
    ///
    /// <list type="bullet">
    ///   <item>M262 and M580 dPACs have NO MQTT runtime (firmware-gated to Soft
    ///         dPAC; MQTT_CONNECTION returns ReturnCode 50 there). Only BX1
    ///         hosts the MQTT runtime.</item>
    ///   <item>For BX1's OWN components (Cover PnP), publishing happens via the
    ///         embedded MqttPub inside each CAT (patched in by
    ///         <c>TemplateLibraryDeployer.PatchCatMqttPublish</c>). Those land
    ///         on BX1 instances and fire natively.</item>
    ///   <item>For M262/M580 components, the embedded MqttPub is dead (compiles
    ///         but ReturnCode 50 at runtime). To bridge their state to the
    ///         broker, this emitter adds a STANDALONE
    ///         <c>MqttFmt_&lt;comp&gt;</c> + <c>MqttPub_&lt;comp&gt;</c> pair on
    ///         BX1 (BucketFor's MqttFmt_/MqttPub_ prefix routes them there) and
    ///         a CROSS-RESOURCE syslay wire from the remote component's
    ///         exposed boundary state (<c>state_out</c>+<c>current_state_to_process</c>
    ///         on actuators, <c>pst_out</c>+<c>Status</c> on sensors) into the
    ///         formatter. EAE bridges the cross-resource event/data at deploy.</item>
    /// </list>
    ///
    /// One emitted pair per remote component, ConnectionID matches the BX1
    /// <c>MqttConn</c>, topic <c>smc/&lt;comp_lowercase&gt;/state</c>. BX1
    /// components are skipped (already published by their own embedded MqttPub).
    /// Gated on <c>MqttPublishEnabled</c>; no-op otherwise.
    /// </summary>
    public static class MqttBridgeEmitter
    {
        /// <summary>
        /// Bridge formatter row Y (above the BX1 frame top at Y=1700; sits in
        /// the BX1 floating zone alongside MqttConn at Y=200).
        /// </summary>
        private const int FormatterRowY = 600;

        /// <summary>Bridge publisher row Y (one row below the formatter row).</summary>
        private const int PublisherRowY = 1100;

        /// <summary>Horizontal pitch between bridge publisher columns.</summary>
        private const int BridgeColumnPitch = 500;

        /// <summary>
        /// Emits one <c>MqttFmt_&lt;comp&gt;</c> + <c>MqttPub_&lt;comp&gt;</c>
        /// pair per M262 / M580 component (sensors + actuators) along with the
        /// cross-resource event/data feed and the local Fmt → Pub chain.
        /// Skipped entirely when <paramref name="cfg"/> has
        /// <c>MqttPublishEnabled = false</c>.
        /// </summary>
        public static void EmitBridge(SyslayBuilder builder, MapperConfig cfg)
        {
            if (cfg == null || !cfg.MqttPublishEnabled) return;

            int xBase = LayoutGrid.ColumnBaseX(PlcAssignment.BX1);  // 29000
            int index = 0;
            foreach (var entry in ComponentRegistry.ByName.Values)
            {
                if (!ShouldBridge(entry)) continue;
                EmitBridgePublisher(builder, cfg, entry, xBase, index);
                index++;
            }
        }

        /// <summary>
        /// True when this entry is a remote (M262 / M580) sensor or actuator
        /// that should be bridged to BX1. BX1 components publish via their own
        /// embedded MqttPub; Process FBs / shared infra have no state to bridge.
        /// </summary>
        private static bool ShouldBridge(ComponentEntry entry)
        {
            // Remote PLC only.
            if (entry.Plc != PlcAssignment.M262 && entry.Plc != PlcAssignment.M580)
                return false;

            // Skip self-Process entries (e.g. Feed_Station has ProcessOwner==itself).
            if (string.Equals(entry.Name, entry.ProcessOwner, StringComparison.Ordinal))
                return false;

            // Shared infra (Area, Station, *_HMI, *_Term) has empty ProcessOwner —
            // nothing to publish.
            if (string.IsNullOrEmpty(entry.ProcessOwner)) return false;

            // Actuators and sensors are bridged; other rows aren't.
            return entry.Row == LayoutRow.Actuator
                || entry.Row == LayoutRow.Process
                || entry.Row == LayoutRow.Sensor;
        }

        private static void EmitBridgePublisher(SyslayBuilder builder, MapperConfig cfg,
            ComponentEntry entry, int xBase, int index)
        {
            string compName = entry.Name;
            string compLower = compName.ToLowerInvariant();
            string fmtName = "MqttFmt_" + compName;
            string pubName = "MqttPub_" + compName;
            string topic = $"{cfg.MqttTopicRoot}/{compLower}/state";
            string connId = cfg.MqttClientId;  // 'SMC_BX1' — same string MqttConn carries

            int fmtX = xBase + index * BridgeColumnPitch;
            int pubX = xBase + index * BridgeColumnPitch;

            // MqttStateFormatter (custom basic FB deployed by DeployMqttFormatter).
            // INT state in → STRING payload out (JSON {state:N}).
            builder.AddFB(FBIdGenerator.GenerateFBId(fmtName),
                fmtName, "MqttStateFormatter", "Main",
                fmtX, FormatterRowY);

            // MQTT_PUBLISH hashed variant — CNTX:=1 channel materialises Topic1
            // / Payload1 / QoS1 / Retain1 / PUBLISH1. Plain base type fails
            // ERR_NO_SUCH_TYPE; hashed variant + InterfaceParams is what works.
            var pubParams = new Dictionary<string, string>
            {
                ["QI"]           = SyslayBuilder.FormatBool(true),
                ["ConnectionID"] = SyslayBuilder.FormatString(connId),
                ["Topic1"]       = SyslayBuilder.FormatString(topic),
                ["QoS1"]         = SyslayBuilder.FormatInt(cfg.MqttQoS),
                ["Retain1"]      = SyslayBuilder.FormatBool(cfg.MqttRetain),
            };
            var pubAttrs = new Dictionary<string, string>
            {
                ["Configuration.GenericFBType.InterfaceParams"] =
                    "Runtime.NetConnectivity#CNTX:=1",
            };
            builder.AddFB(FBIdGenerator.GenerateFBId(pubName),
                pubName, "MQTT_PUBLISH_115480E69E664F878", "Main",
                pubX, PublisherRowY,
                parameters: pubParams,
                attributes: pubAttrs);

            // Source pins on the remote CAT differ by component type:
            //   Actuator (Five_State_Actuator_CAT) — state_out + current_state_to_process
            //     exposed at the boundary by TemplateLibraryDeployer.PatchCatExposeState.
            //   Sensor (Sensor_Bool_CAT) — pst_out + Status are already on the boundary
            //     in the stock CAT (no patch needed).
            // The Process row on M262/M580 carries the SENSORS (after the canonical
            // mapping table's "Process/Sensor share Y=4000" rule), so Process-row entries
            // route to sensor sources here.
            bool isActuator = entry.Row == LayoutRow.Actuator;
            string srcEvent = isActuator ? $"{compName}.state_out" : $"{compName}.pst_out";
            string srcData  = isActuator ? $"{compName}.current_state_to_process"
                                         : $"{compName}.Status";

            // Cross-resource feed: M262/M580 component → BX1 formatter. EAE
            // bridges these app-scope connections at deploy.
            builder.AddEventConnection(srcEvent, $"{fmtName}.REQ");
            builder.AddDataConnection(srcData,  $"{fmtName}.state");

            // Local Fmt → Pub chain (both FBs on BX1 after BucketFor's
            // MqttFmt_/MqttPub_ prefix routing).
            builder.AddEventConnection($"{fmtName}.CNF",     $"{pubName}.PUBLISH1");
            builder.AddDataConnection ($"{fmtName}.payload", $"{pubName}.Payload1");

            // INIT both FBs off the MqttConn bring-up so they only start after
            // the broker connection is up. MqttConn.INITO is also wired to
            // MqttConn.CONNECT on the BX1 sysres by ResourceWireEmitter; event
            // multi-fan-out is allowed in EAE so the same INITO can drive both
            // CONNECT and every bridge FB's INIT.
            builder.AddEventConnection("MqttConn.INITO", $"{fmtName}.INIT");
            builder.AddEventConnection("MqttConn.INITO", $"{pubName}.INIT");
        }
    }
}
