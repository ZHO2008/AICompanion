/*
 * ai_asr_shim.h —— AI 伴侣的语音识别桥接层（稳定 ABI）
 *
 * 为什么需要这一层（重要设计决策）：
 *
 *   whisper.cpp 的 whisper_full_params 是一个包含 40 多个字段、内嵌匿名结构体、
 *   函数指针与 size_t 的巨型结构体。如果直接在 C# 里声明它的 P/Invoke 布局，
 *   一旦有任何一个字段的类型/对齐猜错，编译器不会报错，而是在**运行时破坏调用栈**，
 *   症状是随机崩溃且极难定位。
 *
 *   因此这里把 ABI 风险收敛到一层薄薄的 C 代码上：本文件用 whisper.cpp 的**真实头文件**
 *   编译，由 C 编译器负责所有结构体布局；对 C# 只暴露下面这 5 个参数全为基本类型的函数。
 *   这样 C# 侧完全不需要知道 whisper_full_params 的存在。
 */
#ifndef AI_ASR_SHIM_H
#define AI_ASR_SHIM_H

#if defined(_WIN32)
#  define AI_ASR_API __declspec(dllexport)
#else
#  define AI_ASR_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * 初始化（加载模型）。
 *   model_path   : ggml 模型文件路径（UTF-8）
 *   n_threads    : 推理线程数，<=0 表示自动
 *   use_gpu      : 1 使用 GPU（需要带 CUDA 构建），0 使用 CPU
 *   error        : 失败时写入的可读错误信息缓冲区，可为 NULL
 * 返回 0 表示成功，负数表示失败。
 */
AI_ASR_API int ai_asr_init(const char* model_path, int n_threads, int use_gpu, char* error, int error_capacity);

/*
 * 识别一段 16kHz 单声道 float 音频（取值范围 -1..1）。
 *   language  : "zh" / "en" / NULL 或 "auto" 表示自动检测
 *   translate : 1 表示翻译成英文，0 表示只转写
 *   out_text  : 输出缓冲区（UTF-8，已去除首尾空白）
 * 返回值 >= 0 为写入的字符数，负数表示失败。
 */
AI_ASR_API int ai_asr_transcribe(const float* samples, int n_samples,
                                 const char* language, int translate,
                                 char* out_text, int out_capacity);

/* 重置（当前实现为无状态操作，保留接口以便将来切换到带状态的解码路径）。 */
AI_ASR_API void ai_asr_reset(void);

/* 释放模型与上下文。 */
AI_ASR_API void ai_asr_shutdown(void);

/* 返回 whisper/ggml 的构建与后端信息，用于启动日志。 */
AI_ASR_API const char* ai_asr_system_info(void);

#ifdef __cplusplus
}
#endif

#endif /* AI_ASR_SHIM_H */
