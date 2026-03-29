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