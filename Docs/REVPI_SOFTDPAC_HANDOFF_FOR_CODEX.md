# Revolution Pi + Soft dPAC — Comprehensive Handoff & Help Request (for Codex)

**Read this whole brief before doing anything. You (Codex) are new to Revolution Pi and Soft dPAC — this teaches it from scratch. We (the WMG Automation Systems Group) need help finishing ONE last step. Facts marked VERIFIED were probed live; facts marked UNCERTAIN must be confirmed on the device. Verify, don't assume; say plainly when something can't be verified.**

## 0. TL;DR
We run a small factory cell (the "SMC rig") controlled by one distributed IEC 61499 application in **EcoStruxure Automation Expert (EAE) 24.1**. Three controllers already work perfectly. We want a **Revolution Pi (RevPi) Connect+ S** to be a **fourth controller**, running **Schneider Soft dPAC**, driven by EAE — exactly like the other three. The RevPi is **fully designed in EAE**, but its Soft dPAC runtime has **never been built/started on the physical device**, and we are **locked out** of the RevPi. Nothing is wrong with the control design or the toolchain — the only gap is **getting into the RevPi to build + start its Soft dPAC**. Help us close that gap.

## 1. Concepts you need (from scratch)
- **SMC rig**: a two-station assembly demonstrator (feeder, checker, transfer, assembly, robot…). One distributed IEC 61499 application spread across several controllers.
- **EAE 24.1**: Schneider's IEC 61499 engineering tool (on our Windows PC). You build one app, assign parts to controllers, then **Deploy** + **Login** to each over the network. EAE's "Deploy and Diagnostic" pane lists each controller (a "logical device") at `IP:51443`.
- **Soft dPAC**: Schneider's **software** PLC runtime, packaged as a **Docker container**. Runs on x86 (industrial PCs) and ARM (RevPi). EAE deploys the control app to a *running* Soft dPAC over **TCP 51443**. **EAE cannot START a Soft dPAC** — it only connects to one already running.
- **UAO (Universal Automation Organization)**: the vendor-neutral IEC 61499 runtime standard/shared-source. Soft dPAC is Schneider's UAO-compliant implementation. (UAO ≠ Soft dPAC: UAO is the standard, Soft dPAC is the product.)
- **Soft dPAC Manager**: a service **on the host** (TCP **8080**, HTTPS with **mutual-TLS / client cert**) that creates/starts/stops Soft dPAC container instances. EAE talks to it (with a client certificate) to build and start instances. This is how a Soft dPAC gets brought up.
- **Revolution Pi Connect+ S**: a KUNBUS **ARM (aarch64)** industrial computer running a Debian-based OS. NOT a Schneider device — neutral hardware hosting the Soft dPAC container. Default OS user is `pi`.
- **Docker + partition**: on the RevPi, Soft dPAC needs Docker and a dedicated data partition set up first. This is the DIY part our colleague "Wass" struggled with (see §7).
- **MACVLAN**: a Docker network mode giving the Soft dPAC container its **own LAN IP** (`192.168.1.1`), separate from the host NIC (`192.168.1.2`), so EAE/other controllers reach it as a first-class device. (Known quirk: a Docker host can't reach its own macvlan container — irrelevant here, EAE is a separate machine.)

## 2. Rig, network, controllers (VERIFIED)
- Network `192.168.1.0/24`, mask `255.255.255.0`, gateway `192.168.1.254` (no ping — isolated lab segment).
- **Our EAE engineering PC**: `192.168.1.50` (Windows; EAE 24.1; all probes below were run from here).
- **Working controllers (all deploy/login fine on `:51443`)**:
  - **M262** (Modicon PLC) — `192.168.1.10`
  - **M580** (Modicon PLC) — `192.168.1.20`
  - **BX1** — a **Soft dPAC** on a Schneider **Harmony iPC** (x86, turnkey; Docker/Soft dPAC pre-integrated, no manual setup) — container `192.168.1.151` (51443 OPEN, logged in). Host `192.168.1.209` (pings, 51443 closed — runtime lives in the container).
- **The RevPi (the problem)** — EAE models it as device **"Revolution_Pi"**: host NIC (`NIC_2`) = `192.168.1.2`, Soft dPAC container (`Softdpac_3`, macvlan) = `192.168.1.1`.

### Live port scan (VERIFIED, from .50)
| IP | Role (per EAE) | ping | SSH 22 | 51443 (runtime) | 8080 (Manager) | other |
|---|---|---|---|---|---|---|
| .1 | RevPi Soft dPAC container | yes | OPEN | **CLOSED** | **OPEN** | 502/2375/2376/9090/9000/80/443 closed |
| .2 | RevPi host NIC | yes | OPEN | closed | — | |
| .151 | BX1 Soft dPAC (working) | yes | — | OPEN | — | |
| .6 | poster's old Soft dPAC IP | **NO** | — | — | — | dead |

### SSH fingerprints (VERIFIED — note the tension)
- `192.168.1.1` → `OpenSSH 9.2p1 Debian-2+deb12u6`, **aarch64**, hostname **"Essential-BX1"**.
- `192.168.1.2` → `OpenSSH 8.9p1 Ubuntu 22.04`.
- **UNCERTAIN (resolve on the device):** EAE says host=.2 / container=.1, but the fingerprints are confusing — .1 (aarch64 Debian, named "Essential-BX1") looks like the RevPi itself, while .2 is Ubuntu. Confirm which is the Docker host and which is the Soft dPAC container once you're in.

## 3. The goal
Turn the **Revolution_Pi** row in EAE's Deploy pane from **red** ("Unable to connect… port 51443 not accessible") to **"Logged In"**, then deploy the control app so the RevPi drives its assigned rig actuators — exactly like BX1/M580/M262.

## 4. DONE and PROVEN (successes)
- Full toolchain works: our C# "OSDA Mapper" generates the EAE project and **M262, M580, BX1 all deploy, run, and coordinate the cell.**
- RevPi is **fully designed in EAE** (from EAE's "Manage Soft dPAC → Configuration" dialog):
  - Instance `Softdpac_3`, IP `192.168.1.1`, CPU cores `0,1,2,3`, RAM `524288`, **ARM image `v24.1.25090.08 (softdpac)`** (NOT `x86-v24.1.25090.08` — RevPi is ARM).
  - Network `softdpacDeviceNet`, interface `eth0`, `192.168.1.0/24`, gateway `.254`, **type `macvlan`**.
- We root-caused the failure precisely (§5).

## 5. Root cause (VERIFIED via EAE's Manage Soft dPAC dialog)
The dialog shows two columns — **In Project** vs **Connected**:
- **RevPi (Softdpac_3):** every field **red**, **Connected = `---`** on all tabs (Soft dPACs, Images, Networks). → The Soft dPAC is only a **blueprint in EAE**; **nothing is built/running on the device.**
- **BX1 (Softdpac_1):** In Project == Connected (black). → Actually built and running. That's why BX1 works.

So **the RevPi's Soft dPAC container was never created/started on the device**, which is why `51443` is closed and EAE can't log in. The Soft dPAC **Manager (8080) is running**, but EAE's **Refresh/Apply does nothing** (Connected stays `---`) — EAE can't reach/authenticate to the RevPi's Manager to build the instance, and/or the RevPi's Docker/partition base isn't in place.

## 6. Tried and FAILED (do NOT repeat blindly)
- **SSH shell:** `pi@.1`, `pi@.2`, `ubuntu@.2` → **Permission denied**. `root@.1` → **authenticates but shell is `nologin`** ("This account is currently not available", connection closes) → no usable shell.
- We hold a credential **`hz9r36`** (believed "the RevPi's password", possibly the Soft dPAC *runtime/Manager* password) — **rejected for SSH** on all users tried.
- **SFTP as root** → `Received message too long` (the `nologin` shell corrupts the SFTP stream).
- **Soft dPAC Manager (8080) via browser** → HTTPS mutual-TLS; browser offered 3 client certs (`MS-Organization-Access`, `SE.DMS`, `Automation Device Maintenance`) → **all rejected** (`ERR_BAD_SSL_CLIENT_AUTH_CERT`).
- **Docker API (2375/2376), Cockpit (9090), Portainer (9000)** → all **closed**.
- **EAE Manage Soft dPAC → Refresh/Apply** → **nothing happens** (Connected stays `---`).
- No SSH private keys on the .50 PC (only `known_hosts`).

## 7. Prior art — the colleague "Wass"
Wass set this RevPi up. On the Revolution Pi forum (17 May 2025, https://revolutionpi.com/forum/viewtopic.php?t=4740) he posted that he was **installing Soft dPAC on a RevPi Connect+ S for EAE / UAO / IEC 61499** and **got stuck on the partition + Docker setup** Soft dPAC depends on. KUNBUS replied to check the correct root device (`df`). **Implication: the RevPi's Docker/partition base — the foundation the Soft dPAC container needs — may be incomplete or fragile.** Wass holds the OS credentials and the Manager cert but may be hard to reach.

## 8. Where we're STUCK (the one gap)
The Soft dPAC container must be **created + started** on the RevPi. Two ways, both currently blocked:
1. **From EAE** — Apply the blueprint via the Soft dPAC Manager (8080). Blocked: EAE can't reach/authenticate to the RevPi's Manager (Connected stays `---`; mutual-TLS cert wall).
2. **On the device** — shell in, set up/confirm Docker + partition, create/start the container. Blocked: no working shell (root `nologin`; `pi`/`ubuntu` rejected).

## 9. What we need from you (objectives, in order)
1. **Regain a usable shell on the RevPi.** Root authenticates but is `nologin`. Find (a) the correct non-root admin user + password (which account has `/bin/bash`?), or (b) a way to run commands despite `nologin`, or (c) a **non-destructive** KUNBUS RevPi recovery (eMMC-as-USB / console) to enable a shell user. **Do NOT reflash/wipe** — it destroys Wass's partition/Docker/Soft dPAC setup.
2. **Inventory the RevPi** (once in): OS + arch, users/shells (`/etc/passwd`), Docker installed+running?, Soft dPAC **data partition** present (`df`, `lsblk`)?, Soft dPAC image/container present?, Soft dPAC Manager service state on 8080.
3. **Fix the EAE↔Manager trust** — the mutual-TLS client-cert relationship between EAE (.50) and the RevPi's Soft dPAC Manager, so EAE's Apply can build the instance.
4. **Bring up the Soft dPAC** — create the macvlan network + create/start the container from the ARM image `v24.1.25090.08 (softdpac)`, ideally via EAE's **Apply** (turnkey), else directly per Schneider's Soft dPAC-on-RevPi install steps if the base needs fixing.
5. **Verify** — `192.168.1.1:51443` opens, EAE's Revolution_Pi row logs in, the control app deploys.

## 10. Constraints / cautions
- **Do NOT touch M262, M580, or BX1** — they work; don't risk them.
- **Do NOT reflash/wipe the RevPi eMMC** without explicit approval — it holds Wass's setup (the most valuable thing on it).
- **Do NOT use EAE's "Reset security"** — it wipes cert trust and can lock EAE out further.
- Keep the **ARM image** (`v24.1.25090.08 (softdpac)`), never `x86-…` (RevPi is ARM).
- Authorized, shared lab hardware (we own it, WMG Automation Systems Group). Verify against the device; state what can't be verified.

## 11. How to verify progress
- `Test-NetConnection 192.168.1.1 -Port 51443` (PowerShell) / `nc -vz 192.168.1.1 51443` → **OPEN** = runtime up.
- EAE "Manage Soft dPAC → Configuration": **Connected** column populating (matching In Project) = EAE reached and built the instance.
- EAE Deploy → Revolution_Pi row → "Logged In successfully".

## 12. Key directories / files (on the .50 PC)
- OSDA Mapper source (C# generator): `C:\VueOneMapper`
- Generated EAE project: `C:\Demonstrator`
- EAE Schneider libraries (device support): `C:\ProgramData\Schneider Electric\Libraries\` (`SE.DPAC` = Soft dPAC support, `SE.IoTMx` = M262, `SE.ModbusGateway`, …)
- Rig I/O bindings: `C:\VueOneMapper\MapperTests\TestData\SMC_Rig_IO_Bindings.xlsx`
- Wass's forum thread: https://revolutionpi.com/forum/viewtopic.php?t=4740

**Bottom line: the hard engineering is done. We're one step from a running fourth controller — the step is getting into the RevPi to build + start its Soft dPAC. Help us regain access and bring that runtime up.**
