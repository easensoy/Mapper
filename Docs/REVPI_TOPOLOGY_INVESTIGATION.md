# Revolution Pi / Soft dPAC — EAE Topology Forensic Investigation (read-only)

Read-only analysis. Archives were extracted as **copies** into a temp analysis dir; no original archive or EAE solution was modified, resaved, imported into, or deployed. Every value is cited to an exact file + field. **EVIDENCE** = read directly from a file/binary; **INFERENCE** = reasoned, flagged.

Sources: `DemonstratorWithHMI.sln.zip` (→ `DemoMain`), `DemonstratorWithHMI_20260721-102309340.sln.zip` (→ `DemoTs`), `SMC_Rig_Expo_withClamp_RevPi_20260625_Jyostna-125057240.sln (1)` (RevPi project), `C:\Demonstrator\Demonstrator` (Mapper output), and `SchneiderElectric.Automation.Topology.dll` (EAE 24.1 schema).

---

## 1. Executive conclusion
- **The main Demonstrator solution does NOT contain a deployable Revolution Pi target.** It has no `Equipment_Revolution Pi.json` and no `Revolution_Pi` logical device. Its only Soft_dPAC-typed extra device, `SoftDpac` (uuid `8e5bdb40…`), is an **empty orphan** (empty FBNetwork + empty resource + no topology mapping).
- **`192.168.1.1` and `.2` are HMIP6, not the RevPi.** In every project, `.1` = HMIP6 `Softdpac_2` container and `.2` = HMIP6 `eno1` host. The earlier handoff/target that used `.1/.2` for the RevPi **misattributed HMIP6's addresses.**
- **The documented RevPi container is `192.168.1.6`** (`Softdpac_3`), existing **only** in Jyotsna's dedicated RevPi project. Its host is `.2`, which **collides** with HMIP6's host and Workstation_1 → the RevPi project is an **alternative/experimental topology**, not a simultaneous fourth controller.
- **The RevPi carries real, unique control logic** (a Modbus feed/checking station: `PLC_RW_REVPI` + Feeder + Checker + `Process1_CAT`) that exists in **none** of DemoMain / DemoTs / `C:\Demonstrator`. Adding the RevPi is therefore an application-migration, not a device drop-in.
- The live deployment fails because **there is no running Soft dPAC runtime at the intended RevPi endpoint, and the solution being deployed from has no integrated RevPi target at all.** SSH lockout is a fallback-access issue, not the primary cause.

---

## 2. Topology object comparison
| Object | DemoMain | DemoTs | RevPi project | C:\Demonstrator |
|---|---|---|---|---|
| `Equipment_Revolution Pi.json` | **absent** | **absent** | **present** | **absent** |
| HMIP6_1 (`.2`/`.1`) | present | absent | present | absent |
| Workstation_1 (`.2`) | present | absent | present | absent |
| HMIB1X_1 / BX1 (`.209`/`.151`) | present | present | present | present |
| M262 (`.10`) / M580 (`.20`) | present | present | present | present |
| EtherNetIPDevice_1 (`.210`) | present | present | present | present |
| Switch_1 | present | present | present | present |
| BroadcastDomains | Default,DN1,DN2,DN3 | DN1,DN2 | Default,DN1,DN2 | (see §6) |

`grep -rli "revolution|revpi"` over DemoMain, DemoTs, and `C:\Demonstrator\Demonstrator` → **0 hits each.**

---

## 3. Complete IP-ownership table (evidence-cited)
| IP | Owner(s) | Source |
|---|---|---|
| **.1** | HMIP6 `Softdpac_2` **container** | `Equipment_HMIP6_1.json:45` (DemoMain & RevPi project) |
| **.2** | HMIP6 host `eno1` **+ Workstation_1** (+ RevPi host `NIC_2\eth0` in RevPi project) | HMIP6 `:109`, Workstation_1 `:45`, `Equipment_Revolution Pi.json:45` |
| **.6** | RevPi `Softdpac_3` **container** (RevPi project only) | `Equipment_Revolution Pi.json:78` |
| .10 / .20 | M262 / M580 | `Equipment_M262dPAC_1.json:36` / `Equipment_M580dPAC_1.json:56` |
| .50 | engineering PC (not a topology object) | live probes |
| .151 / .209 | BX1 `Softdpac_1` container / BX1 host `eth0` | `Equipment_HMIB1X_1.json:41` / `:109` |
| .210 | EtherNetIPDevice_1 | `Equipment_EtherNetIPDevice_1.json:48` |
| .254 | gateway (all DeviceNetwork domains) | `BroadcastDomain_*.json` |

**Conflicts flagged:** `.2` is double-claimed in DemoMain (HMIP6 host + Workstation_1) and **triple-claimed** in the RevPi project (HMIP6 host + Workstation_1 + RevPi host). Two Soft dPAC hosts cannot share `.2` on one L2 segment.

---

## 4. RevPi configuration recovered (all from `Topology\Equipment_Revolution Pi.json` unless noted)
| Field | Value | Source |
|---|---|---|
| Equipment identifier / catalog | `Revolution Pi` / `Workstation_V01.00_01.00` | :2–4 |
| Equipment uuid (project-local) | `e4af8b21-5f26-4907-85c3-176b0961605f` | :3 |
| DomainTag (project-local security domain) | `ce4610b6-039d-46e7-8f75-e0efcfea9441` | :17 |
| Host NIC | `NIC_2` (`NIC_EAE_V01.00_01.00`, uuid `3147b81f…`), interface `eth0`, **IP `192.168.1.2`** | :29–46 |
| Container | `Softdpac_3` (`SoftdpacContainer_V01.00_01.00`, uuid `f8a07c46…`), **IP `192.168.1.6`** (domainReadOnly) | :62–79 |
| Image / version / RAM / CPU | `softdpac` / **`v24.1.25090.08`** (ARM) / `524288` / `[0,1,2,3]` | :103–112 |
| RuntimeDEO uuid / typeId / logicalDeviceId | `f985947e-9501-4c0a-aecf-2b5e24c11f5b` / `29797a55…` / **`72a1fde3-a5a7-4cdf-97a9-210fb327873a`** | :116–118 |
| Runtime ports | none explicit → implicit secured **51443** (BX1 pattern; HMIP6 differs with an explicit host runtime 61999/51443) | :119–128 |
| Manager port | **8080** | :158 |
| dockerVlan | `softdpacDeviceNet`, **type 0 = macvlan**, domain `db72f221` (DeviceNetwork_1 /24), interface **`NIC_2\eth0`** | :146–152 |
| Runtime identity cert | `cacert`, key = `f985947e…` (= RuntimeDEO uuid), SAN `192.168.1.2`, Client+Server auth | RevPi `General\Certificates\Certificates.xml` |

---

## 5. What DemoMain lacks vs the dedicated RevPi project
- The entire `Equipment_Revolution Pi.json` object (host + `Softdpac_3` container @ `.6` + macvlan + Manager).
- The `Revolution_Pi` Soft_dPAC logical device (`72a1fde3…`) and its resource `D090B4163A62A815.sysres`.
- The **application**: `RevPI_IO` (`PLC_RW_REVPI`), `Feeder`, `Checker` (`Five_State_Actuator_CAT`), `Process1` (`Process1_CAT`), init/terminator FBs.
- The `PLC_RW_REVPI.fbt` type and the Modbus-master hcf.
- The RevPi runtime identity certs bound to `.2`.
- **DemoMain instead** runs the feed station on M262 (`Feed_Station`=`Process1_Generic` + `Feeder`/`Checker`/`Transfer`/`Ejector` Five_State + `Robot`). DemoTs is the trimmed 3-PLC core (M262/M580/BX1) with none of this.

---

## 6. Logical-device & application allocation
**DemoMain `SoftDpac` (`8e5bdb40…`) — empty orphan.** `…/8e5bdb40….sysdev` = `Type="Soft_dPAC" Namespace="SE.DPAC"`, body `<FBNetwork/>`, **no `<Resources>`**; resource `6C78F462418C6980.sysres` = empty `EMB_RES_ECO`/`RES0`. **No topology equipment references `logicalDeviceId 8e5bdb40…`.** Deploys nothing.

**RevPi `Revolution_Pi` (`72a1fde3…`) — real, deployable.** Resource `D090B4163A62A815.sysres` (9.5 KB, `<Compile SystemResource>`), 7 FBs: `RevPI_IO`(`PLC_RW_REVPI`, Modbus broker: coils `ExtendPusher/ExtendChecker`; sensors `PusherAtWork/Home`, `checkerUp/chekcerDown`, `Hopper`), `Feeder`/`Checker`(`Five_State_Actuator_CAT`), `Process1`(`Process1_CAT`), `FB1`(`DPAC_FULLINIT`), `FB2`(`plcStart`), `CheckingStationTerminator`. Modbus-master hcf; **no EtherNet/IP; no cross-comm** to M262/M580/BX1 (self-contained). Topology-mapped to `.6`. **Feeder/Checker were moved OFF M262 onto the RevPi** (M262 there keeps CheckingStation/Transfer/Rejector/Robot/M262IO only).

**Architectural fork:** RevPi project uses `Process1_CAT` + `PLC_RW_REVPI` (Modbus, feed on RevPi). DemoMain/`C:\Demonstrator` use `Process1_Generic` (feed on M262). Different designs — integrating the RevPi means **adopting the Modbus-feed-on-RevPi architecture and removing Feeder/Checker from M262**, not a copy-paste. **What is lost if only the topology object is copied: all of the above logic** — the object alone is an empty shell.

---

## 7. Docker / macvlan assessment
- **`dockerVlans[].type = 0 = MacVLan` — schema-proven** (not name-inferred): enum `SchneiderElectric.Automation.Topology.VLanType` in `…\SystemManager\SchneiderElectric.Automation.Topology.dll` = `{ MacVLan=0, IPVLan=1 }` (only two members; the DLL also carries `"The vlan type cannot be changed."` → immutable, matching `domainReadOnly`). All three Soft dPAC hosts (BX1/HMIP6/RevPi) use `type 0` = macvlan.
- **`interface: "NIC_2\eth0"` is a valid EAE topology path**, not an OS name and not malformed. The RevPi is modeled as a **generic `Workstation` with a nested child NIC equipment `NIC_2` (`NIC_EAE_V01.00_01.00`)**, so the macvlan parent is qualified `NIC_2\eth0` (backslash = EAE path separator, same as the `"path"` fields). BX1/HMIP6 are appliance catalogs (`HMIB1X`/`HMIP6`) with a direct host NIC (`eth0`/`eno1`), so no child prefix. All resolve to a physical `eth0`/`eno1` at OS level.
- **macvlan is appropriate** for the RevPi: a Soft dPAC container needs its own MAC/IP on the L2 segment to appear as a first-class device at `192.168.1.x`. Uncertainties to confirm on-site: the physical parent NIC on the RevPi (`eth0` vs `eth1`), switch acceptance of multiple MACs, that `.6` is actually free, and host↔container isolation (irrelevant to EAE which is a separate machine; the Manager runs on the host and manages the container via Docker, not over the macvlan).

---

## 8. UUID / relationship map (RevPi project)
```
Equipment "Revolution Pi"  uuid e4af8b21…   DomainTag ce4610b6…
 ├─ NIC_2 (NIC_EAE)         uuid 3147b81f…   eth0 → 192.168.1.2   domain db72f221 (DeviceNetwork_1 /24)
 └─ Softdpac_3 (Container)  uuid f8a07c46…   Eth0 → 192.168.1.6   domain db72f221
     ├─ DockerContainerDEO   image softdpac v24.1.25090.08  RAM 524288  CPU 0-3
     ├─ RuntimeDEO           uuid f985947e…   typeId 29797a55…
     │     └─ logicalDeviceId 72a1fde3… ──► sysdev "Revolution_Pi" (Soft_dPAC)
     │                                         └─ sysres D090B4163A62A815 (7 FBs, PLC_RW_REVPI, Modbus hcf)
     └─ cert cacert key f985947e… (= RuntimeDEO uuid)  SAN 192.168.1.2  Client+Server
 SoftdpacManagerDEO  dockerVlan softdpacDeviceNet type 0(macvlan) interface NIC_2\eth0  Manager :8080
```
**Project-local UUIDs (must NOT be copied verbatim into another solution):** every equipment uuid (HMIP6 is `6f0bd2cd…` here vs `25e3cc7c…` in DemoMain — same device, different uuid), the container/runtime uuids, `logicalDeviceId 72a1fde3…`, and the `DomainTag`.

---

## 9. Broadcast-domain analysis
- **DeviceNetwork_1** = `db72f221-ece1-4b82-8132-731ce655044e` = `192.168.1.0 / 255.255.255.0 (/24)` gw `.254` — **the domain every host/container endpoint references** (HMIP6 `.1`, RevPi `.6`, BX1 `.151`, and the RevPi macvlan).
- **DeviceNetwork_2** = `d205b554-cf3e-4f01-aaaa-c8ce2e3541f4` = `192.168.1.0 / 255.255.0.0 (/16)` gw `.254` — **overlaps the same space, referenced by no endpoint read → stale/erroneous.** Same subnet, two domain UUIDs with different masks is a topology defect to clean up (do not deploy against it).

---

## 10. Root-cause ranking
| # | Cause | Confidence | Evidence | Contrary | Next discriminating test |
|---|---|---|---|---|---|
| 1 | **No integrated RevPi target in the deployable solution** | **Very high** | DemoMain & C:\Demonstrator have no RevPi equipment/logical device; only empty `SoftDpac` orphan. RevPi exists only in Jyotsna's project. | — | Decide source-of-truth solution; confirm which .sln EAE actually deploys from. |
| 2 | **No running Soft dPAC runtime at the RevPi endpoint** | High (live) | `.6` no response; `.1:51443` closed live. | We were on-net earlier; re-verify on-site. | On-site: `Test-NetConnection .6 -Port 51443` and Manage Soft dPAC "Connected" column. |
| 3 | **Wrong IP attribution — `.1` used for RevPi = HMIP6's container** | High | Handoff/target used `.1/.2`; files say RevPi container = `.6`. | Only bites once a RevPi target exists. | Compare the deployed target's container IP vs `.6`. |
| 4 | **Host-address collision at `.2`** | High (if simultaneous) | RevPi host `.2` = HMIP6 host = Workstation_1. | No conflict if RevPi *replaces* HMIP6. | Decide replace-HMIP6 vs 4th-controller; assign a new unique host IP if 4th. |
| 5 | **Local EAE DPWS permission (`config.json` denied)** | Medium | Codex's local log; blocks Manage Soft dPAC Apply locally. | Not re-verified by this pass. | Run EAE 24.1 **elevated**, retry Refresh/Apply. Cheap, do first. |
| 6 | **Manager cert / trust** | Low–Medium | Browser rejected 3 certs. | **Browser certs (MS-Organization-Access, SE.DMS, Automation Device Maintenance) are NOT the EAE runtime certs** — wrong tool; EAE uses its `cacert` runtime certs (RevPi project even holds the RevPi runtime identity cert `f985947e`). No evidence of an EAE-side trust failure. | Retry from EAE (elevated), not a browser. |
| 7 | **Docker/partition base incomplete** | Medium | Wass's forum thread (stuck on partition+Docker on the RevPi). | Circumstantial. | On-site shell: `df -hT`, `lsblk -f`, `docker info`, `systemctl status docker`. |
| 8 | **ARM image availability/version** | Low | RevPi config already specifies ARM `v24.1.25090.08` (present in project). | — | Confirm the ARM image is pulled/available on the RevPi. |
| 9 | **macvlan parent/interface** | Low | `NIC_2\eth0` is schema-valid. | — | On-site: confirm physical parent NIC + switch MAC acceptance. |

---

## 11. Proposed target RevPi configuration (recovered vs proposed)
| Field | Value | Status |
|---|---|---|
| RevPi container IP | **`192.168.1.6`** | **RECOVERED** (strongest documented; verify free on-site) |
| RevPi host IP | **TBD — a NEW unique address (NOT `.2`)** unless RevPi replaces HMIP6 | **PROPOSED** |
| Host interface (parent) | `NIC_2\eth0` (EAE path); physical NIC to confirm on-site | RECOVERED / verify |
| Docker network | `softdpacDeviceNet`, **macvlan (type 0)** | RECOVERED |
| Subnet / mask / gw | `192.168.1.0` / `255.255.255.0` / `.254` (DeviceNetwork_1 db72f221) | RECOVERED |
| Manager port | `8080` | RECOVERED |
| Runtime secured port | `51443` (implicit) | RECOVERED |
| Image / version | `softdpac` / `v24.1.25090.08` (ARM — **never x86**) | RECOVERED |
| RAM / CPU | `524288` / `[0,1,2,3]` | RECOVERED |
| Logical device | `Revolution_Pi` (Soft_dPAC) + Feed sub-app (`PLC_RW_REVPI`/`Feeder`/`Checker`/`Process1_CAT`) | RECOVERED |
| Required links | equipment→container→RuntimeDEO→logicalDeviceId→sysres; macvlan→DeviceNetwork_1; runtime identity cert | RECOVERED |
| Decision required | RevPi **replaces HMIP6** (reuse `.2`, no collision) **vs 4th simultaneous controller** (new host IP) | **OPEN** |

---

## 12. Safe EAE-UI integration procedure (no manual JSON/XML edits)
UUIDs are project-local; do not copy JSON between solutions. Two clean routes:
- **Route A (lowest risk): work in Jyotsna's RevPi project** — it already contains the complete, correct RevPi (device + `.6` + feed sub-app + runtime certs). Validate/commission there first.
- **Route B: reconstruct in DemoMain via the EAE UI**, validating (Solution Integrity + Build) after every stage:
  1. (optional) delete the dead `SoftDpac` orphan device.
  2. Physical Views → add a **Workstation**; add a child **NIC (NIC_EAE)**; set its host IP to a **new unique** address (verified free) — or, if replacing HMIP6, remove HMIP6 and reuse `.2`.
  3. Add a **SoftdpacContainer** child; container IP `.6` (verify free); **macvlan** on `NIC_2\eth0`; **DeviceNetwork_1 /24**; ARM image `v24.1.25090.08`; RAM/CPU.
  4. Create/associate the **`Revolution_Pi` Soft_dPAC** logical device; bind the RuntimeDEO `logicalDeviceId`.
  5. Bring in the RevPi **feed sub-application** (`PLC_RW_REVPI`+`Feeder`+`Checker`+`Process1_CAT`) and **remove Feeder/Checker from M262** — this is the deliberate architecture change; alternatively keep the Mapper's M262 feed and give the RevPi other logic (a design decision to make explicitly).
  6. Provision device trust through EAE (Manage Soft dPAC / Configure security). **Do not use Reset Security.**
- **Do not hand-merge archived JSON** (breaks project-local UUIDs/DomainTags).

---

## 13. On-site verification checklist
1. `arp -a` → match `.1`, `.2`, `.6` MACs to the **physical MAC labels** on the RevPi vs the HMIP6/other hosts. Establish which physical device owns each.
2. Confirm a **new unused RevPi host address** (or the replace-HMIP6 decision).
3. Confirm `.6` is actually free on the segment.
4. Launch **EAE 24.1 elevated** (addresses the `config.json` DPWS write-permission error).
5. Manage Soft dPAC → **Refresh**; read the **Connected** column (populated = EAE reached the Manager).
6. `Test-NetConnection <ip> -Port 8080` (Manager) and `-Port 51443` (runtime) for the true RevPi.
7. Escalate to a Linux console/`rpiboot` on the RevPi **only if** Manager provisioning fails — image/backup first, never reflash.

---

## 14. Direct answers
- **What can be recovered from the RevPi project?** The complete RevPi definition — topology object, `Softdpac_3`@`.6`, macvlan on `NIC_2\eth0` (DeviceNetwork_1 /24), ARM image `v24.1.25090.08`, `Revolution_Pi` Soft_dPAC logical device (`72a1fde3…`) with a real Modbus feed sub-app (`PLC_RW_REVPI`+Feeder+Checker+`Process1_CAT`), Modbus hcf, and the runtime identity cert.
- **Was `.1` ever the RevPi container in the files?** **No.** `.1` = HMIP6 `Softdpac_2`.
- **Does `.1` belong to HMIP6 Softdpac_2?** **Yes.**
- **Is `.6` the strongest documented RevPi container candidate?** **Yes — the only one.**
- **Is `.2` safe for a fourth-controller RevPi host?** **No** — collides with HMIP6 host + Workstation_1. Safe only if the RevPi **replaces** HMIP6.
- **What must be added to DemoMain before RevPi deployment can work?** A full RevPi topology object (host + `Softdpac_3`@`.6` + macvlan), the `Revolution_Pi` logical device + its feed sub-app, the Modbus hcf, a unique host IP (or HMIP6 removal), and provisioned device trust — plus a running Soft dPAC runtime on the physical RevPi.
- **Is the failure topology, provisioning, or both?** **Both** — (1) the deployable solution has no integrated RevPi target (topology/logical-device), and (2) no Soft dPAC runtime is running at the intended RevPi endpoint (provisioning). SSH is only a fallback-access concern.
