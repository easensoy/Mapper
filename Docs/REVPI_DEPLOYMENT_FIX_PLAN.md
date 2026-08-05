> **⚠ SUPERSEDED (2026-08-05) by `REVPI_PROVISIONING.md`.** Three conclusions below are WRONG and were
> corrected by evidence this report did not have:
> 1. **The RevPi container is `.6`, not `.1`.** This report accepted `device.yml`'s `.1` at face value.
>    EAE's own compiler resolved `Revolution_Pi.RES0` to `nxtv3://192.168.1.6:51300` in the reference
>    solution's CrossComm `commdesc.xml`. `.1` is the address the Mapper assigns to the (inert) HMI panel
>    container, which is why a ping to it succeeded — that ping was never evidence about the RevPi.
> 2. **The Mapper did NOT "already fully support RevPi".** `RevPI_IO` was emitted as an orphan resource
>    instance (no `Mapping`, and the syslay half of the injector was unreachable dead code because it
>    searched `<FBNetwork>` while the syslay carries `<SubAppNetwork>`), and the RevPi `.sysres` was
>    registered `<None>` so EAE never compiled its Modbus HWConfig.
> 3. **`RevPiBridgeInsideComposite` does not exist** in any `.cs` — the citation `MapperConfig.cs:44` was
>    wrong (that line is `MergeFeedRing`). There is no one-flag escape hatch for the RevPi bridge.
>
> Sections 1–3 and 10–11 (terminology, macvlan proof, certificates) remain accurate.

# Revolution Pi Soft dPAC — Deployment Diagnosis & Safe Integration Plan

Read-only forensic synthesis. No EAE solution, Mapper source, certificate, controller, Docker host, or network was modified. Evidence is cited to file+line/field; **[E]** = read directly, **[I]** = inference, and status tags **[RECOVERED] / [PROPOSED] / [LIVE-VERIFY]** mark configuration provenance.

**Reliability caveat (read first):** a 10-agent verification workflow ran with its path arguments unresolved ("undefined"), so several agents located project copies by disk discovery and analysed *sibling* copies (e.g. `C:\EAE\SMC_Rig_Expo_withClamp*`, `C:\Demonstrator`) rather than the exact archives named in the brief. Their **field-level evidence is sound and cross-checked against my own prior direct reads of the named archives**, but where "which copy" matters it is flagged. The macvlan enum proof, the Mapper-source findings, and the StudioLog findings were read from unambiguous fixed paths and are solid.

---

## 1. Executive diagnosis

**Proven:**
- **The Mapper is not the gap.** `C:\VueOneMapper` contains a complete, pipeline-wired RevPi generator — `RevPiDeviceEmitter` + `RevPiIoBrokerInjector` + `PLC_RW_REVPI` composite + `RevPiIO.modbus.hcf` — that emits the full stack (Workstation host, NIC, SoftdpacContainer, **macvlan `dockerVlan type 0`**, Manager :8080, RuntimeDEO, DockerContainerDEO, Modbus master hcf), gated on `FeedStationController==RevPi` **[E: `RevPiDeviceEmitter.cs:335-466`, `MapperConfig.cs:12,25`]**. The current `C:\Demonstrator` has no RevPi purely because the last generation ran with the **default `FeedStationController=M262`** and empty `RevPiComponents` — a *selection state, not a capability gap* **[E: `MapperConfig.cs:25,33-38`]**.
- **The Mapper targets the RevPi at `192.168.1.1` (container/runtime) + `.2` (host NIC)** — **not `.6`** **[E: `Config\device.yml:15-17`; the `.6` in `MapperConfig.cs:128` is a stale comment, never emitted]**.
- **`dockerVlans[].type = 0` = macvlan — definitively** (reflected `VLanType{MacVLan=0, IPVLan=1}` from `SchneiderElectric.Automation.Topology.dll` FileVersion **1.0.25093.1**) **[E]**.
- **No RevPi deploy was ever attempted.** The EAE StudioLogs show recent deploys hitting only BX1 `.151`, M262 `.10`, M580 `.20` on 51443, all reaching "Running"; **zero** transactions to `.1`/`.2`/`.6` and **zero** Manage-Soft-dPAC/Apply events **[E: `Log-2026-07-30_17-59-46.log:469-529`]**.
- **The RevPi runtime is simply not running at its endpoint** (`.6` answered nothing; `.1:51443` closed while `.1:8080` Manager and `.1:22` SSH are open) — a *provisioning/runtime* state, not a generation or cert failure.

**Most likely (needs one on-site check):** the physical RevPi is **already at `.1`** (live `.1` = ARM aarch64 Debian with the Soft dPAC **Manager up on 8080** and runtime 51443 down) — which *matches the Mapper's `.1` target*. The hand-authored models label `.1` as the x86 "HMIP6 Softdpac_2 container", but **an aarch64 box cannot be an x86 container** — so the model's `.1=HMIP6` mapping is falsified and the live `.1` is the RevPi (or, less likely, a re-based BX1 host). This is the single fact to confirm.

**Still unknown (all require the engineering PC back on the `.50` subnet):** the MAC/logicalDeviceId identity of `.1`/`.2`/`.6`; whether the RevPi's on-host Docker/Manager/macvlan is actually provisioned; live occupancy of any new address.

**Correction to the prior report (`REVPI_TOPOLOGY_INVESTIGATION.md`):** that report treated `.6` (from Jyotsna's *example* project) as "the documented RevPi container" and the Mapper as lacking RevPi support. Both are now refined: **`.6` is one design-time variant; the Mapper (the maintained generator) and the live rig both use `.1`.** The prior report's core stands (main hand-authored solution carries no RevPi; the RevPi logic is real and self-contained; macvlan proven).

---

## 2. Definitive macvlan answer

**Yes — EAE-managed Docker macvlan, proven from the assembly, not the field name.**
- Assembly `…\SystemManager\SchneiderElectric.Automation.Topology.dll`, **FileVersion/ProductVersion/AssemblyVersion = 1.0.25093.1**; read-only `Reflection.Assembly.LoadFile` → enum `SchneiderElectric.Automation.Topology.VLanType` (Int32) has **exactly two members: `MacVLan = 0`, `IPVLan = 1`** **[E]**.
- All three Soft dPAC hosts declare `dockerVlans[].type = 0` = macvlan: BX1 (`Equipment_HMIB1X_1.json` L152), HMIP6 (L152), RevPi (`Equipment_Revolution Pi.json` L149).
- **How EAE represents/materialises it [E + I]:** the `SoftdpacManagerDEO` (Manager service `logicalPort 8080`) reads the `dockerVlans` entry and creates a Docker network with `driver=macvlan`, `-o parent=<resolved interface>`, `--subnet 192.168.1.0/24 --gateway 192.168.1.254` from the bound `DeviceNetwork_1` (`db72f221`), then attaches the `SoftdpacContainer` at its fixed IP. The container endpoint is `domainReadOnly:true` (its address is dictated by the macvlan), the host NIC endpoint is `domainReadOnly:false` **[E: RevPi L77 vs L44]**. The exact `docker network create` invocation is on-host and was not read (PC off-subnet) — **[I]**.
- **`NIC_2\eth0` is an EAE topology object-path, not a Linux interface name [E].** The RevPi is a generic `Workstation` catalog object whose physical NIC is a **nested child equipment** `NIC_2` (`NIC_EAE_V01.00_01.00`, `path:"Revolution Pi\NIC_2"`), so the Manager (at the equipment root) names the parent by traversing into that child → `NIC_2\eth0` (backslash = EAE path separator). BX1/HMIP6 are appliance catalogs whose NIC is a *root* interface, hence bare `eth0`/`eno1`. All resolve to a physical `eth0`/`eno1` at OS level.
- **Switch multi-MAC / host-side shim [I, on-site]:** macvlan puts a *second* MAC (the container's) on the RevPi's switch port — the switch port must not have port-security/MAC-limit that blocks it. A macvlan host cannot reach *its own* child (they bypass the host bridge); EAE's engineering PC is a *separate* host and reaches the container normally. If the Manager needs host→container reachability for health checks, EAE creates a macvlan *shim* on the host — verify on-site; do not assume.

---

## 3. Recovered RevPi configuration — two sources, two addressings

### 3a. Jyotsna's EXAMPLE project `Equipment_Revolution Pi.json` [RECOVERED]
| Field | Value | Line |
|---|---|---|
| Equipment / catalog / uuid | `Revolution Pi` / `Workstation_V01.00_01.00` / `e4af8b21-…` | 2-4 |
| Host NIC | `NIC_2` (`NIC_EAE`), `eth0`, **IP `192.168.1.2`**, domain `db72f221` | 29-46 |
| Container | `Softdpac_3` (`SoftdpacContainer`), **IP `192.168.1.6`**, `domainReadOnly:true` | 62-79 |
| Image / RAM / CPU | `softdpac` **`v24.1.25090.08`** (ARM) / `524288` / `[0,1,2,3]` | 103-113 |
| RuntimeDEO uuid / logicalDeviceId | `f985947e-…` / `72a1fde3-…` (`Revolution_Pi`) | 116-118 |
| dockerVlan | `softdpacDeviceNet`, **type 0 (macvlan)**, `NIC_2\eth0`, DeviceNetwork_1 | 146-153 |
| Manager | `8080` | 158 |
| Runtime cert | `cacert` key `f985947e`, SAN **`192.168.1.2`** (+172.18.0.2/127.0.0.1) — **not `.6`** | Certificates.xml L15 |

### 3b. The MAPPER's emitted RevPi target [RECOVERED — this is the maintained/deployed addressing]
| Field | Value | Source |
|---|---|---|
| Container / runtime IP | **`192.168.1.1`** | `device.yml:15-17` (`targetIp`) |
| Host NIC IP | **`192.168.1.2`** | `device.yml:15-17` (`hostIp`) |
| dockerVlan | macvlan type 0, Manager :8080, RuntimeDEO(logicalDeviceId=sysdev), DockerContainerDEO | `RevPiDeviceEmitter.cs:417-454` |
| Process FB | **`Process1_Generic`** (ring model) — NOT Jyotsna's `Process1_CAT` | `RevPiDeviceEmitter.cs:26` (Process1_CAT is a forbidden-pattern comment) |
| Modbus bridge | **internalized** in `PLC_RW_REVPI` (`RevPiBridgeInsideComposite=true`) | `MapperConfig.cs:44`, `RevPiIoBrokerInjector.cs:129-200` |
| Runtime cert | **none emitted** (same as working BX1 — trust is EAE-managed at deploy) | `Station2DeviceEmitter.cs:335-347` |

**⚠ The two disagree on the container IP (`.6` vs `.1`) and the process design (`Process1_CAT` vs `Process1_Generic`).** The Mapper's `.1` matches the live rig; treat `.6` as a superseded design variant.

---

## 4. Address-ownership & collision table

| IP | Model claim(s) | Live snapshot | Verdict |
|---|---|---|---|
| **.1** | HMIP6 `Softdpac_2` container (hand models) **/ RevPi runtime (Mapper)** | ARM aarch64 Debian, SSH+**8080 Manager** open, **51443 closed**, host "Essential-BX1" | **Live = a Soft dPAC host; an x86 HMIP6 container is impossible here → this is the RevPi (Mapper agrees).** LIVE-VERIFY MAC. |
| **.2** | HMIP6 host `eno1` **+** Workstation_1 **+** RevPi host (example) — **triple-claim** | Ubuntu 22.04 x86, SSH open, 51443 closed | One real host (HMIP6/Workstation); RevPi-host-at-.2 is stale. |
| **.6** | RevPi `Softdpac_3` container (example only) | no response | Unmaterialised design variant; **not the deployed target**. |
| .10 / .20 | M262 / M580 | (working) | fixed. |
| .50 | — | engineering PC (when on-site) | not in any topology. |
| .151 / .209 | BX1 `Softdpac_1` container / BX1 host | .151 **51443 OPEN** (working) | fixed. |
| .210 | EtherNet/IP TM3BC coupler | — | fixed. |
| .254 | gateway (DeviceNetwork_1) | — | fixed. |

**Domains:** `DeviceNetwork_1` (`db72f221`, **/24**, gw .254) is the live domain every host/container endpoint binds. `DeviceNetwork_2` (`d205b554`, **/16**, 255.255.0.0) is an **orphan — 0 endpoint references** — overlapping and stale; the Mapper-generated project already omits it **[E]**.
**Free 4th-host candidates [PROPOSED, model-unclaimed, LIVE-VERIFY by ARP]:** `.30/.31` (preferred), `.11/.12`, `.21/.22` — appear in none of the four project trees and are outside the used set `{1,2,6,10,20,50,151,209,210,254}`.

---

## 5. Replace-HMIP6 vs fourth-controller

| | **Option A — RevPi replaces HMIP6** | **Option B — RevPi = 4th simultaneous controller** |
|---|---|---|
| Addressing | reuse HMIP6's slot — but **`.2` is a triple-claim and `.1`/HMIP6 is falsified by the aarch64 live box**, so "reuse" is ill-defined | RevPi keeps its own host+container **pair**; a real 4th host must **not** reuse `.2` |
| Topology change | remove HMIP6 (`Equipment_HMIP6_1.json`) + its unbound `Softdpac_2` @`.1` | add a RevPi Workstation+NIC+SoftdpacContainer (Mapper emits this) |
| Logic moved | none from HMIP6 (its `Softdpac_2` is **unbound, all-zeros logicalDeviceId — runs nothing** [E]) | Feeder/Checker move off M262 onto the RevPi (Mapper does this when RevPi selected) |
| HMI/OPC-UA lost | HMIP6 is a **physical Harmony panel-PC** (local operator display). A **headless RevPi cannot provide the panel/OMI runtime** → local visualisation is lost unless the HMI stays on the Workstation/another panel | HMI unaffected if FB IDs preserved; RevPi Feeder/Checker OPC-UA nodes move to the RevPi dPAC server — HMI client must re-point + trust the RevPi endpoint |
| Certs/identities | HMIP6 carries no bound runtime; RevPi brings its own (EAE-managed) | RevPi brings its own (EAE-managed at deploy) |
| Net | **Not a like-for-like swap** — HMIP6 is an HMI-class panel, the RevPi is a headless I/O controller; they serve different roles | Clean, matches the Mapper's design and the live `.1` device |

---

## 6. Recommended architecture

**Option B (fourth controller) is the intended and safer design — with the crucial refinement that the RevPi's real address is `.1`, not `.6`.** Evidence: the Mapper (maintained generator) targets `.1`; the live `.1` is an ARM Soft dPAC host with its Manager up; Jyotsna's `.6`/`Process1_CAT` example is a superseded reference the Mapper was built *from* but does not reproduce. Option A is not a true equivalent (HMIP6 is an HMI panel, not an I/O controller) and its `.1`/`.2` model is contradicted by live evidence.

**Owner decision still required:** (a) is the physical `.1` device the RevPi (confirm by MAC/Manager query) — if yes, no re-addressing is needed; (b) does the Feed *physical wiring* move to the RevPi's Modbus coupler, or stay on the M262 TM3 modules (this decides whether "remove Feeder/Checker from M262" is a logic-only move or an I/O move); (c) is HMIP6 kept as the operator HMI panel or decommissioned.

---

## 7. Provisioning-layer analysis (source of truth ▸ verify ▸ failure symptom ▸ Linux needed? ▸ safe fix ▸ success)

1. **EAE physical topology** ▸ `Equipment_*.json` ▸ EAE Physical Views ▸ device missing/import error ▸ no ▸ generate via Mapper (RevPi selected) ▸ RevPi appears in tree.
2. **RevPi host mgmt IP** ▸ NIC endpoint (`.1` Mapper / `.2` example) ▸ ping/ARP ▸ unreachable ▸ no ▸ correct to the confirmed live IP ▸ host answers.
3. **Manager :8080** ▸ `SoftdpacManagerDEO` ▸ `.1:8080` open (it is, live) ▸ closed ▸ **no** ▸ start the Manager service on-host ▸ Manage-Soft-dPAC "Connected" populates.
4. **Manager auth/trust** ▸ EAE runtime cacert (EAE-managed) ▸ Manage Soft dPAC connects ▸ TLS/authz error in log ▸ no ▸ run **EAE elevated** first; do NOT Reset Security ▸ Connected column matches In-Project.
5. **Docker daemon** ▸ on-host ▸ `docker info` (SSH) ▸ container never builds ▸ **yes** ▸ start/repair dockerd ▸ daemon healthy.
6. **Docker storage/partition** ▸ on-host ▸ `df -hT`,`lsblk` ▸ Apply fails "no space"/mount ▸ **yes** ▸ the partition Wass struggled with ▸ data-root mounted, space free.
7. **ARM Soft dPAC image** ▸ `softdpac:v24.1.25090.08` (ARM) ▸ `docker image ls` ▸ exec-format/arch error ▸ **yes** ▸ pull ARM image (never x86) ▸ image present, aarch64.
8. **macvlan network** ▸ `dockerVlans type 0` ▸ `docker network ls`/`inspect` ▸ container has no address ▸ **yes** (or Manager auto-creates) ▸ let Manager create it ▸ macvlan on parent eth0.
9. **Container** ▸ `SoftdpacContainer` ▸ `docker ps` ▸ **`.6`/`.1:51443` closed (current symptom)** ▸ **yes** ▸ Manager Apply builds+starts it ▸ container Up.
10. **Runtime :51443** ▸ RuntimeDEO ▸ `Test-NetConnection <ip> -Port 51443` ▸ **closed (current)** ▸ no ▸ (opens once container runs) ▸ OPEN.
11. **EAE logical device** ▸ sysdev `Revolution_Pi`/Soft_dPAC ▸ Deploy pane lists it ▸ absent ▸ no ▸ Mapper emits it ▸ present + bound.
12. **IEC 61499 resource/app** ▸ RevPi sysres (7 FBs) ▸ Build ▸ empty/build error ▸ no ▸ Mapper emits ▸ Feeder/Checker/PLC_RW_REVPI present.
13. **Modbus hcf** ▸ `RevPiIO.modbus.hcf` (TCP 502, 1 in-reg/1 out-reg) ▸ Build + on-host Modbus server ▸ I/O dead ▸ **yes (coupler)** ▸ verify piCtory/gateway on the Pi ▸ coils toggle.
14. **HMI/OPC-UA** ▸ RevPi OPC-UA companions + HMI TagName=FB-ID ▸ HMI binds ▸ tags red ▸ no ▸ re-point HMI to RevPi endpoint + trust ▸ faceplates live.

**Do not conflate:** Linux SSH (22) — needed only for Docker/storage/service repair; Manager mutual-TLS (8080) — EAE-managed, no Linux login needed; runtime login (51443) — **the logs show it is TLS-accept-anyway + application HMAC (`SRT61499N-Auth`), not strict mutual-TLS**, so a cert SAN/IP mismatch alone cannot block a deploy **[E: `Log-2026-07-30_17-59-46.log:472-526`]**.

---

## 8. Application-migration analysis

**RevPi resource `D090B4163A62A815.sysres` = 7 FBs [E]:** `FB1/DPAC_FULLINIT`, `FB2/plcStart`, `Feeder`+`Checker` (`Five_State_Actuator_CAT`), `RevPI_IO` (`PLC_RW_REVPI`), `Process1` (`Process1_CAT`), `CheckingStationTerminator`. It owns **exactly two physical outputs** — `ExtendPusher` (output word bit0) and `ExtendChecker` (bit1) — over **Modbus TCP `172.18.0.1:502`, unit 1, 1 input register + 1 output register, cyclic** **[E: PLC_RW_REVPI.fbt; …72a1fde3.hcf:149-232]**. No cross-comm SIFB; the Feed→Assembly handoff crosses RevPi↔M262/M580 via **plain cross-device event/data connections (EAE-bridged)** **[E: `bef30c23…syslay`]**.

**What must be removed from M262 to prevent duplicate actuator ownership:**
- **DRIVE:** no active duplicate — in Jyotsna's variant the old M262 broker `E786…` was replaced by `M262IO_2` and nothing on M262 writes those coils **[E]**.
- **BIND (⚠ real, must fix):** Jyotsna's **RevPi M262 hcf `6fe9f94f…hcf` still binds `DO00=ExtendPusher`, `DO01=ExtendChecker`, `DI00-05` to the *deleted* broker `E786`** — dangling stale channel reservations. **Do NOT copy that hcf forward as a clean template** **[E: hcf:220-225,257-258]**.
- For the **Mapper-generated** M262 (`1459…sysres`, hcf `…002.hcf`): to move Feed to the RevPi you delete the `Feeder`(`60AE…`)/`Checker`(`FC83…`) FB instances and **blank `DO00`(Feeder.OutputToWork)/`DO01`(Checker.OutputToWork)** plus `DI00/DI01/DI03/DI04` (and `DI02` PartInHopper if the hopper read also moves to the RevPi Modbus input word) **[E: hcf:220-224,257-258]**. **The Mapper does this automatically** when RevPi is selected (M262 stays byte-identical except for the removed Feed) — which is why regenerating is safer than hand-editing.

Intended architecture = **move the feed/checking station from M262 to the RevPi** (lift-and-shift; the same DI00-05/DO00-01 signals, re-served over Modbus). Keep all physical outputs inhibited during first commissioning.

---

## 9. Mapper gap analysis

**There is essentially no generation gap** — verified in source **[E]**: `RevPiDeviceEmitter` (topology + macvlan + Manager + Runtime + DockerContainer), `RevPiIoBrokerInjector` (the `RevPI_IO`/`PLC_RW_REVPI` broker bridging Feeder/Checker/PartInHopper), `PLC_RW_REVPI.zip` + `RevPiIO.modbus.hcf` templates, and the `FeedController{M262,RevPi}` routing authority all exist and are wired into `MainForm`/`ComponentRegistry`/`TemplateLibraryDeployer`. The project's own gate reports M262 byte-identical + RevPi 5/5 validators when RevPi is selected. Real caveats found adversarially:
- **Container IP `.1` not `.6`** (device.yml) — matches live; the `.6` comment is stale.
- **No runtime PKI emitted** — by design (BX1 emits none either; EAE-managed trust). Not a gap.
- **`RevPiBridgeInsideComposite=false` fallback is inert** — the flag is read nowhere; `EmbedBridgeInComposite` runs unconditionally and the external-bridge path emits no publisher/subscriber/scan FBs. **[E: `MapperConfig.cs:44`, `TemplateLibraryDeployer.cs:124-131`]** So if the internalized absolute cross-instance symlinks fail in EAE, the documented one-flag escape hatch needs a code change first.
- **Rig-unverified:** whether EAE resolves the nested absolute symlink names inside `PLC_RW_REVPI`, and the Modbus bit-map/timing on the coupler (`T#80ms`), are deferred to a rig run (`CLAUDE.md 2026-07-06`).

**⇒ The Mapper produces a deployable RevPi target; the remaining work is runtime bring-up + rig verification, not code.**

---

## 10. Certificate assessment

- **Browser behaviour was a red herring [E].** The three certs the browser offered (`MS-Organization-Access`, `SE.DMS`, `Automation Device Maintenance`) are **not in either EAE project cert store** — grep = 0. The project stores contain only `cacert` (CA/runtime), the M262/M580/HMI device certs.
- **EAE↔Manager / EAE↔runtime auth is EAE-managed**, using the project `cacert` runtime identities, not a Windows personal-store cert. The RevPi runtime cert `f985947e` (SAN `192.168.1.2`, `serverAuth+clientAuth`, valid 2026-06→2036-06) exists in the example store.
- **The runtime login plane (51443) is TLS-accept-anyway + HMAC**, so the fact that the runtime cert SAN covers `.2` but not `.6` **cannot** block a deploy **[E: log "validated…RemoteCertificateChainErrors. Certificate will be accepted anyway"]**.
- **No cert is expired/future-dated** as of 2026-08-02 (M580 1970 / M262 1980 epochs are clock-independent) **[E]**.
- **Do NOT use Reset Security.** There is no evidence of a certificate/trust failure.

---

## 11. On-site diagnostic decision tree

```
0. Reconnect eng-PC to 192.168.1.0/24 (static .50).
1. arp -a  → MAC OUI of .1
     ├ KUNBUS C8:3E:A7 ........... .1 IS the Revolution Pi  → go 2
     ├ Schneider/Harmony OUI ..... .1 is a BX1/Harmony host → RevPi undeployed; re-base to a free pair (.30/.31) → go 2'
     └ Intel/Realtek/other ....... unknown ARM SBC → stop, identify device
2. Read-only query SoftdpacManager .1:8080 for reported logicalDeviceId
     ├ 72a1fde3… ................. confirmed RevPi (deploy target = .1)
     └ 7b21782a…/other ........... not the RevPi → re-base
3. Test-NetConnection .1 -Port 51443
     ├ OPEN ...................... runtime already up → deploy from EAE (elevated)
     └ CLOSED (expected) ......... runtime not started → go 4
4. Launch EAE 24.1 ELEVATED → Manage Soft dPAC → Refresh
     ├ Connected column populates  → Apply → container builds → 51443 opens → deploy
     └ still --- / Apply errors ... → go 5 (Linux inventory)
5. SSH .1 (host) → docker ps / docker network ls / df -hT / docker image ls / systemctl status (Manager, docker)
     ├ container Exited ........... docker start (Manager-managed) → 51443 opens
     ├ no ARM image .............. pull softdpac:v24.1.25090.08 (ARM) via Manager
     ├ partition/space fault ..... fix data-root (the piece Wass struggled with)
     └ Manager service down ...... start it → retry step 4
6. Deploy → verify .1:51443 login, then I/O ownership test (outputs inhibited).
```
Every branch is read-only until step 4/6; never reflash, repartition, delete Docker resources, expose 2375, or Reset Security.

---

## 12. Proposed final configuration (4th controller)

| Field | Value | Status |
|---|---|---|
| RevPi host mgmt IP | **`192.168.1.1`** if the live `.1` MAC/Manager confirms RevPi; else a free pair | **[LIVE-VERIFY]** (Mapper uses .1; live .1 is an ARM Soft dPAC host) |
| Container/runtime IP | **`192.168.1.1`** (Mapper) — *not* `.6` | **[RECOVERED, Mapper]** / LIVE-VERIFY |
| Parent interface | `NIC_2\eth0` (EAE path) → physical `eth0` | [RECOVERED] |
| Docker network / type | `softdpacDeviceNet` / **macvlan (type 0)** | [RECOVERED, proven] |
| Subnet / mask / gw | `192.168.1.0` / `255.255.255.0` / `192.168.1.254` (DeviceNetwork_1) | [RECOVERED] |
| Manager / runtime port | `8080` / `51443` | [RECOVERED; 8080 LIVE-OPEN, 51443 LIVE-CLOSED] |
| Image / version | ARM `softdpac v24.1.25090.08` (never x86) | [RECOVERED] |
| RAM / CPU | `524288` / `[0,1,2,3]` | [RECOVERED] |
| Logical device | `Revolution_Pi` (Soft_dPAC) + Feed sub-app (PLC_RW_REVPI + Feeder/Checker + Process1_Generic) | [RECOVERED, Mapper] |
| Free-pair fallback (if `.1`≠RevPi) | host+container from `.30/.31` (or `.11/.12`, `.21/.22`) | **[PROPOSED — ARP-verify]** |

---

## 13. Safe EAE integration procedure (no manual UUID copying)

**Preferred — regenerate from the Mapper** (avoids hand-merging project-local UUIDs entirely):
1. In MapperUI set the Feed components' Device = **RevPi** (or `FeedStationController=RevPi`); confirm `device.yml` RevPi `targetIp/hostIp` match the confirmed live target.
2. Generate → the Mapper emits the complete RevPi device + macvlan topology + Feed sub-app and **removes Feed from M262** (M262 otherwise byte-identical); it also blanks the M262 Feed coils — no dangling bindings (unlike Jyotsna's example hcf).
3. Open in EAE → Reload → Build → deploy the RevPi last, after the runtime is up (§11).

**If the deployed solution must remain the hand-authored `DemonstratorWithHMI`** (not the Mapper output), reconstruct via the EAE UI only: add a Workstation+child NIC (new/`.2`-verified host IP) → SoftdpacContainer (`.1`/verified, macvlan on `NIC_2\eth0`, DeviceNetwork_1, ARM image, RAM/CPU) → create/associate the `Revolution_Pi` Soft_dPAC device → import the Feed sub-application and **remove Feeder/Checker + blank their M262 coils**. Validate (Solution Integrity + Build) after every stage. Never hand-edit archived JSON/XML (breaks UUIDs/DomainTag). Never Reset Security.

---

## 14. Commissioning checklist
1. **MAC identity** — `arp -a`; OUI of `.1`/`.2`/`.6` vs the printed RevPi MAC (KUNBUS C8:3E:A7 ⇒ RevPi).
2. **Address-conflict** — confirm chosen host/container pair unclaimed by ARP/ping sweep; `.6` and the triple-`.2` are model artifacts.
3. **EAE elevated** — launch as Administrator (safe first test for the DPWS `config.json` overwrite ACL).
4. **Manager Connected column** — Manage Soft dPAC → Refresh → In-Project == Connected.
5. **Docker/storage inventory** (only if Apply fails) — `docker ps/network/image ls`, `df -hT`, `lsblk`, Manager service state (read-only).
6. **macvlan created** — `docker network inspect` shows driver macvlan, parent eth0, subnet /24.
7. **`<target>:51443`** — `Test-NetConnection` OPEN after container Up.
8. **Deploy** — EAE Login/Deploy to the RevPi (cert accept-anyway), reach "Running".
9. **I/O ownership test** — outputs inhibited; confirm only the RevPi drives ExtendPusher/ExtendChecker and no M262 coil does.
10. **HMI/OPC-UA** — RevPi Feeder/Checker faceplates bind (re-point client to RevPi endpoint + trust).
11. **Controlled reboot** — verify the container auto-starts and 51443 returns (set `restart:always` if not).

---

## 15. Final fix recommendation (shortest safe path)

**On-site, in order:** (1) `arp -a` to confirm `.1`'s MAC is the RevPi (KUNBUS OUI) — this single check resolves the whole `.1`/`.2`/`.6` ambiguity. If confirmed, **`.1` is your deploy target and no re-addressing is needed.** (2) **Launch EAE 24.1 elevated → Manage Soft dPAC on the RevPi → Refresh → Apply** — this asks the (already-running) `.1:8080` Manager to build+start the Soft dPAC container, which opens `.1:51443`. (3) If Apply can't build (Docker/partition/image), SSH the RevPi host and inventory read-only, then `docker start` / pull the ARM image / mount the data-root — the base Wass wrestled with. (4) **Regenerate the solution from the Mapper with RevPi selected** (or, for the hand-authored solution, rebuild the RevPi via the EAE UI) so the Feed station is cleanly on the RevPi with M262's Feed coils blanked. (5) Deploy to `.1:51443` (trust accept-anyway), outputs inhibited, then run the I/O-ownership test.

**Why this is safest:** it never touches M262/M580/BX1/HMIP6, never reflashes or repartitions, never edits certificates, uses the maintained generator (gate-verified, correct `.1` addressing) instead of hand-merging Jyotsna's superseded `.6` example, and brings the runtime up through the supported Manager path rather than a hand-built container. The deployment "problem" is, at root, **a RevPi Soft dPAC runtime that has never been started at `.1:51443` in a solution that (until regenerated with RevPi selected) contains no RevPi target** — not a Docker, macvlan, certificate, or Mapper defect.
