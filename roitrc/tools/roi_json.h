#pragma once

// roi.json -> the vocabulary ta_roienc takes. Deliberately outside the library:
// embedding the encoder should not drag a JSON library along, and only this
// client ever reads a file.

#include "ta_roienc/basics.h"

#include <string>
#include <vector>

struct RoiJson {
    ta_roienc::QualityPlan quality;
    std::vector<ta_roienc::Box> boxes;
};

RoiJson load_roi_json(const std::string& path);
