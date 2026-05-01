#include "file_system/recording_storage.h"

namespace {

/** Subpath check without platform #ifdef: generic_string() uses '/' per [fs.path.generic]. */
bool path_is_within_root(const std::filesystem::path& candidate_norm,
  const std::filesystem::path& base_norm)
{
  const std::string cand = candidate_norm.lexically_normal().generic_string();
  const std::string base = base_norm.lexically_normal().generic_string();

  if (base.empty())
    return false;

  if (cand.size() < base.size())
    return false;

  if (cand.compare(0, base.size(), base) != 0)
    return false;

  if (cand.size() == base.size())
    return true;

  constexpr char k_sep = '/';
  return cand[base.size()] == k_sep;
}

}

std::optional<std::filesystem::path>
recording_storage::resolve_archive_relative_path(const std::string& relative_path) const
{
  if (relative_path.empty())
    return std::nullopt;

  if (relative_path.find("..") != std::string::npos)
    return std::nullopt;

  std::filesystem::path rel{relative_path};
  if (rel.is_absolute())
    return std::nullopt;

  std::error_code ec;
  const auto combined = m_base_path / rel;
  const auto canon_candidate = std::filesystem::weakly_canonical(combined, ec);
  if (ec)
    return std::nullopt;

  const auto canon_base = std::filesystem::weakly_canonical(m_base_path, ec);
  if (ec)
    return std::nullopt;

  if (!path_is_within_root(canon_candidate, canon_base))
    return std::nullopt;

  return canon_candidate;
}

bool recording_storage::stat_archive_file(const std::filesystem::path& absolute_path,
  std::uint64_t& size_bytes,
  std::string& error) const
{
  std::error_code ec;
  if (!std::filesystem::exists(absolute_path, ec))
  {
    error = ec.message();
    return false;
  }

  const auto sz = std::filesystem::file_size(absolute_path, ec);
  if (ec)
  {
    error = ec.message();
    return false;
  }

  size_bytes = static_cast<std::uint64_t>(sz);
  return true;
}
