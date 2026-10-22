# 研究脚本（不属于产品代码）

这里放的是**性能实验**用的脚本，全部在一次性环境里跑过，产物已删除，只留脚本备查。
它们不参与构建，`AICompanion.sln` 里也不包含。

| 文件 | 用途 | 需要的环境 |
| --- | --- | --- |
| `bench_fp8_torch.py` | torch `_scaled_mm` 的 FP8 微基准（fp16 对照） | `torch >= 2.7`（直接用 `E:\cosyvoice\.venv-gpu`） |
| `cosy_profile_stages.py` | CosyVoice 一次合成的分阶段耗时（LLM / flow / hift） | CosyVoice 的 venv + 模型 |
| `trt_fp4_bench4.py` | TensorRT NVFP4 引擎的构建与计时 | `tensorrt-cu12==10.16`、`onnx>=1.18` |
| `trt_prec3.py` | 同一张图上 fp16 / fp8 / nvfp4 三方对比 | 同上 |

## 结论（详见 `VERIFICATION.md` §27.10、§27.11）

- **FP4**：torch 侧没有可用的 FP4 GEMM（`_scaled_mm` 在 sm_120 上直接拒绝），
  torchao 的 NVFP4 是"反量化回 bf16 再算"，比 bf16 慢 2.6 倍。
  TensorRT 能编出真 FP4 引擎，但 batch=1（自回归解码）比 fp16 **慢 3.1 倍**，
  大批量也只是打平、误差 13%。**不值得用。**
- **FP8**：大矩阵有 1.8×，但 sm_120 只支持 per-tensor 缩放（`Per-row scaling is not supported`），
  换成 LLM 上直接 `device-side assert`；而 80% 的时间花在 batch=1 解码上，那一档 FP8 没有收益。
  **保持 fp16。**

## 关于临时环境

`torch 2.9.1` / `torchao` / `tensorrt` 都是装在独立的临时 venv 里，跑完即删，
**不要**把它们装进 `E:\cosyvoice\.venv-gpu`：torchao 0.18 要求 torch ≥ 2.11，
装进 2.7.1 会让 `transformers` 导入失败，整个 CosyVoice 起不来。