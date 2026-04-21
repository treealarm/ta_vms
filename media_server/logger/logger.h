#pragma once

#include <filesystem>
#include <memory>
#include <source_location>
#include <string>

#include <spdlog/logger.h>

enum class log_level
{
  trace,
  debug,
  info,
  warning,
  error,
  critical
};

class logger
{
public:
  static logger& instance();

  void init(
    log_level min_level = log_level::info);

  void log(
    log_level level,
    const std::string& message,
    const std::source_location location =
    std::source_location::current());

  void info(
    const std::string& message,
    const std::source_location location =
    std::source_location::current());

  void warning(
    const std::string& message,
    const std::source_location location =
    std::source_location::current());

  void error(
    const std::string& message,
    const std::source_location location =
    std::source_location::current());

private:
  logger() = default;

  std::string build_json_message(
    log_level level,
    const std::string& message,
    const std::source_location& location) const;
  std::string escape_json(const std::string& text) const;
  std::string get_timestamp() const;
  const char* level_to_string(log_level level) const;
  spdlog::level::level_enum to_spdlog_level(log_level level) const;
private:
  std::filesystem::path m_log_file = "service.log";
  log_level m_min_level = log_level::info;
  std::shared_ptr<spdlog::logger> m_logger;
};