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
  using namespace std::chrono;

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

  using namespace std::chrono;

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

  using namespace std::chrono;

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

  return tp;
}

std::vector<recording_info>
  recording_storage::load_day(const std::filesystem::path& day_path)
{
  std::vector<recording_info> result;

  if (!std::filesystem::exists(day_path))
    return result;

  for (const auto& entry : std::filesystem::directory_iterator(day_path))
  {
    if (!entry.is_regular_file())
      continue;

    if (entry.path().extension() != resolve_file_ext())
      continue;

    auto ts = parse_time(entry.path().filename().string());
    if (!ts)
      continue;

    result.push_back({
      entry.path(),
      *ts,
      std::nullopt
      });
  }

  std::sort(result.begin(), result.end(),
    [](const auto& a, const auto& b)
    {
      return a.start_time < b.start_time;
    });

  return result;
}

std::optional<recording_info>
  recording_storage::find_nearest(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts)
{
  int32_t nearest;
  auto vec = get_recordings(camera_id, ts, 1, nearest);
  if (vec.empty())
    return std::nullopt;

  return vec.front();
}

std::vector<recording_info>
recording_storage::get_recordings(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts,
  uint32_t limit,
  int32_t& nearestIndex // ← индекс ближайшей записи
)
{
  std::vector<recording_info> files;

  auto day_path = make_day_path(camera_id, ts);

  // 1️⃣ грузим текущий день
  auto current = load_day(day_path);
  files.insert(files.end(), current.begin(), current.end());

  // 2️⃣ если мало — подгружаем соседние дни
  int offset = 1;
  while (files.size() < limit * 2) // запас для выбора nearest
  {
    auto prev = load_day(shift_day(day_path, -offset));
    auto next = load_day(shift_day(day_path, offset));

    if (prev.empty() && next.empty())
      break;

    files.insert(files.end(), prev.begin(), prev.end());
    files.insert(files.end(), next.begin(), next.end());

    offset++;
  }

  if (files.empty()) {
    nearestIndex = -1;
    return {};
  }

  // 3️⃣ сортировка по времени
  std::sort(files.begin(), files.end(),
    [](const auto& a, const auto& b) { return a.start_time < b.start_time; });

  // 4️⃣ находим ближайшую запись к ts
  auto it = std::min_element(files.begin(), files.end(),
    [ts](const auto& a, const auto& b) {
      auto da = std::chrono::duration_cast<std::chrono::milliseconds>(a.start_time - ts).count();
      auto db = std::chrono::duration_cast<std::chrono::milliseconds>(b.start_time - ts).count();
      return std::abs(da) < std::abs(db);
    });

  nearestIndex = static_cast<int32_t>(std::distance(files.begin(), it));

  // 5️⃣ формируем результат: просто первые `limit` записей вокруг nearestIndex
  // если нужно, можно взять максимум limit записей, начиная с nearestIndex ± limit/2
  int start = std::max(0, nearestIndex - static_cast<int32_t>(limit / 2));
  int end = std::min<int32_t>(start + limit, static_cast<int32_t>(files.size()));

  // корректируем start, если end обрезан
  start = std::max(0, end - static_cast<int32_t>(limit));

  std::vector<recording_info> result(files.begin() + start, files.begin() + end);

  // пересчитываем nearestIndex внутри result
  nearestIndex -= start;

  return result;
}

std::filesystem::path recording_storage::make_segment_path(
  const std::string& camera_id,
  std::chrono::system_clock::time_point ts)
{
  using namespace std::chrono;

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
    resolve_file_ext().string());

  return  std::filesystem::path(camera_id) / year / md / filename;
}

std::filesystem::path recording_storage::get_absolute_path(const std::string& camera_id,
  std::chrono::system_clock::time_point ts)
{
  return resolve_storage_base_path()/make_segment_path(camera_id, ts);
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

std::filesystem::path recording_storage::resolve_file_ext()
{
  if (const char* value = std::getenv("VMS_FILE_EXT")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return ".mkv";
}