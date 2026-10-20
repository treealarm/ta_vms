#pragma once

#include <filesystem>
#include <string>
#include <string_view>

// Every std::string in this service is UTF-8: protobuf, JSON, the logs, and what FFmpeg takes for a
// file name. std::filesystem::path instead converts narrow strings through the ANSI code page on
// Windows, so a storage folder named in Cyrillic turned into question marks, or into an exception.
// These cross that line explicitly; on Linux they are plain copies.

inline std::filesystem::path path_from_utf8(std::string_view utf8)
{
  return std::filesystem::path(
    std::u8string_view(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size()));
}

inline std::string utf8_string(const std::filesystem::path& path)
{
  const auto u8 = path.u8string();
  return std::string(reinterpret_cast<const char*>(u8.data()), u8.size());
}

// For a path that leaves the process as data — clips.file_path, an export path: separated by '/'
// on every platform, so that a reader can split it without knowing where it was written.
inline std::string utf8_generic_string(const std::filesystem::path& path)
{
  const auto u8 = path.generic_u8string();
  return std::string(reinterpret_cast<const char*>(u8.data()), u8.size());
}
