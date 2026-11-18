cmake_minimum_required(VERSION 3.24)
project(VNotchTranslation LANGUAGES CXX C)

set(SPM_ENABLE_SHARED OFF CACHE BOOL "" FORCE)
set(SPM_ENABLE_MSVC_MT_BUILD OFF CACHE BOOL "" FORCE)
set(SPM_ENABLE_TCMALLOC OFF CACHE BOOL "" FORCE)
set(SPM_BUILD_TEST OFF CACHE BOOL "" FORCE)
add_subdirectory("${SPM_SOURCE}" sentencepiece EXCLUDE_FROM_ALL)

add_library(VNotchTranslation SHARED ../../Native/Services/Translation/LiteTranslationRuntime.cpp)
target_compile_options(VNotchTranslation PRIVATE /std:c++latest /O2 /utf-8 /EHsc)
target_include_directories(VNotchTranslation PRIVATE "${CT2_SOURCE}/include" "${SPM_SOURCE}/src")
target_link_libraries(VNotchTranslation PRIVATE "${CT2_IMPORT_LIB}" sentencepiece-static)
set_target_properties(VNotchTranslation PROPERTIES OUTPUT_NAME "VNotch.Translation"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${RUNTIME_OUTPUT}")

add_executable(VNotchTranslationChunkTests EXCLUDE_FROM_ALL ../../Tests/TranslationChunkTests.cpp)
target_compile_options(VNotchTranslationChunkTests PRIVATE /std:c++latest /O2 /utf-8 /EHsc)
