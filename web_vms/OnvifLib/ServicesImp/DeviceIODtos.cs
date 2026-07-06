namespace OnvifLib
{
  // Settings only — ONVIF's GetRelayOutputs has no field for the relay's current logical
  // (active/inactive) state, only its configured Mode/DelayTime/IdleState (see RelayOutputSettings
  // in onvif.xsd). Live relay state cannot be polled; only tracked client-side after a command.
  public record OnvifRelayOutput(string Token, string Mode, string IdleState, int DelayMs);

  public record OnvifRelayOutputOptions(string Token, List<string> SupportedModes, bool Discrete, int MinDelayMs, int MaxDelayMs, List<int>? DiscreteDelaysMs);

  // IdleState is null when the camera didn't report it (IdleStateSpecified == false) — GetDigitalInputs
  // has no live logical-state field either (per onvif.xsd DigitalInput), only the idle-state config.
  public record OnvifDigitalInput(string Token, string? IdleState);

  public record OnvifDigitalInputOptions(string Token, bool IdleStateConfigurable);
}
