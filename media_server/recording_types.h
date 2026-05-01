#pragma once

#include <chrono>
#include <cstdint>

enum class recording_policy_mode : std::uint8_t
{
  off,
  continuous,
  prealarm
};

struct recording_policy_spec
{
  recording_policy_mode mode = recording_policy_mode::off;
  std::chrono::seconds segment_len{60};
  bool ingest_retained = true;
};
