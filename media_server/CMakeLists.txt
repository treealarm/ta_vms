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
    libswscale
)

set(PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/vms_control.proto)
get_filename_component(PROTO_NAME ${PROTO_FILE} NAME_WE)
set(PROTO_SRC ${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.pb.cc)
set(PROTO_HDR ${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.pb.h)
set(GRPC_SRC ${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.grpc.pb.cc)
set(GRPC_HDR ${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.grpc.pb.h)

add_custom_command(
    OUTPUT ${PROTO_SRC} ${PROTO_HDR} ${GRPC_SRC} ${GRPC_HDR}
    COMMAND protobuf::protoc
    ARGS
      --grpc_out=${CMAKE_CURRENT_BINARY_DIR}
      --cpp_out=${CMAKE_CURRENT_BINARY_DIR}
      -I ${CMAKE_CURRENT_SOURCE_DIR}/../proto
      --plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
      ${PROTO_FILE}
    DEPENDS ${PROTO_FILE}
)

set(SOURCES
    main.cpp
    cam.cpp
    camera_manager.cpp
    camera_control_service.cpp

    media/av_deleters.h
    media/media_packet.h
    media/media_sink.h

    reader/rtsp_reader.cpp
    reader/rtsp_reader.h

    writer/segment_writer.cpp
    writer/segment_writer.h

    stream/jpeg_streamer.cpp
    stream/jpeg_streamer.h

    ${PROTO_SRC}
    ${GRPC_SRC}
)

add_executable(vms ${SOURCES})

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
