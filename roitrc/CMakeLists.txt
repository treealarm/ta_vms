# roitrc -- ta_vms' ROI re-compression service.
#
# The encoder library lives in its own repository (github treealarm/roi_transcode), pinned here
# as the `ta_roienc` submodule. This directory is everything about re-compression that is the
# service's: the gRPC contract VmsFs speaks, the job model that mirrors video_srv's
# EdgeImport, the Dapr notifications and the path sandbox.
#
# The dependency arrow points one way and is narrow: this links `ta_roienc` through its public
# headers alone -- ta_roienc/ta_roienc.h, ta_roienc/transcoder.h and what they include -- and reaches
# into none of the library's internals.

cmake_minimum_required(VERSION 3.22)
include(${CMAKE_CURRENT_SOURCE_DIR}/../cmake/check_vcpkg_toolchain.cmake)
project(roitrc CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(ROITRC_BUILD_TESTS "Build the service's own tests" ON)

# The submodule is private, and this service is nothing without it. A clone without access still
# builds the rest of ta_vms, so here an absent library is a warning and an empty `roitrc` target,
# which keeps `cmake --build --target roitrc` -- and the editor's build:all -- going. A node with
# no roitrc simply does not re-compress (see MEDIA_SERVER_PAIRS). The image cannot be built
# without it either way: its roi-deps base comes from the submodule's docker/intel.
if(NOT EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/ta_roienc/CMakeLists.txt)
    message(WARNING "roitrc/ta_roienc is absent: roitrc is not built. "
        "Run `git submodule update --init roitrc/ta_roienc` if you have access to it.")
    add_custom_target(roitrc COMMAND ${CMAKE_COMMAND} -E echo "roitrc skipped: roitrc/ta_roienc is absent")
    return()
endif()

# The library, and nothing else from that repository: its unit tests run in its own repository.
# Forced rather than merely set, because these are options in the subproject's own cache.
set(TA_ROIENC_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(TA_ROIENC_INSTALL     OFF CACHE BOOL "" FORCE)

# The libavcodec-backed engines, which is where QSV and VA-API live. Off in the library by
# default; this service exists to re-compress clips on hardware, so it turns them on -- without
# this the engine chain has no hardware entry to find and every job quietly takes the CPU path.
set(TA_ROIENC_BUILD_ENGINE_FFMPEG ON CACHE BOOL "" FORCE)

add_subdirectory(ta_roienc)

set(ROITRC_WARNINGS
    $<$<CXX_COMPILER_ID:GNU>:-Wall -Wextra>
    $<$<CXX_COMPILER_ID:Clang>:-Wall -Wextra>
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
)

# pkg_check_modules ran in the subdirectory above, but its results stayed in that scope.
# The smoke tool decodes to compare frames, so it needs FFmpeg on its own account.
find_package(PkgConfig REQUIRED)
pkg_check_modules(FFMPEG REQUIRED
    libavformat
    libavcodec
    libavutil
    libswscale
    libswresample
)
find_package(Protobuf CONFIG REQUIRED)
find_package(gRPC CONFIG REQUIRED)
find_package(nlohmann_json CONFIG REQUIRED)

# The contract, from ta_vms' own proto/ beside every other service contract. There is one
# copy of it again: while this service lived in the encoder's repository each side had to
# carry its own, and protobuf would never have reported them drifting apart.
set(ROI_PROTO_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../proto" CACHE PATH
    "Directory holding roi_transcode.proto and dapr/dapr_runtime.proto")
cmake_path(SET ROI_PROTO_DIR NORMALIZE "${ROI_PROTO_DIR}")
set(ROI_PROTO_FILE ${ROI_PROTO_DIR}/roi_transcode.proto)
set(DAPR_PROTO_FILE ${ROI_PROTO_DIR}/dapr/dapr_runtime.proto)

# ---------------------------------------------------------------------------
# The service
# ---------------------------------------------------------------------------
add_executable(roitrc
    service/main.cpp
    service/job/roi_job_manager.cpp
    service/grpc/proto_convert.cpp
    service/grpc/roi_file_service.cpp
    service/grpc/roi_stream_service.cpp
    service/grpc/dapr_publisher.cpp
    service/grpc/path_guard.cpp
)
protobuf_generate(
    TARGET roitrc
    IMPORT_DIRS ${ROI_PROTO_DIR}
    PROTOS ${ROI_PROTO_FILE} ${DAPR_PROTO_FILE}
)
protobuf_generate(
    TARGET roitrc
    LANGUAGE grpc
    IMPORT_DIRS ${ROI_PROTO_DIR}
    PROTOS ${ROI_PROTO_FILE} ${DAPR_PROTO_FILE}
    PLUGIN protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
    GENERATE_EXTENSIONS .grpc.pb.cc .grpc.pb.h
)
target_include_directories(roitrc PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/service
    ${CMAKE_CURRENT_BINARY_DIR}
)
target_link_libraries(roitrc PRIVATE
    ta_roienc_all
    protobuf::libprotobuf
    gRPC::grpc
    gRPC::grpc++
)
target_compile_definitions(roitrc PRIVATE _CRT_SECURE_NO_WARNINGS)
target_compile_options(roitrc PRIVATE ${ROITRC_WARNINGS})

# ---------------------------------------------------------------------------
# roi_grpc_smoke -- a client, so it reads roi.json the way a caller would
#
# roi_json.cpp is the adapter between roi.json and the library's vocabulary. It lives here
# rather than in the library because embedding the encoder should not drag a JSON library
# along, and only this client ever reads a file.
# ---------------------------------------------------------------------------
add_executable(roi_grpc_smoke
    tools/roi_grpc_smoke.cpp
    tools/roi_json.cpp
)
protobuf_generate(
    TARGET roi_grpc_smoke
    IMPORT_DIRS ${ROI_PROTO_DIR}
    PROTOS ${ROI_PROTO_FILE}
)
protobuf_generate(
    TARGET roi_grpc_smoke
    LANGUAGE grpc
    IMPORT_DIRS ${ROI_PROTO_DIR}
    PROTOS ${ROI_PROTO_FILE}
    PLUGIN protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
    GENERATE_EXTENSIONS .grpc.pb.cc .grpc.pb.h
)
target_include_directories(roi_grpc_smoke PRIVATE
    ${CMAKE_CURRENT_BINARY_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/tools
)
target_include_directories(roi_grpc_smoke SYSTEM PRIVATE ${FFMPEG_INCLUDE_DIRS})
if(FFMPEG_LIBRARY_DIRS)
    target_link_directories(roi_grpc_smoke PRIVATE ${FFMPEG_LIBRARY_DIRS})
endif()
target_link_libraries(roi_grpc_smoke PRIVATE
    ta_roienc_all
    nlohmann_json::nlohmann_json
    ${FFMPEG_LIBRARIES}
    protobuf::libprotobuf
    gRPC::grpc
    gRPC::grpc++
)
target_compile_options(roi_grpc_smoke PRIVATE ${ROITRC_WARNINGS})

# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------
if(ROITRC_BUILD_TESTS)
    enable_testing()
    add_executable(path_guard_test tests/path_guard_test.cpp service/grpc/path_guard.cpp)
    target_include_directories(path_guard_test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/service)
    target_compile_options(path_guard_test PRIVATE ${ROITRC_WARNINGS})
    add_test(NAME path_guard COMMAND path_guard_test)
endif()
