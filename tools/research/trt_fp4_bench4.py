"""TRT NVFP4 正确布局：权重 [N,K] 沿最后一维（K）打包 + DequantizeLinear(block=16) + Transpose + MatMul。"""
import os
import time

import numpy as np
import torch


def quant_nk(w_nk, block=16):
    """w [N,K] → packed uint8 [N,K/2]（沿 K 打包，低 nibble 在前）、scales fp32 [N,K/16]。"""
    levels = torch.tensor([0.0, 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0], device="cuda")
    x = torch.from_numpy(np.ascontiguousarray(w_nk)).cuda().float()
    N, K = x.shape
    blk = x.reshape(N, K // block, block)
    amax = blk.abs().amax(dim=-1, keepdim=True).clamp(min=1e-6)
    scale = (amax / 6.0).clamp(min=2.0 ** -6)
    q = blk / scale
    idx = (q.abs().unsqueeze(-1) - levels).abs().argmin(dim=-1).to(torch.uint8)
    nib = (idx | ((q < 0).to(torch.uint8) << 3)).reshape(N, K)
    packed = (nib[:, 0::2] | (nib[:, 1::2] << 4)).to(torch.uint8).cpu().numpy()
    return packed.tobytes(), scale.squeeze(-1).cpu().numpy().astype(np.float32)


def build(onnx_path, w_nk, fp4=True):
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    N, K = w_nk.shape
    x = helper.make_tensor_value_info("X", TensorProto.FLOAT16, ["M", K])
    y = helper.make_tensor_value_info("Y", TensorProto.FLOAT16, ["M", N])
    if fp4:
        packed, sc = quant_nk(w_nk)
        inits = [
            helper.make_tensor("W_fp4", TensorProto.FLOAT4E2M1, [N, K], packed, raw=True),
            helper.make_tensor("W_scale", TensorProto.FLOAT, [N, K // 16], sc.tobytes(), raw=True),
        ]
        nodes = [
            helper.make_node("DequantizeLinear", ["W_fp4", "W_scale"], ["W_dq"], axis=1, block_size=16),
            helper.make_node("Transpose", ["W_dq"], ["W_t"], perm=[1, 0]),
            helper.make_node("MatMul", ["X", "W_t"], ["Y"]),
        ]
    else:
        inits = [numpy_helper.from_array(w_nk.astype(np.float16), "W_hp")]
        nodes = [helper.make_node("Transpose", ["W_hp"], ["W_t"], perm=[1, 0]),
                 helper.make_node("MatMul", ["X", "W_t"], ["Y"])]
    m = helper.make_model(helper.make_graph(nodes, "g", [x], [y], inits),
                          opset_imports=[helper.make_opsetid("", 21)])
    m.ir_version = 10
    onnx.save(m, onnx_path)


def engine(onnx_path, plan_path, K, logger):
    import tensorrt as trt
    b = trt.Builder(logger)
    net = b.create_network(1 << int(trt.NetworkDefinitionCreationFlag.EXPLICIT_BATCH))
    p = trt.OnnxParser(net, logger)
    with open(onnx_path, "rb") as f:
        if not p.parse(f.read()):
            for i in range(p.num_errors):
                print("    解析失败:", str(p.get_error(i)).split("\n")[0][:130])
            return None
    cfg = b.create_builder_config()
    cfg.set_memory_pool_limit(trt.MemoryPoolType.WORKSPACE, 2 << 30)
    for fl in ("FP16", "FP4"):
        try:
            cfg.set_flag(getattr(trt.BuilderFlag, fl))
        except Exception:
            pass
    prof = b.create_optimization_profile()
    prof.set_shape("X", (1, K), (64, K), (4096, K))
    cfg.add_optimization_profile(prof)
    t = time.time()
    ser = b.build_serialized_network(net, cfg)
    if ser is None:
        return None
    data = bytes(ser)
    with open(plan_path, "wb") as f:
        f.write(data)
    print("    引擎 %.1f s / %.1f MB" % (time.time() - t, len(data) / 1e6))
    return plan_path


def bench(plan_path, M, K, N, w_nk, iters=100):
    import tensorrt as trt
    logger = trt.Logger(trt.Logger.WARNING)
    with open(plan_path, "rb") as f, trt.Runtime(logger) as rt:
        eng = rt.deserialize_cuda_engine(f.read())
    ctx = eng.create_execution_context()
    ctx.set_input_shape("X", (M, K))
    x = torch.randn(M, K, device="cuda", dtype=torch.float16)
    y = torch.empty(M, N, device="cuda", dtype=torch.float16)
    ctx.set_tensor_address("X", x.data_ptr())
    ctx.set_tensor_address("Y", y.data_ptr())
    stream = torch.cuda.current_stream()
    for _ in range(20):
        ctx.execute_async_v3(stream.cuda_stream)
    stream.synchronize()
    s, e = torch.cuda.Event(True), torch.cuda.Event(True)
    s.record(stream)
    for _ in range(iters):
        ctx.execute_async_v3(stream.cuda_stream)
    e.record(stream)
    stream.synchronize()
    el = s.elapsed_time(e) / iters / 1000.0
    ref = x.float() @ torch.from_numpy(np.ascontiguousarray(w_nk)).cuda().float().t()
    err = ((y.float() - ref).pow(2).mean().sqrt() / ref.pow(2).mean().sqrt()).item()
    return el, err


if __name__ == "__main__":
    import tensorrt as trt
    K = N = 4096
    rng = np.random.default_rng(0)
    W = (rng.standard_normal((N, K)) * 0.02).astype(np.float32)
    logger = trt.Logger(trt.Logger.WARNING)
    print("TRT", trt.__version__, "|", torch.cuda.get_device_name(0))

    results = {}
    for tag, fp4 in (("fp16", False), ("nvfp4", True)):
        work = os.environ.get("COSYVOICE_WORK", "cosyvoice-src")
        op, pp = os.path.join(work, "n_%s.onnx" % tag), os.path.join(work, "n_%s.plan" % tag)
        print("\n-- %s --" % tag)
        build(op, W, fp4)
        if engine(op, pp, K, logger) is None:
            continue
        for M in (4096, 512, 1):
            el, err = bench(pp, M, K, N, W)
            print("   M=%-5d %9.4f ms  %8.1f TFLOPS  误差 %5.1f%%"
                  % (M, el * 1e3, 2 * M * N * K / el / 1e12, err * 100))
            results[(tag, M)] = (el, err)

    print("\n== 时延比（nvfp4 / fp16）==")
    for M in (4096, 512, 1):
        a, b = results.get(("fp16", M)), results.get(("nvfp4", M))
        if a and b:
            print("   M=%-5d fp16 %8.4f ms → nvfp4 %8.4f ms   %.2f×   误差 %.1f%%"
                  % (M, a[0] * 1e3, b[0] * 1e3, b[0] / a[0], b[1] * 100))