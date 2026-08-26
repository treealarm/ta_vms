#pragma once

// §11's model, evaluated. The policy itself is public (smartvideo/importance.h); this is
// the arithmetic that reads it.

#include "smartvideo/importance.h"
#include "smartvideo/object.h"

#include <cstdint>

namespace smartvideo {

/// TemporalStability ∈ [floor, 1]: rises with track age. 1.0 when there is no track.
float temporal_stability_of(const TrackEvidence& track, const ImportancePolicy& policy);

/// MotionWeight ∈ [floor, 1] when enabled; otherwise 1.0. Speed is in frame-diagonals/sec.
float motion_weight_of(const TrackEvidence& track, const ImportancePolicy& policy);

/// The five-factor product of §11, clamped to [0, 1]. Pass track evidence when TemporalRoi
/// has one; an empty TrackEvidence leaves the last two factors at 1.0.
float importance_of(const Object& object, const ImportancePolicy& policy,
    const TrackEvidence& track = {});

/// Importance to a QP delta, along the line the specification's own numbers define.
int32_t qp_from_importance(float importance, const ImportancePolicy& policy);

/// The same line read backwards, for a region whose delta was configured by hand rather
/// than weighed. It exists so those regions can be ordered against the weighed ones on one
/// scale -- §12 resolves an overlap by importance, and that is only meaningful if every
/// region has one. Not a claim about what the operator meant: it is the importance their
/// number corresponds to, which is exactly what the ordering needs.
float importance_from_qp(int32_t qp_delta, const ImportancePolicy& policy);

} // namespace smartvideo
