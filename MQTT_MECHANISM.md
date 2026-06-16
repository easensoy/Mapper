# MQTT mechanism — the ONLY allowed shape (read before touching MQTT generation)

> **🔒 HARD RULE — NEVER place standalone publisher pairs.**
> There must be **zero** `MqttFmt_*` and **zero** `MqttPub_*` FBs in the generated
> `.syslay` and `.sysres`. The "BX1 bridge" (`MqttBridgeEmitter`, which emitted one
> `MqttFmt_<comp>`/`MqttPub_<comp>` pair per component) is **DELETED** and must never
> be re-introduced — not framed, not grouped, not "just for M262/M580", not for any
> reason. This has been reversed three times; this document is the final word. If a
> future task says "get M262/M580 data via MQTT", the answer is: that is not possible
> with this mechanism (see *Consequence* below) — do **not** build pairs.

## The mechanism — exactly two FB roles

1. **`MQTT_CONNECTION` — one per PLC resource.** `SystemLayoutInjector.InjectMqttConn`
   emits `MqttConn` (BX1), `MqttConn_M262` (M262), `MqttConn_M580` (M580). All carry the
   SAME `ConnectionID` (`config.MqttClientId` = `SMC_BX1`) — that is the *within-resource*
   link the embedded publisher binds to — and each a UNIQUE `ClientIdentifier`
   (`SMC_BX1` / `SMC_M262` / `SMC_M580`) so the broker doesn't evict them as duplicate
   clients. `INIT`/`CONNECT` are wired (BX1 by `BuildBx1Wiring`; M262/M580 by the
   `INITO→CONNECT` self-loop + `Area.INITO`/`Station2.INITO → MqttConn_*.INIT`).

2. **Embedded `MqttStateFormatter` + `MQTT_PUBLISH` — inside each CAT.**
   `TemplateLibraryDeployer.PatchCatMqttPublish` patches `Five_State_Actuator_CAT` and
   `Sensor_Bool_CAT` so every actuator/sensor instance carries its own `MqttFmt`
   (`MqttStateFormatter`) + `MqttPub` (`MQTT_PUBLISH`), bound to the local
   `MQTT_CONNECTION` by matching `ConnectionID` (no wire), publishing `smc/<component>/state`.
   The formatter+publish ride WITH the instance — they are NOT top-level syslay FBs.

That is the whole mechanism: **per-resource `MQTT_CONNECTION` + embedded formatter/publish
per CAT.** Nothing else.

## Consequence (the accepted trade-off — do NOT re-litigate)

MQTT runs only on the **BX1 Soft dPAC**. The **M262 and M580 dPAC firmware cannot run an
MQTT runtime** — `MQTT_CONNECTION` returns **ReturnCode 50** there. So:

- **BX1 components** (covers): embedded publisher + local `MqttConn` → **publish.** ✓
- **M262 / M580 components**: embedded publisher + local `MqttConn_M262`/`_M580` → on the
  **rig** the connection RC50s, so they **do not publish there**. (In the EAE simulator,
  which is not the real firmware, they connect and publish.)

Getting M262/M580 telemetry onto the broker on the rig requires BX1 to **republish on
their behalf** — that is the bridge, which is **forbidden** (per-component pairs). So
M262/M580 live telemetry on the rig is **not available** with this mechanism. That is the
explicit, accepted trade-off: a clean syslay (no pairs) over M262/M580 rig telemetry.

## Where it is generated (the only files involved)

- `CodeGen/CodeGen/Translation/SystemLayoutInjector.cs` — `InjectMqttConn` (the per-resource
  `MQTT_CONNECTION` FBs + INIT/CONNECT wiring). **No `EmitBridge` call.**
- `CodeGen/CodeGen/Services/TemplateLibraryDeployer.cs` — `PatchCatMqttPublish` (the embedded
  `MqttFmt`+`MqttPub` inside each CAT), gated on `MapperConfig.MqttPublishEnabled`.
- `CodeGen/CodeGen/Devices/Core/SysresFbMirror.cs` — `BucketFor` routes each `MqttConn*` to
  its resource. **There is no `MqttBridgeEmitter`** (deleted).

## Verification (grep proof — must hold on every generated Demonstrator)

```
# syslay + each sysres: MQTT_CONNECTION present, ZERO bridge pairs
grep -c "MQTT_CONNECTION"  <syslay>            # >= 1 (3 across the syslay)
grep -c 'Name="MqttFmt_'   <syslay> <sysres>   # 0
grep -c 'Name="MqttPub_'   <syslay> <sysres>   # 0

# embedded publish lives INSIDE the CATs (not the syslay)
grep -c "MQTT_PUBLISH"      IEC61499/Five_State_Actuator_CAT/Five_State_Actuator_CAT.fbt   # 1
grep -c "MqttStateFormatter" IEC61499/Sensor_Bool_CAT/Sensor_Bool_CAT.fbt                  # >= 1
```
