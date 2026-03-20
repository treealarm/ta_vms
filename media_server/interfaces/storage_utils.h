#pragma once

#include <cstdlib>
#include <string>

inline std::string resolve_storage_base_path()
{
  if (const char* value = std::getenv("VMS_STORAGE_BASE_PATH")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return "records";
}