cmake_minimum_required(VERSION 3.22)
project(VMS CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(PkgConfig REQUIRED)
find_package(Protobuf CONFIG REQUIRED)
find_package(gRPC CONFIG REQUIRED)
find_package(spdlog CONFIG REQUIRED)

pkg_check_modules(FFMPEG REQUIRED
    libavformat
    libavcodec
    libavutil
    libswscale
    libswresample
)

set(VMS_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/vms_control.proto)
set(CFG_CLIENT_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/vms_cfg.proto)
set(UTILS_CLIENT_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/vms_utils.proto)
set(ONVIF_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/onvif.proto)
set(FS_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/vms_fs.proto)
set(LOGGER_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/logger.proto)
set(DAPR_RUNTIME_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/dapr/proto/runtime/v1/dapr.proto)
set(DAPR_APPCALLBACK_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/dapr/proto/runtime/v1/appcallback.proto)
set(DAPR_COMMON_PROTO_FILE ${CMAKE_CURRENT_SOURCE_DIR}/../proto/dapr/proto/common/v1/common.proto)

set(SOURCES
    main.cpp
    cam_stream.cpp
    config_subscriber.cpp
    config_subscriber.h
    mediamtx_monitor.cpp
    mediamtx_monitor.h
    cam_device.cpp
    camera_manager.cpp
    
    grpc_layer/camera_control_service.h
    grpc_layer/camera_control_service.cpp
    grpc_layer/proto_mapper.h
    grpc_layer/proto_mapper.cpp
    grpc_layer/dapr_publisher.cpp
    grpc_layer/dapr_publisher.h
    grpc_layer/cfg_client.h
    grpc_layer/cfg_client.cpp
    grpc_layer/utils_client.h
    grpc_layer/utils_client.cpp
    grpc_layer/state_store_client.h
    grpc_layer/state_store_client.cpp
    grpc_layer/onvif_client.h
    grpc_layer/onvif_client.cpp
    grpc_layer/fs_client.h
    grpc_layer/fs_client.cpp
    interfaces/av_deleters.h
    interfaces/media_packet.h
    interfaces/media_sink.h
    interfaces/sink_container.h
    interfaces/reader.h
    interfaces/web_rtc_utils.h

    reader/rtsp_reader.cpp
    reader/rtsp_reader.h
    reader/file_reader.cpp
    reader/file_reader.h
    reader/snapshot_reader.cpp
    reader/snapshot_reader.h
    reader/archive_frame_jpeg_reader.cpp
    reader/archive_frame_jpeg_reader.h
    
    sink_container/sink_container_impl.h
    sink_container/sink_container_impl.cpp

    arch_writer/segment_writer.cpp
    arch_writer/segment_writer.h

    av_utils/need_reinit.cpp
    av_utils/need_reinit.h

    writer/compat_sink.cpp
    writer/compat_sink.h
    writer/processors/stream_processor.h
    writer/processors/video_copy_processor.cpp
    writer/processors/video_copy_processor.h
    writer/processors/audio_copy_processor.cpp
    writer/processors/audio_copy_processor.h
    writer/processors/video_transcode_processor.cpp
    writer/processors/video_transcode_processor.h
    writer/processors/audio_transcode_processor.cpp
    writer/processors/audio_transcode_processor.h
    writer/rtsp_output_sink.cpp
    writer/rtsp_output_sink.h
    writer/webrtc_output_sink.cpp
    writer/webrtc_output_sink.h
    writer/sync_packet_transcoder.cpp
    writer/sync_packet_transcoder.h

    stream/jpeg_streamer.cpp
    stream/jpeg_streamer.h

    file_system/recording_storage.h
    file_system/recording_storage.cpp

    logger/logger.h
    logger/logger.cpp
)

add_executable(vms ${SOURCES})

protobuf_generate(
    TARGET vms
    IMPORT_DIRS ${CMAKE_CURRENT_SOURCE_DIR}/../proto
    PROTOS
        ${VMS_PROTO_FILE}
        ${CFG_CLIENT_PROTO_FILE}
        ${UTILS_CLIENT_PROTO_FILE}
        ${ONVIF_PROTO_FILE}
        ${FS_PROTO_FILE}
        ${LOGGER_PROTO_FILE}
        ${DAPR_RUNTIME_PROTO_FILE}
        ${DAPR_APPCALLBACK_PROTO_FILE}
        ${DAPR_COMMON_PROTO_FILE}
)

protobuf_generate(
    TARGET vms
    LANGUAGE grpc
    IMPORT_DIRS ${CMAKE_CURRENT_SOURCE_DIR}/../proto
    PROTOS
        ${VMS_PROTO_FILE}
        ${DAPR_RUNTIME_PROTO_FILE}
        ${DAPR_APPCALLBACK_PROTO_FILE}
        ${DAPR_COMMON_PROTO_FILE}
        ${CFG_CLIENT_PROTO_FILE}
        ${UTILS_CLIENT_PROTO_FILE}
        ${ONVIF_PROTO_FILE}
        ${FS_PROTO_FILE}
        ${LOGGER_PROTO_FILE}
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
    spdlog::spdlog
)

target_compile_definitions(vms PRIVATE _CRT_SECURE_NO_WARNINGS)

target_compile_options(vms PRIVATE
    $<$<CXX_COMPILER_ID:GNU>:-Wall -Wextra>
    $<$<CXX_COMPILER_ID:Clang>:-Wall -Wextra>
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
)
