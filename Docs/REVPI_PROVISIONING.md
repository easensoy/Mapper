# Revolution Pi — Runtime Architecture, Generation Contract, and Provisioning Procedure

Supersedes the addressing conclusions in `REVPI_DEPLOYMENT_FIX_PLAN.md` and `REVPI_TOPOLOGY_INVESTIGATION.md`. Where they disagree with this document, this one is correct: it is grounded in the EAE compiler's own output and in a generation that was executed and asserted, not inferred.

---

## 1. Runtime terminology — settled

| Layer | What it is | Evidence |
|---|---|---|
| **EAE 24.1** | The engineering environment. It **deploys a compiled IEC 61499 boot project** — per-FB-type `.bin` modules, a device descriptor, runtime DLLs and hardware config. Not source, not a container image. | `…\IEC61499\bin\Deploy\SE.DPAC#{M262_dPAC,M580_dPAC,Soft_dPAC}\` each hold `IEC61499.dll`, `System.*.sysdev.bin`, `*.dlist.xml`, `HWConfig\`. `System.BX1.dinfo.xml` self-identifies as *"IEC 61499 IDE, Sunshine v24.1"*. |
| **Soft dPAC** | The **containerized build of that same controller runtime**, shipped as Docker image `softdpac`. Installed here as a product (`…Runtime 24.1\SoftdPAC\{SoftdPACService,SoftdPACManager}`). | `IEC61499.dll` is **md5-identical** (`aab6a367d83aeb560053aae003c7fe92`) across the M262, M580 and Soft_dPAC deploy folders; the ST-compiler `profile` is identical too. `Nxt.dll` constant `SoftdpacImageName = "softdpac"`. |
| **UAO** | UniversalAutomation.org — the shared-source IEC 61499 runtime technology/ecosystem the model derives from. **No UAO artifact exists anywhere in the installed toolchain.** | Byte-scan of every `.dll`/`.exe` under Buildtime 24.1, the Automation Expert Platform and `ProgramData\Schneider Electric\Libraries` for `UniversalAutomation` / `universalautomation.org` = **0 hits**. |

**The framing "we are not choosing between an unrelated UAO runtime and an EAE runtime" is CONFIRMED**, and can be sharpened:

> Soft dPAC is not a different runtime. It is the **same** IEC 61499 controller runtime packaged as a container. **Choosing the RevPi is a HOSTING decision — which machine runs the container — not a runtime-technology decision.**

Revolution Pi is an officially supported Soft dPAC Linux host: Schneider's shipped *Soft dPAC User Guide* contains named destinations `RevolutionPiInstallingDockerCompose`, `RevolutionPiStartScriptHasDockerDet`, and sections *"Installing Soft dPAC on Linux"*, *"Install Docker Daemon"*, *"Create the Physical Network"*, *"Create Soft dPAC Instances"*.

---

## 2. Docker macvlan — proven, and why it forces two addresses

`dockerVlans[].type = 0` is **macvlan**, proven by reflection over the installed assembly:

- `…\Buildtime 24.1\SystemManager\SchneiderElectric.Automation.Topology.dll`, FileVersion/ProductVersion/AssemblyVersion **1.0.25093.1**
- `enum SchneiderElectric.Automation.Topology.VLanType : Int32` → **`MacVLan = 0`, `IPVLan = 1`** (exactly two members)
- The DEO models (`DockerVLanDEO`, `DockerContainerDEO`, `SoftdpacManagerDEO`) live in `SchneiderElectric.Automation.Nxt.dll`, same version.

**Why the host and the container need separate IPs.** A macvlan child is given its **own MAC address** on the parent interface, so the switch sees two distinct L2 endpoints on one port. The container is therefore a first-class node on `192.168.1.0/24`, not a port-forward of the host. Consequences:

- **The engineering PC reaches the container normally** — it is a separate host on the segment, so the macvlan parent/child isolation does not apply to it.
- **The Docker host may NOT reach its own macvlan child.** Traffic from the parent to its child bypasses the bridge; this is standard macvlan behaviour, not a fault. If host→container reachability is ever needed (health checks), a host-side macvlan *shim* interface is the usual remedy — verify on-site rather than assume EAE created one.
- **The Manager operates through the Docker socket**, not the child's network IP. That is why the Manager can create and start a container it cannot itself ping.
- **The switch port must permit multiple MACs.** Port-security or a MAC limit of 1 will silently drop the container's traffic.

Do **not** hand-run `docker network create` / `docker run` in place of Manager provisioning: EAE's Manager owns the container lifecycle and reconciles it against the topology.

---

## 3. Address design — final

Domain `db72f221-ece1-4b82-8132-731ce655044e` = *DeviceNetwork_1*, `192.168.1.0/24`, gateway `.254`.

| Role | Address | Service | Status |
|---|---|---|---|
| Engineering PC | `.50` | EAE | live |
| **RevPi Linux/Docker host** | **`.1`** | SSH 22, **Soft dPAC Manager 8080** | **PROVISIONAL — confirm by MAC on-site** |
| **RevPi Soft dPAC container** | **`.6`** | IEC 61499 runtime (EAE Deploy/Login) | **SETTLED — compiler-proven** |
| Harmony HMI host (`Workstation_1`, real `HMI_NET` runtime) | `.2` | HMI runtime | settled |
| HMI panel `HMIP6_1` container (**inert**) | `.3` | none — null logicalDeviceId | moved off `.1` |
| M262 | `.10` | controller | settled |
| M580 | `.20` | controller | settled |
| BX1 host (`HMIB1X_1`) | `.209` | Manager | settled |
| BX1 Soft dPAC container | `.151` | runtime 51443 | settled (known-good) |
| EtherNet/IP coupler | `.210` | TM3BC | settled |
| Gateway | `.254` | — | settled |

**Why `.6` is settled.** EAE's own compiler resolved the RevPi resource to it: in the reference solution, `…\SnapshotCompiles\{…}\CrossComm\Revolution_Pi\System.Revolution_Pi.(Default).commdesc.xml` reads `URI="nxtv3://192.168.1.6:51300/"` for `Resource="Revolution_Pi.RES0"`. That is the compiler resolving a logical resource to an address — stronger than any hand-editable topology field.

**Why the previous `.1` was wrong.** Commit `cc8d07f` changed `targetIp` `.6`→`.1` reasoning *"the Softdpac runtime answers on 192.168.1.1 (ping-verified), not Jyotsna-reference .6 (times out)"*. Both halves are category errors:
- `.6` timed out because **the container had never been created** — an unprovisioned endpoint, not a wrong address.
- `.1` answered ping because **something else is there**: `.1` is the address the Mapper *itself* assigns to the HMI panel's Soft dPAC container. A ping proves only that an address is occupied; it says nothing about which service, and nothing about port 51443.

The net effect was that EAE's deploy target pointed at a **host**, which never listens on the runtime port — so Deploy/Login could not succeed no matter how healthy the RevPi was.

**The host/container role test** (validated against the known-good BX1): the endpoint carrying `SoftdpacManagerDEO` (port 8080) at the equipment ROOT is the **host**; the nested `SoftdpacContainer` carrying a `RuntimeDEO` with a real `logicalDeviceId` is the **container**. BX1: host `.209` (Manager, no root RuntimeDEO) / container `.151` (RuntimeDEO, runtime open). The RevPi follows the identical shape.

**The one unavoidable on-site check.** No project file prescribes the RevPi *host* address — the reference says `.2`, but `.2` is claimed there by three objects and is the Harmony HMI host here. `.1` is used because it is the only address observed answering as an **ARM Linux host running a Manager on 8080**, and the RevPi is the only ARM machine on the rig. **Confirm `.1` against the MAC printed on the physical RevPi (KUNBUS OUI) before deploying**, and confirm `.6` is unused.

---

## 4. What "selecting RevPi" means — and what it does not

`PLC_RW_REVPI`'s interface exposes exactly `ExtendPusher`/`ExtendChecker` (coils) and `PusherAtWork`/`PusherAtHome`/`checkerUp`/`chekcerDown`/`Hopper` (sensors) — i.e. **Feeder, Checker, PartInHopper**. That is a strict subset of the Feed station.

- **Supported (the RevPi mode):** the **per-component swap**. Set the Device column to `RevPi` for Feeder and/or Checker; `PartInHopper` follows automatically; **M262 keeps the rest** → a four-controller project (M262 + RevPi + M580 + BX1).
- **Rejected:** the **whole-Feed swap** (`FeedStationController = RevPi`). It would relocate Transfer, Ejector, Robot and PartAtAssembly off the M262 that owns their channels, onto a coupler with no signals for them — they would deploy unable to actuate. `RevPiSelectionValidator` now fails generation by name rather than shipping that.

The swappable set is **derived from the coupler's own signal tables** (`RevPiIoBrokerInjector.CoveredComponents`), so extending `PLC_RW_REVPI` automatically extends what may be selected.

---

## 5. Deployment contract — 14 stages

The Mapper generates the deployable project **and the Soft dPAC blueprint**. It cannot install or start Docker or the Soft dPAC on the RevPi. Stages 1–2 are the Mapper's; 3–9 are the Manager's; 10–14 are EAE's and the rig's.

| # | Stage | Responsible | Required artifact | Endpoint | Auth | Log | Pass condition | Failure symptom |
|---|---|---|---|---|---|---|---|---|
| 1 | Generate project | **Mapper** | sysdev/sysres/`Equipment_Revolution_Pi.json`/Modbus `.hcf`/dfbproj+topologyproj+Folders.xml | local FS | — | MapperUI activity log | `[Addr] PASS`, `[Parity] PASS`, `[Target] Feed controller: M262 + Revolution Pi` | generation throws on an invalid selection or a fatal address collision |
| 2 | Open + build | **EAE** | the generated solution | local | — | EAE Message Log / StudioLog | build succeeds, no Missing Project Files | unresolved type / missing file → a registration gap |
| 3 | Manager reachable | **RevPi host** | Soft dPAC Manager service | `hostIp:8080` (TLS) | EAE-managed cacert | host service log | *Manage Soft dPAC* → Refresh populates the Connected column | column blank/`---` → Manager down or wrong host address |
| 4 | Docker healthy | **RevPi host** | dockerd | local socket | root/SSH | `journalctl -u docker` | `docker info` OK | Apply fails to build |
| 5 | Persistent storage | **RevPi host** | data-root partition | local | root/SSH | `df -hT`, `lsblk` | space free, data-root mounted | "no space"/mount error on Apply |
| 6 | ARM image | **Manager** | `softdpac:v24.1.25090.08` (**ARM**) | Docker registry / import | Manager | Manager log | `docker image ls` shows it, aarch64 | exec-format error → x86 image pulled |
| 7 | macvlan network | **Manager** | `dockerVlans` type 0, parent `NIC_2\eth0`, subnet `/24`, gw `.254` | Docker socket | Manager | Manager log | `docker network inspect` → driver macvlan, correct parent | container gets no address |
| 8 | Container created | **Manager** | `SoftdpacContainer`, RAM 524288, cpus 0–3, IP `targetIp` | Docker socket | Manager | Manager log | `docker ps` shows it Up | Apply error |
| 9 | Runtime listening | **Soft dPAC** | container runtime | `targetIp:51443` | — | container log | `Test-NetConnection <targetIp> -Port 51443` OPEN | **closed = the current symptom; the container was never created** |
| 10 | Deploy / Login | **EAE** | compiled boot project | `targetIp:51443` | TLS accept-anyway + `SRT61499N-Auth` HMAC | StudioLog | device reaches **Running** | cannot connect → stage 9 not met |
| 11 | Application runs | **Soft dPAC** | `Revolution_Pi` resource FBs | — | — | EAE Watch | Feeder/Checker FBs live | empty resource → mirror/parity gap |
| 12 | Modbus I/O | **RevPi + coupler** | `RevPiIO.modbus.hcf`, `RevPI_IO` broker | `172.18.0.1:502` | — | EAE Watch | coils toggle, sensors read | no traffic → suspect `.sysres` registration (stage 2) or the on-Pi Modbus server |
| 13 | Ownership | **rig** | M262 feed channels blanked | — | — | — | only the RevPi drives ExtendPusher/ExtendChecker | two controllers on one coil |
| 14 | HMI / OPC UA | **EAE HMI** | faceplates bound by TagName | HMI runtime | — | — | Feeder/Checker tiles live | tags red → re-point client to the RevPi endpoint + trust |

**Do not conflate the three planes:** SSH 22 is only for host repair; Manager 8080 is EAE-managed mutual TLS (no Linux login needed); runtime 51443 is TLS-accept-anyway plus application HMAC — so a certificate SAN/IP mismatch alone can never block a deploy.

---

## 6. On-site procedure (shortest safe path)

1. **`arp -a`** — confirm the MAC at `.1` is the RevPi (KUNBUS OUI). This single check resolves the whole `.1`/`.2`/`.6` question. If it is not the RevPi, re-address `revPi.hostIp` in `Config/device.yml` and regenerate; the address validator will reject any collision.
2. Confirm **`.6` is unused** (`ping`, `arp -a`).
3. In MapperUI set **Feeder** and/or **Checker** Device = `RevPi` → **Generate**. Expect `[Target] Feed controller: M262 + Revolution Pi`, `[Addr] PASS`, `[Parity] PASS`.
4. Open in EAE → **Build**.
5. **Manage Soft dPAC** → target the RevPi host → **Refresh** → **Apply**. This creates the macvlan, pulls the ARM image and starts the container. Run EAE **elevated** for the first attempt.
6. Verify `Test-NetConnection <targetIp> -Port 51443` is **OPEN**.
7. **Deploy + Login** to the RevPi. Keep outputs inhibited.
8. Run the **I/O-ownership test**: confirm only the RevPi drives the pusher/checker coils and that no M262 channel does.
9. Controlled reboot: confirm the container auto-starts and 51443 returns.

**Never** reflash, repartition, factory-reset, Reset Security, or delete Docker resources before inventory. **Never** assign `.1` to a container or reuse `.2` for the RevPi.

---

## 7. What the Mapper now guarantees

Asserted by an executed generation (56/56 checks, harness `scratchpad/revpi_prove`) plus 29 in-repo tests:

- RevPi topology: Workstation host + `NIC_2/eth0` at `hostIp`; `Softdpac_3` container at `targetIp`; **macvlan type 0**, parent `NIC_2\eth0`, `softdpacDeviceNet`; image `softdpac v24.1.25090.08`; RAM 524288; cpus 0–3; Manager 8080; `RuntimeDEO` bound to the RevPi logical device; container on `DeviceNetwork_1`.
- Logical device + resource emitted, resource **non-empty**, hosting Feeder, Checker, PartInHopper and the `RevPI_IO` broker — **with a `Mapping`** (no orphan instance) and the FB id the `.hcf` LinkNames resolve against.
- `RevPI_IO` is declared in the **syslay** too, so the resource instance has an application counterpart.
- Registration: sysdev `<Compile SystemDevice>`, **sysres `<Compile SystemResource>`** (so EAE compiles its Modbus HWConfig), `.hcf` registered, equipment in `topologyproj`, sysdev in `Folders.xml`.
- M262 de-ownership: Feeder/Checker removed from the M262 resource, M262 keeps what it still owns, and **no dangling `.hcf` binding** anywhere.
- Every referenced broadcast domain is declared; no stale overlapping `/16`.
- **Fail-loud guards:** unsupportable selection, host==container, malformed address, and any Soft dPAC **container** address collision all stop generation.
- Default M262 mode is unaffected apart from the inert HMI panel's address; the real `HMI_NET` runtime stays bound to `Workstation_1`.

**Known and deliberately not changed:** `192.168.1.2` is claimed by both `Workstation_1` and the `HMIP6_1` host NIC. This is pre-existing, present in the reference too, and tolerated by EAE (both import), so it is reported as a WARNING rather than silently re-addressing a panel whose physical address cannot be verified from here.
