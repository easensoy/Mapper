using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // Route the Seven_State swivel (Bearing_PnP) to Five_State_Actuator_CAT when true. static
        // readonly (not const) so the real-Seven_State branches gated on it stay live (const would
        // make them CS0162 dead code).
        public static readonly bool StubSevenStateActuatorsAsFiveState = false;

        // Set true only by the StateTransitionTableForm preview. Chooses the Seven_State home-preamble
        // WAIT target: 0 (AtHomeInit) in sim where the swivel boots home; 6 (AtHome sensor) on the rig
        // where it boots at a work position and a WAIT-for-0 would false-match the blank state_table.
        public static bool SimulatorRecipeMode = false;

        // When true (default), each recipe runs once and parks on its END row; false loops it to step 0.
        public static bool RecipeRunOnce = true;

        // When true, every non-Cover recipe's END points back to step 0 (overrides RecipeRunOnce);
        // false = each process runs once and parks on its END row.
        // Must stay false: cyclic restart re-fires each recipe's step-0 trigger on a held level and
        // overlaps processes on shared actuators; re-enable only with a real mutual-exclusion mechanism.
        public static bool EnableCyclicRestart = false;

        // The recipe generator's auto-retract safety net runs only for these processes (the Feed_Station
        // twin advances the Checker but never retracts it). Every other process is emitted verbatim.
        public static readonly string[] AutoRetractProcesses = new[] { "Feed_Station" };

        // When true, the Assembly recipe prepends "CMD swivel Home -> WAIT AtHomeInit=0". Keep FALSE on
        // the rig: if Bearing_PnP is already home, CMD Home is a no-op with no fresh report, so the
        // engine stalls at step 0.
        public static bool EnableSevenStateHomePreamble = false;

        // When TRUE, the deployed CAT's 'atHome' algorithm HOLDS both coils (outputToWork1/2 := TRUE)
        // so a Centre-Home swivel cylinder WITH a mechanical mid-stop is driven into and held at centre
        // (catches the coast-past-centre overshoot); FALSE de-energises both coils at centre. 'toHome'
        // (the drive) is untouched; only the hold-at-centre changes. SAFETY: if the cylinder has NO
        // mid-stop, both-on drives toward an extreme instead. See PatchSwivelAtHomeBothCoils.
        public static bool SwivelHomeHoldBothCoils = false;

        // When TRUE the centre-home swivel (Bearing_PnP) homes DIRECTLY from AtWork1 in Disassembly and
        // the deployed CAT brakes it at centre: at the DI02 edge the 'atHome' algorithm REVERSES the
        // driving coil for bearingPnpHomeBrakeMs (toward AtWork1, away from the ejector) to arrest the
        // coast, then de-energises. The brake is directional (only when homing from AtWork1) so Assembly
        // (homes from AtWork2) is unchanged, and errs safe (a longer pulse only pushes toward AtWork1).
        // FALSE homes from AtWork2 with no brake. Rig-tune the pulse via config.yaml bearingPnpHomeBrakeMs.
        // See PatchSwivelBrakeHome.
        public static bool SwivelBrakeHome = true;

        /// <summary>
        /// When true, Disassembly gets its reverse recipe (covers off -> shaft -> bearing -> unclamp),
        /// Assembly holds the clamp and publishes a handshake sentinel instead of opening it, and the
        /// M580 wiring threads Disassembly into the ring. False = Disassembly parked + Assembly opens
        /// the clamp at its tail.
        /// </summary>
        public static bool UnparkDisassembly = true;

        /// <summary>
        /// Assembly_Station and Disassembly are concurrent M580 processes sharing the physical
        /// bearing_pnp and cover_hr. FALSE (current): serialized by the one-way handshake (Assembly
        /// publishes assembly_handshake_done=7; Disassembly WAITs on it before coverRemove) plus the
        /// within-recipe ordering + the bearing-clear WAIT before every cover_hr advance. TRUE would
        /// add a mutual-exclusion idle sentinel; only re-enable it wired as a leading WAIT, since a
        /// fresh ProcessEngine does not publish a leading CMD.
        /// </summary>
        public static bool SerializeAssemblyDisassembly = false;

        // MERGE FEED RING (gated, default OFF -> byte-identical). When true the isolated M262 Feed ring
        // is merged into the one main cross-PLC state-report ring so Feed_Station and Disassembly can see
        // each other's state. This makes two cross-process Control.xml conditions the generator otherwise
        // drops satisfiable: Feed holds Transfer advanced until Disassembly homes the bearing, and
        // Disassembly waits for Transfer to return before the ejector. Feed_Station's process_id is
        // re-id'd off the (now-shared) Shaft_Hr id-10 slot to a free state_table slot.
        public static bool MergeFeedRing = false;

        // The state Disassembly stamps on its own process_id ({DisassemblyProcessId, this}) once the
        // bearing is home, so Feed's TransferAdvancing WAIT can key on it. Distinct from the handshake's
        // 7. Emitter (DisassemblyRecipe) and consumer (RecipeStateClassifier) share this one value.
        public const int MergeFeedRingBearingHomeState = 6;

        // The value a process publishes on its OWN process_id slot to mean "I am idle / at
        // Initialisation". A process's state_table slot carries its last CMD state (1/3/5/7 for
        // actuator commands + handshake), so the idle marker must be a value NO recipe ever issues as
        // a CMD state -- 0 is safe. Assembly publishes {AssemblyProcessId, 0} at its Initialisation
        // (AssemblyRecipe) and Feed's readiness gate WAITs on the SAME value (RecipeStateClassifier),
        // so the two always match regardless of the twin's design-time State_Number for Initialisation.
        public const int ProcessIdleSentinelState = 0;

        /// <summary>
        /// When true, Assembly_Station + Disassembly recipes are DERIVED from their Control.xml process
        /// state machines (the generic ProcessRecipeArrayGenerator walk, commandFromCondition=true — the
        /// SAME walk Feed_Station already uses), instead of replayed from the hardcoded blocks in
        /// Config/recipes.yml. The walk already RUNS for these stations (BuildProcessFbParameters passes
        /// commandFromCondition:true); the hardcoded AssemblyRecipe/DisassemblyRecipe.Apply calls merely
        /// overwrite its result. This flag suppresses those overwrites so the data-driven recipe stands.
        /// The walk derives the MOTION only; the cross-station handoffs the twin can't express (the
        /// Feed→Assembly material gate and the Assembly↔Disassembly handshake) are injected around it by
        /// DataDrivenHandoffInjector. FALSE (current) keeps the hardcoded recipes; the derived
        /// Disassembly is not yet verified end-to-end on the rig.
        /// </summary>
        public static bool DataDrivenRecipes = false;

        // Process-FB process_id slots + the robot's state_table slot; data in Config/smc-rig.yml.
        // Each must sit above the component id space (ValidateProcessIdInvariant enforces it). The
        // Assembly->Disassembly handshake rides AssemblyProcessId, so stamp and WAIT share one source.
        public static int FeedStationProcessId    => RigCatalog.Current.ProcessIds.FeedStation;
        public static int AssemblyProcessId       => RigCatalog.Current.ProcessIds.Assembly;
        public static int DisassemblyProcessId    => RigCatalog.Current.ProcessIds.Disassembly;

        public static int RobotActuatorId         => RigCatalog.Current.RobotActuatorId;

        // Real rig DI sensors the twin doesn't model; data in Config/smc-rig.yml. Kept OFF the
        // M262 Feed ring; rides the M262->M580 cross-device segment so the report lands only in M580
        // state_table[id], which HandoffPlanner makes Assembly's row-0 wait. Emitted + HCF-bound when
        // HandoffPlanner.DischargeActive.
        public static (string Name, string Pin, int Id)[] M262SynthSensors =>
            RigCatalog.Current.SynthSensors
                .Select(s => (s.Name, s.Pin, s.Id))
                .ToArray();

        /// <summary>The Disassembly Ejector+Robot discharge tail; a thin alias to
        /// HandoffPlanner.DischargeActive (the single cross-PLC discharge decision).</summary>
        public static bool EnableRobotTaskTail => CodeGen.Translation.HandoffPlanner.DischargeActive;

        // Recipe test isolation: restrict RecipeTestProcessName's recipe to RecipeTestActuatorAllowlist
        // (empty = no restriction). Every other actuator's CMD/WAIT is dropped (parked).
        public static readonly string RecipeTestProcessName = "Assembly_Station";
        // Empty = no restriction (full recipe). Populate with actuator names to park the rest.
        public static readonly string[] RecipeTestActuatorAllowlist = Array.Empty<string>();

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
        public string M262TargetIp { get; set; } = DeviceConfig.Current.M262.TargetIp;

        /// <summary>
        /// Subnet/network parameters used by the M262 topology emitter so the
        /// Physical Devices canvas shows the M262 wired to a logical network with
        /// a configured IP. Driven from the rig wiring — defaults are the SMC rig
        /// 192.168.1.0/24.
        /// </summary>
        public string M262SubnetAddress { get; set; } = DeviceConfig.Current.M262.SubnetAddress;
        public string M262SubnetMask { get; set; } = DeviceConfig.Current.M262.SubnetMask;
        public string M262Gateway { get; set; } = DeviceConfig.Current.M262.Gateway;
        public string M262LogicalNetworkName { get; set; } = "DeviceNetwork_1";

        /// <summary>
        /// IPV4 address of the M580 controller, written into the M580 Equipment JSON's seGmac0 endpoint.
        /// EAE constraint: a device with no concrete IP is not listed in Deploy &amp; Diagnostic, so this
        /// must be a real address, not a placeholder.
        /// </summary>
        public string M580TargetIp { get; set; } = DeviceConfig.Current.M580.TargetIp;

        /// <summary>
        /// BroadcastDomain UUID the M580 seGmac0 endpoint binds to. Pinned to the live "Default Network"
        /// domain (matches Topology/BroadcastDomain_Default Network.json) so EAE shows the Logical
        /// Network / Subnet / Gateway columns. M262 is intentionally left on NOCONF — do not touch it.
        /// </summary>
        public string M580BroadcastDomainUuid { get; set; }
            = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        /// <summary>
        /// Subnet base address the "Default Network" BroadcastDomain JSON declares (192.168.0.0/24).
        /// The device-side M580 IP (192.168.1.20) sits outside this /24; EAE tolerates the mismatch
        /// (only highlights the subnet/gateway rows in the connect dialog).
        /// </summary>
        public string DefaultNetworkSubnetAddress { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetAddress;

        /// <summary>
        /// Subnet mask for the "Default Network" BroadcastDomain JSON.
        /// </summary>
        public string DefaultNetworkSubnetMask { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetMask;

        /// <summary>
        /// Gateway address for the "Default Network" BroadcastDomain JSON (192.168.0.254). The physical
        /// M580 reports 0.0.0.0 for its gateway; EAE flags the row but tolerates it.
        /// </summary>
        public string DefaultNetworkGateway { get; set; } = DeviceConfig.Current.DefaultNetwork.Gateway;

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
        public string BX1TargetIp { get; set; } = DeviceConfig.Current.Bx1.TargetIp;

        /// <summary>
        /// IPV4 address of the HMIB1X industrial panel that HOSTS the BX1 softdpac
        /// container. On the reference SMC_Rig_Expo_withClamp the panel (HMIB1X_1)
        /// is 192.168.1.209 and the softdpac runtime it hosts is BX1TargetIp
        /// (192.168.1.151). EAE deploys/logs in to the runtime IP (.151); the host
        /// IP (.209) is the panel's management interface. This is the field that
        /// makes BX1 a REMOTE HMIB1X panel rather than a local Workstation (whose
        /// runtime EAE resolves to 127.0.0.1 — the "cannot connect to BX1" error).
        /// </summary>
        public string BX1HostIp { get; set; } = DeviceConfig.Current.Bx1.HostIp;

        /// <summary>
        /// Emit the BX1 EtherNet/IP remote-I/O coupler (Equipment_EtherNetIPDevice_1.json + its FDT
        /// Content). The BX1 softdpac's EtherNet/IP scanner in the .hcf references it; without the
        /// topology device the physical-devices section is incomplete. When FALSE the emitter also
        /// sweeps any previously-deployed copy so the topology imports clean.
        /// EAE constraint: an FDT project copied verbatim from another solution can make the topology
        /// server throw a 500 on import.
        /// </summary>
        public bool EmitBx1EtherNetIpDevice { get; set; } = true;

        /// <summary>
        /// Deploy the BX1 EtherNet/IP cover-I/O broker (BX1_IO, PLC_RW_BX1 + changeEventM262_2) so the
        /// .hcf EtherNet/IP word symlinks resolve, bridge the broker's I/O words to the covers'
        /// athome/atwork/OutputTo* symlinks, and drive the local cover cycle. The broker (physical I/O
        /// words) is separate from the cover stateRprtCmd ring (process state).
        /// </summary>
        public bool DeployBx1IoBroker { get; set; } = true;

        /// <summary>
        /// BX1 cover-I/O bridge placement. TRUE (default) = INTERNALIZED: the per-cover
        /// sensor/coil symlink bridge + scan cycle live INSIDE the PLC_RW_BX1 composite
        /// (Bx1IoBrokerInjector.EmbedCoverBridgeInComposite generates them from the cover↔bit
        /// map at deploy time), so the generated BX1 sysres/syslay carries ONLY the single
        /// <c>BX1_IO</c> instance — no BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle FBs.
        /// FALSE (current) = the proven EXTERNAL bridge: Bx1IoBrokerInjector injects the 6 symlink FBs
        /// + E_DELAY into the resource. BX1-only — M262/M580 unaffected. EAE-runtime unknown for the
        /// internalized path: whether a SYMLINKMULTIVAR with an ABSOLUTE cross-instance NAME
        /// (BX1_RES.CoverPNP_Vr.OutputToWork) resolves from INSIDE a composite type.
        /// </summary>
        public bool Bx1BridgeInsideComposite { get; set; } = false;

        /// <summary>
        /// SAFETY (default TRUE). Inserts the <c>Bx1CoverFailsafe</c> safe-start gate into the deployed
        /// <c>PLC_RW_BX1</c> broker: on every deploy/cold/warm start the broker forces CoverPNP_Hr HOME
        /// (ToWork=0, ToHome=1) + Vr/gripper coils off and holds until the Hr at-home sensor is TRUE,
        /// then passes the live coils through — so while the BX1 logic RUNS cover_hr can never
        /// auto-energise Work (swivel-collision hazard). Run-time only: it does NOT act on EAE
        /// Clean/STOP/fault. Hardware invariant: the double-acting cover holds its last coupler output
        /// when stopped, so homing it while STOPPED needs the TM3BC coupler ToHome fallback (word
        /// 16#0002) set on the coupler's own web server (192.168.1.210), outside EAE. BX1-only.
        /// </summary>
        public bool Bx1CoverSafeStart { get; set; } = true;

        /// <summary>
        /// M262 resource name written into the .sysres root and the .sysdev's &lt;Resource&gt; entry.
        /// "M262_RES" so the EAE Deploy &amp; Diagnostic tree reads "M262 &gt; M262_RES" rather than the
        /// generic "RES0" (M580/BX1 are equivalently M580_RES / BX1_RES — see
        /// Station2DeviceEmitter.M580ResourceName / BX1ResourceName).
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
        /// Emit the Process recipe as one <c>Recipe : ARRAY OF RecipeStep</c> struct input instead of
        /// the six parallel arrays. Drives DeployRecipeStepDatatype + the Normalize*RecipeArrays +
        /// BuildProcessFbParameters so the FB interface, engine ST and instance parameter stay in
        /// lock-step. CmdTargetName is STRING[150] so long names (coverpnp_gripper) do not overflow.
        /// </summary>
        public bool UseRecipeStruct { get; set; } = true;

        // MQTT event-driven state publishing. MQTT_PUBLISH fires in-scan on the actuator's pst_out,
        // before any HMI sampler, so brief states are not aliased out. These fields drive the single
        // shared MQTT_CONNECTION; every embedded MQTT_PUBLISH binds to it by matching ConnectionID.

        /// <summary>
        /// Master opt-in for MQTT: one MQTT_CONNECTION per resource + the FORMATTER (MqttStateFormatter)
        /// and PUBLISH (MQTT_PUBLISH) embedded INSIDE each CAT (PatchCatMqttPublish). No standalone
        /// bridge. Default TRUE (a key-less mapper_config.json still enables MQTT); a broker outage just
        /// buffers/retries, so default-on is safe.
        /// </summary>
        public bool MqttPublishEnabled { get; set; } = true;

        /// <summary>Broker host:port for MQTT_CONNECTION.URL. The SCHEME is set by
        /// <see cref="MqttSecureTls"/>, not by what you write here — the host:port is taken
        /// from this value, the scheme is forced to match the mode so the URL and the mode can
        /// never drift. EAE 24.1's <c>MQTT_CONNECTION</c> is SECURE-BY-DEFAULT, and its own
        /// doc.xml defines the two failure codes RUNTIME-PROVEN on this rig:
        /// <list type="bullet">
        ///   <item><b>ReturnCode 101</b> ("URL error: Secure URL required for secure application")
        ///     — a plain <c>mqtt://</c> URL is rejected UNLESS the device has
        ///     <c>Security → Insecure Application → Enable</c> set in EAE.</item>
        ///   <item><b>ReturnCode 100</b> ("TLS error") — an <c>mqtts://</c> URL forces a TLS
        ///     handshake; against a PLAIN broker (mosquitto on 1883, no certfile) the handshake fails.</item>
        /// </list>
        /// Two clean paths to ReturnCode 0:
        /// <list type="number">
        ///   <item><b>Insecure demo</b> (<see cref="MqttSecureTls"/>=false, the default): scheme
        ///     <c>mqtt://</c> against the plain broker; you MUST enable "Insecure Application" on
        ///     the BX1 device in EAE, else RC101.</item>
        ///   <item><b>Secure</b> (<see cref="MqttSecureTls"/>=true): scheme <c>mqtts://</c> against
        ///     a TLS-enabled mosquitto listener (certfile/keyfile, conventionally port 8883) with
        ///     <see cref="MqttCaCert"/> + <see cref="MqttValidateCert"/> set.</item>
        /// </list>
        /// flags that as an impossible config. From Config/telemetry.yml.</summary>
        public string MqttBrokerUrl { get; set; } = TelemetrySettings.Current.BrokerUrl;

        /// <summary>FALSE = insecure mqtt:// demo (needs the device "Insecure Application" override);
        /// TRUE = mqtts:// + TLS. The URL scheme is derived from this. From Config/telemetry.yml.</summary>
        public bool MqttSecureTls { get; set; } = TelemetrySettings.Current.SecureTls;

        /// <summary>Secure mode CA certificate (MQTT_CONNECTION.CACert). From Config/telemetry.yml.</summary>
        public string MqttCaCert { get; set; } = TelemetrySettings.Current.CaCert;

        /// <summary>Secure mode MQTT_CONNECTION.ValidateCert (USINT). From Config/telemetry.yml.</summary>
        public int MqttValidateCert { get; set; } = TelemetrySettings.Current.ValidateCert;

        /// <summary>BX1 broker ClientIdentifier (unique per resource). From Config/telemetry.yml.</summary>
        public string MqttClientId { get; set; } = TelemetrySettings.Current.ClientBx1;

        /// <summary>M262 broker ClientIdentifier. From Config/telemetry.yml.</summary>
        public string MqttClientM262 { get; set; } = TelemetrySettings.Current.ClientM262;

        /// <summary>M580 broker ClientIdentifier. From Config/telemetry.yml.</summary>
        public string MqttClientM580 { get; set; } = TelemetrySettings.Current.ClientM580;

        /// <summary>Shared MQTT_CONNECTION.ConnectionID — the binding key every resource's connection
        /// AND its embedded MQTT_PUBLISH carries, so publishers bind locally. From Config/telemetry.yml.</summary>
        public string MqttConnectionName { get; set; } = TelemetrySettings.Current.ConnectionName;

        /// <summary>Wrap each MQTT_CONNECTION in the Telemetry_CAT composite (Config/Health structs)
        /// instead of the raw FB. From Config/telemetry.yml. FALSE = raw MQTT_CONNECTION revert.</summary>
        public bool UseTelemetryCat { get; set; } = TelemetrySettings.Current.UseTelemetryCat;

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
        /// interval. Default 60000 ms = 60 s. EAE constraint: this must reach the TIME port as a TIME
        /// literal (<c>T#60000ms</c> via <c>SyslayBuilder.FormatTimeMs</c>), not a bare INT (raises
        /// ERR_CAST_CONSTANT); an unset value defaults to T#0s and aborts the connect.
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
            // Process1_Generic.fbt is the outer composite process template.
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
            BX1HcfTemplatePath = @"C:\VueOneMapper\IO\BX1IO.ethernetip.hcf",
            M262TargetIp = DeviceConfig.Current.M262.TargetIp,
            M262SubnetAddress = DeviceConfig.Current.M262.SubnetAddress,
            M262SubnetMask = DeviceConfig.Current.M262.SubnetMask,
            M262Gateway = DeviceConfig.Current.M262.Gateway,
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
