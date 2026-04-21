#include <vector>

#include <spdlog/sinks/rotating_file_sink.h>
#include <spdlog/sinks/stdout_color_sinks.h>
#include <spdlog/spdlog.h>

#include "logger.h"
#include "file_system/recording_storage.h"

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
  log_level minLevel)
{
  const std::filesystem::path logFile = "service.log";

  m_log_file = recording_storage::resolve_storage_base_path() / logFile;
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

  std::vector<spdlog::sink_ptr> sinks{file_sink, console_sink};
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

std::string logger::build_json_message(
  log_level level,
  const std::string& message,
  const std::source_location& location) const
{
  const auto file =
    std::filesystem::path(location.file_name())
    .filename()
    .string();

  return std::format(
    R"({{"timestamp":"{}","level":"{}","file":"{}","line":{},"function":"{}","message":"{}"}})",
    get_timestamp(),
    level_to_string(level),
    escape_json(file),
    location.line(),
    escape_json(location.function_name()),
    escape_json(message));
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
