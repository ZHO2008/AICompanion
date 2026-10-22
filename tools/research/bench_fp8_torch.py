"""在 torch 2.7.1 + sm_120 上测 FP8：能否用 _scaled_mm，batch=1 时到底是快是慢。

形状按 CosyVoice3-0.5B 的 LLM 实际尺寸取（hidden 896、FFN 4864、attention 投影），
另加大矩阵做对照。
"""
import torch


def bench(fn, iters=50, warmup=10):
    for _ in range(warmup):
        fn()
    torch.cuda.synchronize()
    s, e = torch.cuda.Event(True), torch.cuda.Event(True)
    s.record()
    for _ in range(iters):
        fn()
    e.record()
    torch.cuda.synchronize()
    return s.elapsed_time(e) / iters / 1000


def fp8_quant_rowwise(x):
    amax = x.abs().amax(dim=-1, keepdim=True).clamp(min=1e-8)
    scale = amax / 448.0
    return (x / scale).to(torch.float8_e4m3fn), scale.float()


def fp8_quant_tensor(x):
    amax = x.abs().max().clamp(min=1e-8)
    scale = amax / 448.0
    return (x / scale).to(torch.float8_e4m3fn), scale.float().reshape(1, 1)


def case(M, K, N, tag=""):
    a = torch.randn(M, K, device="cuda", dtype=torch.float16)
    w = torch.randn(N, K, device="cuda", dtype=torch.float16)
    results = {}
    el = bench(lambda: torch.nn.functional.linear(a, w))
    results["fp16"] = el

    qa, sa = fp8_quant_rowwise(a)
    qw, sw = fp8_quant_rowwise(w)
    sw = sw.t()   # [1,N]，_scaled_mm 要求 rowwise 的 scale_b 是这个形状
    wt = qw.t()   # [K,N]，col-major 视图
    try:
        torch._scaled_mm(qa, wt, sa, sw, out_dtype=torch.float16)
        results["fp8row"] = bench(lambda: torch._scaled_mm(qa, wt, sa, sw, out_dtype=torch.float16))
    except Exception as e:
        print("    fp8 rowwise 失败:", type(e).__name__, str(e)[:110])

    qa2, sa2 = fp8_quant_tensor(a)
    qw2, sw2 = fp8_quant_tensor(w)
    wt2 = qw2.t()
    try:
        torch._scaled_mm(qa2, wt2, sa2, sw2, out_dtype=torch.float16)
        results["fp8ts"] = bench(lambda: torch._scaled_mm(qa2, wt2, sa2, sw2, out_dtype=torch.float16))
    except Exception as e:
        print("    fp8 tensorwise 失败:", type(e).__name__, str(e)[:110])

    line = "  %-14s M=%-5d K=%-5d N=%-5d" % (tag, M, K, N)
    for k in ("fp16", "fp8row", "fp8ts"):
        if k in results:
            line += "  %s %8.3f ms" % (k, results[k] * 1e3)
    if "fp16" in results and "fp8row" in results:
        line += "   rowwise/fp16 %.2f×" % (results["fp8row"] / results["fp16"])
    print(line)


if __name__ == "__main__":
    print("torch", torch.__version__, "| cuda", torch.version.cuda,
          "| cap", torch.cuda.get_device_capability(0),
          "| gpu", torch.cuda.get_device_name(0))
    try:
        a = torch.randn(1, 896, device="cuda", dtype=torch.float16)
        w = torch.randn(4864, 896, device="cuda", dtype=torch.float16)
        qa, sa = fp8_quant_rowwise(a)
        qw, sw = fp8_quant_rowwise(w)
        sw = sw.t()
        o = torch._scaled_mm(qa, qw.t().contiguous().t(), sa, sw, out_dtype=torch.float16)
        torch.cuda.synchronize()
        print("  _scaled_mm fp8 可用，输出", tuple(o.shape), o.dtype)
    except Exception as e:
        print("  _scaled_mm fp8 不可用:", type(e).__name__, str(e)[:200])

    print("\n== CosyVoice3-0.5B 的 LLM 形状（batch=1 = 解码常态）==")
    case(1, 896, 4864, "FFN 上行")
    case(1, 4864, 896, "FFN 下行")
    case(1, 896, 896, "attn 投影")
    print("\n== 大矩阵 ==")
    case(512, 896, 4864, "FFN 上行")
    case(4096, 4096, 4096, "方阵")