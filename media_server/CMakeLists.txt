cmake_minimum_required(VERSION 3.22)
project(VMS CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(PkgConfig REQUIRED)
find_package(Protobuf REQUIRED)
find_package(gRPC REQUIRED)

pkg_check_modules(FFMPEG REQUIRED
    libavformat
    libavcodec
    libavutil
    libswscale
    libswresample
)

set(VMS_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/vms_control.proto)

set(DAPR_RUNTIME_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/dapr/proto/runtime/v1/dapr.proto)
set(DAPR_COMMON_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/dapr/proto/common/v1/common.proto)

set(SOURCES
    main.cpp
    cam.cpp
    arch_player.cpp
    arch_player.h
    camera_manager.cpp
    camera_control_service.cpp
    dapr_publisher.cpp
    dapr_publisher.h

    media/av_deleters.h
    media/media_packet.h
    media/media_sink.h
    media/sink_container.h

    reader/rtsp_reader.cpp
    reader/rtsp_reader.h
    reader/file_reader.cpp
    reader/file_reader.h
    reader/reader.h

    writer/segment_writer.cpp
    writer/segment_writer.h

    writer/compat_sink.cpp
    writer/compat_sink.h
    writer/rtsp_output_sink.cpp
    writer/rtsp_output_sink.h
    writer/webrtc_output_sink.cpp
    writer/webrtc_output_sink.h

    stream/jpeg_streamer.cpp
    stream/jpeg_streamer.h
)

add_executable(vms ${SOURCES})

protobuf_generate(
    TARGET vms
    IMPORT_DIRS ${CMAKE_CURRENT_SOURCE_DIR}/../proto
    PROTOS
        ${VMS_PROTO_FILE}
        ${DAPR_RUNTIME_PROTO_FILE}
        ${DAPR_COMMON_PROTO_FILE}
)

protobuf_generate(
    TARGET vms
    LANGUAGE grpc
    IMPORT_DIRS ${CMAKE_CURRENT_SOURCE_DIR}/../proto
    PROTOS
        ${VMS_PROTO_FILE}
        ${DAPR_RUNTIME_PROTO_FILE}
        ${DAPR_COMMON_PROTO_FILE}
    PLUGIN protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
    GENERATE_EXTENSIONS .grpc.pb.cc .grpc.pb.h
)

target_include_directories(vms
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_BINARY_DIR}
        ${FFMPEG_INCLUDE_DIRS}
)

target_link_directories(vms PRIVATE ${FFMPEG_LIBRARY_DIRS})

target_link_libraries(vms
    PRIVATE
    ${FFMPEG_LIBRARIES}
    protobuf::libprotobuf
    gRPC::grpc
    gRPC::grpc++
)
