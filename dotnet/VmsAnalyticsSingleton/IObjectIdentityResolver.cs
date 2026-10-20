using Analytics;

namespace VmsAnalyticsSingleton;

// Resolves a stable object_id (the physical object across cameras and time) for a detection.
//
// v1 has a single implementation, EphemeralReidGallery, that only remembers recent sightings in
// memory. The seam exists so a persistent reference gallery (enrolled faces by embedding, plates by
// text) that returns stable ids like "person:john" / "plate:A123BC" can be layered in front later
// without touching callers — the return type is just a string, so enrolled and ephemeral
// ("obj-{guid}") ids coexist.
public interface IObjectIdentityResolver
{
  // embedding is the L2-normalized appearance vector from video_a (carried on every frame of a
  // track; empty when no embedder model), and embeddingModel the model that produced it -- vectors of
  // different models are never compared. embeddingIsFresh is true only on the frame it was actually
  // recomputed, so the resolver records a gallery sighting per real observation, not per frame.
  // recognizedText is the plate/OCR text when present.
  string Resolve(DetectionKind kind, IReadOnlyList<float> embedding, string? embeddingModel, bool embeddingIsFresh,
    string cameraId, long trackId, string? recognizedText);

  // Read-only: the object_id already resolved for this (camera, track), or null if none yet. Used by
  // the live overlay to label boxes without minting new ids or touching the gallery.
  string? Peek(string cameraId, long trackId);
}
