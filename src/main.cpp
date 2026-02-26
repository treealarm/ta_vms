#include <csignal>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>
#include <thread>

#include <grpcpp/grpcpp.h>

#include "camera_control_service.h"

namespace {
std::atomic<bool> g_stop_requested{false};

void on_signal(int /*signal*/) {
  g_stop_requested = true;
}
}  // namespace

int main(int argc, char** argv) {
  std::string server_address = "0.0.0.0:50051";
  if (argc > 1) {
    server_address = argv[1];
  } else if (const char* grpc_vms_port = std::getenv("GRPC_VMS_PORT")) {
    if (*grpc_vms_port != '\0') {
      server_address = "0.0.0.0:" + std::string(grpc_vms_port);
    }
  }

  std::signal(SIGINT, on_signal);
  std::signal(SIGTERM, on_signal);

  camera_manager manager;
  camera_control_service service(manager);

  grpc::ServerBuilder builder;
  builder.AddListeningPort(server_address, grpc::InsecureServerCredentials());
  builder.RegisterService(&service);

  std::unique_ptr<grpc::Server> server(builder.BuildAndStart());
  if (!server) {
    std::cerr << "Failed to start gRPC server on " << server_address << "\n";
    return 1;
  }

  std::cout << "Camera gRPC server is listening on " << server_address << "\n";

  if (argc > 3)
  {
    std::string error;
    const bool success = manager.add_camera(argv[3],
      argv[2],
      true,
      error);
  }

  while (!g_stop_requested) {
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
  }

  manager.stop_all();
  server->Shutdown();
  server->Wait();
  return 0;
}
