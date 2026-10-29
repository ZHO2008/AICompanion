"""三方对比：同一个 TRT 图结构下 fp16 / fp8(E4M3) / nvfp4 的 GEMM 时延与误差。"""
import os
import time

import numpy as np
import torch

from trt_fp4_bench4 import bench, engine, quant_nk


def build_prec(path, w_nk, prec):
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    N, K = w_nk.shape
    x = helper.make_tensor_value_info("X", TensorProto.FLOAT16, ["M", K])
    y = helper.make_tensor_value_info("Y", TensorProto.FLOAT16, ["M", N])

    if prec == "fp16":
        inits = [numpy_helper.from_array(w_nk.astype(np.float16), "W_hp")]
        nodes = [helper.make_node("Transpose", ["W_hp"], ["W_t"], perm=[1, 0]),
                 helper.make_node("MatMul", ["X", "W_t"], ["Y"])]
    elif prec == "fp8":
        amax = float(np.abs(w_nk).max())
        scale = w_nk / amax
        q = torch.from_numpy(scale).to(torch.float8_e4m3fn).view(torch.uint8).numpy().tobytes()
        inits = [
            helper.make_tensor("W_fp8", TensorProto.FLOAT8E4M3FN, [N, K], q, raw=True),
            helper.make_tensor("W_scale", TensorProto.FLOAT, [1], np.array([amax], dtype=np.float32).tobytes(), raw=True),
        ]
        nodes = [helper.make_node("DequantizeLinear", ["W_fp8", "W_scale"], ["W_dq"]),
                 helper.make_node("Transpose", ["W_dq"], ["W_t"], perm=[1, 0]),
                 helper.make_node("MatMul", ["X", "W_t"], ["Y"])]
    else:  # nvfp4
        packed, sc = quant_nk(w_nk)
        inits = [
            helper.make_tensor("W_fp4", TensorProto.FLOAT4E2M1, [N, K], packed, raw=True),
            helper.make_tensor("W_scale", TensorProto.FLOAT, [N, K // 16], sc.tobytes(), raw=True),
        ]
        nodes = [helper.make_node("DequantizeLinear", ["W_fp4", "W_scale"], ["W_dq"],
                                  axis=1, block_size=16),
                 helper.make_node("Transpose", ["W_dq"], ["W_t"], perm=[1, 0]),
                 helper.make_node("MatMul", ["X", "W_t"], ["Y"])]

    m = helper.make_model(helper.make_graph(nodes, "g", [x], [y], inits),
                          opset_imports=[helper.make_opsetid("", 21)])
    m.ir_version = 10
    onnx.save(m, path)


if __name__ == "__main__":
    import tensorrt as trt
    K = N = 4096
    rng = np.random.default_rng(0)
    W = (rng.standard_normal((N, K)) * 0.02).astype(np.float32)
    logger = trt.Logger(trt.Logger.WARNING)
    print("TRT", trt.__version__, "|", torch.cuda.get_device_name(0))

    res = {}
    for prec in ("fp16", "fp8", "nvfp4"):
        work = os.environ.get("COSYVOICE_WORK", "cosyvoice-src")
        op, pp = os.path.join(work, "p_%s.onnx" % prec), os.path.join(work, "p_%s.plan" % prec)
        print("\n-- %s --" % prec)
        try:
            build_prec(op, W, prec)
        except Exception as e:
            print("    建图失败:", type(e).__name__, str(e)[:150]); continue
        if engine(op, pp, K, logger) is None:
            continue
        for M in (4096, 512, 1):
            el, err = bench(pp, M, K, N, W)
            tf = 2 * M * N * K / el / 1e12
            print("   M=%-5d %9.4f ms  %7.2f TFLOPS  误差 %5.1f%%" % (M, el * 1e3, tf, err * 100))
            res[(prec, M)] = (el, tf, err)

    print("\n== 汇总 ==")
    print("   M       fp16(ms/TFLOPS)     fp8(ms/TFLOPS)      nvfp4(ms/TFLOPS)    误差(fp8/fp4)")
    for M in (4096, 512, 1):
        row = "   %-6d" % M
        for prec in ("fp16", "fp8", "nvfp4"):
            v = res.get((prec, M))
            row += ("  %8.4f / %6.2f" % (v[0] * 1e3, v[1])) if v else "        -     "
        e8 = res.get(("fp8", M)); e4 = res.get(("nvfp4", M))
        row += "    %s / %s" % (("%.1f%%" % (e8[2] * 100)) if e8 else "-",
                                ("%.1f%%" % (e4[2] * 100)) if e4 else "-")
        print(row)