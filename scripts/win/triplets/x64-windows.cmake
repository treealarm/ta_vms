# Release-only x64-windows for CI: .github/workflows/windows.yml puts this directory on
# VCPKG_OVERLAY_TRIPLETS. It keeps the name of vcpkg's built-in triplet, so nothing that names
# the triplet has to change.
#
# Release-only halves both the port build time and the binary cache the workflow keeps between
# runs. The price is that only a Release build can link against it: a Debug vms.exe would mix
# the debug CRT and _ITERATOR_DEBUG_LEVEL=2 with these release libraries, and MSVC refuses
# (LNK2038). That is why developer machines keep the built-in triplet -- F5 builds Debug.
#
# Compiler tracking is off so that an MSVC update in the runner image does not change every
# port's ABI hash and throw the whole cache away. MSVC 14.x toolsets are binary compatible:
# libraries built by an older one link into a newer one.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE dynamic)
set(VCPKG_BUILD_TYPE release)
set(VCPKG_DISABLE_COMPILER_TRACKING ON)
