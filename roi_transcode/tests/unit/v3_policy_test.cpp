// ObjectPriority ladder (v3 §11.1), geometry policy (§11.2–11.3), QP slew (§13).

#include "importance.h"
#include "object_normalizer.h"
#include "qp_slew.h"
#include "roi_lag.h"
#include "smartvideo/object_priority.h"

#include <cstdio>
#include <string>
#include <vector>

namespace {

int32_t failures = 0;

void check(bool ok, const std::string& what)
{
    if (!ok) {
        std::fprintf(stderr, "FAIL: %s\n", what.c_str());
        ++failures;
    }
}

void check_eq(int32_t got, int32_t want, const std::string& what)
{
    if (got != want) {
        std::fprintf(stderr, "FAIL: %s (got %d, want %d)\n", what.c_str(), got, want);
        ++failures;
    }
}

void ladder_maps_kinds_to_canonical_deltas()
{
    check_eq(smartvideo::qp_delta_for(smartvideo::ObjectPriority::Critical), -8, "critical");
    check_eq(smartvideo::qp_delta_for(smartvideo::ObjectPriority::High), -6, "high");
    check_eq(smartvideo::qp_delta_for(smartvideo::ObjectPriority::Normal), -4, "normal");
    check_eq(smartvideo::qp_delta_for(smartvideo::ObjectPriority::Low), -2, "low");
    check_eq(smartvideo::qp_delta_for(smartvideo::ObjectPriority::Background), 0, "background");

    check(smartvideo::priority_from_importance(1.00f) == smartvideo::ObjectPriority::Critical,
        "plate importance is Critical");
    check(smartvideo::priority_from_importance(0.80f) == smartvideo::ObjectPriority::High,
        "person importance is High");
    check(smartvideo::priority_from_importance(0.60f) == smartvideo::ObjectPriority::Normal,
        "vehicle importance is Normal");
}

void detections_use_ladder_by_default()
{
    smartvideo::Quality q;
    q.use_v3_priority_ladder = true;
    q.margin_mode = smartvideo::MarginMode::FixedPixels;
    q.margin_px = 0; // isolate QP from geometry
    q.min_roi_side = 0;
    smartvideo::RoiMapBuilder builder;
    smartvideo::Box plate;
    plate.kind = "license_plate";
    plate.x = 0.1f;
    plate.y = 0.1f;
    plate.width = 0.2f;
    plate.height = 0.1f;
    const auto dets = builder.detections_from({plate}, q, 1280, 720);
    check(dets.size() == 1, "one detection");
    check_eq(dets[0].qp_delta, -8, "plate lands on Critical −8");
    check(dets[0].priority == smartvideo::ObjectPriority::Critical, "priority Critical");
}

void min_roi_side_expands_a_tiny_box()
{
    smartvideo::Quality q;
    q.margin_mode = smartvideo::MarginMode::FixedPixels;
    q.margin_px = 0;
    q.min_roi_side = 64;
    smartvideo::Box tiny;
    tiny.kind = "license_plate";
    tiny.x = 0.5f;
    tiny.y = 0.5f;
    tiny.width = 10.0f / 1280.0f;
    tiny.height = 10.0f / 720.0f;
    smartvideo::RoiMapBuilder builder;
    const auto dets = builder.detections_from({tiny}, q, 1280, 720);
    check(dets.size() == 1, "one detection");
    check(dets[0].x2 - dets[0].x1 >= 64, "width at least 64");
    check(dets[0].y2 - dets[0].y1 >= 64, "height at least 64");
}

void fixed_margin_grows_the_body()
{
    smartvideo::Quality q;
    q.margin_mode = smartvideo::MarginMode::FixedPixels;
    q.margin_px = 8;
    q.min_roi_side = 0;
    smartvideo::Box b;
    b.kind = "vehicle";
    b.x = 0.25f;
    b.y = 0.25f;
    b.width = 0.10f;
    b.height = 0.10f;
    smartvideo::RoiMapBuilder builder;
    const auto dets = builder.detections_from({b}, q, 1000, 1000);
    // 0.25*1000=250, width 100 → without margin 250..350; with 8px → 242..358
    check_eq(dets[0].x1, 242, "left expanded by 8");
    check_eq(dets[0].x2, 358, "right expanded by 8");
}

void qp_slew_limits_a_jump()
{
    smartvideo::QpSlewLimiter slew;
    std::vector<smartvideo::Detection> a(1);
    a[0].track_id = 7;
    a[0].qp_delta = -2;
    slew.apply(a, 2);
    check_eq(a[0].qp_delta, -2, "first sample free");

    std::vector<smartvideo::Detection> b(1);
    b[0].track_id = 7;
    b[0].qp_delta = -8;
    slew.apply(b, 2);
    check_eq(b[0].qp_delta, -4, "jump of 6 is clamped to step 2");
}

void roi_lag_acceptance_matches_v3_thresholds()
{
    smartvideo::Box a{.x = 0.1f, .y = 0.1f, .width = 0.2f, .height = 0.2f};
    smartvideo::Box b = a;
    b.x = 0.12f;
    const float iou = smartvideo::box_iou(a, b);
    check(iou >= 0.50f, "near boxes still overlap enough");

    std::vector<smartvideo::RoiLagSample> good;
    for (int32_t i = 0; i < 100; ++i) {
        good.push_back(smartvideo::measure_roi_lag(a, b, i < 95 ? 1 : 2, 0.033));
    }
    check(smartvideo::roi_lag_acceptance(good), "P95/P99 inside §14");

    std::vector<smartvideo::RoiLagSample> bad;
    for (int32_t i = 0; i < 100; ++i) {
        bad.push_back(smartvideo::measure_roi_lag(a, b, 10, 0.033));
    }
    check(!smartvideo::roi_lag_acceptance(bad), "ten-frame lag fails acceptance");
}

} // namespace

int main()
{
    ladder_maps_kinds_to_canonical_deltas();
    detections_use_ladder_by_default();
    min_roi_side_expands_a_tiny_box();
    fixed_margin_grows_the_body();
    qp_slew_limits_a_jump();
    roi_lag_acceptance_matches_v3_thresholds();

    if (failures > 0) {
        std::fprintf(stderr, "v3_policy_test: %d failure(s)\n", failures);
        return 1;
    }
    std::puts("v3_policy_test ok");
    return 0;
}
