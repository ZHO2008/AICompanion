## 未发布 — 整合与清理（死代码 / 潜在泄漏）

按"整合所有改动、查死循环与泄漏"做了一次清理：

- **删除死代码**：`MainWindow` 里"按尺寸重建 Live2D 渲染器"的
  `ScheduleLive2dRebuild` / `RebuildLive2D` / `_live2dRebuildTimer`
  （调用点早已停用；且实测重建会把渲染器弄坏）。连同残留注释一起删掉。
- **给隔离区加上限**：`Live2DRenderer.FailedContexts` 原先是无界静态列表，
  用户反复用坏模型试错时会一直积累（每个失败上下文带着 GL 表面与 Cubism 资源）。
  现在是"最多 8 份"，足够覆盖正常试错。
- **确认无空转循环**：空闲调度器对"取消"这一支**已经**在 finally 里统一进冷却
  （注释里记着那个"每秒一轮刷了六分钟"的历史 bug），本次复查无新增问题。
- **确认探针资源已释放**：FBO 探针在 finally 里恢复默认帧缓冲并 `DeleteTexture` /
  `DeleteFramebuffer`，只跑一次。
- **确认无残留实验代码**：`DimOverlay`、`SetDim`、`AI_DBG_*` 开关、`DescribeState`
  均已不在代码中。

整合后自检：`ngl=28/28`、`LLM 就绪=True`、`启动问题 0 项`；
主程序 554 MB / 3024 句柄，llama-server 1387 MB / 405 句柄（7B/1.5B 全卸载量级正常）。
## 未发布 — LLM 模型适配：mmproj 多模态

- 新增 `llm.mmproj_path`（空 = 纯文本）。存在时给 llama-server 传 `--mmproj`。
- 留空时自动在模型同目录找 `*mmproj*.gguf`（取体积最小者，避免误捡大文件），
  找不到则按纯文本运行，并在日志里说明。
- 验证：qwen2.5-1.5B（无 mmproj）启动无异常，`ngl=28/28`；日志不出现 mmproj 行。

至此三项"未适配"的处理结论：

| 项 | 结果 |
| --- | --- |
| MoE 每层低估 | ✅ 读 `expert_count`，每层 ×1.15（仅内存，不写配置） |
| mmproj 多模态 | ✅ 配置项 + 自动发现 + `--mmproj` |
| token 计数 | ⚠️ 维持零开销启发式，不引入真分词（见下） |

token 计数的取舍：真分词有两条路——走 llama-server `/tokenize`（每条消息一次 HTTP 往返，
上下文裁剪被拖慢）或本地实现 BPE（几千行 + 词表加载）。两者都与"性能开销小"的要求冲突，
故保留启发式，偏差约 ±20%，只影响摘要裁剪精度，不影响生成。
## 未发布 — LLM 模型适配补齐（三项）

### 1) MoE 模型：识别专家数，预算留 15% 余量

读 GGUF 的 `<arch>.expert_count`；> 0 时把每层占用乘 1.15（仅内存中的 `GpuConfig.MoeExtraFactor`，
不写配置文件），并打印一行说明。稠密模型不受影响（实测 qwen2.5-1.5B 仍为 42 MB/层）。

### 2) token 计数：保持零开销启发式，但说明清楚

`EstimateTokens` 已是"中文 1 字≈1 token、其余 4 字符≈1 token"的启发式，**不引入真 tokenizer**：
真分词要么走 llama-server `/tokenize`（每条消息一次 HTTP 往返，上下文裁剪会被拖慢），
要么本地实现 BPE（几千行 + 词表加载）。这里选择**零额外开销**，代价是裁剪精度有 ±20% 偏差，
不影响生成本身。要精确时再加，属于可选优化。

### 3) 多模态 mmproj：留待下一步

`mmproj`（视觉/音频塔）是**独立文件**，不是主权重的一部分，需要新增配置项并传给
llama-server（`--mmproj`），同时把它的体积算进显存预算。当前未实现，遇到多模态模型会
只加载语言部分（能跑，但没有视觉能力）。
## 未发布 — LLM 模型适配（上下文长度 / 架构 / 层数）

之前只做了"层数（GGUF block_count）+ 每层占用（文件大小）"，上下文长度仍是写死的 4096：
32k/128k 的长上下文模型用不满，换小模型时又可能超过它训练时的上限。

- 新增 `GgufMetadataReader.TryReadModelInfo`：读 `general.architecture` 与 `<arch>.context_length`。
- `llm.context_length: 0` = 按模型自动：取 `min(模型训练上限, 8192)`；
  显式写非 0 则尊重配置，只在超过模型上限时夹回并告警。
- 启动日志会打印实际采用的依据。

实测（qwen2.5-1.5B）：

```
模型元数据：架构 qwen2，训练上下文 32768；上下文自动取 8192（上限 8192，再大会把 KV Cache 撑爆显存）
显存档位：8 GB（… KV 预留 按层数×上下文估算 448 MB，层数 28（从 GGUF 元数据探测））
启动完成：GPU 模式（ngl=28/28）
```

仍未适配的部分（如实记录）：

1. **token 计数是字符启发式**（`EstimateTokens`），不是真 tokenizer —— 对中文/英文混排的估算会有偏差，
   影响的是上下文裁剪的精度，不影响生成正确性。
2. **MoE 模型**（如 Qwen-MoE、DeepSeek-MoE）的每层占用会被低估：专家权重按层均摊不均，
   按"文件大小 ÷ 层数"算出的每层偏小 → ngl 给多了可能在运行时 OOM（有 OOM 回退兜底，会减层重启）。
3. **多模态/视觉模型**（mmproj）未特别处理，目前只按主权重估算。
## 未发布 — ngl / KV 全部按"实际模型 + 实际显存"自动算

之前只把"预留 + 安全系数"做成档位，每层占用和 KV 预留还是写死的。现在四项全部自适应：

| 参数 | 自动依据 |
| --- | --- |
| `gpu.reserved_mb` | 显存档位（1/2/3/4/6/8/10/12/16 GB） |
| `gpu.safety_factor` | 显存档位 |
| `gpu.per_layer_mb` | **GGUF 文件大小 ÷ 层数 × 1.1**（1.5B≈42、3B≈60、7B≈178、14B≈200 MB） |
| `gpu.kv_cache_reserved_mb` | **层数 × 上下文长度**（16 MB/层·4096ctx，夹在 128 MB 与总显存 25% 之间） |
| 层数 L | GGUF 元数据；读不到才回退 28 |

所以换模型（不限 1.5B）时不用改配置。按公式推算（同一台 8 GB 卡）：

| 模型 | 每层 | KV | ngl | 结论 |
| --- | --- | --- | --- | --- |
| 1.5B Q4（28 层） | 41 MB | 448 MB | 28/28 | 全卸载 |
| 3B Q4（36 层） | 60 MB | 576 MB | 36/36 | 全卸载 |
| 7B Q4（28 层） | 178 MB | 448 MB | 28/28 | 全卸载 |
| 14B Q4（48 层） | 200 MB | 768 MB | 24/48 | 部分卸载 |

1.5B 实测仍为 `ngl=28/28`；启动日志会把四项依据一次打全：

```
显存档位：8 GB（总 8151 MB；预留 档位 1024 MB，安全系数 档位 0.80，
每层 按 GGUF 估算 42 MB，KV 预留 按层数×上下文估算 448 MB，层数 28（从 GGUF 元数据探测））
```
## 未发布 — 按显存档位自动决定 ngl（1/2/3/4/6/8/10/12/16 GB）

以前用固定预算 `reserved_mb=4096` + `per_layer_mb=150`（按 7B 拍的），两头都不对：
4 GB 及以下的卡扣完 4 GB 直接算成"显存不足"走 CPU；8 GB 卡又白白让出 4 GB，
1.5B 模型只卸载 14/28 层 → 46.8 tok/s。

改动：

- 新增 `VramTiers`：按总显存分档给"系统预留 + 安全系数"
  1 GB → 192 MB × 0.55；2 GB → 256 × 0.60；3 GB → 384 × 0.65；4 GB → 512 × 0.70；
  6 GB → 768 × 0.75；8 GB → 1024 × 0.80；10 GB → 1280 × 0.82；12 GB → 1536 × 0.84；
  16 GB → 2048 × 0.85。（阈值按 MiB 实测量级，8 GB 卡 nvidia-smi 报 8151）
- 每层占用改为**按 GGUF 文件大小 ÷ 层数自动估算**（1.5B ≈ 42 MB/层，而不是固定 150）。
- 三个配置项 `gpu.reserved_mb` / `gpu.per_layer_mb` / `gpu.safety_factor` 现在 **0 = 自动**，
  写具体值则照旧覆盖（保留手动调参能力）。
- 日志会打印选中的档位与依据，例如：
  `显存档位：8 GB（总 8151 MB；预留 档位 1024 MB，安全系数 档位 0.80，每层 按 GGUF 估算 42 MB）`

实测（RTX 5060 8 GB + qwen2.5-1.5B Q4）：`ngl` 从 14/28 → **28/28**，
应用内吞吐 **46.8 → 159.7 tok/s**（3.4 倍）。

另外删除了 `bin\` 里旧的 llama.cpp 产物（10 月 17 日构建，比新编慢 4 倍多），
现在统一走 `modules/llm-cuda`（新编 CUDA）或 `modules/llm-vulkan`。
## 未发布 — 双后端（CUDA / Vulkan）实测对照

同一份 `third_party/llama.cpp`、同一套编译参数，只改后端；模型 qwen2.5-1.5B Q4_K_M，
预热后、单次 64 max tokens：

| 后端 / 设备 | 满卸载（ngl=99） | 应用内（ngl=14/28） |
| --- | --- | --- |
| CUDA / RTX 5060（新编） | **182.8 tok/s** | 46.8 tok/s |
| Vulkan / RTX 5060 | 81.9 tok/s | — |
| CUDA / RTX 5060（旧 bin，10 月构建） | 41.4 tok/s | — |
| Vulkan / AMD 780M | 25.9 tok/s | 23.9 tok/s |
| CPU（8 核） | 19.0 tok/s | — |

结论：

1. 独显上 CUDA 仍是快路径（比 Vulkan 快 2.2 倍），而且**旧的那份 CUDA 构建慢了 4 倍多**
   （41.4 → 182.8），`bin\` 里那份该换了。
2. Vulkan 的价值在核显：780M 上 25.9 tok/s，比纯 CPU 快 36%，这是 CUDA 覆盖不到的场景。
3. 应用内只有 46.8 tok/s 的原因不是后端，而是 **ngl 算得太保守**（`gpu.reserved_mb=4096`
   把 14/28 层留在 CPU）。8 GB 卡跑 1.5B 完全够全卸载，值得单独调。

后端已按模块摆放：`modules/llm-cuda/`（34.7 MB ggml-cuda.dll）与 `modules/llm-vulkan/`
（40.6 MB ggml-vulkan.dll），产物不入库（.gitignore）。
## 未发布 — Vulkan 后端可用

编译出带 Vulkan 后端的 llama.cpp（`build-vulkan/`，不入库）。同一模型实测：

| 后端 / 设备 | tok/s |
| --- | --- |
| Vulkan / RTX 5060 | 81.9 |
| CUDA / RTX 5060 | 41.4 |
| Vulkan / AMD 780M | 25.9 |
| CPU（8 核） | 19.0 |

- 纯核显（780M + Vulkan）比纯 CPU 快约 36%，集显模式下终于吃到核显。
- 这份 llama.cpp 的 Vulkan 后端在 5060 上比现有 CUDA 后端快约一倍，值得重编 CUDA 后端再比。
- 构建脚本：`tools/build-vulkan.cmd`（依赖 VS 2026、Vulkan SDK、便携 CMake+Ninja）。
## 未发布 — Live2D 不显示的真正原因：跨显卡互操作

把今天改动过的渲染代码整份退回 `ef55f84` 复现，**照样不显示**（角色区 0.31%），
证明不是这轮代码改坏的。真实原因是**适配器不一致**：

- GL 上下文落在 **AMD Radeon 780M**，而 WPF/DWM 的 D3D 合成走 **NVIDIA RTX 5060**。
- `GLWpfControl` 靠共享纹理把画面交给 WPF，**跨适配器时共享失败但不报错** ——
  表现就是"渲染器已就绪、帧数在涨、探针能读到 15.6% 不透明像素，窗口里却什么都没有"。

验证：给 `AICompanion.exe` 设 Windows 显卡偏好「高性能（NVIDIA）」后，GL 上下文变成
`Vendor=NVIDIA / RTX 5060`，**角色区立刻从 0.31% 变成 13.8%**，模型正常显示。

注册表位置（GpuPreference=2 表示高性能/独显）：

```
HKCU\Software\Microsoft\DirectX\UserGpuPreferences
  AICompanion.exe = GpuPreference=2;
```

这也解释了"以前好好的"：切到集显模式、或驱动/显卡拓扑变化后，程序被分配的适配器变了，
GL 与合成器不再同一块卡。**代码没变，分配变了。**

顺带留下的诊断：`Live2DRenderer` 首帧后把一帧渲染进自建 FBO 并 `ReadPixels`，
日志会打印"[探针] FBO …里不透明像素 X 个（Y%）"，用于区分"没画"与"没合成"。

# 更新记录

版本号来自 `src/AICompanion.App/AICompanion.App.csproj` 的 `<Version>`，
打包脚本 `tools/pack.ps1` 会把它写进分发目录名、安装包名与 `VERSION.txt`。

---

## 未发布 — 空白角色（根因：路径根目录）

用户截图暴露的真相：程序显示「角色形象缺失，已使用纯色背景」，报错路径全是
`E:\DEEPSEEK\src\AICompanion.App\bin\Debug\net8.0-windows\Assets\Default\...` ——
说明相对路径的根被当成了 exe 目录，而不是仓库根。于是模型、立绘、表情一个都找不到，
界面只剩一块纯色背景，看起来就是"Live2D 空白、没显示"。

- `AppPaths` 的根初始值从"exe 目录"改成"自动找 config.yaml 所在目录"：
  先看当前工作目录，再从 exe 目录逐级向上找（`bin\Debug\net8.0-windows` → 仓库根）。
  这样即使从 `C:\` 或 exe 目录启动，也能定位到 `E:\DEEPSEEK`，不会再误降级成纯色。
- 同时说明：此前"切模式后重建渲染器"的做法会破坏渲染器创建（重建后没有"渲染器已就绪"），
  已停用，改为不在启动阶段创建过早的渲染器。

## 未发布 — Live2D 不显示（已修）

两个独立原因，都会让 Live2D 模型**整块消失**而日志一切正常（渲染器已就绪、帧数在涨、控件可见）：

- **缩放倍率 ≠ 1.00 时整树 LayoutTransform 会丢弃 D3DImage 内容。**
  `RootGrid` 上挂着 LayoutTransform 做倍率缩放，而 GLWpfControl 的内容是 D3D 共享纹理 ——
  实测倍率 1.04 时模型完全不可见，把倍率改回 1.00 立刻恢复（同一份构建、同一台机器）。
  用户此前把角色大小调到 104%，正好命中。现在 Live2D 活动时不挂整树变换，
  缩放交给"窗口变大 + 模型按视口铺满"。
- **宿主尺寸变化后旧的 D3D 表面不再被合成。** 切透明/覆盖会改变 GL 控件尺寸，
  之后旧表面失去合成，模型消失（实测覆盖态 10.3% 像素 → 透明态 0.7% → 重建后 17.9%）。
  现在尺寸变化（切模式、用户改倍率）后自动重建渲染器（去抖 350ms），
  实测覆盖 46.9% / 透明 35.9%，两种模式都能看到角色。

排查中被否定的假设（记录以免后人重走）：整体不透明度、WS_EX_TRANSPARENT、
WM_NCHITTEST/HTTRANSPARENT、输入区隐藏、气泡清空 —— 逐个单独排除后都不是原因。

## 未发布 — 设置面板开关 + 集显模式

- **AI 功能开关搬进设置面板。** 新增「AI 功能」页，三个勾选框（对话 / 语音输出 / 语音输入）
  直接对应 `llm.enabled` / `tts.enabled` / `asr.enabled`。面板存一份覆盖到 `Assets\appearance.json`
  （`llmEnabled` / `ttsEnabled` / `asrEnabled`），**不回写 config.yaml**（准则 9），点「全部跟随 config.yaml」清除。
  覆盖在启动最早期应用，所以关掉的功能压根不会被创建 —— 不载模型、不拉服务、不开麦克风。
  改动在下次启动生效（组件生命周期绑在启动阶段），界面上写明了这一点。
- **TTS 双降级断头。** 外部引擎（GPT-SoVITS）不可用时降级到 Piper，但原代码装完 Piper 就结束了，
  不再验一次 Piper 到底能不能用。实测（屏蔽独显后 GPT-SoVITS 的 CUDA 没了）：日志写"实际引擎=piper"，
  可本机根本没装 `piper.exe` 与语音模型，于是**一句话都说不出来，也不报错**。
  现在装完会复查 `IsAvailable`，不可用就继续降到 Windows 内置语音（SAPI），并如实提示"语音服务与 Piper 都不可用"。
- **屏蔽独显时 llama-server 会以 exit code 1 退出**（显卡消失导致 CUDA 上下文失效），程序按设计不自动重启，
  只提示"LLM 服务已离线"。切换显卡模式后需要重启程序，已记入 README。

集显模式实测（RTX 5060 关闭、仅 Radeon 780M、单屏）：`ngl=0`、llama-server 2.6 秒就绪、
1.5B Q4 实测 19 tok/s、whisper 正常、Live2D 在 780M 上渲染正常、启动问题 0 项。

## 未发布 — NPU 调研结论（文档）

查过 AMD 官方文档后确认本机 NPU 用不上，记录在 README「六、已知差距」第 7、8 条：

- 本机 CPU 是 Hawk Point（Family 25 Model 117，XDNA1 NPU，PCI 1022:1502）。
  AMD 兼容矩阵里 PHX/HPT 只支持 CNN INT8，`NLP BF16` 与 `LLM (OGA)` 仅 STX/KRK（Ryzen AI 300）。
  所以 LLM / TTS / ASR 都上不了 NPU；whisper.cpp 的 NPU 分支同样只支持 Ryzen AI 300。
- 另一个差距：`bin\` 里没有 Vulkan 后端，屏蔽独显后 llama.cpp 只能退回 CPU，
  Radeon 780M 的 12 CU 完全没参与。要利用 iGPU 需另编 `-DGGML_VULKAN=ON` 的版本。

## 未发布 — 功能开关与 Live2D 兼容性

- **LLM / TTS / ASR 可以单独关掉。** 新增 `llm.enabled` / `tts.enabled` / `asr.enabled`（默认 `true`）。
  关掉的那条链路完全不初始化：不加载模型、不占显存、不拉起外部服务、不打开麦克风、不占全局热键。
  之前 `tts.enabled=false` 只是"不合成"，日志仍打印 `TTS=gpt_sovits`、健康监控仍每 30 秒探活一次，
  看起来像"引擎起来了却没声音"；`asr.enabled=false` 也照样把识别模型读进内存。现在这些都按开关跳过，
  汇总行如实写"已关闭"，关掉 LLM 后发消息直接提示"对话功能已关闭"而不是报连接错误。
- **默认动作组（无名组 `""`）里的动作不生效。** 很多免费模型把 Motions 放在无名组，
  以前被当成"没有动作"（日志 `动作组 []`），角色站着不动且没有任何错误日志。现在归一成 `Idle`，
  渲染器还会在请求的组不存在时回落到默认组。
- **坏模型会让进程崩溃。** `model3.json` 里声明的 `.moc3` / 贴图缺失时，加载失败路径刻意不释放 GL 上下文，
  该上下文的终结器随后在 GC 线程抛 `Win32Exception(13) 数据无效`，没有任何 catch 能接住，进程直接消失
  （日志里只剩一行"后台线程致命异常"）。现在创建原生资源**之前**先校验 moc3 与贴图，缺则跳过并降级静态图；
  仍失败的上下文则进入隔离区，既不释放也不回收。

Live2D 实测：Hiyori（10 动作）、免费模型冰糖（无 Motions）、免费模型艾莲（无名默认组 2 动作）均可加载；
含中文与空格的路径正常；无 `AvatarProfile.json` 时用内置默认映射并告警。

## 未发布 — 多显示器

本机实测环境：主屏 2560×1600 @150%（核显）、副屏 1920×1080 @100%（独显），
两块屏缩放率不同，正好是最容易出问题的那种组合。

- **角色拖不到副屏。** 所有夹取都用 `SystemParameters.WorkArea`，它只返回主屏工作区。
  主屏 150% 时工作区右边界是 1707 DIP，`Left` 被锁在约 2400 物理像素以内，而副屏从 2560 才开始，
  于是永远够不到；副屏在左边（负坐标）同理。现在改成按"窗口所在显示器"的**物理像素**工作区夹取，
  拖动位移也改用物理像素 —— 旧代码把整个位移按当前屏 DPI 除了一次，跨越缩放率不同的两块屏时
  误差叠加，实测鼠标停在 x=3400、窗口却落在 x=1390。
- **右上角按钮在主屏之外点不动。** 按钮与角色窗口右上角是**重叠**的，两者又都是独立的 Topmost 窗口。
  角色一旦 `Activate()` 就把按钮压住，点击实际落在角色上（`WindowFromPoint` 在按钮矩形内命中的是「AI 伴侣」）。
  现在把按钮和功能栏设为角色的 owned window，WPF 保证 owned 永远在 owner 之上。
- **功能栏第一次右键跑到屏幕左上角。** 窗口还没显示过时 HWND 是 0，`SetWindowPos` 什么也没做，
  窗口随后落在系统默认级联位置 (177,177)。现在定位前先 `EnsureHandle()` 把 HWND 建出来。
- **副屏上悬停不切回覆盖。** 命中判定用 `PointFromScreen`，它依赖 WPF 内部记录的窗口原点；
  窗口被挪到另一块缩放率不同的屏后原点不同步，于是鼠标明明在角色身上也判"窗口外"。
  现在直接用屏幕物理矩形比较，两块屏缩放率差多少都不影响。
- **`logging.minimum_level` 是死配置。** sink 在读 config.yaml 之前就按 Info 建好了，之后再没人改它，
  于是设成 debug 也一条诊断都看不到。现在 Host 启动完成后按配置改写 sink 级别。

顺带确认可用的：副屏上全屏检测正常（主屏全屏、副屏全屏都测了）；角色贴副屏右边缘时会保留 30% 可见，
右上角按钮自动夹在副屏内不被裁掉。

## 未发布 — 极端环境兼容性

不是看代码猜的，是拿分发目录在真实场景里逐项跑出来的。四个会真正挡住用户的：

- **安装路径带中文 → 语音识别直接不可用。** whisper 的原生层用 `fopen()` 开模型文件，
  按系统 ANSI 代码页（简体中文是 GBK）解释路径；C# 传过去的是 UTF-8 字节，于是在原生那边
  拼成乱码，`ai_asr_init` 返回 -2「无法打开模型文件」。英文目录一切正常，所以这个只有换台
  中文路径的机器才会暴露。现在路径含非 ASCII 时，用内容哈希在 `%LOCALAPPDATA%\\AICompanion\\cache\\asr`
  下建一个 ASCII 名字的硬链接交给原生层（跨盘时退回复制），配置与文件都不动。
- **config.yaml 存成 GBK（记事本"另存为 ANSI"）→ 中文静默变问号。** 按 UTF-8 硬读不会报错，
  只会把中文全变成 U+FFFD，用户看到"我填的角色名怎么成了问号"而日志里毫无线索。
  现在严格按 UTF-8 解码，失败则按 GBK 再解一次并告警。
- **重复双击会起两套。** 第二个实例会再拉一个 llama-server、抢同一个端口（GameMonitor 通信
  直接失败），显存被双份模型吃掉。现在有单实例互斥：第二次启动只把已在运行的窗口唤出来，
  然后安静退出。
- **宿主被强杀后 llama-server 变孤儿。** 清理逻辑压根不会跑，实测连续强杀攒到 10 个
  llama-server 常驻，用户下次启动看到"显存不足，切 CPU 模式"，其实是自己上次留下的。
  现在进程启动时把自己放进一个 `KILL_ON_JOB_CLOSE` 的 Job Object，宿主一死内核就把子进程收走。

另外给 manifest 补了 `longPathAware`：安装位置超过 260 字符时，没有这条连进程创建都失败。
（>260 的启动失败还有一层系统限制，`\\?\\` 前缀之外无法绕过，这条只能解决应用自身的文件读写。）

## 0.9.3 — 2026-10-30

修掉一个全屏场景下的老问题：前台有游戏或全屏视频时，角色窗口会自己抢模式。

原因是悬停切换按"窗口矩形"判定，而角色是 Topmost 的。全屏时那块矩形正压在画面上，
光标扫过就把模式切成「覆盖」，于是本该给游戏的点击被角色窗口截走；光标离开三秒又退回「透明」，
看起来就是"模式自己在跳、切不动"。

现在的处理是检测前台窗口是否铺满它所在的显示器：进全屏 → 切透明并暂停悬停切换，
退全屏 → 恢复成全屏前的模式；全屏期间手动点右上角按钮仍然有效，且以用户的选择为准。
开关是 `ui.fullscreen_quiet_mode`，默认 `true`。

另外两处顺手修的：

- **空闲维护会自己刷成死循环。** 维护任务本身要把 llama-server 拉满，CPU 一过 70% 就被取消；
  取消后条件又立刻成立，于是"开始 → 取消 → 开始"每秒一轮，实测连续跑了六分钟、日志刷了上千行、
  CPU 长期 70~80%。现在每轮结束（完成 / 取消 / 失败）都进冷却，
  由 `idle.maintenance_cooldown_minutes` 控制（默认 10 分钟）。
- **程序退出前没有任何线索。** 日志里只有"正在关闭 → 已退出"，分不清是自己崩了还是有人点了托盘退出。
  现在托盘「退出」与主窗口关闭都会写一行日志；另外补了三个未处理异常的兜底
  （`DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`），
  记录到日志并弹气泡，界面线程的异常不再直接把进程带走。

## 0.9.2 — 2026-10-29

修了一个从 0.8 起就存在、一直没被发现的毛病：`tts.gpt_sovits` 和 `tts.cosyvoice` 这两段配置从来没被读过。

代码里的属性叫 `GptSoVits` / `CosyVoice`，反序列化库按驼峰规则推出来的键名是 `gpt_so_vits` / `cosy_voice`，
跟配置里写的对不上；解析器又设了"不认识的键就跳过"，于是整段被丢掉，程序一直拿默认值在跑。
表现是：配置里填了权重和参考音频，引擎还是报"需要参考音频"，`top_p` 也落回 1.0，切权重的接口压根没被调用。
修法是给这两个属性加显式别名，键名不再靠猜。

**0.9.1 及更早的版本都有这个问题。** 只用 piper 或系统语音的人感觉不到；要接 GPT-SoVITS / CosyVoice 的，必须升级。

其它改动：

- `config.default.yaml` 的默认模型改成包里真正带的那份（`models/llm/qwen2.5-1.5b-instruct-q4_k_m.gguf`）。
  原来写的是 `models/qwen2.5-7b-q4_k_m.gguf`，照模板装出来的机器会提示"LLM 模型文件缺失"。
- 打包时如果没有角色图，就用 `tools/draw_avatar.ps1` 生成一张占位立绘，免得新用户只看到一块纯色背景。
- 打包一律用 `config.default.yaml` 生成 `config.yaml`，不再复制开发机根目录那份（以前会把本机路径打进公开包）。
- 包里不再带 `Assets/appearance.json`、`Knowledge/.pending` 这类运行期状态。
- `config.yaml` 不再入库，模板仍在 `Core/Configuration/config.default.yaml`，缺文件时首次启动会自动生成。
- 工具脚本里写死的 `E:\DEEPSEEK` 换成了「`AICOMPANION_ROOT` → 当前目录 → 脚本位置」；
  `E:\cosyvoice-src\…` 改成读环境变量 `COSYVOICE_WORK`。
- README 补了"用自己训练的音色"一节：权重放哪、参考音频和文本怎么对、必填参数、看哪几行日志。
  顺带记了实测数：52 字出 13.36 秒音频、合成 5.4 秒；13 字热起来 2.8 秒。


## 0.9.1 — 2026-10-23

这一版是**真机运行验证**（VERIFICATION §31）之后的重打：修掉一个每次退出都必崩、
并且会把 `llama-server` 留成占显存孤儿进程的缺陷。

### 修复

- **退出必崩（0xC0000374 / 0xC0000005）**
  关窗时在 WPF UI 线程上释放 Live2D 的 GL/Cubism 原生对象会崩在 `ntdll.dll`（堆破坏）
  或 `nvoglv64.dll`（GL 上下文只活在 `GLWpfControl` 的渲染线程上）。
  现在只停渲染循环、把控件从可视树摘下，原生对象交给进程退出回收。
  连带修好：崩溃会跳过 `App.OnExit`，导致 `llama-server` 每次退出都残留 —— 现在托盘退出
  会正常走 `AppHost.DisposeAsync()` → "正在关闭 → 终止 llama-server → 已退出"，耗时 0.23 秒。
- **PowerShell 脚本编码**
  `tools/verify_dist.ps1` 与 `installer/install.ps1` 丢失 UTF-8 BOM 后，PowerShell 5.1 用
  `-File` 会按系统 ANSI（GBK）解析中文，整份脚本语法报错。已补回 BOM，`tools/*.ps1` 与
  `installer/*.ps1` 共 8 个脚本用 PS 5.1 语法解析器验证，错误 0。

### 变更

- `config.default.yaml` 的默认模型改成包里真正自带的那份
  （`models/llm/qwen2.5-1.5b-instruct-q4_k_m.gguf`）。以前指向 `models/qwen2.5-7b-q4_k_m.gguf`，
  新装的机器必然报"LLM 模型文件缺失"，开箱不可用。
- 打包时若没有角色图，自动用 `tools/draw_avatar.ps1` 生成一张占位立绘（自己画的，无第三方版权），
  新用户装完不会只看到一块纯色背景。
- 分发目录不再带运行期个人状态：`Assets/appearance.json`（窗口尺寸/缩放）、`Knowledge/.pending`（提醒状态）。
- 仓库里最后几处写死的本机绝对路径（`E:\DEEPSEEK`）清掉了：
  `tools/Fetcher`、`tools/probes/ExportProbe`、`tools/probes/OomProbe`、`tools/draw_avatar.ps1`、`tools/soak_test.ps1`
  现在统一按「环境变量 `AICOMPANION_ROOT` → 当前工作目录 → 脚本自身位置」解析，换台机器 clone 下来就能直接跑。
- README 里的目录树与日志示例改用相对写法，不再出现开发机盘符。
- `tools/pack.ps1` 改为**总是**用 `config.default.yaml` 生成分发目录里的 `config.yaml`：
  以前直接复制开发机的根目录 `config.yaml`，会把本机绝对路径（模型、参考音频、端口）打进公开安装包。


- README 与实测对齐：目录树补 `tools/cosyvoice-patches`、`tools/research`、`probes/TtsBackendsProbe`；
  §九 验证记录改成按章节查的索引；§六 补 Piper 语音模型路径（`models/piper/zh_CN`），
  `ngl` 说明改成实测的 5/8/9 / 28 并写清浮动原因。
- 新增 VERIFICATION §31（第四轮真机运行：启动、退出、真实推理的证据与遗留清单）、
  §32（0.9.1 打包过程与异目录验证输出）。
- 清理 `Knowledge/.pending` 下 403 条自动化测试留下的候选（备份为 `artifacts/*.zip`，不入库）。
- 版本号 0.9.0 → 0.9.1：`csproj`、`installer/AICompanion.iss`、`installer/install.ps1`、
  `tools/verify_dist.ps1`、README 产物清单。

### 打包与验证

- 分发目录 `dist/AICompanion-0.9.1-win-x64`：**1.90 GB**
- 安装包 `dist/AICompanion-0.9.1-full-setup.exe`：**1621.4 MB**
- 异目录启动验证（`tools/verify_dist.ps1`）：必需文件 7/7、无授权素材、主窗口出现、
  内存 359.9 MB / 句柄 766、日志 **0 错误行**、`ngl=10/28`、子进程清理干净 → 通过
- 构建：`dotnet build AICompanion.sln -c Debug` → 0 Warning / 0 Error
- GitHub Release [`v0.9.1`](https://github.com/ZHO2008/AICompanion/releases/tag/v0.9.1) 附带
  `AICompanion-0.9.1-full-setup.exe`（1621.4 MB，SHA256 `c01d7315…249cc7`）与 `SHA256SUMS.txt`；
  GitHub 记录的 `digest` 与本机计算的哈希一致

---

## 0.9.0 — 2026-10-21

首个公开基线（源码 + 双许可），包含：

- 对话管线（预处理 → LLM → TTS）、上下文滑动窗口与三级摘要、知识库热重载与待审核候选
- 按住说话（Home 键）、WebRTC VAD、whisper.cpp 桥接（`bin/ai_asr.dll`）
- 三条 TTS 后端：Piper / GPT-SoVITS / CosyVoice3，另有 Windows SAPI 兜底
- Live2D 真渲染（Cubism SDK，动作/口型/表情联动）、静态立绘与纯色块三级降级
- GameMonitor 游戏感知与显存让路、OOM 回退与 `ngl` 自动计算
- 自包含打包（含 .NET 8 运行时、模型与 CUDA 运行库；不含需要单独授权的 Live2D 素材）
