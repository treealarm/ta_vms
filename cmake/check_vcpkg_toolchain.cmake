# Included before project() by video_srv and roitrc.
#
# Their presets take the toolchain from $env{VCPKG_ROOT}. With the variable unset that expands to
# "/scripts/buildsystems/vcpkg.cmake", and CMake's own complaint about it -- "Could not find
# toolchain file", from deep inside CMakeDetermineSystem -- names neither the variable nor the
# fix. Worse, the preset writes that path into CMakeCache.txt, so the build directory stays broken
# for every later configure until one runs with the variable set.
#
# Only the case where a toolchain was asked for and is not there is checked. The Docker builds
# pass none through a preset -- ta-deps sets CMAKE_TOOLCHAIN_FILE in the environment -- and never
# need VCPKG_ROOT.

if(DEFINED CMAKE_TOOLCHAIN_FILE AND NOT EXISTS "${CMAKE_TOOLCHAIN_FILE}")
    if("$ENV{VCPKG_ROOT}" STREQUAL "")
        message(FATAL_ERROR
            "VCPKG_ROOT is not set, so the preset's toolchain path came out as "
            "'${CMAKE_TOOLCHAIN_FILE}'.\n"
            "Set it to the vcpkg checkout (e.g. `export VCPKG_ROOT=/mnt/wdblue/vcpkg` in ~/.profile), "
            "restart VS Code so it inherits the variable, and configure again.")
    else()
        message(FATAL_ERROR
            "Toolchain file '${CMAKE_TOOLCHAIN_FILE}' does not exist. "
            "VCPKG_ROOT is '$ENV{VCPKG_ROOT}' -- is that the vcpkg checkout?")
    endif()
endif()
