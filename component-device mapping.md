# Component → Device (PLC) Mapping — SMC Rig

**Single source of truth in code:** `CodeGen/CodeGen/Mapping/ComponentRegistry.cs`
(`Build()`). Every other site — sysres bucketing (`SysresFbMirror.BucketFor`),
HCF symbol PLC guess (`HcfSymbolIndex`), layout geometry (`LayoutGrid`), recipe
ownership (`ControllerMap`) — reads from that one table. **Change the PLC of a
component in exactly one place: that registry row.**

This document is the human-readable contract. It was verified against
`ComponentRegistry.cs` on 2026-06-02 and matches it row-for-row.

---

## The three PLCs

| PLC | EAE Device | Device Type | sysdev GUID | Resource name | Resource ID (.sysres) |
|-----|-----------|-------------|-------------|---------------|-----------------------|
| **M262** | `M262` | `M262_dPAC` | `00000000-…-000000000002` | `M262_RES` | `1459BCD12760907D` |
| **M580** | `M580` | `M580_dPAC` | `00000000-…-000000000003` | `RES0` | `3E5C2B7F1A4D6C8E` |
| **BX1** | `BX1` | `Soft_dPAC` | `00000000-…-000000000004` | `BX1_RES` | `C9F2A4B7E1D3F5A8` |

> **Only one `.sysres` may live in each sysdev folder** — the one whose filename
> stem equals the `<Resource ID>` in that `.sysdev`. A second `.sysres` (e.g. a
> stale `RES0`/`916E…` left over from a rename) makes EAE raise
> *Solution Integrity → Repair Instances* on every instance declared twice.
> `SystemLayoutInjector.SweepOrphanSysresPerSysdev` deletes such orphans on
> every Generate.

---

## M262 dPAC — Feed Station (`M262_RES`)

| Component | Kind | Process owner |
|-----------|------|---------------|
| **Feeder** | Actuator | Feed_Station |
| **Checker** | Actuator | Feed_Station |
| **Transfer** | Actuator | Feed_Station |
| **Ejector** *(a.k.a. Rejector)* | Actuator | Feed_Station |
| PartInHopper | Sensor | Feed_Station |
| PartAtChecker | Sensor | Feed_Station |
| Feed_Station | Process FB | (self) |
| Area_HMI, Station1_HMI | HMI | — |
| Area, Station1, Area_Term, Stn1_Term | Structural | — |

## M580 dPAC — Assembly + Disassembly (`RES0`)

| Component | Kind | Process owner |
|-----------|------|---------------|
| **Bearing_PnP** | Actuator (Seven_State swivel) | Assembly_Station |
| **Bearing_Gripper** | Actuator | Assembly_Station |
| **Shaft_Hr** | Actuator | Assembly_Station |
| **Shaft_Vr** | Actuator | Assembly_Station |
| **Shaft_Gripper** | Actuator | Assembly_Station |
| Clamp | Actuator | Assembly_Station |
| BearingSensor | Sensor | Assembly_Station |
| ShaftSensor | Sensor | Assembly_Station |
| Assembly_Station | Process FB | (self) |
| Disassembly | Process FB | (self) |
| Station2_HMI, Station2, Stn2_Term | Structural | — |

## BX1 Soft dPAC — Cover PnP (`BX1_RES`)

BX1 has **no Process FB of its own** — its cover actuators are commanded by
`Assembly_Station` on the M580 across resources (transparent cross-resource
event/data bridging). BX1 is the only PLC that runs an MQTT client (Pub/Sub is
Soft-dPAC-only; M262/M580 firmware return ReturnCode 50).

| Component | Kind | Process owner |
|-----------|------|---------------|
| **CoverPNP_Hr** | Actuator | Assembly_Station |
| **CoverPNP_Vr** | Actuator | Assembly_Station |
| **CoverPnp_Gripper** | Actuator | Assembly_Station |
| TopCoverSenosr | Sensor | Assembly_Station |
| MqttConn | MQTT_CONNECTION | — |

---

## One-line summary (your spec, confirmed)

```
Feeder, Checker, Transfer, Rejector(Ejector)   → M262 dPAC
Bearing actuators, Shaft actuators (+ Clamp)    → M580 dPAC
Cover actuators (+ TopCoverSenosr)              → BX1 Soft dPAC
```
