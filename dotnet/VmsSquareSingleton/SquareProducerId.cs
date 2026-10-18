namespace VmsSquareSingleton;

/// Who ta_vms is in Square's object tree.
///
/// Deliberately its own setting rather than this process's APP_ID. The value becomes the root object
/// of ta_vms's subtree (SquareCameraPush.ToObjectId derives the id from it deterministically), so
/// deriving it from the app id tied the identity of the data to which process happened to run the
/// integration — moving that code between services would silently re-parent the whole tree, and two
/// instances under different app ids would split it in half.
///
/// Both sides configure it: ta_vms names Square's receiving app in
/// SQUARE_INTEGRATION_SINK_APP_ID, Square names this value. Because the id is derived from the
/// string rather than handed out in a handshake, nothing has to be exchanged at runtime.
///
/// Treat the value as permanent: changing it orphans everything already registered under the old
/// root, exactly as moving the producer used to.
public sealed record SquareProducerId(string Value);
