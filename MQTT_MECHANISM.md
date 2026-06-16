# MQTT mechanism — the BX1 bridge IS the working path (read before touching MQTT)

> **🔒 HARD RULE — DO NOT remove the BX1 bridge for "cleanliness."**
> The `MqttBridgeEmitter` / `EmitBridge` path was deleted **three times** under a
> "no standalone pairs / clean syslay" instruction, and **every time it killed the
> M262/M580 live stream** (`smc/feeder`, `smc/checker`, `smc/transfer`, …). It is
> the **rig-real** path. The earlier "never reintroduce bridge pairs" version of
> this file was WRONG and is replaced. If the syslay clutter is the concern, the
> answer is the **grouped-frame form** (all pairs inside one labeled "MQTT Bridge"
> frame) — NOT deleting the bridge.

## Why per-resource / embedded-only does NOT work on the rig

The M262 and M580 dPAC firmware **cannot run an MQTT client** — `MQTT_CONNECTION`
returns **ReturnCode 50/101** there (observed: with a per-resource `MqttConn` on each
PLC, all three sit at RC 101 and nothing publishes). Only **BX1 (Soft dPAC)** has a
working MQTT runtime. So M262/M580 **cannot publish themselves**; something on BX1 has
to publish on their behalf. That something is the bridge.

## The mechanism — what MUST be generated

1. **ONE `MQTT_CONNECTION` on BX1** (`MqttConn`, `ConnectionID = SMC_BX1`).
   Injected by `SystemLayoutInjector.InjectMqttConn`. There are **no** `MqttConn_M262`
   / `MqttConn_M580` — M262/M580 can't run MQTT, so a per-resource connection there
   only RC50/RC101s.

2. **BX1's OWN components** (Cover PnP) publish via the **embedded** `MqttFmt`
   (`MqttStateFormatter`) + `MqttPub` (`MQTT_PUBLISH`) inside their CAT
   (`TemplateLibraryDeployer.PatchCatMqttPublish`), bound to `MqttConn` by `ConnectionID`.

3. **M262/M580 components are BRIDGED** by `MqttBridgeEmitter.EmitBridge`
   (called right after `InjectMqttConn`): for each M262/M580 sensor/actuator it emits a
   `MqttFmt_<comp>` (formatter) + `MqttPub_<comp>` (publish) pair **on BX1**, inside ONE
   labeled **"MQTT Bridge" frame** (`FRAME_MqttBridge`), cross-resource-wired to the
   component's state output (`state_out`+`current_state_to_process` for actuators,
   `pst_out`+`Status` for sensors), publishing `smc/<comp>/state` through BX1's `MqttConn`.
   `ShouldBridge` accepts only M262 + M580 (BX1's own covers already publish via the
   embedded path — bridging them would double-publish).

End-to-end: `M262/M580 component state → cross-resource wire → BX1 MqttFmt_/MqttPub_ pair
→ BX1 MQTT_CONNECTION → broker`.

## Files

- `CodeGen/CodeGen/Translation/SystemLayoutInjector.cs` — `InjectMqttConn` (ONE BX1
  connection) **+ `MqttBridgeEmitter.EmitBridge(builder, config)`** right after it.
- `CodeGen/CodeGen/Services/MqttBridgeEmitter.cs` — the bridge (frame-grouped pairs).
  **Do not delete.**
- `CodeGen/CodeGen/Services/TemplateLibraryDeployer.cs` — `PatchCatMqttPublish`
  (BX1's own embedded publish).
- `CodeGen/CodeGen/Devices/Core/SysresFbMirror.cs` — `BucketFor` routes `MqttFmt_*` /
  `MqttPub_*` to BX1.

## Verification (must hold on every generated Demonstrator)

```
grep -c "MQTT_CONNECTION"  <syslay>            # 1  (BX1 only)
grep -c 'Name="MqttConn_M' <syslay>            # 0  (no per-resource connections)
grep -c 'Name="MqttFmt_'   <syslay>            # > 0 (one per M262/M580 component)
grep -c 'Name="MqttPub_'   <syslay>            # equal to MqttFmt_ count
grep -c 'Name="FRAME_MqttBridge"' <syslay>     # 1  (the labeled frame)
```
