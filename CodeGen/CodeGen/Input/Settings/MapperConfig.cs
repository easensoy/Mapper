using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // Route the Seven_State swivel (Bearing_PnP) to Five_State_Actuator_CAT when true. See
        // INVARIANTS I-4 for the six sites that must agree. static readonly (not const) so the
        // real-Seven_State branches gated on it stay live (const would make them CS0162 dead code).
        public static readonly bool StubSevenStateActuatorsAsFiveState = false;

        // Set true only by the StateTransitionTableForm preview. Chooses the Seven_State home-preamble
        // WAIT target: 0 (AtHomeInit) in sim where the swivel boots home; 6 (AtHome sensor) on the rig
        // where it boots at a work position and a WAIT-for-0 would false-match the blank state_table.
        public static bool SimulatorRecipeMode = false;

        // When true (default), each recipe runs once and parks on its END row; false loops it to step 0.
        public static bool RecipeRunOnce = true;

        // When true, every non-Cover recipe's END points back to step 0 (overrides RecipeRunOnce).
        // Each recipe's step 0 is a trigger WAIT, so the line self-sequences off fresh trigger messages:
        // robot drops the part in the hopper -> PartInHopper goes TRUE -> Feed_Station (parked at its
        // step-0 WAIT(PartInHopper)) re-fires the pusher -> the whole cycle repeats.
        public static bool EnableCyclicRestart = true;

        // The recipe generator's auto-retract safety net runs only for these processes (the Feed_Station
        // twin advances the Checker but never retracts it). Every other process is emitted verbatim.
        public static readonly string[] AutoRetractProcesses = new[] { "Feed_Station" };

        // When true, the Assembly recipe prepends "CMD swivel Home -> WAIT AtHomeInit=0". Keep FALSE on
        // the rig: if Bearing_PnP is already home, CMD Home is a no-op with no fresh report, so the
        // engine stalls at step 0.
        public static bool EnableSevenStateHomePreamble = false;

        // CENTRE-HOME OVERSHOOT FIX (2026-06-25, ENABLED per user request). The Centre-Home swivel
        // reaches home by driving the OPPOSITE coil until the DI02 centre sensor trips, then
        // DE-ENERGISING both coils. On a 3-position cylinder that VENTS when de-energised the arm coasts
        // PAST centre and rests off-centre (Codex/rig: "between AtWork2 and home"). With this TRUE, the
        // deployed CAT's 'atHome' algorithm instead HOLDS both coils (outputToWork1/2 := TRUE) so a
        // cylinder WITH a mechanical mid-stop is driven into and held at centre (catches the overshoot)
        // -- so Disassembly (homes from AtWork1) ends at the SAME centre as Assembly (homes from
        // AtWork2). 'toHome' (the drive) is untouched; only the hold-at-centre changes. Bidirectional:
        // set FALSE to restore the proven de-energise. SAFETY: if the cylinder has NO mid-stop, both-on
        // drives toward an extreme instead -- test with the e-stop ready, abort if it heads toward Work2
        // (then set FALSE and use the braking-pulse fallback). See PatchSwivelAtHomeBothCoils.
        public static bool SwivelHomeHoldBothCoils = false;

        // CENTRE-HOME BRAKE (2026-06-26, gated; see PatchSwivelBrakeHome). When TRUE the centre-home
        // swivel (Bearing_PnP) homes DIRECTLY from AtWork1 in Disassembly (the empty AtWork2 restage is
        // dropped from the recipe) and the deployed CAT brakes it at centre: at the DI02 edge the 'atHome'
        // algorithm REVERSES the driving coil for bearingPnpHomeBrakeMs (toward AtWork1, AWAY from the
        // ejector) to arrest the coast, then de-energises. The brake is DIRECTIONAL -- it only reverses
        // when homing from AtWork1 (outputToWork2 was driving); homing from AtWork2 (Assembly) still
        // de-energises, so Assembly is unchanged. Errs SAFE: a longer pulse only pushes further toward
        // AtWork1, never into the ejector. Default TRUE = the user's AtWork2 -> AtWork1 -> Home request.
        // Set FALSE to revert to the proven empty-restage staging (homes from AtWork2, no brake); the
        // ECC/CAT are force-refreshed so FALSE deploys the pristine de-energise home. RIG-TUNE the pulse
        // via config.yaml bearingPnpHomeBrakeMs (longer = further toward AtWork1).
        public static bool SwivelBrakeHome = true;

        /// <summary>
        /// When true, Disassembly gets its reverse recipe (covers off -> shaft -> bearing -> unclamp),
        /// Assembly holds the clamp and publishes a handshake sentinel instead of opening it, and the
        /// M580 wiring threads Disassembly into the ring. False = Disassembly parked + Assembly opens
        /// the clamp at its tail.
        /// </summary>
        public static bool UnparkDisassembly = true;

        /// <summary>
        /// SAFETY (Bearing_PnP <-> CoverPNP_Hr collision). Assembly_Station and Disassembly are two
        /// concurrent M580 processes that SHARE the physical bearing_pnp and cover_hr actuators. With
        /// only the Assembly->Disassembly handshake, the Assembly's NEXT cycle restarts and can drive
        /// bearing_pnp into place WHILE Disassembly is still advancing cover_hr (and vice versa) -> the
        /// swivel and the horizontal cover enter the same volume = collision. The cross-PLC interlock
        /// that would guard the cover side (CoverPNP_Hr on BX1) is unsound on the BX1 evaluator (it
        /// deadlocks / reads stale M580 state), so it ships RuleCount=0 -> nothing stops the cover.
        /// When true, the two processes are made MUTUALLY EXCLUSIVE on M580 (reliable, no cross-PLC):
        /// Disassembly publishes a "disassembly_done=7" idle sentinel at its row 0, and Assembly WAITs
        /// on it (DisassemblyProcessId=7) right after its material gate -- so Assembly never starts a
        /// cycle while Disassembly is mid-cycle. Combined with the existing within-recipe ordering
        /// (bearing homes before the cover advances; cover homes before the handshake) and the explicit
        /// bearing-clear WAIT before every cover_hr advance, bearing_pnp and cover_hr can never be
        /// commanded into their collision states at the same time. Default TRUE. Set FALSE to revert to
        /// the concurrent processes (one rebuild) -- only if the M580-local handshake is shown to stall.
        /// </summary>
        public static bool SerializeAssemblyDisassembly = true;

        /// <summary>
        /// When true, Assembly_Station + Disassembly recipes are DERIVED from their Control.xml process
        /// state machines (the generic ProcessRecipeArrayGenerator walk, commandFromCondition=true — the
        /// SAME walk Feed_Station already uses), instead of replayed from the hardcoded blocks in
        /// Config/recipes.yml. The walk already RUNS for these stations (BuildProcessFbParameters passes
        /// commandFromCondition:true); the hardcoded AssemblyRecipe/DisassemblyRecipe.Apply calls merely
        /// overwrite its result. This flag suppresses those overwrites so the data-driven recipe stands.
        /// The walk derives the MOTION only; the cross-station handoffs the twin can't express (the
        /// Feed→Assembly material gate and the Assembly↔Disassembly handshake) are injected around it by
        /// DataDrivenHandoffInjector, so the derived recipe carries the SAME WAIT(matgate) start and
        /// assembly_handshake_done/WAIT(17,7) handshake the proven hardcoded recipe has — just over
        /// motion read straight from the corrected Control.xml. Default reverted to FALSE: the derived
        /// Disassembly did not complete on the rig (its cross-PLC handshake + cover chain need proving),
        /// which left the centre-home swivel (Bearing_PnP) parked at a work position — in CoverPNP_Hr's
        /// path. The cross-PLC bearing_pnp↔cover_hr collision guard (M580↔BX1) can't be trusted, so the
        /// safe state is the rig-proven hardcoded recipe that reliably homes the swivel. Flip TRUE again
        /// only after the derived Disassembly is verified end-to-end on the rig.
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
        /// IPV4 address of the M580 controller on the rig network. Written into
        /// the M580 Equipment JSON the Topology emitter produces, on the
        /// seGmac0 endpoint. Without a real IP (i.e. the prior hard-coded
        /// "0.0.0.0" placeholder) EAE's Deploy &amp; Diagnostic tab refuses to
        /// list the device — the M262 in the same project IS listed despite
        /// having the same "00000000-0000-0000-0000-000000000000" domain UUID
        /// because its IP is concrete, so the IP is the discriminator.
        /// Default matches the reference SMC_Rig_Expo_withClamp rig wiring.
        /// </summary>
        public string M580TargetIp { get; set; } = DeviceConfig.Current.M580.TargetIp;

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
        public string DefaultNetworkSubnetAddress { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetAddress;

        /// <summary>
        /// Subnet mask for the "Default Network" BroadcastDomain JSON.
        /// </summary>
        public string DefaultNetworkSubnetMask { get; set; } = DeviceConfig.Current.DefaultNetwork.SubnetMask;

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
        /// ISOLATION (2026-06-08): emit the BX1 EtherNet/IP remote-I/O coupler
        /// (Equipment_EtherNetIPDevice_1.json + its FDT Content) only when TRUE.
        /// A DtmDeviceDEO forces EAE's FDT framework to LOAD an FdtProject.prj on
        /// topology import; an FDT project copied verbatim from another solution can
        /// make EAE's topology server throw an immediate 500 ("Unable to import
        /// topology / Internal Server Error"). The BX1 HMIB1X login does NOT need
        /// this device (it is the covers' physical I/O, a separate concern), so it
        /// is held OUT by default until the HMIB1X import + login is confirmed
        /// working. When FALSE the emitter also SWEEPS any previously-deployed copy
        /// (equipment JSON + Content files + topologyproj registrations) so the
        /// topology imports clean. Flip to TRUE once a DTM-import path is proven.
        /// </summary>
        // 2026-06-09: re-enabled. The topology-import 500 was an ORPHANED WIRE
        // (Wire_Wire 145.json → the dead Workstation NIC uuid …053), now auto-swept
        // by TopologyNetworkEmitter.SweepOrphanWires — NOT this device. With that
        // fixed + the SE.FieldDevice/Standard.IoEtherNetIP libraries referenced, the
        // EtherNet/IP cover-I/O coupler imports cleanly, so it is emitted again (the
        // BX1 softdpac's EtherNet/IP scanner in the .hcf references it; without the
        // topology device the physical-devices section is incomplete).
        public bool EmitBx1EtherNetIpDevice { get; set; } = true;

        /// <summary>
        /// Master gate for the BX1 EtherNet/IP cover-I/O broker (BX1_IO). When TRUE:
        ///   (Stage 1) deploys PLC_RW_BX1 + changeEventM262_2 and instantiates the
        ///     BX1_IO broker (id F6C04A4BA6FA8593) on the BX1 sysres + SubApp, wiring
        ///     its INIT — so the .hcf EtherNet/IP symlinks (RES0.BX1_IO.EIP_Input_Word_1
        ///     / _Output_Word_1) resolve instead of showing red/unresolved;
        ///   (Stage 2) bridges the broker's word I/O to OUR ring-model covers'
        ///     symlinks (RES0.&lt;cover&gt;.athome/atwork in; .OutputToWork/OutputToHome out)
        ///     — no cover CAT changes, our Five_State_Actuator_CAT already exposes them;
        ///   (Stage 3) a local BX1 cover pick/place cycle drives the covers.
        /// FALSE = today's working compile (no broker). The BX1 EtherNet/IP cover-I/O broker is
        /// wholly separate from the cover stateRprtCmd ring — the broker bridges physical I/O
        /// words; the ring carries process state.
        /// </summary>
        public bool DeployBx1IoBroker { get; set; } = true;

        /// <summary>
        /// BX1 cover-I/O bridge placement. TRUE (default) = INTERNALIZED: the per-cover
        /// sensor/coil symlink bridge + scan cycle live INSIDE the PLC_RW_BX1 composite
        /// (Bx1IoBrokerInjector.EmbedCoverBridgeInComposite generates them from the cover↔bit
        /// map at deploy time), so the generated BX1 sysres/syslay carries ONLY the single
        /// <c>BX1_IO</c> instance — no BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle FBs.
        /// FALSE = the proven EXTERNAL bridge: Bx1IoBrokerInjector injects the 6 symlink FBs
        /// + E_DELAY into the resource (the path verified live at EIP_Output_Word=16#0004).
        /// BX1-only — M262/M580 unaffected. The one EAE-runtime unknown the internalized path
        /// rests on is whether a SYMLINKMULTIVAR with an ABSOLUTE cross-instance NAME
        /// (BX1_RES.CoverPNP_Vr.OutputToWork) resolves from INSIDE a composite type; flip to
        /// FALSE + clean-rebuild to restore the external path if it doesn't.
        /// </summary>
        // 2026-06-26: flipped to FALSE (external bridge) — the internalized absolute-symlink path
        // (BX1_RES.CoverPNP_Vr.OutputToWork inside PLC_RW_BX1) is the rig-unproven one; reverted to
        // the external BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle FBs that were verified at 16#0004.
        public bool Bx1BridgeInsideComposite { get; set; } = false;

        /// <summary>
        /// SAFETY (default TRUE). Inserts the <c>Bx1CoverFailsafe</c> safe-start gate into the
        /// deployed <c>PLC_RW_BX1</c> broker. On every deploy / cold / warm start the broker forces
        /// CoverPNP_Hr to HOME (ToWork=0, ToHome=1) and the Vr/gripper coils off, and holds that until
        /// the Hr at-home sensor is TRUE, then passes the live cover coils through. So cover_hr can
        /// NEVER auto-energise Work on deploy/clean/restart (the swivel-collision hazard) and is
        /// actively driven home if it was left at Work — the BX1 EtherNet/IP equivalent of the M580
        /// clamp de-energising on stop. BX1-only; the clamp and all M580/M262 I/O are untouched.
        /// Set FALSE to revert to the raw broker (one rebuild) only if the gate is shown to misbehave.
        /// </summary>
        public bool Bx1CoverSafeStart { get; set; } = true;

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
        /// today's working output. Nothing sets this flag TRUE today; it is
        /// retained as inert config (default FALSE) so the hardware path stays
        /// stable. The behaviour described above is what the simulator pipeline
        /// WOULD do if it were ever re-enabled.</para>
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
        /// Default TRUE: runtime and simulator both use the single RecipeStep
        /// array input. Recipe content/ordering is generated upstream; the
        /// carrier mechanism stays stable.
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
        /// Master opt-in for the MQTT mechanism: ONE MqttConn (MQTT_CONNECTION) at the top level + the
        /// FORMATTER (MqttStateFormatter) + PUBLISH (MQTT_PUBLISH) EMBEDDED INSIDE each CAT
        /// (PatchCatMqttPublish). NO standalone bridge — the per-component MqttFmt_/MqttPub_ bridge was
        /// deleted 2026-06-16 (it cluttered the syslay); the embedded-CAT mechanism is the only one.
        /// DEFAULT TRUE (2026-06-16): the user wants MQTT on, and a
        /// mapper_config.json that OMITS this key was silently defaulting it to false at runtime (the
        /// repo-root config lacks the key) — which is why no MQTT was generated. Defaulting TRUE means
        /// a key-less config still enables MQTT; a config can still set it false explicitly to opt out.
        /// If the broker is down the MqttConn just buffers/retries (harmless), so default-on is safe.
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
        ///     handshake; against a PLAIN broker (mosquitto on 1883, no certfile) the handshake
        ///     fails. (Earlier note claimed mqtts:// "uses plain transport on the port" — the
        ///     RUNTIME disproved it: mqtts://…:1883 gave RC100, not RC0.)</item>
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
