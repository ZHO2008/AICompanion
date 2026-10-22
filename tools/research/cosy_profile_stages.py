"""CosyVoice3 一次合成的分阶段耗时：LLM / flow（扩散解码）/ hifigan。
用来判断"FP8 只在 M>=512 有 1.8×"到底能省多少总时间。"""
import sys
import time

import torch

sys.path.insert(0, r"E:\cosyvoice")
sys.path.insert(0, r"E:\cosyvoice\third_party\Matcha-TTS")

MODEL_DIR = r"E:\cosyvoice\pretrained_models\Fun-CosyVoice3-0.5B"
TEXT = "你好，我是你的桌面伙伴，今天由我来陪你。"
PROMPT_TEXT = "今天天气不错，我们一起去公园散步吧。<|endofprompt|>"
PROMPT_WAV = r"E:\DEEPSEEK\models\tts\ref_zh.wav"

STATS = {}


def timed(name, fn):
    torch.cuda.synchronize()
    t = time.time()
    out = fn()
    torch.cuda.synchronize()
    STATS[name] = STATS.get(name, 0.0) + (time.time() - t)
    return out


def main():
    from cosyvoice.cli.cosyvoice import AutoModel

    t0 = time.time()
    model = AutoModel(model_dir=MODEL_DIR, fp16=True)
    print("加载 %.1f 秒" % (time.time() - t0))

    llm = model.model.llm
    flow = model.model.flow
    hift = model.model.hift

    orig_llm_inf = llm.inference
    orig_flow_inf = flow.inference
    orig_hift_inf = hift.inference

    llm.inference = lambda *a, **k: timed("LLM", lambda: orig_llm_inf(*a, **k))
    flow.inference = lambda *a, **k: timed("flow", lambda: orig_flow_inf(*a, **k))
    hift.inference = lambda *a, **k: timed("hift", lambda: orig_hift_inf(*a, **k))

    # 预热
    for _ in model.inference_zero_shot(TEXT, PROMPT_TEXT, PROMPT_WAV, stream=False):
        pass
    STATS.clear()

    t_all = time.time()
    for _ in model.inference_zero_shot(TEXT, PROMPT_TEXT, PROMPT_WAV, stream=False):
        pass
    total = time.time() - t_all

    print("\n=== 一次合成的分阶段耗时 ===")
    acc = 0.0
    for k in ("LLM", "flow", "hift"):
        v = STATS.get(k, 0.0)
        acc += v
        print("  %-6s %6.2f 秒  %5.1f%%" % (k, v, v / total * 100))
    print("  %-6s %6.2f 秒（其余 %.2f 秒是前端/拼接）" % ("合计", acc, total - acc))
    print("  总耗时 %.2f 秒" % total)

    # 再统计 LLM 的 prefill / decode 次数
    print("\n提示：LLM 只在 prefill 时 M>1，自回归解码每步 M=1；"
          "FP8 在 M=1 上没有收益（实测持平或更慢）")


if __name__ == "__main__":
    main()