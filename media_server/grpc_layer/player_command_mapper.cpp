#include "player_command_mapper.h"

std::optional<player_command> map_player_command(const ::vms::control::PlayerCommand& req)
{
  using proto = vms::control::PlayerCommand;

  switch (req.cmd_case())
  {
  case proto::CmdCase::kPlay:
  {
    player_command cmd{};
    cmd.cmd = player_command::type::play;
    cmd.speed = req.play().has_speed() ? req.play().speed() : 1.0;
    return cmd;
  }

  case proto::CmdCase::kPause:
  {
    player_command cmd{};
    cmd.cmd = player_command::type::pause;
    return cmd;
  }

  case proto::CmdCase::kStop:
  {
    player_command cmd{};
    cmd.cmd = player_command::type::stop;
    return cmd;
  }

  case proto::CmdCase::kStep:
  {
    player_command cmd{};
    cmd.cmd = player_command::type::step;
    cmd.step_frames = req.step().step_frames();
    return cmd;
  }

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