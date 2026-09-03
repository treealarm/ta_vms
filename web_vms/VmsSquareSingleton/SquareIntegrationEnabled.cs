namespace VmsSquareSingleton;

/// Whether SQUARE_INTEGRATION_SINK_APP_ID names a Square to push to.
///
/// A record rather than a bare bool so the container can inject it, and rather than a re-read of
/// the environment variable so there is one decision: Program.cs makes it once, and the hosted
/// services that do the pushing are registered on exactly the same condition.
public sealed record SquareIntegrationEnabled(bool Value);
