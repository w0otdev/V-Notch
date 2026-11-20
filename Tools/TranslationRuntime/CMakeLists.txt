cmake_minimum_required(VERSION 3.24)
project(VNotchTranslation LANGUAGES CXX C)

add_executable(VNotchTranslationChunkTests EXCLUDE_FROM_ALL ../../Tests/TranslationChunkTests.cpp)
target_compile_options(VNotchTranslationChunkTests PRIVATE /std:c++latest /O2 /utf-8 /EHsc)
option(VNOTCH_CHUNK_TESTS_ONLY "Configure just the standalone chunk tests" OFF)
if(VNOTCH_CHUNK_TESTS_ONLY)
    enable_testing()
    add_test(NAME translation-chunks COMMAND VNotchTranslationChunkTests)
    return()
endif()

set(SPM_ENABLE_SHARED OFF CACHE BOOL "" FORCE)
set(SPM_ENABLE_MSVC_MT_BUILD OFF CACHE BOOL "" FORCE)
set(SPM_ENABLE_TCMALLOC OFF CACHE BOOL "" FORCE)
set(SPM_BUILD_TEST OFF CACHE BOOL "" FORCE)

# Override the release's FetchContent git tags with verified archives. Use the
# matching protoc binary so a runtime build need only compile libprotobuf-lite.
include(FetchContent)
FetchContent_Declare(abseil-cpp
    URL https://github.com/abseil/abseil-cpp/archive/refs/tags/20260817.0.zip
    URL_HASH SHA256=21485fc6ddcfd723df931f7f02c5f2920924609678da098bd8a50ab7b083b8aa)
FetchContent_Declare(protobuf
    URL https://github.com/protocolbuffers/protobuf/archive/refs/tags/v36.2.zip
    URL_HASH SHA256=9f920d506147af23d9500278661e31f670ec541c79a8f46041ae1f833400e086)
FetchContent_Declare(flatbuffers
    URL https://github.com/google/flatbuffers/archive/refs/tags/v25.12.19-2026-02-06-03fffb2.zip
    URL_HASH SHA256=08efeb8a2c9a4b5f6c305ef1ced64d08f18c6da6bd2b7692f2f109d84a04c904)
FetchContent_Declare(protoc_tool
    URL https://github.com/protocolbuffers/protobuf/releases/download/v36.2/protoc-36.2-win64.zip
    URL_HASH SHA256=f0c128dc0d8492eceece83bb459a4c0e316764b929ffbf1aa416357fd644edd3)
FetchContent_MakeAvailable(protoc_tool)
set(SPM_PROTOC_EXECUTABLE "${protoc_tool_SOURCE_DIR}/bin/protoc.exe")
set(SPM_ENABLE_LITE OFF CACHE BOOL "" FORCE) # V-Notch uses SentencePieceProcessor with protobuf models.
add_subdirectory("${SPM_SOURCE}" sentencepiece EXCLUDE_FROM_ALL)
if(CMAKE_CXX_COMPILER_ID STREQUAL "MSVC")
    # Protobuf 36.2: RepeatedString's lambda returns for both Utf8Type values,
    # but MSVC emits C4715 for each instantiation of its exhaustive switch.
    get_target_property(protobuf_dir libprotobuf-lite SOURCE_DIR)
    set_property(SOURCE "${protobuf_dir}/src/google/protobuf/generated_message_tctable_lite.cc"
        TARGET_DIRECTORY libprotobuf-lite APPEND PROPERTY COMPILE_OPTIONS /wd4715)
endif()
# The public processor header exposes absl::Status and absl::Span.
target_link_libraries(sentencepiece-static INTERFACE absl::status)

add_library(VNotchTranslation SHARED ../../Native/Services/Translation/LiteTranslationRuntime.cpp)
target_compile_options(VNotchTranslation PRIVATE /std:c++latest /O2 /utf-8 /EHsc)
target_include_directories(VNotchTranslation PRIVATE "${CT2_SOURCE}/include" "${SPM_SOURCE}/src")
target_link_libraries(VNotchTranslation PRIVATE "${CT2_IMPORT_LIB}" sentencepiece-static)
set_target_properties(VNotchTranslation PROPERTIES OUTPUT_NAME "VNotch.Translation"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${RUNTIME_OUTPUT}")
