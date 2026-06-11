#include <vector>

#include <spdlog/sinks/rotating_file_sink.h>
#include <spdlog/sinks/stdout_color_sinks.h>
#include <spdlog/spdlog.h>
#include <google/protobuf/util/json_util.h>
#include <google/protobuf/timestamp.pb.h>
#include "logger.grpc.pb.h"

#include "logger.h"

namespace
{
  constexpr std::size_t kMaxLogFileSizeBytes = 10 * 1024 * 1024; // 10 MB
  constexpr std::size_t kMaxLogFiles = 5;
}

logger& logger::instance()
{
  static logger instance;
  return instance;
}

void logger::init(
  const std::string& app_id,
  log_level minLevel)
{
  const char* log_path_env = std::getenv("VMS_LOG_PATH");
  if (!log_path_env || log_path_env[0] == '\0')
    throw std::runtime_error("Required environment variable 'VMS_LOG_PATH' is not set");

  m_log_file = std::filesystem::path(log_path_env) / app_id / (app_id + ".log");
  m_min_level = minLevel;

  auto file_sink =
    std::make_shared<spdlog::sinks::rotating_file_sink_mt>(
      m_log_file.string(),
      kMaxLogFileSizeBytes,
      kMaxLogFiles,
      true);
  auto console_sink =
    std::make_shared<spdlog::sinks::stdout_color_sink_mt>();

  file_sink->set_pattern("%v");
  console_sink->set_pattern("%v");

  std::vector<spdlog::sink_ptr> sinks{ file_sink, console_sink };
  m_logger = std::make_shared<spdlog::logger>("service_logger", sinks.begin(), sinks.end());
  m_logger->set_level(to_spdlog_level(m_min_level));
  m_logger->flush_on(spdlog::level::trace);
}

void logger::log(
  log_level level,
  const std::string& message,
  const std::source_location location)
{
  if (level < m_min_level)
    return;

  if (!m_logger)
    return;

  const auto json_message =
    build_json_message(level, message, location);
  m_logger->log(
    to_spdlog_level(level),
    json_message);
}

void logger::debug(
  const std::string& message,
  const std::source_location location)
{
  log(log_level::debug, message, location);
}

void logger::info(
  const std::string& message,
  const std::source_location location)
{
  log(log_level::info, message, location);
}

void logger::warning(
  const std::string& message,
  const std::source_location location)
{
  log(log_level::warning, message, location);
}

void logger::error(
  const std::string& message,
  const std::source_location location)
{
  log(log_level::error, message, location);
}

//std::string logger::build_json_message(
//  log_level level,
//  const std::string& message,
//  const std::source_location& location) const
//{
//  const auto file =
//    std::filesystem::path(location.file_name())
//    .filename()
//    .string();
//
//  return std::format(
//    R"({{"timestamp":"{}","level":"{}","file":"{}","line":{},"function":"{}","message":"{}"}})",
//    get_timestamp(),
//    level_to_string(level),
//    escape_json(file),
//    location.line(),
//    escape_json(location.function_name()),
//    escape_json(message));
//}

#include <google/protobuf/util/json_util.h>
#include <google/protobuf/timestamp.pb.h>

std::string logger::build_json_message(
  log_level level,
  const std::string& message,
  const std::source_location& location) const
{
  vms::logger::LogEntry entry;

  // timestamp
  google::protobuf::Timestamp ts;
  ts.set_seconds(std::time(nullptr));
  ts.set_nanos(0);

  // если нужен миллисекундный timestamp — можно через chrono,
  // но для примера оставим так

  *entry.mutable_timestamp() = ts;

  // other fields
  entry.set_level(level_to_string(level));

  entry.set_file(
    std::filesystem::path(location.file_name())
    .filename()
    .string());

  entry.set_function(location.function_name());
  entry.set_message(message);

  google::protobuf::util::JsonPrintOptions options;

  // компактный JSON в одну строку
  options.add_whitespace = false;

  // сохранять оригинальные proto field names:
  // timestamp, level, file...
  options.preserve_proto_field_names = true;

  // если нужны поля даже с default values
  options.always_print_fields_with_no_presence = false;

  std::string json;

  auto status =
    google::protobuf::util::MessageToJsonString(
      entry,
      &json,
      options);

  if (!status.ok())
  {
    // fallback чтобы логгер не упал
    return std::format(
      R"({{"level":"ERROR","message":"failed to serialize log entry"}})");
  }

  return json;
}

std::string logger::escape_json(const std::string& text) const
{
  std::string escaped;
  escaped.reserve(text.size());

  for (const char ch : text)
  {
    switch (ch)
    {
    case '\\': escaped += "\\\\"; break;
    case '"': escaped += "\\\""; break;
    case '\n': escaped += "\\n"; break;
    case '\r': escaped += "\\r"; break;
    case '\t': escaped += "\\t"; break;
    default: escaped += ch; break;
    }
  }

  return escaped;
}

std::string logger::get_timestamp() const
{
  const auto now =
    std::chrono::floor<std::chrono::milliseconds>(
      std::chrono::system_clock::now());

  return std::format(
    "{:%FT%TZ}",
    now);
}

const char* logger::level_to_string(log_level level) const
{
  switch (level)
  {
  case log_level::trace: return "TRACE";
  case log_level::debug: return "DEBUG";
  case log_level::info: return "INFO";
  case log_level::warning: return "WARNING";
  case log_level::error: return "ERROR";
  case log_level::critical: return "CRITICAL";
  default: return "UNKNOWN";
  }
}

spdlog::level::level_enum logger::to_spdlog_level(log_level level) const
{
  switch (level)
  {
  case log_level::trace: return spdlog::level::trace;
  case log_level::debug: return spdlog::level::debug;
  case log_level::info: return spdlog::level::info;
  case log_level::warning: return spdlog::level::warn;
  case log_level::error: return spdlog::level::err;
  case log_level::critical: return spdlog::level::critical;
  default: return spdlog::level::info;
  }
}
