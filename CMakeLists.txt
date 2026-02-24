cmake_minimum_required(VERSION 3.18)
project(VMS CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(PkgConfig REQUIRED)

find_package(Protobuf CONFIG REQUIRED)
find_package(gRPC CONFIG REQUIRED)

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


set(SOURCES
    src/main.cpp

    src/media/av_deleters.h
    src/media/media_packet.h
    src/media/media_sink.h

    src/reader/rtsp_reader.cpp
    src/reader/rtsp_reader.h

    src/writer/segment_writer.cpp
    src/writer/segment_writer.h
)

add_executable(vms ${SOURCES} "src/cam.h" "src/cam.cpp")

# добавляем корневую папку src как include
target_include_directories(vms
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(vms
    ${FFMPEG_LIBRARIES}
    protobuf::libprotobuf
    gRPC::grpc
    gRPC::grpc++
)
