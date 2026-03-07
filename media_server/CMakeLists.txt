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

set(VMS_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/vms_control.proto)
get_filename_component(VMS_PROTO_NAME ${VMS_PROTO_FILE} NAME_WE)
set(VMS_PROTO_SRC ${CMAKE_CURRENT_BINARY_DIR}/${VMS_PROTO_NAME}.pb.cc)
set(VMS_PROTO_HDR ${CMAKE_CURRENT_BINARY_DIR}/${VMS_PROTO_NAME}.pb.h)
set(VMS_GRPC_SRC ${CMAKE_CURRENT_BINARY_DIR}/${VMS_PROTO_NAME}.grpc.pb.cc)
set(VMS_GRPC_HDR ${CMAKE_CURRENT_BINARY_DIR}/${VMS_PROTO_NAME}.grpc.pb.h)

set(DAPR_RUNTIME_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/dapr/proto/runtime/v1/dapr.proto)
set(DAPR_COMMON_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/dapr/proto/common/v1/common.proto)

set(DAPR_RUNTIME_PROTO_SRC ${CMAKE_CURRENT_BINARY_DIR}/dapr/proto/runtime/v1/dapr.pb.cc)
set(DAPR_RUNTIME_PROTO_HDR ${CMAKE_CURRENT_BINARY_DIR}/dapr/proto/runtime/v1/dapr.pb.h)
set(DAPR_RUNTIME_GRPC_SRC ${CMAKE_CURRENT_BINARY_DIR}/dapr/proto/runtime/v1/dapr.grpc.pb.cc)
set(DAPR_RUNTIME_GRPC_HDR ${CMAKE_CURRENT_BINARY_DIR}/dapr/proto/runtime/v1/dapr.grpc.pb.h)

set(DAPR_COMMON_PROTO_SRC ${CMAKE_CURRENT_BINARY_DIR}/dapr/proto/common/v1/common.pb.cc)
set(DAPR_COMMON_PROTO_HDR ${CMAKE_CURRENT_BINARY_DIR}/dapr/proto/common/v1/common.pb.h)

add_custom_command(
    OUTPUT ${VMS_PROTO_SRC} ${VMS_PROTO_HDR} ${VMS_GRPC_SRC} ${VMS_GRPC_HDR}
    COMMAND protobuf::protoc
    ARGS
      --grpc_out=${CMAKE_CURRENT_BINARY_DIR}
      --cpp_out=${CMAKE_CURRENT_BINARY_DIR}
      -I ${CMAKE_CURRENT_SOURCE_DIR}/../proto
      --plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
      ${VMS_PROTO_FILE}
    DEPENDS ${VMS_PROTO_FILE}
)

file(MAKE_DIRECTORY
    ${CMAKE_CURRENT_BINARY_DIR}/proto/runtime/v1
    ${CMAKE_CURRENT_BINARY_DIR}/proto/common/v1
)

add_custom_command(
    OUTPUT ${DAPR_RUNTIME_PROTO_SRC} ${DAPR_RUNTIME_PROTO_HDR} ${DAPR_RUNTIME_GRPC_SRC} ${DAPR_RUNTIME_GRPC_HDR}
    COMMAND protobuf::protoc
    ARGS
      --grpc_out=${CMAKE_CURRENT_BINARY_DIR}
      --cpp_out=${CMAKE_CURRENT_BINARY_DIR}
      -I ${CMAKE_CURRENT_SOURCE_DIR}/../proto
      --plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
      ${DAPR_RUNTIME_PROTO_FILE}
    DEPENDS ${DAPR_RUNTIME_PROTO_FILE}
)

add_custom_command(
    OUTPUT ${DAPR_COMMON_PROTO_SRC} ${DAPR_COMMON_PROTO_HDR}
    COMMAND protobuf::protoc
    ARGS
      --cpp_out=${CMAKE_CURRENT_BINARY_DIR}
      -I ${CMAKE_CURRENT_SOURCE_DIR}/../proto
      ${DAPR_COMMON_PROTO_FILE}
    DEPENDS ${DAPR_COMMON_PROTO_FILE}
)

set(SOURCES
    main.cpp
    cam.cpp
    camera_manager.cpp
    camera_control_service.cpp
    dapr_publisher.cpp
    dapr_publisher.h

    media/av_deleters.h
    media/media_packet.h
    media/media_sink.h

    reader/rtsp_reader.cpp
    reader/rtsp_reader.h
    reader/file_reader.cpp
    reader/file_reader.h
    reader/reader.h

    writer/segment_writer.cpp
    writer/segment_writer.h

    stream/jpeg_streamer.cpp
    stream/jpeg_streamer.h

    ${VMS_PROTO_SRC}
    ${VMS_GRPC_SRC}
    ${DAPR_RUNTIME_PROTO_SRC}
    ${DAPR_RUNTIME_GRPC_SRC}
    ${DAPR_COMMON_PROTO_SRC}
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
