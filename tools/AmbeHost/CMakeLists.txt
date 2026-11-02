# SPDX-License-Identifier: AGPL-3.0-only
#
# AmbeHost - 32-bit x86 out-of-process host for the external AMBE.DLL vocoder.
#
# Build (must be 32-bit x86 to load AMBE.DLL):
#   cmake -S tools/AmbeHost -B build-ambehost -A Win32
#   cmake --build build-ambehost --config Release
#
cmake_minimum_required(VERSION 3.16)
project(AmbeHost C)

add_executable(AmbeHost AmbeHost.c)

if (MSVC)
    # static CRT so AmbeHost.exe has no VC++ redistributable dependency
    set_property(TARGET AmbeHost PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
    target_compile_definitions(AmbeHost PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()

# Test-only stand-in for AMBE.DLL (exports the same functions with trivial behavior).
# It contains no vocoder code and is never shipped.
option(AMBEHOST_BUILD_TEST_STUB "Build the AMBE.DLL test stand-in" OFF)
if (AMBEHOST_BUILD_TEST_STUB)
    add_library(AMBE SHARED test/stub_ambe.c)
    set_target_properties(AMBE PROPERTIES OUTPUT_NAME "AMBE" PREFIX "")
    if (MSVC)
        set_property(TARGET AMBE PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
    endif()
endif()
