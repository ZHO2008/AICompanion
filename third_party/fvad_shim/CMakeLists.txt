cmake_minimum_required(VERSION 3.14)

# ============================================================================
#  fvad_shim —— 把 libfvad 打成带导出符号的 Windows DLL
#
#  为什么需要这一层：
#    libfvad 自带的 CMake 只产出 **静态库**，而且 include/fvad.h 里没有任何
#    __declspec(dllexport) 宏，所以直接 BUILD_SHARED_LIBS=ON 出来的 DLL
#    在 Windows 上没有任何导出符号，C# 侧 NativeLibrary.TryGetExport 会全部失败。
#
#  做法：把 libfvad 的 13 个源文件直接编进本 DLL（不是链静态库，避免
#  /WHOLEARCHIVE 与静态库符号裁剪的问题），再用 fvad_exports.def
#  精确声明要导出的 6 个公开函数。导出的名字与 fvad.h 完全一致，
#  因此 Core 里的 FvadDetector 不需要任何改动。
# ============================================================================

project(fvad_shim LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

set(FVAD_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../libfvad")

if(NOT EXISTS "${FVAD_ROOT}/src/fvad.c")
    message(FATAL_ERROR
        "找不到 libfvad 源码：${FVAD_ROOT}\n"
        "请把 libfvad 放到 third_party/libfvad。")
endif()

add_library(fvad_shim SHARED
    fvad_exports.def
    "${FVAD_ROOT}/src/fvad.c"
    "${FVAD_ROOT}/src/signal_processing/division_operations.c"
    "${FVAD_ROOT}/src/signal_processing/energy.c"
    "${FVAD_ROOT}/src/signal_processing/get_scaling_square.c"
    "${FVAD_ROOT}/src/signal_processing/resample_48khz.c"
    "${FVAD_ROOT}/src/signal_processing/resample_by_2_internal.c"
    "${FVAD_ROOT}/src/signal_processing/resample_fractional.c"
    "${FVAD_ROOT}/src/signal_processing/spl_inl.c"
    "${FVAD_ROOT}/src/vad/vad_core.c"
    "${FVAD_ROOT}/src/vad/vad_filterbank.c"
    "${FVAD_ROOT}/src/vad/vad_gmm.c"
    "${FVAD_ROOT}/src/vad/vad_sp.c")

target_include_directories(fvad_shim PRIVATE
    "${FVAD_ROOT}/include"
    "${FVAD_ROOT}/src")

if(MSVC)
    # 静态运行时库，避免部署时还要装 VC 运行库
    set_property(TARGET fvad_shim PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    # /utf-8：源文件里可能有非 ASCII 注释，而本机是 936 代码页
    target_compile_options(fvad_shim PRIVATE /utf-8 /W1)
endif()

# 输出到项目根 bin\，与 ai_asr.dll / llama-server.exe 放在一起
set_target_properties(fvad_shim PROPERTIES
    PREFIX ""
    OUTPUT_NAME "libfvad"
    RUNTIME_OUTPUT_DIRECTORY         "${CMAKE_CURRENT_SOURCE_DIR}/../../bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/../../bin"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_CURRENT_SOURCE_DIR}/../../bin")

message(STATUS "fvad_shim: libfvad 源目录 = ${FVAD_ROOT}")
message(STATUS "fvad_shim: 输出 = ${CMAKE_CURRENT_SOURCE_DIR}/../../bin/libfvad.dll")
