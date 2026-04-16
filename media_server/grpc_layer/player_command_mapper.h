#pragma once

#include <optional>
#include <grpcpp/grpcpp.h>
#include "vms_control.grpc.pb.h"

#include "camera_manager.h"

std::optional<player_command> map_player_command(const vms::control::PlayerCommand& req);
vms::control::PlayerMode to_grpc_mode(player_mode mode);