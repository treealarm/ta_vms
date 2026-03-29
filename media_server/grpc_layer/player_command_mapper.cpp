#include "player_command_mapper.h"

std::optional<player_command> map_player_command(const ::vms::control::PlayerCommand& req)
{
  using proto = vms::control::PlayerCommand;

  switch (req.cmd_case())
  {
  case proto::CmdCase::kPlay:
    return player_command{ player_command::type::play };

  case proto::CmdCase::kPause:
    return player_command{ player_command::type::pause };

  case proto::CmdCase::kStop:
    return player_command{ player_command::type::stop };

  default:
    return std::nullopt;
  }
}

vms::control::PlayerMode to_grpc_mode(player_mode mode)
{
  switch (mode)
  {
  case player_mode::playing:
    return vms::control::PLAYING;

  case player_mode::paused:
    return vms::control::PAUSED;

  case player_mode::stopped:
    return vms::control::STOPPED;

  case player_mode::undefined:
  default:
    return vms::control::PLAYER_MODE_UNSPECIFIED;
  }
}