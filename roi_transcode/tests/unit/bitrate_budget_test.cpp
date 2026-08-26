// Outer bitrate controller for TZ v2.0 §15. Pure arithmetic -- no codec.

#include "bitrate_budget.h"

#include <cmath>
#include <cstdio>
#include <string>

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

// Inactive until a target is set.
void inactive_without_a_target()
{
    const smartvideo::BitrateBudget b;
    check(!b.active(), "default budget is inactive");
    check_eq(b.background_extra(), 0, "no background extra");
    check_eq(b.roi_soften(), 0, "no ROI soften");
}

// Overshoot raises background first; ROI softens only after background is maxed.
void overshoot_crushes_background_before_roi()
{
    smartvideo::RateControlPolicy policy;
    policy.target_bitrate = 1'000'000; // 1 Mbps
    policy.max_average_overshoot = 0.1f;
    smartvideo::BitrateBudget b(policy);
    check(b.active(), "a target activates the budget");

    // 2 Mbps average: well above the 1.1 Mbps ceiling. Feed 0.25 s steps so each
    // observe can take one controller step after the 0.5 s warm-up.
    for (int32_t i = 0; i < 4; ++i) {
        // 2e6 bits/s * 0.25 s = 500000 bits = 62500 bytes
        b.observe(62500, 0.25);
    }
    check(b.background_extra() > 0, "overshoot raises background QP");
    check_eq(b.roi_soften(), 0, "ROI still untouched while background can climb");

    // Drive background to the cap, then further overshoot should soften ROI.
    for (int32_t i = 0; i < 80; ++i) {
        b.observe(62500, 0.25);
    }
    check_eq(b.background_extra(), 20, "background extra saturates");
    check(b.roi_soften() > 0, "ROI softens only after background is maxed");
}

// Undershoot walks the extras back down, ROI first.
void undershoot_releases_roi_before_background()
{
    smartvideo::RateControlPolicy policy;
    policy.target_bitrate = 1'000'000;
    smartvideo::BitrateBudget b(policy);
    for (int32_t i = 0; i < 100; ++i) {
        b.observe(62500, 0.25); // hot
    }
    check(b.roi_soften() > 0 && b.background_extra() > 0, "precondition: both extras up");

    const int32_t soft_before = b.roi_soften();
    // 0.5 Mbps -- under the 0.9 Mbps floor.
    for (int32_t i = 0; i < 8; ++i) {
        b.observe(15625, 0.25);
    }
    check(b.roi_soften() < soft_before, "undershoot releases ROI soften first");
}

// apply() only moves the background; soften leaves critical detections alone.
void apply_and_soften_respect_critical_objects()
{
    smartvideo::RateControlPolicy policy;
    policy.target_bitrate = 1'000'000;
    policy.critical_importance = 0.95f;
    smartvideo::BitrateBudget b(policy);
    for (int32_t i = 0; i < 100; ++i) {
        b.observe(62500, 0.25);
    }
    check(b.background_extra() > 0 && b.roi_soften() > 0, "precondition: extras up");

    smartvideo::Quality q;
    q.background_qp_delta = 6;
    const auto adjusted = b.apply(q);
    check(adjusted.background_qp_delta == 6 + b.background_extra(),
        "apply raises background by the extra");
    check(q.background_qp_delta == 6, "apply does not mutate the original");

    std::vector<smartvideo::Detection> dets(2);
    dets[0].importance = 1.00f; // plate
    dets[0].qp_delta = -12;
    dets[0].pad_qp_delta = -6;
    dets[1].importance = 0.60f; // vehicle
    dets[1].qp_delta = -6;
    dets[1].pad_qp_delta = -3;
    b.soften_detections(dets);
    check_eq(dets[0].qp_delta, -12, "a critical plate is not softened");
    check(dets[1].qp_delta > -6,
        "a non-critical vehicle is softened (may go positive under pressure)");
}

} // namespace

int main()
{
    inactive_without_a_target();
    overshoot_crushes_background_before_roi();
    undershoot_releases_roi_before_background();
    apply_and_soften_respect_critical_objects();

    if (failures > 0) {
        std::fprintf(stderr, "bitrate_budget_test: %d failure(s)\n", failures);
        return 1;
    }
    std::puts("bitrate_budget_test ok");
    return 0;
}
