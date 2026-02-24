#include <iostream>
#include <map>
#include <string>
#include <memory>
#include "cam.h"


int main(int argc, char** argv) {
  if (argc < 3) {
    std::cerr << "usage: app <rtsp_url> <camera_id>\n";
    return -1;
  }

  std::map<std::string, std::unique_ptr<cam>> m_cams;

  // === default camera ===
  const std::string default_cam_id = argv[2];
  const std::string default_rtsp_url = argv[1];

  try {
    auto c = std::make_unique<cam>(default_rtsp_url, default_cam_id);
    c->start();
    m_cams[default_cam_id] = std::move(c);
    std::cout << "Added default camera " << default_cam_id << "\n";
  }
  catch (const std::exception& e) {
    std::cerr << "Failed to start default camera: " << e.what() << "\n";
  }

  // === main command loop ===
  std::string cmd;
  while (true) {
    std::cout << "command> ";
    std::getline(std::cin, cmd);

    if (cmd == "exit" || cmd == "esc") {
      for (auto& [id, c] : m_cams)
        c->stop();
      break;
    }
    else if (cmd.rfind("new ", 0) == 0) {
      // new <cam_id> <rtsp_url>
      auto parts = cmd.substr(4);
      size_t sep = parts.find(' ');
      if (sep == std::string::npos) continue;

      std::string cam_id = parts.substr(0, sep);
      std::string url = parts.substr(sep + 1);

      try {
        auto c = std::make_unique<cam>(url, cam_id);
        c->start();
        m_cams[cam_id] = std::move(c);
        std::cout << "Added cam " << cam_id << "\n";
      }
      catch (const std::exception& e) {
        std::cerr << "Failed to add camera: " << e.what() << "\n";
      }
    }
  }

  return 0;
}