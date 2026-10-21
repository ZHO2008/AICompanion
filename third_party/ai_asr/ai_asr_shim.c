/*
 * ai_asr_shim.c —— 见 ai_asr_shim.h 顶部的设计说明。
 *
 * 本文件由 C 编译器按 whisper.cpp 的真实头文件编译，
 * 因此所有结构体布局、字段类型与对齐都由编译器保证，不存在手工猜布局的风险。
 */
#include "ai_asr_shim.h"

#include "whisper.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* 全局上下文。本程序同一时刻只会有一个 ASR 会话（手册 §3.5 的单引擎设计）。 */
static struct whisper_context* g_ctx = NULL;
static int g_n_threads = 4;

static void set_error(char* buffer, int capacity, const char* message)
{
    if (buffer == NULL || capacity <= 0)
    {
        return;
    }

    snprintf(buffer, (size_t)capacity, "%s", message != NULL ? message : "");
}

/* 去掉首尾空白：whisper 的片段文本通常以空格开头，直接拼接会出现多余空格。 */
static void trim_inplace(char* text)
{
    if (text == NULL)
    {
        return;
    }

    size_t length = strlen(text);

    size_t start = 0;
    while (start < length && (text[start] == ' ' || text[start] == '\t' || text[start] == '\r' || text[start] == '\n'))
    {
        start++;
    }

    while (length > start && (text[length - 1] == ' ' || text[length - 1] == '\t' ||
                              text[length - 1] == '\r' || text[length - 1] == '\n'))
    {
        length--;
    }

    if (start > 0)
    {
        memmove(text, text + start, length - start);
    }

    text[length - start] = '\0';
}

AI_ASR_API int ai_asr_init(const char* model_path, int n_threads, int use_gpu, char* error, int error_capacity)
{
    if (g_ctx != NULL)
    {
        whisper_free(g_ctx);
        g_ctx = NULL;
    }

    if (model_path == NULL || model_path[0] == '\0')
    {
        set_error(error, error_capacity, "model_path 为空");
        return -1;
    }

    FILE* probe = fopen(model_path, "rb");
    if (probe == NULL)
    {
        set_error(error, error_capacity, "无法打开模型文件");
        return -2;
    }
    fclose(probe);

    struct whisper_context_params cparams = whisper_context_default_params();
    cparams.use_gpu = (use_gpu != 0) ? true : false;
    /* flash_attn 需要特定后端支持，这里保守关闭以保证兼容性。 */
    cparams.flash_attn = false;

    g_ctx = whisper_init_from_file_with_params(model_path, cparams);
    if (g_ctx == NULL)
    {
        set_error(error, error_capacity, "whisper_init_from_file_with_params 失败（模型格式不被支持或内存不足）");
        return -3;
    }

    g_n_threads = (n_threads > 0) ? n_threads : 4;
    return 0;
}

AI_ASR_API int ai_asr_transcribe(const float* samples, int n_samples,
                                 const char* language, int translate,
                                 char* out_text, int out_capacity)
{
    if (g_ctx == NULL)
    {
        return -1;
    }

    if (samples == NULL || n_samples <= 0)
    {
        return -2;
    }

    if (out_text == NULL || out_capacity <= 0)
    {
        return -3;
    }

    out_text[0] = '\0';

    struct whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);

    params.n_threads = g_n_threads;
    params.translate = (translate != 0) ? true : false;
    /* language 为 NULL 或 "auto" 时 whisper 会自行检测语种。 */
    params.language = language;
    params.detect_language = false;

    /* 每段独立识别：不把上一句当上下文，避免对话中出现串词。 */
    params.no_context = true;
    params.no_timestamps = true;
    params.single_segment = false;

    /* 全部打印开关关闭：日志由 C# 侧统一管理（准则 6）。 */
    params.print_progress = false;
    params.print_realtime = false;
    params.print_timestamps = false;
    params.print_special = false;

    /*
     * 说明：whisper.cpp 新版内置了 VAD（params.vad / vad_model_path）。
     * 本项目的 VAD 在 C# 侧的音频线程上独立运行（WebRTC VAD 优先，能量 VAD 兜底），
     * 因此这里保持内置 VAD 关闭，避免两级 VAD 叠加导致语音段被二次切分。
     */
    params.vad = false;

    const int rc = whisper_full(g_ctx, params, samples, n_samples);
    if (rc != 0)
    {
        return -4;
    }

    const int segment_count = whisper_full_n_segments(g_ctx);
    int written = 0;

    for (int i = 0; i < segment_count; ++i)
    {
        const char* segment = whisper_full_get_segment_text(g_ctx, i);
        if (segment == NULL)
        {
            continue;
        }

        const int length = (int)strlen(segment);
        if (length <= 0)
        {
            continue;
        }

        /* 预留 1 字节给结尾的 '\0'。 */
        int room = out_capacity - written - 1;
        if (room <= 0)
        {
            break;
        }

        const int copy = (length < room) ? length : room;
        memcpy(out_text + written, segment, (size_t)copy);
        written += copy;
        out_text[written] = '\0';
    }

    trim_inplace(out_text);
    return (int)strlen(out_text);
}

AI_ASR_API void ai_asr_reset(void)
{
    /*
     * 由于参数固定为 no_context = true，每次 whisper_full 都是独立解码，
     * 上下文里不残留上一句，因此这里无需清空任何状态。
     * 保留该函数是为了在将来引入带状态的流式解码时有一个稳定的挂载点。
     */
}

AI_ASR_API void ai_asr_shutdown(void)
{
    if (g_ctx != NULL)
    {
        whisper_free(g_ctx);
        g_ctx = NULL;
    }
}

AI_ASR_API const char* ai_asr_system_info(void)
{
    return whisper_print_system_info();
}
