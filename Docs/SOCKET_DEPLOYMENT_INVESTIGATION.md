# Socket-Based IEC 61499 Management-Command Deployment — Investigation Findings

**Date:** 2026-06-24
**Status:** Investigation only. No deployment code written, no runtime started, no commands sent.
**Question:** Can we deploy an IEC 61499 FB network to a runtime by streaming management commands over a socket, instead of patching EAE/UAO project files (.syslay/.sysres/.dfbproj)?

**One-line verdict:** **Viable for 4diac FORTE** (with a type-compilation caveat); **not viable today for the UAO/EAE runtime our rig actually uses** — EAE deploys a *compiled boot project* over a *proprietary* "Deploy & Diagnostic" channel, with no public evidence of an open Holobloc management socket. The decisive test for UAO is a port scan + packet capture of a real EAE deploy.

---

## Q1 — The IEC 61499 management-command protocol

**What it is.** The protocol is a set of XML **`Request`** packets exchanged with a device's **management Service Interface FB**, the `DEV_MGR` (device manager). `DEV_MGR` takes an XML `RQST` input and emits an XML `RESP` output. The wire syntax is defined by the **FBMGT DTD** in the **HOLOBLOC IEC 61499 Compliance Profile** (James Christensen), §4.
Source: <https://www.holobloc.com/doc/ita/s4.htm> ("The `Request` and `Response` elements defined in the FBMGT DTD represent the XML syntax for the `RQST` input and `RESP` output … of the `DEV_MGR` … type").

**Normative status (important nuance).** The IEC 61499‑1 standard defines the *device-management model* (a device manager that creates/configures/starts resources via management commands). The exact **XML wire format is the HOLOBLOC compliance-profile convention**, which 4diac adopted — it is the de-facto interoperable form, not a byte-for-byte mandate of the standard itself. So "the standard defines these commands" is true at the model level; the concrete `<Request …>` XML is the Holobloc/4diac realisation.

**Envelope.**
- `Request` attributes: `ID` ("A unique identifier for the Request/Response transaction") and `Action` ("The requested operation").
- Child elements by action: `FB` (Name/Type), `Connection` (Source/Destination), `Parameter`/value, `FBType`, `AdapterType`, `DataType`, `Resource`.
- **Addressing:** an empty destination (`DST`) directs the request **to the device**; a named `DST` directs it **to that resource**. (Source: s4.htm — "An empty `DST` input directs requests to the device; otherwise to a named resource.") This is how a create-resource command (device-level) is distinguished from create-FB-inside-resource (resource-level).

**Representative packets** (the standard 4diac/FORTE form of the FBMGT-DTD requests; structure per s4.htm):

```xml
<!-- device-level: create the resource first (empty DST = to device) -->
<Request ID="1" Action="CREATE"><FB Name="EMB_RES1" Type="EMB_RES"/></Request>

<!-- the following are addressed to resource EMB_RES1 -->
<Request ID="2" Action="CREATE"><FB Name="E_CYCLE_0" Type="E_CYCLE"/></Request>           <!-- CREATE FB -->
<Request ID="3" Action="CREATE"><Connection Source="START.COLD" Destination="E_CYCLE_0.START"/></Request>  <!-- CREATE connection -->
<Request ID="4" Action="WRITE"><Connection Source="T#1s" Destination="E_CYCLE_0.DT"/></Request>            <!-- WRITE parameter -->
<Request ID="5" Action="START"/>                                                          <!-- START the whole resource -->
```

- **CREATE FB** → `Action="CREATE"` + `<FB Name Type/>`.
- **CREATE connection** → `Action="CREATE"` + `<Connection Source Destination/>` (same element for event and data connections; the ports' types disambiguate).
- **WRITE parameter** → `Action="WRITE"` + `<Connection Source="<value>" Destination="<fb>.<input>"/>` (4diac/FORTE form). *The compliance profile also describes a `Parameter` child with `Reference`/`Value`; FORTE accepts the Connection-with-literal-source form.*
- **START / STOP / KILL / RESET / DELETE / QUERY** → addressed to an `FB` or to the resource; resource-level `START` with no child starts all managed FBs. `QUERY` supports wildcards (`Name="*"`, `Type="*"`).

**Response.**
```xml
<Response ID="2"/>                          <!-- success: NO Reason attribute -->
<Response ID="2" Reason="UNSUPPORTED_TYPE"/> <!-- failure -->
```
- Rule (s4.htm): the `Response.ID` equals the originating `Request.ID`, and **absence of a `Reason` attribute means normal completion**.
- Documented `Reason` codes include `INVALID_DST`, `UNSUPPORTED_TYPE`, `DUPLICATE_OBJECT`, `NO_SUCH_OBJECT`, `INVALID_STATE`.
Source: <https://www.holobloc.com/doc/ita/s4.htm>.

---

## Q2 — How 4diac FORTE accepts these commands

- **Port / transport.** FORTE listens for management commands on **TCP, default port 61499** (configurable). The port is the device's `MGR_ID`; the 4diac IDE's runtime launcher and the device's `MGR_ID` must match.
  Sources: 4diac deployment tutorial (search snippet, eclipse.dev/4diac — "The current port for FORTE_PC is 61499"); FORTE FAQ — "Make sure that the port numbers used in the Runtime Launcher of the Deployment view are the same as those used for the … `MGR_ID` of the Devices" (<https://eclipse.dev/4diac/doc/faq.html>).
- **What it is.** FORTE is the reference implementation of the compliance-profile management protocol; it instantiates the same `DEV_MGR`/`EMB_RES` management SIFBs and parses the `<Request …>` packets above, replying with `<Response …>` packets. Source repo: <https://github.com/eclipse-4diac/4diac-forte> (the runtime that "supports these management commands", per the deployment overview below).
- **Response packet.** Same `<Response ID="…" [Reason="…"]/>` form as Q1 (success = no `Reason`).
- **Framing.** Raw TCP carrying the XML request/response packets (length/delimiter framing handled by FORTE's communication layer). *Exact byte-framing not quoted here — confirmable in the FORTE source (`stdfblib`/`cominfra` device-management classes) or a packet capture if byte-exactness is needed.*

---

## Q3 — How the 4diac IDE's Download / Deployment Console deploys

- **It streams the commands live.** Deploy is via the **Download View → Download button**; "Progress Information of the download is shown in a separate dialog window" (the Deployment Console). The IDE opens a socket to the running device and sends the CREATE/WRITE/START sequence.
  Source: <https://fordiac.sourceforge.net/ehelp/html/overview/deployment.html>.
- **It can ALSO export a boot file instead of streaming.** The IDE has **"Create FORTE boot-files…"** (context menu). When **`FORTE_SUPPORT_BOOTFILE`** is enabled, "on startup FORTE tries to load a so-called boot-file" named **`forte.fboot`**. The boot file is the *same management-command sequence* persisted to disk and replayed by FORTE at startup.
  Source: <https://fordiac.sourceforge.net/ehelp/html/overview/deployment.html>.
- **Implication:** for FORTE, "stream over a socket" and "emit a boot file" are two serialisations of the *same* command set. Either can be produced without touching any IDE project file.
- **Sequence** (per the protocol/addressing): create resource → create FBs → create connections → write parameters → start. *(Exact `DeploymentExecutor` ordering not quoted verbatim here; it follows this dependency order.)*

---

## Q4 — Does Schneider UAO / EcoStruxure Automation Expert expose the same protocol?

This is the question that actually governs our rig (M262 + M580 + BX1 all run UAO). **The answer, from this repo's own primary-source runtime logs, is: EAE does *not* drive UAO with Holobloc CREATE/WRITE/START commands. It ships a compiled *boot project* via a proprietary "Deploy & Diagnostic" command channel.**

**Primary evidence — the UAO runtime's own device log** (`MapperUI\MapperUI\bin\Debug\net10.0-windows\_gated1\Demonstrator\IEC61499\Log\System.M262.0.device.log`):

```
INFO: Communication.D&D-Commands: Received 'clean' command from the Engineering Tool
INFO: Communication.D&D-Commands: Received 'deploy' command from the Engineering Tool
INFO: Communication.D&D-Commands: Received 'boot_proj_save' command from the Engineering Tool
INFO: Communication.D&D-Commands: Received 'persremove' command from the Engineering Tool
INFO: Communication.D&D-Commands: Received 'boot_proj_clear' command from the Engineering Tool
INFO: Communication.D&D-Commands: Received 'restart' / 'stop' / 'reboot' command from the Engineering Tool
INFO: Runtime.BootProject: Boot project saved   /   Boot project deleted
INFO: Runtime.DeviceState: Parse OK [ReadyStop] -> [Running]
INFO: Runtime.ReportAppState: FBI{M262_RES.FB1.reportConfigEventFinished(Runtime.Management#REPORT_APP_STATE)}: Application initialized
```

Reading this:
1. **The deployment unit is a whole "boot project," not per-FB commands.** `deploy` is followed by `Parse OK` and `boot_proj_save` — the runtime receives and **parses a compiled application as one artifact**, then persists it. There is no stream of `CREATE FB X`, `CREATE connection Y` visible — the granularity is coarse lifecycle verbs: `clean`, `deploy`, `boot_proj_save`, `boot_proj_clear`, `persremove`, `restart`, `stop`, `reboot`.
2. **It is a proprietary channel.** The category is `Communication.D&D-Commands` ("Deploy & Diagnostic"), sourced "from the Engineering Tool" (EAE). This is not the Holobloc `<Request Action="CREATE">` protocol.
3. **UAO does have a management entity** (`Runtime.Management#REPORT_APP_STATE`, `reportConfigEventFinished`) and a boot-project concept analogous to FORTE's `forte.fboot` — but it is driven internally by EAE's compile-and-push, not exposed as an open CREATE/WRITE/START socket.

**Is UAO 4diac/FORTE?** No. UAO (Universal Automation runtime, governed by UniversalAutomation.org and used by Schneider EAE/AVEVA) is a **distinct, closed-source runtime** — not the Eclipse FORTE codebase. There is **no public evidence** it accepts Holobloc management commands on an open port.

**What EAE adds that a raw socket client cannot easily replicate** (from this repo's history):
- **A compile step.** EAE compiles the project files (.syslay/.sysres/.dfbproj/.hcf) into the boot project at *Build* time; "the deployed config regenerated by EAE at Build from the device Properties" (`CLAUDE.md`, 2026-06-22). The runtime parses that compiled output, not our XML directly.
- **A security/trust handshake.** Devices require login/trust; BX1's Soft-dPAC is secure-by-default and needs a `SecurityApp/InsecureApplication` override that *only EAE compiles into the runtime config at Build* (`CLAUDE.md`, 2026-06-22 RC101 saga). A bare socket client would have to satisfy this handshake.
- **An in-memory device model** EAE owns and caches (only a full Close+Reopen re-reads externally written device files — `CLAUDE.md`).

**Could a socket client target UAO directly today?**
- On public evidence: **no** — there is no documented open Holobloc management port, and the observed transport is a proprietary D&D channel carrying a compiled boot project behind a trust handshake.
- Honestly scoped: **unknown whether UAO has *any* reachable management socket**, because we have only the runtime-side log, not the wire. **The test that would resolve it:** on the EAE host during a Deploy, run `netstat -ano` to capture the active TCP connection (port) to the dPAC (M262 .10 / M580 .20 / BX1 .151), and take a **Wireshark capture** of that connection. If the payload is a single compiled boot project (likely), socket-streaming is a non-starter without re-implementing EAE's compiler + D&D protocol + security. If — unexpectedly — it carries Holobloc-style packets on an open port, re-evaluate.

---

## Q5 — Constraints and failure modes

**FORTE**
- **Resource first.** A resource (e.g. `EMB_RES`) must be created (device-level CREATE) before CREATE-FB requests addressed to it — the addressing model requires a named `DST` to exist.
- **FB *types* must be compiled into the FORTE binary.** Custom FB *types* are not loadable as XML at runtime in the general case — you "export your Function Block and compile 4diac FORTE before you download again" (FAQ, <https://eclipse.dev/4diac/doc/faq.html>). So socket deployment freely creates **instances, connections, parameters, and start** of types **already compiled in**, but a **new type requires recompiling/redeploying the FORTE binary** (or using FORTE's newer dynamic/LUA type-loading, which is limited). Our CAT library (`Five_State_Actuator_CAT`, `Process1_Generic`, `Sensor_Bool_CAT`, `Robot_Task_CAT`, …) would have to be pre-compiled into the FORTE image.
- **Volatility.** A live-built network is RAM-resident; without writing a boot file it does **not** survive a runtime restart. Persistence = emit `forte.fboot`.

**UAO**
- **Deploys a compiled boot project**, not incremental live commands (per the D&D log). Changing the application means recompiling and re-pushing the boot project.
- **Types are managed by EAE/runtime libraries**, versioned with the runtime; the engineering tool owns the type set.
- **Security handshake / trust / login** is mandatory; the insecure-application override must be compiled into the runtime config by EAE.
- **EAE owns the device model**; externally written changes need a Build (and often a full EAE restart) to take effect.

---

## Verdict

**(a) 4diac FORTE — YES, socket-based management deployment is viable**, and is in fact the native deployment mechanism (the IDE streams CREATE/WRITE/START to port 61499, or exports the identical sequence as `forte.fboot`). **Caveat:** it deploys *instances/connections/params/start* of types **already compiled into FORTE**; introducing a *new FB type* still requires recompiling the FORTE binary. For our Mapper this means: a one-time FORTE build that bakes in the CAT type library, after which layout changes deploy as pure command streams — no project-file patching, no EAE.

**(b) UAO / EAE — NO, not viable as a drop-in today** (confidence: high that EAE's path is a proprietary compiled-boot-project channel; the residual unknown is only whether *any* open management socket exists at all). EAE compiles the project into a boot project and pushes it via the proprietary `D&D-Commands` channel behind a security/trust handshake; there is no documented open Holobloc port. **To convert this "no" into a definitive "no/yes," run the one test:** `netstat` + Wireshark a real EAE Deploy to M262/M580/BX1 and inspect the transport and payload granularity.

---

## Contrast: socket deployment vs. our current file-patching Mapper

**What socket deployment would REMOVE (the wins):**
- All project-file editing — `SystemLayoutInjector`, `ResourceWireEmitter`, `Station2WireEmitter`, `HcfPatchService`, `DfbprojRegistrar`, the orphan-`.sysres` sweeps, the byte-identical regression gate (`_gate/`), and the syslay↔sysres parity machinery (`MainForm.cs:957–1295`, `:1133–1280`).
- **Conformance risk to EAE's file formats** — today every change must keep `.syslay`/`.sysres`/`.dfbproj`/`.hcf` mutually consistent and exactly as EAE expects (the whole `SyslaySysresParityValidator` / `HcfReferenceValidator` exist only because of this coupling).
- **EAE-in-the-loop friction** — the cache that only clears on full Close+Reopen, the "runtime config regenerated at Build," the device-recreate dance for the MQTT insecure-app override (`CLAUDE.md`, 2026-06-22). A command stream targets the runtime directly; none of this applies.

**What it would INTRODUCE (the new dependency):**
- **A live protocol to the runtime** instead of files — the Mapper becomes a deployment *client* (open socket, sequence CREATE/WRITE/START, handle `Response.Reason` errors, manage resource lifecycle/ordering). This is a *runtime* dependency, not a file-format one.
- **A type-library precondition** — every CAT type must already exist in the target runtime (compiled into FORTE; or present in EAE's library set for UAO). Socket deployment moves instances, not types.
- **For UAO specifically: re-implementing EAE's compiler + D&D protocol + security handshake** — which is undocumented, proprietary, unsupported, and would break on Schneider/UAO version changes. This is strictly *more* fragile than file patching, not less.

**Open risks:**
1. **Runtime mismatch.** Socket deployment is a clean win for FORTE, but **our rig runs UAO**, where it is blocked. The realistic options are: keep file-patching on UAO; *or* adopt a FORTE-based target (a different runtime) to unlock socket deployment; *or* invest in reverse-engineering EAE's deploy protocol (high effort, fragile, unsupported).
2. **Type compilation.** Even on FORTE, the CAT library must be baked into the FORTE image up front; type changes still need a rebuild.
3. **Persistence/safety.** Live command streams are volatile (need a boot file to survive restart) and bypass EAE's validation/trust — which on a real PLC is a safety consideration, not just a convenience.
4. **The UAO transport is unconfirmed at the wire level** — the single de-risking test (netstat + Wireshark of an EAE deploy) should be run before any UAO-direct effort is scoped.

---

## Sources

- HOLOBLOC IEC 61499 Compliance Profile §4 (FBMGT DTD / `DEV_MGR` management SIFB; Request/Response; Reason codes; DST addressing): <https://www.holobloc.com/doc/ita/s4.htm>
- 4diac Deployment overview (management commands, Download View/Deployment Console, `forte.fboot`, `FORTE_SUPPORT_BOOTFILE`): <https://fordiac.sourceforge.net/ehelp/html/overview/deployment.html>
- 4diac FORTE FAQ (custom types require export + recompile; `MGR_ID` port matching): <https://eclipse.dev/4diac/doc/faq.html>
- 4diac FORTE source: <https://github.com/eclipse-4diac/4diac-forte>
- Port 61499 default: 4diac deployment tutorial (eclipse.dev/4diac) — confirmed via search; port is configurable as the device `MGR_ID`.
- **Repo — UAO runtime D&D-Commands + boot project (primary evidence for Q4):** `MapperUI\MapperUI\bin\Debug\net10.0-windows\_gated1\Demonstrator\IEC61499\Log\System.M262.0.device.log` (and the `_gated*`/`_gatesnap_*` siblings).
- Repo — current file-patching pipeline: `MapperUI\MapperUI\Forms\MainForm.cs:957–1295` (Test Runtime pipeline) and `:1133–1280` (validators); `CodeGen\CodeGen\Planning\SystemLayoutInjector.cs`; `CodeGen\CodeGen\Artefacts\Resource\ResourceWireEmitter.cs`; `CodeGen\CodeGen\Devices\M262\HcfPatchService.cs`; `CodeGen\CodeGen\Artefacts\Templates\Registration\DfbprojRegistrar.cs`.
- Repo — EAE coupling/caching, runtime-config-at-Build, trust/insecure-app: `CLAUDE.md` Status entries 2026-06-16 → 2026-06-22.

*No socket was opened and no command was sent in producing this report; the UAO transport claims rest on the runtime-side log plus public documentation, and the named netstat/Wireshark test is what would confirm the wire behaviour.*
