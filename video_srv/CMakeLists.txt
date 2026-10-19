cmake_minimum_required(VERSION 3.22)
include(${CMAKE_CURRENT_SOURCE_DIR}/../cmake/check_vcpkg_toolchain.cmake)
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

# The hardware encoder chain, from the ta_roienc submodule under roitrc/. Live HEVC->H.264 for
# WebRTC goes through it (see writer/processors/hw_transcode_processor.cpp) rather than through a
# second copy of the same probe-and-fall-back logic written here.
#
# A clone made without --recursive has no submodule, and it still has to build. Without the
# library the service is complete but CPU-only on that path: every HEVC stream a browser
# watches is re-encoded by video_transcode_processor (libx264). That is a warning, not an error --
# except where VMS_REQUIRE_TA_ROIENC says otherwise, which the image build does, because an image
# that lost its hardware encoder looks from outside exactly like a service that burns CPU.
option(VMS_REQUIRE_TA_ROIENC "Fail the configure when the roitrc/ta_roienc submodule is absent" OFF)
set(VMS_TA_ROIENC_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../roitrc/ta_roienc)
if(EXISTS ${VMS_TA_ROIENC_DIR}/CMakeLists.txt)
    set(VMS_WITH_TA_ROIENC ON)
elseif(VMS_REQUIRE_TA_ROIENC)
    message(FATAL_ERROR "roitrc/ta_roienc is empty and VMS_REQUIRE_TA_ROIENC is on: "
        "run `git submodule update --init roitrc/ta_roienc`")
else()
    set(VMS_WITH_TA_ROIENC OFF)
    message(WARNING "roitrc/ta_roienc is absent: building without hardware live transcode. "
        "HEVC streams are re-encoded for WebRTC on the CPU (libx264).")
endif()

if(VMS_WITH_TA_ROIENC)
    # Only the library: its unit tests run in its own repository. Forced rather than merely set,
    # because these are options in the subproject's own cache.
    set(TA_ROIENC_BUILD_TESTS OFF CACHE BOOL "" FORCE)
    set(TA_ROIENC_INSTALL     OFF CACHE BOOL "" FORCE)

    # The libavcodec-backed engines, which is where h264_vaapi lives. Off in the library by
    # default, and wrong for us -- hardware is the entire reason this service links ta_roienc at
    # all, and without this the engine chain has no hardware entry to find and every stream
    # quietly takes the CPU path.
    set(TA_ROIENC_BUILD_ENGINE_FFMPEG ON CACHE BOOL "" FORCE)

    add_subdirectory(${VMS_TA_ROIENC_DIR} ta_roienc)
endif()

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
    grpc_layer/entity_state_publisher.cpp
    grpc_layer/entity_state_publisher.h
    grpc_layer/cfg_client.h
    grpc_layer/cfg_client.cpp
    grpc_layer/utils_client.h
    grpc_layer/utils_client.cpp
    grpc_layer/state_store_client.h
    grpc_layer/state_store_client.cpp
    grpc_layer/onvif_client.h
    grpc_layer/onvif_client.cpp
    grpc_layer/onvif_metadata_client.h
    grpc_layer/onvif_metadata_client.cpp
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
    reader/onvif_metadata_assembler.cpp
    reader/onvif_metadata_assembler.h
    reader/file_camera_reader.cpp
    reader/file_camera_reader.h
    reader/snapshot_reader.cpp
    reader/snapshot_reader.h
    reader/archive_frame_jpeg_reader.cpp
    reader/archive_frame_jpeg_reader.h
    
    sink_container/sink_container_impl.h
    sink_container/sink_container_impl.cpp

    arch_writer/segment_writer.cpp
    arch_writer/segment_writer.h

    edge_import/replay_source.h
    edge_import/onvif_replay_source.cpp
    edge_import/onvif_replay_source.h
    edge_import/tcp_socket.cpp
    edge_import/tcp_socket.h
    edge_import/onvif_replay_rtsp.cpp
    edge_import/onvif_replay_rtsp.h
    edge_import/edge_import_worker.cpp
    edge_import/edge_import_worker.h
    edge_import/edge_import_manager.cpp
    edge_import/edge_import_manager.h

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
    writer/rtsp_timeline_cache.cpp
    writer/rtsp_timeline_cache.h
    writer/analytics_output_sink.cpp
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

if(VMS_WITH_TA_ROIENC)
    target_sources(vms PRIVATE
        writer/processors/hw_transcode_processor.cpp
        writer/processors/hw_transcode_processor.h
    )
    target_compile_definitions(vms PRIVATE VMS_WITH_TA_ROIENC)
    # "Everything this build has", which is the name the library keeps for add_subdirectory
    # consumers so they need not learn its component list. Linking the core alone is not enough:
    # Transcoder sits in a separate target (a demuxer and decoder around the encoder, kept apart so
    # libavformat does not spread into the core), and the engine registry that
    # RegisterDeploymentEngines fills is a separate object library again.
    target_link_libraries(vms PRIVATE ta_roienc_all)
endif()

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
