using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // Interim flag (2026-05-28): route every Seven_State actuator
        // (Bearing_PnP swivel) to Five_State_Actuator_CAT instead.
        // WHY: Process1_Generic commands actuators only through the stateRprtCmd
        // ring, and Seven_State_Actuator_CAT.fbt declares NO ring port — so the
        // recipe can neither command Bearing_PnP nor read its state, and the
        // Assembly sequence stalls forever at the bearing step (command goes
        // nowhere, the WAIT never satisfies). Five_State sits on the ring, so the
        // recipe drives it (work/home) and the whole clamp->bearing->shaft
        // sequence cycles. Trade-off: no true two-position pick/place swivel
        // until task #69 gives Seven_State its own stateRprtCmd adapter — flip
        // this to false in that same commit. Gated at all four Seven_State
        // detection sites (ResolveActuatorFBType, SevenStateActuators,
        // IsFiveStateCommandable, IsSevenStateCommandable) so the FB type and the
        // recipe command vocabulary stay in lock-step.
        // static readonly (not const) on purpose: the const form makes the
        // compiler treat the real-Seven_State branches gated on this flag as dead
        // code (CS0162). They are not dead — they are the path #69 re-enables when
        // it flips this to false. static readonly keeps both branches live.
        // 2026-05-29 (task #69): FLIPPED to false. Seven_State_Actuator_CAT.fbt
        // now carries a stateRprtCmd ring node (an updateComponentState FB wired
        // exactly like Five_State's StateHandling) so Process1_Generic commands
        // and reads Bearing_PnP through the report ring, and ResourceWireEmitter
        // now rings it (NoRingAdapterTypes is empty; it stays off the station
        // chain via NoStationAdapterTypes). Bearing_PnP therefore resolves to
        // Seven_State_Actuator_CAT, not the Five_State stub.
        // 2026-05-29 (re-flip to TRUE): my Seven_State_Actuator_CAT surgery is
        // unverified and on the rig it leaves Bearing_PnP stuck at INIT (the ring
        // command is never processed -> "nothing triggered"). Reverted to the
        // proven Five_State stub so the recipe drives Bearing_PnP and it is
        // forceable for testing. Flip back to FALSE when Jyotsna's real
        // Seven_State_Actuator_CAT (with interlock) lands.
        // 2026-05-30: flipped to FALSE for the "Seven_State end-to-end in Test Simulator"
        // session. SimulatorPostProcessor.InjectSimSwivelForce now publishes atwork1/
        // atwork2 in sim by mirroring the actuator's own current_state{1,2}_to_plc
        // outputs (sensors close instantly when coil energises). The 3 deferred rig
        // fixes (committed surgical CAT in the .cat.zip + BuildStation2Wiring skips
        // Seven from stationChain + BuildMinimalActuatorParameters parameterises
        // process_state_name = lowercased name) all landed earlier in this session.
        // Re-flip to TRUE only if the sim demo regresses and you can't afford to debug
        // the Seven_State path during that demo window.
        public static readonly bool StubSevenStateActuatorsAsFiveState = false;

        // TEST ISOLATION (2026-05-29, TEMPORARY): restrict ONE process's recipe to a
        // subset of actuators so a single mechanism can be exercised on the rig
        // without the others moving. RecipeTestProcessName = the process to restrict
        // (empty string = apply to every process); RecipeTestActuatorAllowlist = the
        // actuator names (lower-case, matching the recipe CmdTargetName) that may
        // still be commanded. Every OTHER actuator's CMD/WAIT step in that process is
        // dropped, so the actuator is PARKED — never commanded, stays where it is.
        // EMPTY allowlist = no restriction (normal full recipe).
        //
        // 2026-05-29 update: cleared to restore the FULL Assembly_Station cycle for the
        // end-to-end simulator demo. The bench rig is unsafe (clamp damaged + swivel
        // collision risk) so testing moves to the "Test Simulator" button (Cfg
        // .SimulatorFullSystem=true): all 3 PLCs collapse into one SIM resource, every
        // Five_State_Actuator_CAT is forced no-sensor so the internal No_Sensor_Handler
        // timer self-advances the ECC (toWorkTime → atwork, toHomeTime → athome), and
        // the single ring resolves the cross-PLC/cross-process Wait1Id refs Assembly
        // makes to BX1 cover components and Feed_Station handoffs. Bearing_PnP stays
        // on the Five_State stub (StubSevenStateActuatorsAsFiveState above) — Iss
        // SevenStateCommandable returns false under the stub, so the recipe commands
        // bearing with work/home like any other Five_State actuator, and it self-
        // advances on the timer instead of waiting for a 3-position swivel sensor the
        // simulator has no model for. To return to the bearing-only bench test, repopu
        // late the allowlist with { "bearing_pnp", "bearing_gripper" }.
        public static readonly string RecipeTestProcessName = "Assembly_Station";
        public static readonly string[] RecipeTestActuatorAllowlist = new string[0];

        public string SystemXmlPath { get; set; } = string.Empty;
        public string MappingRulesPath { get; set; } = string.Empty;
        public string TemplateLibraryPath { get; set; } = string.Empty;
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string ProcessCATTemplatePath { get; set; } = string.Empty;
        public string RobotTemplatePath { get; set; } = string.Empty;
        public string RobotBasicTemplatePath { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public string SyslayPath2 { get; set; } = string.Empty;
        public string SysresPath2 { get; set; } = string.Empty;

        /// <summary>
        /// Simulator-target syslay/sysres paths, mirroring the Demonstrator
        /// folder tree under C:\DemonstratorSim. Used only by the
        /// "Test Station 1 Pusher-Simulator" button — the full unchanged
        /// generation pipeline runs against these instead of SyslayPath2/
        /// SysresPath2, then the sim .sysres Resource Type is flipped
        /// EMB_RES_ECO → SIM_RES for EAE's software simulation runtime.
        /// </summary>
        public string SyslayPathSim { get; set; } = string.Empty;
        public string SysresPathSim { get; set; } = string.Empty;

        public string IoBindingsPath { get; set; } = "Input/SMC_Rig_IO_Bindings.xlsx";

        /// <summary>
        /// Path to a folder containing a validated M262 hardware-configuration baseline
        /// (an EAE project's <c>HwConfiguration/</c> folder plus the <c>.hcf</c> file
        /// under <c>IEC61499/System/{sys-guid}/{sysdev-guid}/{sysdev-guid}.hcf</c>).
        /// The TM3 module slot/topology layout — BMTM3 → TM262L01MDESE8T → TM3DI16_G →
        /// TM3DQ16T_G — is fixed by the physical SMC rig wiring and therefore cannot
        /// be synthesised from Control.xml; the deployer copies it verbatim from this
        /// path and only overwrites the channel ParameterValue strings (DI00, DI01,
        /// DO00) using IoBindings.
        /// </summary>
        public string M262HardwareConfigBaselinePath { get; set; } = string.Empty;

        /// <summary>
        /// IPV4 address of the M262 controller on the rig network. Written into the
        /// M262 sysdev as <c>&lt;Parameter Name="IPV4Address" Value="..."/&gt;</c>
        /// so EAE's Physical Devices canvas shows the controller pre-populated. Operator
        /// can still override in EAE before deploy if the rig moves networks.
        /// </summary>
        public string M262TargetIp { get; set; } = "192.168.1.10";

        /// <summary>
        /// Subnet/network parameters used by the M262 topology emitter so the
        /// Physical Devices canvas shows the M262 wired to a logical network with
        /// a configured IP. Driven from the rig wiring — defaults are the SMC rig
        /// 192.168.1.0/24.
        /// </summary>
        public string M262SubnetAddress { get; set; } = "192.168.1.0";
        public string M262SubnetMask { get; set; } = "255.255.255.0";
        public string M262Gateway { get; set; } = "192.168.1.254";
        public string M262LogicalNetworkName { get; set; } = "DeviceNetwork_1";

        /// <summary>
        /// IPV4 address of the M580 controller on the rig network. Written into
        /// the M580 Equipment JSON the Topology emitter produces, on the
        /// seGmac0 endpoint. Without a real IP (i.e. the prior hard-coded
        /// "0.0.0.0" placeholder) EAE's Deploy &amp; Diagnostic tab refuses to
        /// list the device — the M262 in the same project IS listed despite
        /// having the same "00000000-0000-0000-0000-000000000000" domain UUID
        /// because its IP is concrete, so the IP is the discriminator.
        /// Default matches the reference SMC_Rig_Expo_withClamp rig wiring.
        /// </summary>
        public string M580TargetIp { get; set; } = "192.168.1.20";

        /// <summary>
        /// BroadcastDomain UUID the M580 seGmac0 IP-Address endpoint binds to.
        /// Mapper used to emit the all-zeros NOCONF UUID here so EAE left the
        /// device on "no broadcast domain" — but that hides the Logical Network
        /// / Subnet / Gateway columns in EAE's hardware property editor, and the
        /// user expects the M580 panel to read "Default Network / 192.168.0.0 /
        /// 255.255.255.0 / 192.168.0.254" matching the Workstation NIC and the
        /// BroadcastDomain_Default Network.json file that EAE 24.1 ships with
        /// every fresh Demonstrator. Pin the M580 endpoint to the live
        /// "Default Network" broadcast domain UUID
        /// 2131fbdd-0a41-4e41-abfb-a14a5ca9218d (matches the value in
        /// Topology/BroadcastDomain_Default Network.json on the rig). M262 is
        /// intentionally left on NOCONF per user — don't touch the M262 file.
        /// </summary>
        public string M580BroadcastDomainUuid { get; set; }
            = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        /// <summary>
        /// Subnet base address the "Default Network" BroadcastDomain JSON
        /// declares. Pinned to the reference SMC_Rig_Expo_withClamp value
        /// (192.168.0.0/24) so EAE sees a byte-identical topology when the
        /// user opens that solution to Take Ownership of the M580. The rig's
        /// device-side IP (192.168.1.20) sits OUTSIDE this /24 — EAE tolerates
        /// the mismatch (the reference ships this way and works), the connect
        /// dialog just highlights the subnet/gateway rows in yellow. Default
        /// follows reference; override if you commission a rig on a strictly
        /// matching subnet later.
        /// </summary>
        public string DefaultNetworkSubnetAddress { get; set; } = "192.168.0.0";

        /// <summary>
        /// Subnet mask for the "Default Network" BroadcastDomain JSON.
        /// </summary>
        public string DefaultNetworkSubnetMask { get; set; } = "255.255.255.0";

        /// <summary>
        /// Gateway address for the "Default Network" BroadcastDomain JSON.
        /// Pinned to the reference SMC_Rig_Expo_withClamp value (192.168.0.254)
        /// so the Demonstrator topology mirrors the SMC_Rig_Expo solution
        /// exactly. The physical M580 reports 0.0.0.0 for its own gateway —
        /// EAE flags this row yellow in the connect dialog but tolerates it
        /// (the reference shipped this way and works). Default follows
        /// reference; override only if you commission a rig with an actual
        /// gateway set on the device.
        /// </summary>
        public string DefaultNetworkGateway { get; set; } = "192.168.0.254";

        /// <summary>
        /// UUID of the "Default Network" BroadcastDomain. Matches the live
        /// Topology/BroadcastDomain_Default Network.json on the rig
        /// (2131fbdd-...). Kept as a single constant on both the M580 endpoint
        /// binding and the BroadcastDomain JSON so they always cross-reference.
        /// </summary>
        public string DefaultNetworkUuid { get; set; }
            = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        /// <summary>
        /// IPV4 address of the BX1 soft-dPAC workstation on the rig network.
        /// Same Deploy &amp; Diagnostic visibility constraint as the M580 above.
        /// Default matches the reference SMC_Rig_Expo_withClamp rig wiring,
        /// where the BX1 softdpac runtime listens on 192.168.1.151 (the
        /// HMIB1X_1 panel hosts it; 192.168.1.209 is the panel itself).
        /// </summary>
        public string BX1TargetIp { get; set; } = "192.168.1.151";

        /// <summary>
        /// M262 resource name written into the .sysres root and the .sysdev's
        /// &lt;Resource&gt; entry. Default "M262_RES" so the EAE Deploy &amp;
        /// Diagnostic tree reads "M262 &gt; M262_RES" rather than the generic
        /// Schneider default "RES0", making the device-target binding
        /// self-evident in multi-runtime projects (the M580 + BX1 sysres are
        /// equivalently named M580_RES / BX1_RES — see
        /// Station2DeviceEmitter.M580ResourceName / BX1ResourceName).
        ///
        /// <para>An earlier attempt forced this to "RES0" on the hypothesis
        /// that EAE 24.1's catalog templates rendered a phantom RES0 alongside
        /// any custom-named sysres, surfacing "Device &lt;name&gt; contains 2
        /// instances of Runtime.Management.EMB_RES_ECO" at compile. The real
        /// root cause turned out to be a duplicate-Layer-ID .syslay stub
        /// (handled by CompileCachePurger's sweep), not the resource name.
        /// Per-PLC names are now safe and resumed.</para>
        /// </summary>
        public string ResourceName { get; set; } = "M262_RES";

        /// <summary>
        /// Folder holding per-PLC HCF (Hardware Configuration File) templates exported
        /// from EAE. Each .hcf carries the TM3/X80 slot layout plus DI/DO channel
        /// ParameterValue bindings to symbolic names (e.g. 'RES0.M262IO.PusherAtHome').
        /// Mapper copies these templates verbatim into the deployed project and
        /// rewrites only the symbol bindings from IoBindings.xlsx. Bus topology is
        /// fixed by physical rig wiring and never synthesised from Control.xml.
        /// </summary>
        public string IoFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// HCF template for the M262 PLC (Station 1 — BMTM3 bus + TM262L01MDESE8T CPU
        /// + TM3DI16_G + TM3DQ16T_G modules). Holds the Feed_Station IO bindings:
        /// Pusher/Checker/Transfer atHome/atWork sensors and Extend* output coils.
        /// </summary>
        public string M262HcfTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// HCF template for the M580 PLC (Station 2 — BMXBUS + BMEXBP0400 rack +
        /// BMED581020 CPU + BMXDDM16025 modules). Holds the Assembly_Station IO
        /// bindings: SwivelArm AtPick/AtPlace/AtHome sensors, Bearing_Gripper,
        /// Shaft_Vr/Shaft_Hr atHome/atWork, Shaft_Gripper, Clamp.
        /// </summary>
        public string M580HcfTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// HCF template for the BX1 PLC (secondary IO island — Cover PnP + Ejector +
        /// CoverGripper + TopCoverSensor bindings). Currently exists only as a wiring
        /// reference (BX1 IO.png in the IO folder) and no .hcf file has been exported
        /// yet; the path is reserved so Mapper can pick it up once available.
        /// </summary>
        public string BX1HcfTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// When TRUE, the simulator pipeline collapses the entire SMC rig
        /// (Feed_Station + Assembly_Station + Disassembly + Robot orchestrator)
        /// into a SINGLE resource (one SIM device, one sysres, one syslay).
        /// All 4 Processes, all 13 actuators and all 4 sensors live on one
        /// flat FBNetwork with a single CaSBus init chain and a single
        /// stateRptCmd ring. No cross-device SIFB channels, no M580/BX1
        /// sysdev/hcf, no commdesc.xml. Cross-process handshakes that the
        /// hardware path drops (because Feed_Station/HandShake waits on
        /// Disassembly/handshake which lives on a different PLC) are
        /// preserved here by wiring Process[i].state_update directly into
        /// Process[j].state_change on the shared canvas.
        ///
        /// <para>The hardware path (Button 2 / btnTestStation1) ignores this
        /// flag — the Feed Station slice must regenerate byte-identical to
        /// today's working output. Only the "Test Simulator" button flips
        /// this on before running the pipeline. Default FALSE so a fresh
        /// MapperConfig keeps the hardware path stable.</para>
        ///
        /// <para>Bearing_PnP (a 13-state branched actuator) is stubbed with
        /// Five_State_Actuator_CAT when this flag is on, with an activity
        /// warning that the assembly/disassembly branch selection is
        /// approximated — the rest of the system still generates and runs.</para>
        /// </summary>
        public bool SimulatorFullSystem { get; set; } = false;

        /// <summary>
        /// Emit the Process recipe as one <c>Recipe : ARRAY OF RecipeStep</c>
        /// struct input instead of the six parallel arrays (StepType,
        /// CmdTargetName, CmdStateArr, Wait1Id, Wait1State, NextStep) — on the
        /// HARDWARE / Test Runtime path, not just the simulator. The exact same
        /// machinery the simulator already uses (DeployRecipeStepDatatype +
        /// NormalizeProcess1RecipeArrays + NormalizeProcessRuntimeRecipeArrays +
        /// BuildProcessFbParameters useRecipeStruct) is driven by
        /// <c>(SimulatorFullSystem || UseRecipeStruct)</c>, so the FB interface,
        /// the engine ST and the instance parameter stay in lock-step. The
        /// RecipeStep struct's CmdTargetName is STRING[150] (not the simulator's
        /// old STRING[15]) so long names like 'coverpnp_gripper' don't overflow.
        /// Default TRUE (the user asked to see the struct in EAE). Set FALSE to
        /// instantly revert the runtime to the six parallel arrays — a clean
        /// rollback if the struct ever misbehaves on the rig.
        /// </summary>
        public bool UseRecipeStruct { get; set; } = true;

        // ============================================================
        // MQTT event-driven state publishing (no-loss fix)
        // ------------------------------------------------------------
        // The OPC UA / WebSocket HMI paths sit downstream of the runtime
        // sampler (200 ms M262/BX1, 100 ms M580), so brief states (ToWork=1,
        // AtWork=2, ToHome=3, AtHomeEnd=4) shorter than the sample interval
        // are aliased out and never reach the client. MQTT_PUBLISH is a
        // function block inside the scan: it fires on the actuator's pst_out
        // the same scan the state changes, before any sampler, so nothing is
        // lost. These fields drive the single shared MQTT_CONNECTION the
        // Mapper injects; every embedded MQTT_PUBLISH binds to it by matching
        // ConnectionID value (no wire between them).
        // ============================================================

        /// <summary>
        /// Master opt-in. When FALSE (default) the Mapper emits exactly what
        /// it does today — no MQTT_CONNECTION injected, no ConnectionID
        /// stamped — so the hardware/sim paths stay byte-stable and backward
        /// safe. Flip TRUE only after the two-part jitter gate passes on the
        /// rig (dead broker + slow broker, ActuatorCore scan stays flat).
        /// </summary>
        public bool MqttPublishEnabled { get; set; } = false;

        /// <summary>Broker endpoint for MQTT_CONNECTION.URL. EAE 24.1's
        /// <c>CMQTTClientStateMgr.validateEndpoint</c> has two gates:
        /// <list type="number">
        ///   <item>Scheme must be one of <c>mqtt://</c> / <c>mqtts://</c> / <c>ws://</c>
        ///     / <c>wss://</c> — <c>tcp://</c> trips "The URI scheme is not MQTT".</item>
        ///   <item>The runtime defaults to <b>secure-by-default</b>: plain
        ///     <c>mqtt://</c> trips "Insecure configuration prohibited; TLSconfig"
        ///     unless a TLS cert is wired. The proven workaround (matches the
        ///     reference TrainingIIoT MQTT_CONNECTION) is to use scheme
        ///     <c>mqtts://</c> against a plain broker on port 1883 — EAE
        ///     accepts the scheme name and uses plain transport on the port,
        ///     no actual TLS negotiation occurs.</item>
        /// </list></summary>
        public string MqttBrokerUrl { get; set; } = "mqtt://127.0.0.1:1883";

        /// <summary>MQTT_CONNECTION.ClientIdentifier — one per runtime/resource.</summary>
        public string MqttClientId { get; set; } = "SMC_M262";

        /// <summary>
        /// MQTT_CONNECTION.ConnectionID — the registry key. The single
        /// injected connection sets this, and every embedded MQTT_PUBLISH
        /// carries the same value so they bind. Default 1.
        /// </summary>
        public int MqttConnectionId { get; set; } = 1;

        /// <summary>
        /// MQTT_CONNECTION.QueueDepth — offline buffer depth. When the broker
        /// is unreachable the connection queues up to this many messages in
        /// PLC memory and redelivers on reconnect (the PLC half of the
        /// end-to-end no-loss buffer). Default 100.
        /// </summary>
        public int MqttQueueDepth { get; set; } = 100;

        /// <summary>MQTT_PUBLISH.QoS1 — 1 = at-least-once (broker acks). Default 1.</summary>
        public int MqttQoS { get; set; } = 1;

        /// <summary>
        /// MQTT_CONNECTION.CleanSession — FALSE keeps the broker session
        /// across drops so QoS1 messages are not lost on a reconnect. Default false.
        /// </summary>
        public bool MqttCleanSession { get; set; } = false;

        /// <summary>
        /// MQTT_PUBLISH.Retain1 — FALSE for an event stream (every transition
        /// is a discrete message the logger captures). Default false.
        /// </summary>
        public bool MqttRetain { get; set; } = false;

        /// <summary>
        /// MQTT_CONNECTION.KeepAlive in milliseconds. The MQTT keepalive ping
        /// interval — the client tells the broker "I'm still here" this often,
        /// and the broker disconnects clients that miss two periods. Default
        /// 60000 ms = 60 s (standard MQTT 3.1.1 recommendation).
        /// <para>Was left empty by Mapper for months because passing an INT
        /// constant to the TIME port raised ERR_CAST_CONSTANT at compile
        /// time. The fix is to format it as a TIME literal (<c>T#60000ms</c>)
        /// via <c>SyslayBuilder.FormatTimeMs</c>, not as a bare int. Without
        /// any value at all the M262 firmware applied <c>T#0s</c> as default,
        /// which made the connection give up before the first SYN-ACK and
        /// produced the rig symptom "broker never sees a connection attempt
        /// from 192.168.1.10".</para>
        /// </summary>
        public int MqttKeepAliveMs { get; set; } = 60000;

        /// <summary>
        /// MQTT_CONNECTION.ConnectionTimeout in milliseconds. How long the FB
        /// waits for the TCP 3-way handshake + MQTT CONNACK before deciding
        /// the connect attempt failed. Default 5000 ms = 5 s. EAE's implicit
        /// default for an unset TIME port is T#0s which aborts the connect
        /// before the first SYN-ACK round-trip completes.
        /// </summary>
        public int MqttConnectionTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// MQTT_CONNECTION.ConnectionRetryCount — how many times the FB
        /// retries after a failed connect before giving up. Default 999
        /// (effectively infinite so the FB keeps trying through transient
        /// network blips). An unset value defaults to 0 = give up after the
        /// first failure, which is the wrong choice for a rig that may boot
        /// before its broker is reachable.
        /// </summary>
        public int MqttConnectionRetryCount { get; set; } = 999;

        /// <summary>
        /// MQTT_CONNECTION.ConnectionRetryTime in milliseconds. Wait between
        /// retry attempts. Default 2000 ms = 2 s. Without an explicit value
        /// the firmware applies T#0s which either disables retry entirely or
        /// busy-loops them (depends on firmware revision).
        /// </summary>
        public int MqttConnectionRetryTimeMs { get; set; } = 2000;

        /// <summary>
        /// Topic root. The per-instance topic is built as
        /// <c>{MqttTopicRoot}/{instance}/state</c>. When the CAT's
        /// RootPath='$${PATH}' macro resolves inside the MQTT parameter this
        /// is informational; if it does NOT resolve, the Mapper stamps the
        /// resolved RootPath per instance using this prefix. Default "smc".
        /// </summary>
        public string MqttTopicRoot { get; set; } = "smc";

        public string ActiveSyslayPath =>
            !string.IsNullOrEmpty(SyslayPath2) ? SyslayPath2 : SyslayPath;

        public string ActiveSysresPath =>
            !string.IsNullOrEmpty(SysresPath2) ? SysresPath2 : SysresPath;

        public string TemplateIec61499Dir =>
            Path.GetDirectoryName(Path.GetDirectoryName(ActuatorTemplatePath)) ?? string.Empty;

        public string TemplateHmiDir =>
            Path.Combine(Path.GetDirectoryName(TemplateIec61499Dir) ?? string.Empty, "HMI");

        public static MapperConfig Load()
        {
            var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);

            if (!File.Exists(configPath))
            {
                var def = CreateDefault();
                Save(configPath, def);
                return def;
            }

            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<MapperConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new Exception($"Failed to deserialise config from '{configPath}'");
        }

        private static MapperConfig CreateDefault() => new()
        {
            SystemXmlPath = @"C:\VueOne\system\Control.xml",
            MappingRulesPath = @"Input\VueOne_IEC61499_Mapping.xlsx",
            TemplateLibraryPath = @"C:\VueOneMapper\Template Library",
            ActuatorTemplatePath = @"C:\Station1\IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt",
            SensorTemplatePath = @"C:\Station1\IEC61499\Sensor_Bool_CAT\Sensor_Bool_CAT.fbt",
            // Process1_Generic.fbt is the new outer composite template (Phase 1+2);
            // the legacy Process1_CAT.fbt is no longer deployed by Mapper.
            ProcessCATTemplatePath = @"C:\Station1\IEC61499\Process1_Generic\Process1_Generic.fbt",
            RobotTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_CAT\Robot_Task_CAT.fbt",
            RobotBasicTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_Core.fbt",
            SyslayPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPathSim = @"C:\DemonstratorSim\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPathSim = @"C:\DemonstratorSim\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            IoBindingsPath = @"Input\SMC_Rig_IO_Bindings.xlsx",
            M262HardwareConfigBaselinePath = string.Empty,
            // IO authoring folder + per-PLC .hcf exports. Defaulted so a fresh
            // config (e.g. launched from a working dir without a saved
            // mapper_config.json) still finds the hardware-config files instead
            // of silently skipping the .hcf copy and leaving M580/BX1 empty.
            IoFolderPath = @"C:\VueOneMapper\IO",
            M262HcfTemplatePath = @"C:\VueOneMapper\IO\M262IO.hcf",
            M580HcfTemplatePath = @"C:\VueOneMapper\IO\M580IO.hcf",
            BX1HcfTemplatePath = @"C:\VueOneMapper\IO\BX1IO.hcf",
            M262TargetIp = "192.168.1.10",
            M262SubnetAddress = "192.168.1.0",
            M262SubnetMask = "255.255.255.0",
            M262Gateway = "192.168.1.254",
            M262LogicalNetworkName = "DeviceNetwork_1",
            ResourceName = "M262_RES",
        };

        private static void Save(string path, MapperConfig config)
        {
            var json = JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
