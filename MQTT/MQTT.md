# MQTT on the SMC Rig — How to get `MqttConn.IsConnected = TRUE`

> Written 2026-06-01 after a long debugging session. This is the **working recipe**
> plus every dead-end we hit, so future-you doesn't repeat them.

---

## TL;DR — what makes it connect

`MqttConn` (the `MQTT_CONNECTION` FB on BX1) reads `IsConnected = TRUE`,
`ReturnCode = 0` when **all** of these hold:

1. **Broker (mosquitto) is running and bound to `0.0.0.0:1883`** — NOT the
   localhost-only Windows service. Run it from the conf:
   ```powershell
   & "C:\Program Files\mosquitto\mosquitto.exe" -v -c C:\VueOneMapper\MQTT\mosquitto.conf
   ```
2. **URL scheme is `mqtt://` or `mqtts://`** — never `tcp://`
   (`tcp://` → runtime error *"The URI scheme is not MQTT"*).
3. **URL host matches the runtime's network reach:**
   - **Simulator (Test Simulator):** `mqtt://127.0.0.1:1883`. The sim runtime
     runs on this PC and **cannot dial the PC's own LAN IP** (`192.168.1.50`) —
     that connection stalls/times out and surfaces as `IsConnected=FALSE` /
     `ReturnCode 50`. Loopback works.
   - **Rig (Test Runtime):** `mqtt://192.168.1.50:1883` — the real
     M262/M580/BX1 hardware reaches the broker over the LAN.
   - The Mapper now **auto-rewrites the host to `127.0.0.1` on the sim path**
     (gated on `SimulatorFullSystem`); the rig keeps the configured LAN IP.
     See `SystemLayoutInjector` MqttConn injection.
4. **MqttConn bring-up is wired on the BX1 sysres** (by `ResourceWireEmitter`):
   `FB1.INITO → MqttConn.INIT` and `MqttConn.INITO → MqttConn.CONNECT`.
5. **Topology imports cleanly** — every `"domain"` UUID referenced by an
   `Equipment_*.json` has a matching `BroadcastDomain_*.json`. A dangling
   domain ref → *"Unable to import topology / Internal Server Error"*. The
   Mapper's `BroadcastDomainEmitter.EnsureReferencedDomains` creates any
   missing domain at `192.168.1.0/24` on the sim path.

A non-blocking `WARNING: Insecure application usage; TLSconfigMQTT` is **fine** —
the connection still opens. It only means no TLS is configured.

---

## The platform truth (do not re-litigate)

**MQTT Pub/Sub is supported on Soft dPAC ONLY.** Per the EAE v24.1 Catalog
(DIA3ED2201101EN) page 13 protocol matrix:

| Protocol | Soft dPAC | M580 dPAC | M262 dPAC |
|---|---|---|---|
| MQTT Pub/Sub | ● | – | – |
| OPC UA / Modbus / EtherNet IP / Open TCP/IP | ● | ● | ● |

- `MQTT_CONNECTION` on **M262/M580 returns `ReturnCode 50`** ("feature not
  available") and never opens a socket. The FB type compiles everywhere
  (declared in `SE.DPAC`) but the client implementation is linked into the
  **Soft dPAC binary only**.
- **Only BX1 (Soft dPAC) publishes MQTT.** M262/M580 component state reaches
  the broker by being **bridged to BX1** (see "Cross-PLC bridge" below).
- The EcoStruxure **Machine Expert** M262 (a different platform, IEC 61131-3)
  *does* have MQTT — that's NOT the EAE dPAC. Don't confuse them.

> Note: `ReturnCode 50` is overloaded in our experience — on M262 it means
> "feature unavailable", but on BX1-in-sim it appeared as a **failed-connect
> symptom** (couldn't reach `192.168.1.50` from the sim). The PDF could not
> confirm the exact `50` decode. Treat `50` as "connection did not open" and
> check the URL host first.

---

## The MqttConn FB (what the Mapper emits on BX1)

4-parameter reference shape (matches Schneider's working TrainingIIoT sample):

```xml
<FB Name="MqttConn" Type="MQTT_CONNECTION" Namespace="Runtime.NetConnectivity">
  <Parameter Name="QI"               Value="TRUE" />
  <Parameter Name="ConnectionID"     Value="'SMC_BX1'" />
  <Parameter Name="URL"              Value="'mqtt://127.0.0.1:1883'" />   <!-- sim; rig = 192.168.1.50 -->
  <Parameter Name="ClientIdentifier" Value="'SMC_BX1'" />
</FB>
```

- **Do NOT add `CACert`** with a Windows path — `C:\...` trips
  `CTcpClientStateMgr.getUriStrValue: Unsupported parameter format` (the `$`
  in IEC 61131-3 string literals also mangles backslash paths). TLS was a
  rabbit hole; the plain `WARNING` path works.

---

## Per-component topics

Each component publishes to `smc/<lowercased-name>` via an **embedded
`MqttPub`** inside its CAT (patched by `TemplateLibraryDeployer.PatchCatMqttPublish`):

- `RootPath = 'smc'` (literal prefix)
- `Topic1` is **wired** from the CAT's per-instance name InputVar
  (`actuator_name` for Five_State, `name` for Sensor_Bool) — NOT a static
  `'state'`. (EAE does **not** resolve the `$${PATH}` placeholder at runtime,
  so the old `smc/$${PATH}/state` made every instance share one literal topic.)
- Payload is `{state:N}` (an `MqttStateFormatter` basic FB converts the INT
  state → JSON string).
- `MqttPub` binds to `MqttConn` by **matching `ConnectionID='SMC_BX1'`** —
  there is no wire between them.

**Only the multi-channel `CNTX:=1` MQTT_PUBLISH variant is proven**
(`MQTT_PUBLISH_115480E69E664F878`). There is no known multi-channel hash, so
each component needs its own `MqttPub` (CNTX:=1).

---

## Cross-PLC bridge (M262 / M580 → BX1)  — SIM-GATED

Because M262/M580 can't publish, their state is bridged to BX1:

- For every M262/M580 sensor/actuator, `MqttBridgeEmitter` emits a
  `MqttFmt_<comp>` + `MqttPub_<comp>` pair **on BX1**, inside a labeled
  "MQTT Bridge" frame below the station frames.
- A **cross-resource event/data wire** carries the remote component's state
  (`pst_out` + `current_state_to_process` for actuators; `CNF` + `Status` for
  sensors) into `MqttFmt_<comp>.REQ/.state`. EAE bridges this at deploy —
  the catalog's "transparent and implicit cross-communications".
- Actuators need `PatchCatExposeState` to expose `state_out` +
  `current_state_to_process` at the Five_State CAT boundary (sensors already
  expose theirs). This is **sim-gated** so the rig CAT body is untouched.
- Gated on `cfg.SimulatorFullSystem`. Un-gate (remove the gate on `EmitBridge`
  + `PatchCatExposeState`) to run the bridge on the rig — syslay + CAT-body
  only, no `.sysdev`/device-trust touch.

---

## Run commands

**Window 1 — broker:**
```powershell
& "C:\Program Files\mosquitto\mosquitto.exe" -v -c C:\VueOneMapper\MQTT\mosquitto.conf
```
(If port 1883 is held by the localhost-only Windows service, stop it once:
`Stop-Service mosquitto -Force; Set-Service mosquitto -StartupType Manual`.)

**Window 2 — subscribe (loopback for sim):**
```powershell
$log = "C:\VueOneMapper\MQTT\smc_$(Get-Date -Format yyyyMMdd_HHmmss).jsonl"
& "C:\Program Files\mosquitto\mosquitto_sub.exe" -h 127.0.0.1 -t "smc/#" -v -F "%I  %t  %p" -q 1 | Tee-Object -FilePath $log
```

**MapperUI → Test Simulator. EAE → Reload Solution → Build → Deploy → Login →
Start** (Active Network Profile = **Local Test**). Tick **all** devices in
Deploy & Diagnostic so every component's state publishes.

**Coverage check (after a recipe cycle):**
```powershell
$latest = Get-ChildItem C:\VueOneMapper\MQTT\smc_*.jsonl | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $latest | ForEach-Object { ($_ -split '\s{2,}')[1] } | Sort-Object -Unique
```

---

## Troubleshooting ledger (errors → fix)

| Symptom | Cause | Fix |
|---|---|---|
| `The URI scheme is not MQTT` | URL used `tcp://` | use `mqtt://` / `mqtts://` |
| `Insecure configuration prohibited; TLSconfig` (ERROR) | strict secure-by-default rejected plain `mqtt://` on that build | use `mqtts://`, or accept the WARNING variant |
| `Insecure application usage; TLSconfigMQTT` (WARNING) | no TLS configured | **ignore — connection still opens** |
| `ReturnCode 50`, `IsConnected=FALSE` | (sim) URL host = LAN IP the sim can't reach; (M262/M580) firmware gate | sim → `127.0.0.1`; M262/M580 → bridge to BX1 |
| `ReturnCode 100` | TLS handshake / cert path issue | drop `CACert`, use plain `mqtt://`/`mqtts://` |
| `CTcpClientStateMgr.getUriStrValue: Unsupported parameter format` | `CACert` Windows path with backslashes | remove `CACert` (or forward-slash + verify) |
| `Unable to import topology / Internal Server Error` | dangling `"domain"` UUID with no `BroadcastDomain` file | `EnsureReferencedDomains` (auto on sim) or create the domain file |
| `mosquitto_sub … actively refused / timeout` | localhost-only mosquitto service, or dialing the PC's LAN IP | run the conf (binds 0.0.0.0); subscribe with `-h 127.0.0.1` |
| Only some components publish, all `{state:0}` | recipe not cycling actuators / publish-before-connect race | run the recipe; see "live data" note below |
