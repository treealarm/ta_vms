#include "grpc_layer/log_service.h"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <format>
#include <iomanip>
#include <sstream>
#include <optional>
#include <regex>
#include <string>
#include <vector>
#include <chrono>
#include <google/protobuf/util/json_util.h>


#include "file_system/recording_storage.h"
#include "logger/logger.h"

namespace
{
  struct parsed_log_entry
  {
    google::protobuf::Timestamp timestamp;
    std::string level;
    std::string file;
    std::string function;
    std::string message;
  };

  bool parse_iso8601_utc(const std::string& ts,
    google::protobuf::Timestamp* out)
  {
    if (!out || ts.empty() || ts.back() != 'Z')
      return false;

    std::string core = ts.substr(0, ts.size() - 1);
    std::string datetime = core;
    std::string frac;

    const auto dot_pos = core.find('.');
    if (dot_pos != std::string::npos)
    {
      datetime = core.substr(0, dot_pos);
      frac = core.substr(dot_pos + 1);
    }

    std::tm tm{};
    std::istringstream ss(datetime);
    ss >> std::get_time(&tm, "%Y-%m-%dT%H:%M:%S");

    if (ss.fail())
      return false;

    using namespace std::chrono;

    auto tp = sys_days{
        year{tm.tm_year + 1900} /
        month{static_cast<unsigned>(tm.tm_mon + 1)} /
        day{static_cast<unsigned>(tm.tm_mday)}
    }
      + hours{ tm.tm_hour }
      + minutes{ tm.tm_min }
    + seconds{ tm.tm_sec };

    auto epoch_seconds = duration_cast<seconds>(
      tp.time_since_epoch()
    ).count();

    int nanos = 0;

    if (!frac.empty())
    {
      std::string ns = frac.substr(0, 9);

      while (ns.size() < 9)
        ns.push_back('0');

      try
      {
        nanos = std::stoi(ns);
      }
      catch (...)
      {
        return false;
      }
    }

    out->set_seconds(epoch_seconds);
    out->set_nanos(nanos);

    return true;
  }

  bool parse_log_line(const std::string& line, parsed_log_entry* out)
  {
    if (!out)
    {
      return false;
    }

    vms::logger::LogEntry proto_entry;

    google::protobuf::util::JsonParseOptions options;
    options.ignore_unknown_fields = true;

    auto status =
      google::protobuf::util::JsonStringToMessage(
        line,
        &proto_entry,
        options
      );

    if (!status.ok())
    {
      return false;
    }

    out->timestamp = proto_entry.timestamp();
    out->level = proto_entry.level();
    out->file = proto_entry.file();
    out->function = proto_entry.function();
    out->message = proto_entry.message();

    return true;
  }

  int compare_timestamps(const google::protobuf::Timestamp& lhs, const google::protobuf::Timestamp& rhs)
  {
    if (lhs.seconds() < rhs.seconds())
    {
      return -1;
    }
    if (lhs.seconds() > rhs.seconds())
    {
      return 1;
    }
    if (lhs.nanos() < rhs.nanos())
    {
      return -1;
    }
    if (lhs.nanos() > rhs.nanos())
    {
      return 1;
    }
    return 0;
  }

  std::string to_lower_copy(std::string value)
  {
    std::transform(value.begin(), value.end(), value.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
    return value;
  }

  bool contains_case_insensitive(const std::string& haystack, const std::string& needle)
  {
    if (needle.empty())
    {
      return true;
    }

    const std::string haystack_lower = to_lower_copy(haystack);
    const std::string needle_lower = to_lower_copy(needle);
    return haystack_lower.find(needle_lower) != std::string::npos;
  }

  bool matches_filters(
    const parsed_log_entry& entry,
    const google::protobuf::Timestamp& from,
    const std::optional<google::protobuf::Timestamp>& to,
    const std::optional<std::string>& level_filter,
    const std::optional<std::string>& source_filter)
  {
    if (compare_timestamps(entry.timestamp, from) < 0)
    {
      return false;
    }

    if (to && compare_timestamps(entry.timestamp, *to) > 0)
    {
      return false;
    }

    if (level_filter && !contains_case_insensitive(entry.level, *level_filter))
    {
      return false;
    }

    if (source_filter)
    {
      const bool source_match =
        contains_case_insensitive(entry.file, *source_filter) ||
        contains_case_insensitive(entry.function, *source_filter) ||
        contains_case_insensitive(entry.message, *source_filter);
      if (!source_match)
      {
        return false;
      }
    }

    return true;
  }

  void read_log_file(const std::filesystem::path& file,
    const google::protobuf::Timestamp& from,
    const std::optional<google::protobuf::Timestamp>& to,
    const std::optional<std::string>& level_filter,
    const std::optional<std::string>& source_filter,
    std::size_t limit,
    vms::logger::LogResponse* response)
  {
    if (!response || !std::filesystem::exists(file))
    {
      return;
    }

    std::ifstream stream(file);
    if (!stream.is_open())
    {
      logger::instance().warning(std::format("log_service: failed to open log file '{}'", file.string()));
      return;
    }

    std::string line;
    while (std::getline(stream, line))
    {
      parsed_log_entry entry{};
      if (!parse_log_line(line, &entry))
      {
        continue;
      }

      if (!matches_filters(entry, from, to, level_filter, source_filter))
      {
        continue;
      }

      if (response->logs_size() >= static_cast<int>(limit))
      {
        response->set_has_more(true);
        return;
      }

      auto* out = response->add_logs();
      *out->mutable_timestamp() = entry.timestamp;
      out->set_level(entry.level);
      out->set_file(entry.file);
      out->set_function(entry.function);
      out->set_message(entry.message);
    }
  }

  std::vector<std::filesystem::path> resolve_log_files(const std::filesystem::path& base_dir)
  {
    std::vector<std::pair<int, std::filesystem::path>> indexed_files;
    const std::filesystem::path active_log = base_dir / "service.log";
    if (std::filesystem::exists(active_log))
    {
      indexed_files.emplace_back(0, active_log);
    }

    if (!std::filesystem::exists(base_dir) || !std::filesystem::is_directory(base_dir))
    {
      std::vector<std::filesystem::path> files;
      for (const auto& [index, path] : indexed_files)
      {
        (void)index;
        files.push_back(path);
      }
      return files;
    }

    static const std::regex rotation_pattern(R"(^service(?:\.log\.(\d+)|\.(\d+)\.log)$)");
    for (const auto& entry : std::filesystem::directory_iterator(base_dir))
    {
      if (!entry.is_regular_file())
      {
        continue;
      }

      const std::string filename = entry.path().filename().string();
      std::smatch match;
      if (!std::regex_match(filename, match, rotation_pattern))
      {
        continue;
      }

      const std::string first = match[1].str();
      const std::string second = match[2].str();
      const std::string index_str = first.empty() ? second : first;
      if (index_str.empty())
      {
        continue;
      }

      try
      {
        indexed_files.emplace_back(std::stoi(index_str), entry.path());
      }
      catch (...)
      {
        continue;
      }
    }

    std::sort(indexed_files.begin(), indexed_files.end(),
      [](const auto& lhs, const auto& rhs)
      {
        return lhs.first > rhs.first;
      });

    std::vector<std::filesystem::path> files;
    files.reserve(indexed_files.size());
    for (const auto& [index, path] : indexed_files)
    {
      (void)index;
      files.push_back(path);
    }

    return files;
  }
} // namespace

grpc::Status log_service::GetLogs(grpc::ServerContext* /*context*/,
  const vms::logger::LogRequest* request,
  vms::logger::LogResponse* response)
{
  if (!request || !response)
  {
    return grpc::Status(grpc::StatusCode::INVALID_ARGUMENT, "empty request/response");
  }

  const std::size_t limit = request->limit() == 0 ? 100 : request->limit();
  constexpr std::size_t kMaxLimit = 10000;
  if (limit > kMaxLimit)
  {
    return grpc::Status(grpc::StatusCode::INVALID_ARGUMENT,
      std::format("limit must be <= {}", kMaxLimit));
  }

  const google::protobuf::Timestamp from = request->from();
  std::optional<google::protobuf::Timestamp> to;
  if (request->has_to())
  {
    to = request->to();
    if (compare_timestamps(*to, from) < 0)
    {
      return grpc::Status(grpc::StatusCode::INVALID_ARGUMENT, "'to' must be greater than or equal to 'from'");
    }
  }

  std::optional<std::string> level_filter;
  if (request->has_level() && !request->level().empty())
  {
    level_filter = request->level();
  }

  std::optional<std::string> source_filter;
  if (request->has_source() && !request->source().empty())
  {
    source_filter = request->source();
  }

  response->clear_logs();
  response->set_has_more(false);

  const auto base = recording_storage::resolve_storage_base_path();
  const auto log_files = resolve_log_files(base);
  for (const auto& file : log_files)
  {
    read_log_file(file, from, to, level_filter, source_filter, limit, response);
    if (response->has_more())
    {
      break;
    }
  }

  return grpc::Status::OK;
}
