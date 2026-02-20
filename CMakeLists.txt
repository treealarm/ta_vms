cmake_minimum_required(VERSION 3.18)
project(VMS CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(PkgConfig REQUIRED)

pkg_check_modules(FFMPEG REQUIRED
    libavformat
    libavcodec
    libavutil
)

include_directories(
    ${FFMPEG_INCLUDE_DIRS}
)

link_directories(
    ${FFMPEG_LIBRARY_DIRS}
)

add_executable(vms main.cpp)

target_link_libraries(vms
    ${FFMPEG_LIBRARIES}
)
