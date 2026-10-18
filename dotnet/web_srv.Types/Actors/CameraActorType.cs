namespace web_srv.Types.Actors;

/// The Dapr actor type name for CameraActor, suffixed with the node whose camera subnet the host
/// shares.
///
/// Placement routes actors by <b>type name</b>, not by app id: every host registering "CameraActor"
/// joins one hash ring, and an actor for a camera could then be placed on any of them. ONVIF only
/// reaches a camera from its own node, so the ring has to be per node — and a per-node ring is
/// exactly a per-node type name. Host and caller derive it from the same setting, so a typo shows up
/// as a placement error naming a type nobody registered, instead of a camera that looks offline.
public static class CameraActorType
{
  public static string For(string ownServerId)
  {
    // Dapr puts the type name straight into the actor URL (/v1.0/actors/{type}/{id}/method/{m}), so
    // anything needing escaping would break invocation in a way that is tedious to trace back here.
    if (string.IsNullOrWhiteSpace(ownServerId)
      || !ownServerId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
    {
      throw new InvalidOperationException(
        $"OWN_SERVER_ID '{ownServerId}' must be non-empty and contain only letters, digits, '-' or '_': "
        + "it becomes part of the Dapr actor type name and therefore of the actor URL");
    }

    return $"CameraActor-{ownServerId}";
  }
}
