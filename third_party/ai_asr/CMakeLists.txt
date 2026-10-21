# ============================================================================
#  ai_asr —— 语音识别桥接层（见 ai_asr_shim.h 顶部的设计说明）
#
#  构建产物：
#     build/ai_asr.dll      <- C# 侧 P/Invoke 的目标
#     同时会构建 whisper.cpp 静态库并链接进来，因此只需分发一个 dll。
#
#  用法（在 third_party/ai_asr 目录下）：
#     cmake -S . -B build -G "Visual Studio 18 2026" -A x64
#     cmake --build build --config Release
#
#  想要 GPU 推理就加 -DGGML_CUDA=ON（需要 CUDA 工具包）。
#  默认构建 CPU 版本：whisper-base 在 CPU 上对几秒语音的延迟已经可以接受，
#  而且可以避免与本地 LLM 争抢显存（手册 §0 的"游戏场景让出显存"原则也受益于此）。
# ============================================================================
cmake_minimum_required(VERSION 3.14)

# whisper.cpp 是 C++ 项目，因此必须同时启用 CXX，否则配置阶段会报
# "No known features for CXX compiler"（在 add_subdirectory 之后才暴露）。
project(ai_asr LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

set(WHISPER_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../whisper.cpp")

if(NOT EXISTS "${WHISPER_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR
        "未找到 whisper.cpp 源码：${WHISPER_ROOT}\n"
        "请先运行项目根目录的 fetch_assets 或手动把 whisper.cpp 放到 third_party/whisper.cpp。")
endif()

# ---- 只构建我们需要的 whisper 库，跳过示例/测试/服务端，显著缩短构建时间 ----
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS    OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER   OFF CACHE BOOL "" FORCE)

# 静态链接进 ai_asr.dll，最终只产出一个 dll，便于分发。
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

add_subdirectory("${WHISPER_ROOT}" "${CMAKE_CURRENT_BINARY_DIR}/whisper.cpp" EXCLUDE_FROM_ALL)

add_library(ai_asr SHARED ai_asr_shim.c)

target_include_directories(ai_asr PRIVATE
    "${WHISPER_ROOT}/include"
    "${WHISPER_ROOT}/ggml/include")

target_link_libraries(ai_asr PRIVATE whisper)

# Windows 上把运行时库设为静态，避免部署时还要求装 VC 运行库。
if(MSVC)
    set_property(TARGET ai_asr PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

    # /utf-8 是必须的：本机是中文 Windows（代码页 936），而 ai_asr_shim.c/h 是
    # UTF-8（无 BOM）并且带有中文注释。不加这个开关，MSVC 会按 936 解析源文件，
    # 中文注释的字节被误读，轻则 C4819 警告，重则 C2001「字符串字面量中的换行符」。
    target_compile_options(ai_asr PRIVATE /utf-8 /W3)
endif()

# 输出到固定的 build/ 目录，方便 config.yaml 里写相对路径。
set_target_properties(ai_asr PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY         "${CMAKE_CURRENT_SOURCE_DIR}/build"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/build"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_CURRENT_SOURCE_DIR}/build"
    LIBRARY_OUTPUT_DIRECTORY         "${CMAKE_CURRENT_SOURCE_DIR}/build")

message(STATUS "ai_asr: whisper.cpp 源目录 = ${WHISPER_ROOT}")
message(STATUS "ai_asr: GGML_CUDA = ${GGML_CUDA}")
