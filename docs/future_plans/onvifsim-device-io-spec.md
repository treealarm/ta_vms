# OnvifSim's device I/O against the ONVIF spec

Found 2026-11-06 while checking M2.6's part-matching rule on real cameras
(`docs/plans/square-merge/m2.6-device-diagrams.md`, section 7 and "Реальные камеры"). The
simulator lives in `../OnvifSim`; the findings come from reading `DeviceIoContract`,
`DeviceIoHandlers`, `DeviceEvents` and the two worlds, then confirming the live values with
`OnvifLib.Probe` against the running sim.

The spec text used is the ONVIF Device IO Service Specification 17.12 (§5.7, §5.10) and the
conformance checks in `onvif/testspecs`,
`device/ONVIF_DeviceIO_Device_Test_Specification.xml`.

## What is right

- Relay event: topic `tns1:Device/Trigger/Relay`, `Source.RelayToken` (`tt:ReferenceToken`),
  `Data.LogicalState` `active`/`inactive`. The getter returns token and `Properties` — `Mode`,
  `DelayTime` as `xs:duration`, `IdleState`. The contact follows
  `Active != (IdleState == Closed)`, and a monostable relay returns to idle after `DelayTime`.
- Digital input: `GetDigitalInputs` returns the token and the `IdleState` attribute; the event's
  source is `InputToken`; there is no state getter.
- Capabilities counts and `DigitalInputOptions`.
- The live relay states in `GetRelayOutputs` are `ons:` extensions in `urn:onvifsim`, inside the
  type's `xs:any` — allowed.

## What is wrong

1. **The digital input's `LogicalState` is inverted.** For a device that supports
   `SetDigitalInputConfigurations` — the sim advertises `DigitalInputOptions=true` and implements
   it — the spec says `true` is the *active* state and `false` the *idle* one. The sim's inputs
   have `IdleState=open`, and at rest the relay contact is open, so the idle circuit is open and
   `LogicalState` must be `false`. The sim reports `true` (the world comments say "open, the
   sensors read 1"; `DeviceIo.InputActive` is `!wires.Any(RelayClosed)`), and its tests assert it.
   Live: `sim-ptz`'s input, idle open, `peer` inactive → `LogicalState=true`.

   Consequence: `VmsOnvif.DeviceIoStateService.Contact` applies the spec rule
   (`closed = idle == open ? logical : !logical`), so the map's `contact` is the inverse of the
   physical contact. `DemoDevices.SensorSettings()` sets `trigger_on=open` and `check_demo.py`
   expects the sensor off while the relay is active *because of* this inversion.

   Fix: `InputActive = wires.Any(RelayClosed)` (true = circuit closed), update the world comments
   and `DeviceEventsTests`; then the demo's `trigger_on` becomes `closed` and the check's sensor
   expectations swap. Two repositories, one commit in each. The stack's polarity rule itself is
   correct for configurable cameras (both real cameras we probed support configuration).

2. **`GetRelayOutputOptions.DelayTimes` has the wrong wire type.** The schema is `tt:FloatList` —
   space-separated seconds; the sim writes `PT0.5S PT1S …` from
   `XmlConvert.ToString(TimeSpan)`. `OnvifLib.OnvifDuration.ToMs` accepts both, so ta_vms works,
   but a strict client will not parse it. The real FIS camera returns numbers in seconds. Fix:
   join `Delays.Select(d => d.TotalSeconds)` with the invariant culture.

3. **Topic-set types.** `GetEventProperties` declares the relay's `LogicalState` as `xs:string`;
   the spec requires `tt:RelayLogicalState`. It declares the digital input's as `xs:string`; the
   spec requires `xs:boolean`. The values on the wire are right — only the description is wrong.

4. **Minor.** `GetDigitalInputConfigurationOptions` without a token must return the device's
   generic options; the sim faults with `InvalidArgValue`. An unknown relay token should carry
   `ter:InvalidArgVal/ter:RelayToken` and an invalid monostable delay `ter:ModeError`; the sim
   returns only `InvalidArgVal`.

## What is not a contradiction

- The `Name` fields in the world model are editor-only; `DeviceIoContract` does not expose them,
  and ONVIF has no name for a relay or an input. That matches the real cameras we probed.
- Short tokens (`lamp`, `peer`, `input`) are legitimate `ReferenceToken`s. They do not exercise
  the vendor styles the real cameras showed (`AlarmOut_0`, `RELAY_OUTPUT_000`), and the
  index-ordering hazard recorded in the M2.6 plan stays untested on the sim.
- `PhysicalState` in the relay event data and in `GetRelayOutputs` is a simulator extension, not
  part of the spec's `MessageDescription`. The getter's `xs:any` allows it; the event `Data` is
  unbounded, so it is tolerated, but no client should depend on it.
