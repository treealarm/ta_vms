#include "file_system/recording_storage.h"

#include <sstream>
#include <iomanip>
#include <algorithm>

using namespace std::chrono;

recording_storage::recording_storage()
  : m_base_path(resolve_storage_base_path())
{
}

std::filesystem::path recording_storage::make_day_path(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts)
{
  auto days = floor<std::chrono::days>(ts);
  year_month_day ymd{ days };

  char year[5];
  char md[6];

  std::snprintf(year, sizeof(year), "%04d", int(ymd.year()));
  std::snprintf(md, sizeof(md), "%02u-%02u",
    unsigned(ymd.month()),
    unsigned(ymd.day()));

  return m_base_path / camera_id / year / md;
}

std::filesystem::path recording_storage::shift_day(
  const std::filesystem::path& current_day,
  int offset_days)
{
  // ... / camera / YEAR / MM-DD
  auto day_str = current_day.filename().string();
  auto year_str = current_day.parent_path().filename().string();

  int Y, M, D;
  if (std::sscanf((year_str + "-" + day_str).c_str(),
    "%4d-%2d-%2d", &Y, &M, &D) != 3)
  {
    return {};
  }

  year_month_day ymd{
    year{Y},
    month{unsigned(M)},
    day{unsigned(D)}
  };

  if (!ymd.ok())
    return {};

  sys_days days{ ymd };
  days += std::chrono::days{ offset_days };

  year_month_day new_ymd{ days };

  char year[5];
  char md[6];

  std::snprintf(year, sizeof(year), "%04d", int(new_ymd.year()));
  std::snprintf(md, sizeof(md), "%02u-%02u",
    unsigned(new_ymd.month()),
    unsigned(new_ymd.day()));

  return current_day.parent_path().parent_path() / year / md;
}

std::optional<std::chrono::system_clock::time_point>
  recording_storage::parse_time(const std::string& filename)
{
  if (filename.size() < 19)
    return std::nullopt;

  // YYYY-MM-DD_HH-MM-SS
  int Y, M, D, h, m, s;

  if (std::sscanf(filename.c_str(), "%4d-%2d-%2d_%2d-%2d-%2d",
    &Y, &M, &D, &h, &m, &s) != 6)
  {
    return std::nullopt;
  }

  std::chrono::year_month_day ymd{
    std::chrono::year{Y},
    std::chrono::month{unsigned(M)},
    std::chrono::day{unsigned(D)}
  };

  if (!ymd.ok())
    return std::nullopt;

  sys_days days{ ymd };

  auto tp = days
    + hours{ h }
    + minutes{ m }
  + seconds{ s };

  // ts — локальное время из имени файла
  zoned_time zt{ "UTC", tp }; // преобразуем в UTC
  auto utc_tp = zt.get_sys_time(); // system_clock::time_point в UTC

  return utc_tp;
}

std::vector<recording_info>  recording_storage::load_day(const std::filesystem::path& day_path)
{
  std::vector<recording_info> result;

  if (!std::filesystem::exists(day_path))
    return result;

  for (const auto& entry : std::filesystem::directory_iterator(day_path))
  {
    if (!entry.is_regular_file())
      continue;

    std::string ext = entry.path().extension().string();
    std::transform(ext.begin(), ext.end(), ext.begin(), ::tolower);

    if (std::none_of(k_allowed_exts.begin(), k_allowed_exts.end(),
      [&](std::string_view e) { return ext == e; }))
    {
      continue;
    }

    auto ts = parse_time(entry.path().filename().string());
    if (!ts)
      continue;

    result.push_back({
      entry.path(),
      *ts
      });
  }

  std::sort(result.begin(), result.end(),
    [](const auto& a, const auto& b)
    {
      return a.start_time < b.start_time;
    });

  return result;
}

std::optional<recording_info> recording_storage::find_nearest(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts)
{
  // 1️ быстрый путь: проверка точного файла
  auto abs_path = get_absolute_path(camera_id, ts);

  if (std::filesystem::exists(abs_path))
  {
    return recording_info
    {
      abs_path,
      ts
    };
  }

  // 2️ fallback — старая логика
  int32_t nearest;
  auto vec = get_recordings(camera_id, ts, 1, nearest);

  if (vec.empty())
    return std::nullopt;

  return vec.front();
}

std::vector<recording_info> recording_storage::get_recordings(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts,
  uint32_t limit,
  int32_t& nearestIndex
)
{
  const size_t half = static_cast<size_t>(limit / 2);
  const size_t right_target = half + static_cast<size_t>(limit % 2);
  const int32_t maxDays = 30;

  auto day_path = make_day_path(camera_id, ts);

  // 1️⃣ грузим текущий день
  auto curr = load_day(day_path);

  if (curr.empty()) {
    // если день пуст — будем искать ближайший вообще
    curr = {};
  }

  // предполагаем, что load_day уже возвращает отсортированные данные
  auto it = std::lower_bound(curr.begin(), curr.end(), ts,
    [](const recording_info& rec, const auto& value) {
      return rec.start_time < value;
    });

  size_t pos = static_cast<size_t>(std::distance(curr.begin(), it));

  // ближайший внутри дня (если есть)
  if (!curr.empty())
  {
    if (pos >= curr.size())
    {
      pos--; // только последний доступен
    }
    else if (pos > 0)
    {
      auto d_curr = std::chrono::duration_cast<std::chrono::milliseconds>(
        curr[pos].start_time - ts);

      auto d_prev = std::chrono::duration_cast<std::chrono::milliseconds>(
        ts - curr[pos - 1].start_time);

      auto threshold = get_segment_duration();

      // ключевая логика:
      // если предыдущий не сильно дальше — берём его
      if (d_prev <= d_curr + threshold)
      {
        pos--;
      }
    }
  }

  std::vector<recording_info> left;
  std::vector<recording_info> right;

  // 2️⃣ заполняем из текущего дня
  for (size_t i = pos; i > 0 && left.size() < half; --i)
    left.push_back(curr[i - 1]);

  for (size_t i = pos; i < curr.size() && right.size() < right_target; ++i)
    right.push_back(curr[i]);

  // 3️⃣ догружаем слева
  int dayOffset = 1;
  while (left.size() < half && dayOffset <= maxDays) {
    auto prev = load_day(shift_day(day_path, -dayOffset));

    for (size_t i = prev.size(); i > 0 && left.size() < half; --i)
      left.push_back(prev[i - 1]);

    dayOffset++;
  }

  // 4️⃣ догружаем справа
  dayOffset = 1;
  while (right.size() < right_target && dayOffset <= maxDays) {
    auto next = load_day(shift_day(day_path, dayOffset));

    for (size_t i = 0; i < next.size() && right.size() < right_target; ++i)
      right.push_back(next[i]);

    dayOffset++;
  }

  // 5️⃣ собираем результат
  std::reverse(left.begin(), left.end());

  std::vector<recording_info> result;
  result.reserve(left.size() + right.size());

  result.insert(result.end(), left.begin(), left.end());
  result.insert(result.end(), right.begin(), right.end());

  // 6️⃣ индекс ближайшего
  nearestIndex = static_cast<int32_t>(left.size());

  if (!result.empty() && nearestIndex >= static_cast<int32_t>(result.size()))
    nearestIndex = static_cast<int32_t>(result.size()) - 1;

  if (result.empty())
    nearestIndex = -1;


  return result;
}

std::filesystem::path recording_storage::make_segment_path(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts)
{
  auto days = floor<std::chrono::days>(ts);
  year_month_day ymd{ days };

  char year[5];
  char md[6];

  std::snprintf(year, sizeof(year), "%04d", int(ymd.year()));
  std::snprintf(md, sizeof(md), "%02u-%02u",
    unsigned(ymd.month()),
    unsigned(ymd.day()));

  // имя файла = timestamp
  auto ts_sec = floor<std::chrono::seconds>(ts);
  std::string filename = std::format("{:%Y-%m-%d_%H-%M-%S}{}", 
    ts_sec, 
    file_ext().string());

  return  std::filesystem::path(camera_id) / year / md / filename;
}

std::filesystem::path recording_storage::get_absolute_path(const std::string& camera_id,
  std::chrono::system_clock::time_point ts)
{
  return resolve_storage_base_path()/make_segment_path(camera_id, ts);
}

std::filesystem::path recording_storage::file_ext()
{
  if (const char* value = std::getenv("VMS_FILE_EXT")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return k_ts_ext;
}

std::filesystem::path recording_storage::resolve_storage_base_path()
{
  if (const char* value = std::getenv("VMS_STORAGE_BASE_PATH")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return "records";
}

std::optional<recording_info> recording_storage::find_offset(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts,
  int offset)
{
  if (offset == 0)
    return find_nearest(camera_id, ts);

  auto ts_sec = std::chrono::floor<std::chrono::seconds>(ts);

  int direction = (offset > 0) ? 1 : -1;
  int remaining = std::abs(offset);

  auto day_path = make_day_path(camera_id, ts_sec);

  const int maxDays = 30;

  for (int dayShift = 0; dayShift <= maxDays; ++dayShift) 
  {
    auto current_day = shift_day(day_path, dayShift * direction);

    if (!std::filesystem::exists(current_day))
      continue;

    std::vector<recording_info> candidates;

    for (const auto& entry : std::filesystem::directory_iterator(current_day)) 
    {
      if (!entry.is_regular_file())
        continue;

      if (entry.path().extension() != file_ext())
        continue;

      auto file_ts = parse_time(entry.path().filename().string());
      if (!file_ts)
        continue;

      // фильтр только для первого дня
      if (dayShift == 0) 
      {
        if (direction > 0 && *file_ts <= ts_sec)
          continue;
        if (direction < 0 && *file_ts >= ts_sec)
          continue;
      }

      candidates.push_back(
        {
        entry.path(),
        *file_ts
        });
    }

    if (candidates.empty())
      continue;

    // сортируем только текущий день (локально, не глобально)
    std::sort(candidates.begin(), candidates.end(),
      [](const auto& a, const auto& b) 
      {
        return a.start_time < b.start_time;
      });

    if (direction < 0)
      std::reverse(candidates.begin(), candidates.end());

    if (remaining <= static_cast<int>(candidates.size())) 
    {
      return candidates[remaining - 1];
    }

    remaining -= static_cast<int>(candidates.size());
  }

  return std::nullopt;
}
