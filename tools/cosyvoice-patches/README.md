# CosyVoice 本地补丁（非上游代码）

上游 `E:\cosyvoice` 是通过 clone 得到的，下面四个改动都发生在**应用外部**，
所以放在这里做存档：重新 clone 上游后，把这两个文件覆盖回去即可。

| 仓库里的文件 | 覆盖到 |
| --- | --- |
| `server.py` | `<cosyvoice>\runtime\python\fastapi\server.py` |
| `frontend.py` | `<cosyvoice>\cosyvoice\cli\frontend.py` |

    Copy-Item tools\cosyvoice-patches\server.py   E:\cosyvoice\runtime\python\fastapi\server.py -Force
    Copy-Item tools\cosyvoice-patches\frontend.py E:\cosyvoice\cosyvoice\cli\frontend.py -Force

三个补丁都带注释"本地补丁（非上游代码）"，用 `Select-String -Pattern '本地补丁'` 能全部找出来。

## 1. server.py：上传落地成临时文件

上游把 `UploadFile` 读成 tensor 再传给 `inference_*`，而 `frontend.load_wav` 只接受路径，
必然抛 `TypeError: Invalid file: tensor([[...]])`。改为 `_save_upload` 落盘、`_stream_and_cleanup`
拖完删掉。**真正会崩，不修不行。**

## 2. server.py：`--fp16`（默认自动）

GPU 上 `fp16=True` 走 autocast，实测 RTF 1.30 → 1.00。参数 `-1` 表示"有 CUDA 就开"。

## 3. server.py：`--warmup`（默认开）

启动后先合成一句并丢弃。每个进程的第一次推理要多付 20–70 秒（CUDA kernel 编译 +
onnxruntime EP 初始化），预热把这笔开销挪到启动阶段：服务就绪后的**第一句**从 23–26 秒
降到 5.0 秒。代价是启动慢 30–70 秒。`--warmup 0` 可关，`--warmup_wav` 可换参考音频。

## 4. frontend.py：wetext 优先读本地 FST

上游每次都 `snapshot_download("pengzhendong/wetext")`，本机 TLS 被拦截就退化成"不规范化"
（数字不念、日期不展开）。改为优先 `WETEXT_DIR` 或 `<repo>/pretrained_models/wetext` 下的
本地 FST，找不到才回退上游行为。FST 放在 `E:\cosyvoice\pretrained_models\wetext\`。

## 5. frontend.py：语音 tokenizer 的 ONNX provider 改为显式开关

装 `onnxruntime-gpu` 后上游会自动选 CUDA EP，但 8 GB 卡上"CosyVoice + 另一个 GPU 服务"
同开会爆显存，且收益只有约 0.9 秒。改为：只有 `COSYVOICE_ORT_GPU=1` 才用 CUDA EP，
默认仍 `CPUExecutionProvider`。

## 验证

- `python -m py_compile` 两个文件；
- `tools\probes\TtsBackendsProbe` 49/49（真实服务）；
- 细节与实测数字见 `VERIFICATION.md` §27.4。

上游文件版权归阿里巴巴（Apache-2.0），这两个副本保留了原始版权头。