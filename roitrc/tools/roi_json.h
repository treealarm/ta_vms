#pragma once

// roi.json -> the vocabulary sve takes. Deliberately outside the library:
// embedding the encoder should not drag a JSON library along, and only the CLI
// and the service ever read a file.

#include "ta_roienc/types.h"

#include <string>
#include <vector>

struct RoiJson {
    ta_roienc::Quality quality;
    std::vector<ta_roienc::Box> boxes;
};

RoiJson load_roi_json(const std::string& path);
