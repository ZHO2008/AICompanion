# AI 伴侣桌面程序

[![最新版本](https://img.shields.io/github/v/release/ZHO2008/AICompanion?label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC)](https://github.com/ZHO2008/AICompanion/releases/latest)
[![许可证](https://img.shields.io/github/license/ZHO2008/AICompanion)](LICENSE)

在 Windows 上运行的本地化 AI 桌面伴侣。完全本地推理、低延迟语音对话、桌宠交互、
长对话记忆与角色一致性，GPU 优先、CPU 兜底，并在检测到游戏时自动让出显存。

本文件是**唯一权威的实现说明**：手册里存在内部矛盾或不可实现之处的地方，
这里逐条记录了「手册怎么写」「实际怎么做」「为什么」，便于后续接手者判断。
各版本改了什么见 [CHANGELOG.md](./CHANGELOG.md)，发布包见
[Releases](https://github.com/ZHO2008/AICompanion/releases)。

---

## 一、当前进度

手册 §7 的四个阶段均已实施：基础设施、核心骨架、对话管线与空闲调度、表现层与打包分发。
托管代码与原生组件可完整构建；对话、语音输入输出、游戏让路、知识库都能端到端跑通。
仍未闭合的部分（CUDA 运行时随包、Piper C API、目标机性能验收）在第六节逐条列出，不含糊。

| 模块 | 状态 | 说明 |
| --- | --- | --- |
| 配置系统 `ConfigLoader` | ✅ 完成 | 缺失自动生成、逐字段兜底、回环地址强制 |
| 日志 `FileLogger` | ✅ 完成 | 按日轮转、环形缓冲、按天清理、绝不抛异常 |
| `UnifiedKnowledgeManager` | ✅ 完成 | JSON 加载、原子热重载、无锁查询、导入三策略 |
| `PendingReviewService` | ✅ 完成 | 启动清理过期候选、每天最多提醒一次 |
| `GPUMemoryManager` | ✅ 完成 | nvidia-smi 查询、ngl 计算、GGUF 层数探测；OOM 回退与 `ngl=0` 切 CPU 三条路径均实测 |
| `LlamaServerController` | ✅ 完成 | 主程序持有进程、stderr OOM 回退、断流停止、杀进程卸载（三项均实测） |
| `HealthMonitor` | ✅ 完成 | 30s 探活、失败阈值、休眠唤醒处理 |
| `TTSManager` + `ITTSEngine` | ✅ 完成 | 三级音色兜底、降级、软重启、托盘重置 |
| `PiperEngine` | ⚠️ 部分 | 进程外 `piper.exe` 可用；C API 的 P/Invoke 待库编译后补齐 |
| `ChatterboxEngine` | ✅ 完成 | HTTP 合成、`/health` 可用性、软重启 |
| `GptSoVitsEngine` | ✅ 完成 | HTTP 合成、`/set_*_weights` 切训练模型、零样本；真实服务 30/30 |
| `CosyVoiceEngine` | ✅ 完成 | 5 种推理模式、裸 PCM→WAV、CosyVoice3 的 `<|endofprompt|>` 前缀；真实服务 49/49 |
| `ASRManager` | ✅ 完成 | 高优先级音频线程、VAD 分段、Faulted 状态机 |
| `WhisperCppRecognizer` | ✅ 完成 | 经 `ai_asr.dll` 桥接层调用 whisper.cpp（真实编译 + 真实推理验证，模型加载 279ms） |
| WebRTC VAD | ✅ 完成 | libfvad 真实构建为 `bin\libfvad.dll`；真实中文语音检出 78%，激进度单调性已验；修复了会让语音输入静默失效的 `fvad_reset` 缺陷 |
| `ContextManager` | ✅ 完成 | 滑动窗口、三级摘要、锚点记忆、归档；压缩已实测（48→30 条，摘要入栈，原文归档），并修掉 `.jsonl` 非合法 JSONL 的缺陷 |
| `ConversationRefiner` | ✅ 完成 | 显存门槛、GBNF 约束、只写 `.pending/` |
| `IdleTaskScheduler` | ✅ 完成 | 500ms 轮询、三条件触发、双条件中断 |
| `GameMonitor.exe` | ✅ 完成 | 动态采样、双重判断、心跳、冷却期（真实 TCP 往返 26 项断言全通过） |
| `GameModeCoordinator` | ✅ 完成 | 优雅卸载/恢复、冷却期回传 |
| WPF 表现层 | ✅ 完成 | 透明窗口、鼠标穿透、托盘、静态图三级兜底、气泡、设置面板 |
| `start_all.bat` | ✅ 完成 | 已修正手册中的两处失效逻辑；实际运行五个步骤全部通过 |
| 对话管线 §4 | ✅ 完成 | 端到端验证全通过（预处理 17ms / LLM 436ms on GPU） |
| Live2D 真渲染 | ✅ 完成 | Cubism SDK（`Live2DCSharpSDK.OpenGL.WPF`）真实渲染，Hiyori 完整显示；动作/口型/表情联动 46 项断言全过；合成与命中问题已定位修复（VERIFICATION §19–§20） |
| Piper C 库 | ⏸ 后置 | 决策 Q9；已有 SAPI 兜底，不影响出声 |
| SAPI 语音兜底 | ✅ 完成 | Piper 不可用时自动降级到 Windows 内置语音，任何机器都能发声 |
| llama-server CUDA 构建 | ✅ 完成 | `bin\ggml-cuda.dll` 54MB；GPU 计算进程列表 + 显存 +1152MB 三重证据；CPU 版备份在 `bin\cpu-backup\` |
| 退出与资源回收 | ✅ 完成 | 关窗只停渲染循环、托盘"退出"走 `Shutdown()` → `AppHost.DisposeAsync()` 终止 llama-server；第四轮真机跑出"退出必崩 + 孤儿进程"并修复（VERIFICATION §31） |

> 验证细节与**尚未验证项**的完整清单见 [VERIFICATION.md](./VERIFICATION.md)。
> 截至第四轮（2026-10-23），VERIFICATION 记录到 §32：启动 8.4 秒、退出 0.23 秒、真实推理 0.7 秒/16 token；
> 0.9.1 打包与异目录验证见 §32，0.9.2 修了什么见 [CHANGELOG.md](./CHANGELOG.md)。

---

## 二、快速开始

### 2.0 只想先跑起来（不编译）

到 [Releases](https://github.com/ZHO2008/AICompanion/releases/latest) 下
`AICompanion-<版本>-full-setup.exe`（0.9.2 是 1621.8 MB），双击安装 —— 自带 .NET 8 运行时、
LLM/ASR 模型与 CUDA 运行库，装完即可对话，不需要管理员权限，装到
`%LOCALAPPDATA%\Programs\AICompanion`。校验值在同页的 `SHA256SUMS.txt`。

包里不含需要单独授权的 Live2D 素材，按包内 `素材说明.md` 自取；缺了会降级到静态立绘，不会崩。
下面 2.1 起是"从源码自己构建"的路子。

### 2.1 先决条件：全新 clone 与"开发机"的差别

仓库里只放代码与文档。上游源码、专有库、模型都因为体积或许可原因**不入库**，
所以直接 clone 下来是跑不起来的，先按这张表补齐：

| 需要什么 | 怎么来 | 缺了会怎样 |
| --- | --- | --- |
| .NET 8 SDK | [dot.net](https://dot.net) | 编不出托管代码 |
| VS 2022 Build Tools（MSVC x64） | 安装时勾选"使用 C++ 的桌面开发" | 编不出原生组件 |
| **whisper.cpp 1.9.5** | clone 到 `third_party\whisper.cpp` 并切到该版本 | `build_native.bat asr` 失败——`ai_asr` 桥接层是按这个版本的 C API 编的 |
| **llama.cpp 0.7.0** | clone 到 `third_party\llama.cpp` 并切到该版本 | 编不出 `llama-server.exe` |
| CMake + Ninja | 用 `tools\Fetcher` 下载，解压到 `tools\toolchain\` | `build_*.bat` 找不到工具链 |
| `Live2DCubismCore.dll` | Live2D 官方 Cubism SDK for Native 里取，放**仓库根目录** | 编译出警告，运行时 Live2D 不可用（降级为静态立绘） |
| LLM 模型（GGUF） | 放 `models\llm\` 并改 `llm.model_path` | 无法对话 |
| ASR 模型 | whisper 的 `ggml-base.bin` → `models\asr\ggml-base.bin` | 无法识别语音 |
| Piper（可选） | 二进制度放 `tools\piper\`、模型放 `models\piper\` | 自动降级到系统 SAPI（系统装有中文语音包即可） |
| Live2D 模型（可选） | 解压到 `Hiyori\`，保持 `Hiyori.model3.json` 这个路径 | 降级为静态立绘；再没有立绘则纯色块 |

> 版本号取自构建时所用源码的 `CMakeLists.txt`（whisper.cpp 的 `WHISPER_VERSION`、llama.cpp 的 `LLAMA_VERSION`）。
> `ai_asr` 直接调用 whisper.cpp 的 C API，上游大改接口后需要同步适配，所以版本尽量对上。

补齐上述依赖后，按顺序执行：

```bat
REM 1) 构建托管代码
dotnet build AICompanion.sln -c Release

REM 2) 构建原生组件，产物统一进 bin\
build_native.bat asr        REM ai_asr.dll（whisper.cpp 桥接层）
tools\build_fvad.bat        REM libfvad.dll（WebRTC VAD）
build_native.bat llama cpu  REM llama-server 的 CPU 版（只依赖 MSVC）
REM    想要 GPU 推理则改用下面这条（Ninja 并行 + CUDA，约 20 分钟）：
REM    tools\build_llama_cuda.bat 6

REM 3) 一键启动（清理残留 → GameMonitor → 主程序）
start_all.bat
```

首次运行时程序会自动生成 `config.yaml`（从嵌入资源写出，带完整中文注释）与 `Knowledge/README.md`。

> 两个 llama-server 版本可以互换：CUDA 构建会把原 CPU 版备份到 `bin\cpu-backup\`，
> 回退时复制回去即可。主程序会自动识别当前二进制是否具备 GPU 后端，
> 如实上报运行模式（见"决策 4"）。

---

## 三、目录结构

```
<安装目录>
├── AICompanion.sln
├── config.yaml                     # 首次运行自动生成
├── start_all.bat                   # 启动脚本 (GBK 编码)
├── build_native.bat                # 原生组件构建 (GBK 编码)
├── game_list.txt                   # GameMonitor 的游戏列表
├── Live2DCubismCore.dll            # Live2D 原生库（不入库，需自备，见 2.1）
├── LICENSE                         # GNU AGPL-3.0 许可证正文
├── LICENSING.md                    # 双授权说明（AGPL / 商业许可）与第三方组件清单
├── src
│   ├── AICompanion.Core            # 核心逻辑，net8.0，不依赖 WPF
│   │   ├── Abstractions            # IAppLogger / IBubbleNotifier / ITTSEngine
│   │   ├── Configuration           # AppConfig / ConfigLoader / AppPaths / config.default.yaml
│   │   ├── Models                  # KnowledgeEntry / VoiceConfig / ChatMessage
│   │   ├── Knowledge               # 知识库、导入器、待审核服务
│   │   ├── Gpu                     # nvidia-smi、GGUF 读取、ngl 计算、运行模式
│   │   ├── Llm                     # llama-server 进程控制 + HTTP 客户端
│   │   ├── Health                  # 探活
│   │   ├── Tts                     # 引擎工厂 / Piper / Chatterbox / 管理器
│   │   ├── Asr                     # VAD / 采集 / whisper 识别 / 管理器
│   │   ├── Context                 # 上下文与摘要
│   │   ├── Refine                  # 对话整理（产出待审核候选）
│   │   ├── Scheduling              # 空闲任务调度
│   │   ├── Monitor                 # GameMonitor 协议 / 服务端 / 客户端 / 协调器
│   │   ├── Pipeline                # 对话管线
│   │   ├── Audio                   # WAV 解析 + 播放
│   │   ├── Interop                 # Win32 / 进程 / 端口 / 系统指标
│   │   └── Logging
│   ├── AICompanion.App             # WPF 表现层，net8.0-windows
│   └── GameMonitor                 # 独立监控进程
├── third_party
│   ├── ai_asr                      # ★ whisper.cpp 桥接层（见下文"ABI 决策"）
│   ├── fvad_shim                   # libfvad 导出层（产出 bin\libfvad.dll）
│   ├── libfvad                     # 源码
│   ├── whisper.cpp                 # 源码
│   └── llama.cpp                   # 源码（不含构建产物）
├── Assets
│   └── Default
│       ├── default.png             # 默认立绘（由 tools\draw_avatar.ps1 生成）
│       └── Expressions             # 表情差分 <动画名>.png（wave/happy/sad/shy/...）
├── tools                           # 开发与验证工具（脚本在根，探针在 probes，构建工具在 toolchain）
│   ├── pack.ps1                    # 打包：发布 + 复制资源 + 调 Inno Setup
│   ├── verify_dist.ps1             # 分发包异目录启动校验
│   ├── soak_test.ps1               # 长时间连续运行采样（内存/句柄/CPU/错误）
│   ├── ptt_smoke.ps1               # 按住说话冒烟：模拟按键 + 按时间戳筛日志
│   ├── make_speech_wav.ps1         # 生成测试中文语音（SAPI 只写文件，不播放）
│   ├── make_icon.ps1               # 生成应用图标（纯几何图形，无第三方素材）
│   ├── draw_avatar.ps1             # 角色立绘与表情差分生成
│   ├── build_llama_cuda.bat        # Ninja + CUDA 并行构建 llama-server
│   ├── build_fvad.bat              # 构建 bin\libfvad.dll
│   ├── Fetcher                     # 多镜像断点续传下载器（开发工具）
│   ├── cosyvoice-patches            # 上游 CosyVoice server.py/frontend.py 的本地补丁副本
│   ├── research                    # 精度/性能试验脚本（fp8、fp4、分支耗时剖析，全部可弃）
│   ├── probes                      # 全部验证探针，每个都是独立控制台工程
│   │   ├── AnimationProbe          # 形象/动作/口型逻辑回归（46 项断言）
│   │   ├── AsrPttProbe             # 按住说话逻辑（27 项断言）
│   │   ├── AsrPttRealProbe         # 真实语音 + 真实 whisper 走 PTT（8 项）
│   │   ├── AsrRecoveryProbe        # ASR 设备自愈（12 项）
│   │   ├── ExportProbe             # ai_asr.dll 功能验证
│   │   ├── MicProbe                # 麦克风设备诊断（逐台录 2 秒看有没有数据）
│   │   ├── MonitorProbe            # GameMonitor 协议与冷却期验证
│   │   ├── OomProbe                # OOM 回退 / ngl 边界验证
│   │   ├── PipelineProbe           # 对话管线 + VAD + 语音合成验证
│   │   ├── TtsBackendsProbe        # GPT-SoVITS / CosyVoice3 / Chatterbox 三条 TTS 后端直连验证
│   │   ├── Live2DPoc               # Live2D 最小可行性验证（PoC）
│   │   ├── FakeLlamaServer         # 假 llama-server，用于注入 OOM
│   │   └── testdata                # 测试用真实语音样本（SAPI 合成）
│   └── toolchain                   # 本机构建工具（不入库，可重新下载）
│       ├── cmake                   # 便携版 CMake 4.4.4
│       └── ninja                   # 便携版 Ninja 1.13.2（CUDA 并行构建用）
├── installer                       # Inno Setup 脚本 + 中文语言包 + install.ps1
├── Hiyori                          # Live2D 模型（不入库，需自备，见下文"素材与隐私"）
├── bin                             # 原生产物：ai_asr.dll / llama-server.exe / ggml-cuda.dll / libfvad.dll
│   └── cpu-backup                  # CUDA 构建前的 CPU 版备份，用于回退
├── models                          # LLM / ASR 模型（不进版本库）；Piper 语音模型可选
├── Knowledge                       # 知识库（+ .pending 待审核目录）
├── logs                            # app / gamemonitor / chat 日志（不进版本库）
├── dist                            # 打包产物：分发目录 + 安装包（不进版本库）
└── artifacts                       # 临时产物：测试语音、发布中间目录（不进版本库）
```

> 目录里**不含任何构建产物与下载缓存**：`third_party/*/build*`、各项目的 `bin/obj`、
> 已解压的源码压缩包都已清理（清理掉约 1.1 GB）。原生组件靠 `build_*.bat` 重新生成，
> 产物统一落到 `bin/`。

### 仓库里不含第三方素材，也不含个人数据

公开仓库只放**代码与文档**。下面这些在原开发机上存在，但不会随仓库分发：

| 内容 | 为什么排除 | 你怎么补上 |
| --- | --- | --- |
| `Hiyori\`（Live2D 示例模型） | 模型是 Live2D 官方示例素材，授权不允许随第三方仓库再分发 | 从 Live2D 官网下载 Cubism 示例模型，解压后保持 `Hiyori\Hiyori.model3.json` 这个路径即可 |
| `Assets\Default\character.png` | 从他人作品截图裁出的角色立绘 | 把任意 PNG/JPG 丢进 `Assets\Default\`，程序自动选用（见下一节） |
| `Assets\source\` | 上面那张图的原图 | 不需要 |
| `Knowledge\.pending\` | 从对话里自动提取的待审核候选，属于个人数据 | 程序自己生成，无需补 |
| `Assets\appearance.json` | 每个人拖出来的位置与缩放不同，属于本机状态 | 首次保存外观时自动生成 |
| `models\`、`bin\`、`dist\`、`logs\`、`artifacts\` | 模型、原生产物、打包产物、日志 | 见下方各自的获取/构建方式 |

**仓库里的 `Assets\app.ico` 是自己画的**：`tools\make_icon.ps1` 用纯几何图形（圆角渐变底 + 对话气泡）
生成 256×256 的 ICO，不含任何第三方素材。想换图标直接改这个脚本重新生成，或丢一个自己的 ico 进去。

缺少这些素材时程序**不会崩**，而是逐级降级：Live2D 模型缺失 → 用 `Assets\Default` 的静态立绘 →
仍然没有 → 纯色块占位。启动日志会写明降级到了哪一级。

### `Assets\Default` 就是"默认形象目录"

把图片放进 `Assets\Default` 就会作为角色的默认立绘生效，**不关心文件名，也不要求特定格式**：

| 规则 | 说明 |
| --- | --- |
| 支持格式 | `.png` `.jpg` `.jpeg` `.bmp` `.gif` `.tif` `.tiff`（WebP 不在其中，WPF 无内置解码器） |
| 只有一张 | 直接用它 |
| 有多张 | 优先名字以 `default` 开头的；否则取**最后修改时间最新**的那张 |
| 非图片文件 | 忽略（放 `.txt` 之类不会干扰） |
| 透明通道 | 建议用带 alpha 的 PNG。不透明的图会让角色显示成一块矩形，背景不会自动消失 |

每次启动都会把命中的文件与取舍理由写进日志，例如：

```
角色形象：使用 Assets\Default 中的图片 Assets\Default\我的角色图.png（目录中唯一的图片）
```

`Assets\Expressions` 下放 `<动画名>.png`（如 `wave.png`）作为表情差分；
`Assets\source` 用于存放原始素材（程序不读取）。

---

## 四、关键设计决策

以下是开发过程中必须裁决的问题，以及最终选择与理由。**手册与实现不一致时以本表为准。**

### 决策 1：llama-server 的生命周期归主程序

- **手册原文**：§1.6 让 `start_all.bat` 用 `-ngl 99` 启动 llama-server；§2/§3.3 又要求 `-ngl`
  由 `GPUMemoryManager` 在启动时计算。
- **矛盾**：bat 先起来时主程序还没算出 ngl；而 `-ngl 99` 在 8GB 显存上必然 OOM。
  更严重的是，§3.3 要求解析 llama-server 的 **stderr** 做 OOM 回退，
  但 `start /b` 启动的进程，主程序既没有 Process 对象也拿不到 stderr 句柄。
- **实现**：主程序完全持有 llama-server 进程（启动 / 探活 / 解析 stderr / OOM 回退 / 终止）。
  `start_all.bat` 不再启动它。
- **收益**：一次性解决"启动顺序冲突""OOM 回退拿不到 stderr""卸载时无法终止进程"三个问题。

### 决策 2：`/stop` 与 `/unload` 用官方真实能力实现

- **手册原文**：§4 要求"停止生成"调用 llama-server 的 `/stop`；§3.9 要求调用 `/unload`。
- **事实**：llama.cpp 的公开端点是 `/health`、`/v1/chat/completions`、`/completion`、
  `/embeddings`、`/reranking`、`/slots`、`/props`、`/metrics`，**没有 `/stop`**；
  单模型模式下也**没有 `/unload`**（只有 Router Mode 才有模型管理接口）。
  官方支持的取消生成方式是客户端断开连接（llama.cpp PR #9679）。
- **实现**：
  - 停止生成 = 取消流式请求 → 释放 HTTP 连接 → 服务端中止生成；再补一次 `/slots/0?action=erase` 清槽。
  - 让出显存 = 先尝试 `/unload`（带 5 秒超时，真实服务端会快速返回 404），
    随后终止 llama-server 进程 —— 因为 `/unload` 即使可用也只卸载模型，
    进程本体仍占着显存。
- **收益**：不依赖任何自造协议，升级 llama.cpp 不会失效。

### 决策 3：ASR 通过 C 桥接层访问 whisper.cpp（本项目最重要的工程决策）

- **问题**：`whisper_full_params` 有 40 多个字段，内嵌匿名结构体、函数指针与 `size_t`。
  在 C# 里手工声明它的 P/Invoke 布局，一旦有任何一个字段的类型或对齐猜错，
  **编译器不会报错**，而是在运行时破坏调用栈，表现为随机崩溃且极难定位。
- **实现**：新增 `third_party/ai_asr`，用 whisper.cpp 的**真实头文件**编译一个极薄的 C 桥接层，
  对外只暴露 5 个参数全为基本类型的函数：

  ```c
  int  ai_asr_init(const char* model_path, int n_threads, int use_gpu, char* error, int error_capacity);
  int  ai_asr_transcribe(const float* samples, int n_samples, const char* language, int translate, char* out_text, int out_capacity);
  void ai_asr_reset(void);
  void ai_asr_shutdown(void);
  const char* ai_asr_system_info(void);
  ```

- **收益**：所有结构体布局由 C 编译器保证，C# 侧完全不需要知道 `whisper_full_params` 的存在。
  这类 ABI 风险被彻底消除，而不是"记录在案、等待踩坑"。

### 决策 4：运行模式必须如实上报

- **问题**：如果 llama-server 是 CPU-only 构建，它收到 `-ngl 13` 会打印警告并**忽略**该参数。
  此时若 UI 仍显示"GPU 模式（ngl=13）"，就与手册 §2 的运行模式语义直接冲突。
- **实现**：`LlamaServerController` 解析 stderr，一旦发现就**改回 CPU 模式**并气泡告知。
- **实测文本**（本机 CPU 版 llama-server，`-ngl 13`）：

  ```
  warning: no usable GPU found, --gpu-layers option will be ignored
  warning: one possible reason is that llama.cpp was compiled without GPU support
  ```

  注意实际措辞是 "compiled **without** GPU support"，而不是想当然的
  "not compiled with GPU offload" —— 最初的匹配串一个都没命中，
  是按实测输出改正后才生效的（见 `VERIFICATION.md` §6.1）。
- **收益**：UI 不会谎报，用户不会被误导，并且气泡里直接给出如何获得 GPU 版本。

### 决策 5：GameMonitor 冷却期绑定"最后退出的游戏进程名"

- **手册原文**：§3.9"冷却期绑定到**触发恢复的那个具体进程名**"。
- **问题**：restore 的触发前提是"游戏列表中所有进程均已退出"，
  此时根本不存在"触发恢复的那个进程"。
- **实现**：GameMonitor 记住最后退出的进程名，并在 `restore` 消息里带上 `process` 字段；
  主程序据此设置冷却期，同时通过新增的 `cooldown` 反向消息把冷却状态回传给 GameMonitor。
- **双保险**：即使 GameMonitor 没有正确执行冷却期，主程序在收到同一进程的 `unload` 时也会直接拒绝。

### 决策 6：`ngl ≤ 0` 时直接切 CPU

- **手册原文**：`ngl = max(1, min(L, 保守可用显存 / 150))`。
- **问题**：空闲显存低于 5120MB 时该式为负，`max(1, 负数)` 会强行上 1 层，
  而 1 层同样会 OOM（KV Cache 与上下文仍占显存），结果把"可解释的降级"变成"不可解释的启动失败"。
- **实现**：`保守可用 ≤ 0` 或算出的层数 `< 1` 时直接 `ngl=0` 并切 CPU 模式，气泡说明原因。
- **实测**（预留量设为 100GB 强制触发）：保守可用 `-85,128 MB` → `ngl=0`、运行模式 `Cpu`、
  气泡"可用显存不足…已切换为 CPU 模式"，且**没有**强行上 1 层（见 `VERIFICATION.md` §7.3）。

### 决策 7：其余设计选择

| 项 | 选择 | 理由 |
| --- | --- | --- |
| 表现层 | 自建轻量 WPF 壳，不用 VPet | 透明窗+穿透+托盘+气泡是几百行可控代码；且无法核实 VPet 许可证 |
| Live2D | 静态图 + 表情贴图切换 | 手册 §1.1 明确允许降级；三级兜底链在静态图下即可完整验证 |
| Piper | 进程外 `piper.exe` 兜底，C API 后置 | P/Invoke 目标库本轮不编译；进程外路径当前即可用 |
| ASR 语种 | 默认 `zh` | 手册面向中文场景 |
| 麦克风采集 | NAudio（接口隔离） | NuGet 通道可靠；采集栈是最容易出兼容问题的一环，不值得自造 |
| whisper 后端 | 默认 CPU 构建 | 避免与本地 LLM 抢显存；whisper-base 在 CPU 上延迟可接受。可用 `-DGGML_CUDA=ON` 切换 |
| 批处理编码 | GBK（代码页 936） | cmd.exe 按 OEM 代码页读取 .bat，UTF-8 中文会导致语法解析失败 |

---

## 五、已修正的手册内部矛盾

| # | 手册位置 | 问题 | 处理 |
| --- | --- | --- | --- |
| 1 | §1.6 vs §2/§3.3 | `-ngl` 的归属与启动顺序互斥 | 决策 1 |
| 2 | §3.3 | `start /b` 起的进程拿不到 stderr，OOM 回退无法实现 | 决策 1 |
| 3 | §4 / §3.9 | llama-server 没有 `/stop`、`/unload` 端点 | 决策 2 |
| 4 | §1.3 / §5.1 | ASR 只写"进程内 C++ 共享库"，未指定引擎；`model_path` 是占位符 `"models/asr/..."` | 选 whisper.cpp；给出真实默认路径 |
| 5 | §3.9 | 冷却期绑定的"触发恢复的进程名"在语义上不存在 | 决策 5 |
| 6 | §1.5/§3.9 vs 准则 8 | 要求 GameMonitor 用"固定端口"却未给数值，而准则 8 禁止硬编码 | 端口 47777 写入 `config.yaml` 可覆盖 |
| 7 | §3.3 | `max(1, ...)` 在显存不足时导致必 OOM | 决策 6 |
| 8 | §5.2 | `taskkill /f /im python.exe /fi "WINDOWTITLE eq chatterbox*"` 永远匹配不到（`start /b` 的无窗口进程标题是 `N/A`） | `start_all.bat` 改为按端口反查占用进程 |
| 9 | §5.2 | bat 硬编码 `-ngl 99` 与模型路径 | bat 从 `config.yaml` 读出 `llm.model_path`，不再启动 llama-server |
| 10 | §1.2 | 手册把 4GB 预留、1GB KV、150MB/层、0.9 系数、N=30、500 token、30 锚点等常量写在正文里 | 全部外提为配置节（`gpu`/`context`/`idle`/`monitor`/`ui`），默认值等于手册取值 |

---

## 六、已知差距（如实记录）

1. **GameMonitor 内存指标未达标**
   手册 §1.4 要求"内存占用低于 10MB"。.NET 运行时基线工作集在 25-45MB，
   即便 NativeAOT 自包含版本也在 15-20MB，**C# 无法达成该指标**。
   已与用户确认（决策 Q13）：本轮先跑通协议与功能，指标留到阶段 4 再决定是否用原生 C++ 重写。
   本机工具链（MSVC 14.51）具备重写条件。

2. **CUDA 运行时 DLL 未随包分发**
   `bin\` 里有 `ggml-cuda.dll`，但**没有** `cublas64_*.dll` / `cudart64_*.dll`——
   CUDA 运行时是通过系统工具包（已在 PATH）解析的。
   本机可用，但换到没装 CUDA 工具包的机器上会因缺 DLL 启动失败。
   分发时需把 CUDA 运行时 DLL 一并复制到 `bin\`，或要求目标机安装对应版本。

3. **Live2D 模型与原生库需要自备**
   渲染链路已打通（真实 Cubism 渲染，见 VERIFICATION §19–§20），但模型（如 Hiyori）与
   `Live2DCubismCore.dll` 属第三方素材/专有库，不随仓库分发。缺模型时逐级降级：
   Live2D → `Assets\Default` 静态立绘 → 纯色块，启动日志写明降到了哪一级。

4. **Piper C API 未接入**
   决策 Q9 已确认后置。当前 `PiperEngine` 走进程外 `piper.exe`，需要该可执行文件存在，
   并且要把 Piper 语音模型放到 `models\piper\zh_CN\`（`model.onnx` + 同名 `.json`）。
   本机该目录是空的，所以实测一直是 SAPI 兜底。
   好消息是**不影响出声**：Piper 不可用时会自动降级到 Windows 内置语音（SAPI），
   日志与气泡都会标明"降级"，不是静默替换。

5. **性能类验收项未在目标机器验证**
   手册 §9 的"预处理 <650ms""LLM <2s""12 小时无泄漏"等指标需要在部署机
   （RTX 3060 12GB）上以正式 7B 模型验证。本轮开发机为 RTX 5060 Laptop 8GB，
   且 GPU 推理是部分卸载（实测 `ngl=5/8/9 / 28`，随实时空闲显存浮动：
   `gpu.reserved_mb=4096` + `kv_cache_reserved_mb=1024` 的保守策略）。

6. **CosyVoice 有 GPU 与 CPU 两条路，速度差 5 倍多**
   CosyVoice3 在 Windows 原生**可以**跑（Python 3.10，依赖里已经没有 `pynini`，
   换成了纯 Python 的 `wetext`），本机实测通过。有四件事得说清楚：
   - **速度**：同一句 20 字（约 4.2 秒音频），CPU 上要 39 秒（RTF 7.2），
     GPU（`torch 2.7.1+cu128` + fp16）只要 4.7–5.2 秒（RTF 1.0–1.1）。
     **fp16 默认开**（约快 20%）；fp8/fp4 走不通——PyTorch 里 fp4 根本没有对应 dtype，
     fp8 的 `addmm_cuda` 也没实现（详见 VERIFICATION §27.4）。
     `tools\start-cosyvoice.ps1` 会自动优先用带 CUDA 的 `.venv-gpu`，没有才回退 CPU。
   - **启动预热**：服务启动时会先合成一句丢掉，把 CUDA kernel 与 onnxruntime EP 的一次性
     初始化（20–70 秒）提前付掉。代价是启动慢一点，换来**服务就绪后第一句就是 5 秒**，
     而不是卡 20 多秒。`--warmup 0` 可关。
   - **上游 bug**：官方 `runtime/python/fastapi/server.py` 把上传的 wav 先读成 tensor 再传给
     `inference_*`，而 `frontend.load_wav` 只接受路径，必然抛
     `TypeError: Invalid file: tensor([...])`。本机副本已就地改成"上传落地成临时文件再传路径"，
     重新 clone 上游后需要照 VERIFICATION §27.4 重打补丁。
   - **显存/内存边界**：两个服务同开时显存 6.4 / 8.1 GB 能跑，但 CosyVoice 会从 5 秒劣化到
     8–14 秒；更麻烦的是两个 python 服务各提交约 8.5 GB 内存，16 GB 机器上会把页面文件逼到
     8 GB、任务管理器显示 98%，合成直接掉到 12–30 秒。**8 GB 显卡 + 16 GB 内存的机器，
     同一时间只跑一个 TTS 服务**（没跑的那个由 App 自动降级到 Piper），另外关掉 NVIDIA App
     的浮层还能多让出约 1 GB 显存。


---

## 七、配置说明

`config.yaml` 由程序首次运行时从嵌入资源生成，**删掉任何键都不会崩溃**——
程序会回退到模板默认值，并在启动气泡中告知有多少项被兜底、设置面板中列出具体清单。

手册模板中未给出但本项目新增的配置节（默认值均等于手册正文的常量）：

| 配置节 | 关键项 | 作用 |
| --- | --- | --- |
| `monitor` | `port` / `cooldown_minutes` / 采样与阈值 | GameMonitor 通信与检测参数 |
| `gpu` | `reserved_mb` / `kv_cache_reserved_mb` / `per_layer_mb` / `safety_factor` | ngl 计算参数 |
| `context` | `max_recent_rounds` / `summary_token_limit` / `anchor_limit` | 上下文与摘要 |
| `idle` | `idle_minutes` / `cpu_low_percent` / `cpu_high_percent` / `maintenance_cooldown_minutes` | 空闲调度门槛、维护两轮之间的冷却 |
| `ui` | `click_through` / `scale` / `pet_image_path` / `fallback_display_name` | 表现层 |
| `ui` | `transparent_hover_switch` / `transparent_hover_delay_ms` / `transparent_auto_revert_seconds` / `fullscreen_quiet_mode` | 透明态悬停切换、全屏时自动让路 |
| `llm` / `tts` / `asr` | `enabled` | **按功能单独关闭**（见下） |

> `ui.click_through` 的语义是"**启动时**是否处于透明穿透态"，默认 `false`（覆盖）。
> 运行中随时可用右键切换，该值只决定初始状态。
>
> `ui.scale` 为 `0` 表示按屏幕自动适配（默认）；设为 `0.5`–`2.5` 则是固定倍率。
> 用户在界面上调过的值存在 `Assets/appearance.json`，优先级高于本文件。

> `llm.enabled` / `tts.enabled` / `asr.enabled`：三项各自独立，默认全 `true`。
> 关掉之后对应链路**完全不初始化** —— 不载模型、不占显存、不拉外部服务、不打开麦克风，
> 启动日志里那一行会直接写"已关闭"，不会显示成"起来了但没声音"。
>
> ```yaml
> # 只想挂着桌宠：三个都关，不再拉 llama-server / TTS 服务 / 识别模型
> llm: { enabled: false }
> tts: { enabled: false }
> asr: { enabled: false }
> ```
>
> 只关 `tts` 就是"只弹字、不出声"；只关 `asr` 就是"只能打字、不能说话"；
> 关掉 `llm` 后发消息会直接提示"对话功能已关闭"，而不是报一个假的连接错误。

### Live2D 模型兼容性（实测）

| 模型 | model3 版本 | 动作 | 结果 |
| --- | --- | --- | --- |
| Hiyori（官方示例） | 3 | 10 个（Idle / TapBody） | ✅ |
| 免费模型冰糖 | 3 | 0 个（文件里没有 Motions） | ✅ 能显示；无动作，只有物理摆动 |
| 免费模型艾莲 | 3 | 2 个（在**无名默认组**里） | ✅ 修正后可用 |

- Cubism 3 的 `.moc3` 不用转换，现行 SDK 直接加载；贴图 3~4 张、
  单个 4K 贴图 10 MB 量级都没问题。
- **动作放在无名组 `""` 里**是规范允许的写法，很多免费模型这么做。以前这种模型会被判成
  "没有动作"（日志写 `动作组 []`），角色站着不动且没有任何报错；现在会把默认组归一成 `Idle`
  并按默认组播放。
- 路径含中文和空格正常：实测把模型放在 `E:\测试 目录\模型 艾莲\` 也能加载（贴图、物理、表情都读到了）。
- `model3.json` 里声明的 `.moc3` 或贴图缺失时**不会崩**：创建 GL 之前先校验文件，
  缺就跳过 Live2D 降级到静态立绘。（之前这种坏模型会让进程在 GC 线程上崩掉，见 CHANGELOG。）
- 没有 `AvatarProfile.json` 时会用内置默认动作映射并给出 Warning，不影响显示；
  想让动作/口型对得上，按模型自己的 `Motions` 写一份 `AvatarProfile.json` 放在模型旁边即可。

### 语音合成：GPT-SoVITS / CosyVoice

`tts.engine` 现在接受四个值：`piper`（默认，本地 CPU）、`chatterbox`、`gpt_sovits`、`cosyvoice`。
后两者是**外部 HTTP 服务**——程序只当客户端，服务没起就按手册降级到 Piper 并弹气泡说明，
不会静默换引擎。

| 引擎 | 配置节 | 前置条件 |
| --- | --- | --- |
| GPT-SoVITS | `tts.gpt_sovits` | 整合包里的 `runtime\python.exe api_v2.py -a 127.0.0.1 -p 9880` |
| CosyVoice | `tts.cosyvoice` | `tools\start-cosyvoice.ps1`（Windows 原生，Python 3.10） |

GPT-SoVITS 支持三种用法，可自由组合：

- **训练好的模型**：填 `gpt_weights_path`（`*.ckpt`）与 `sovits_weights_path`（`*.pth`），
  引擎启动时会调 `/set_gpt_weights`、`/set_sovits_weights` 切过去；
- **零样本**：只填 `ref_audio_path` + `prompt_text`（3~10 秒干净人声，文字必须与音频一致）；
- **两者叠加**：训练模型 + 指定参考音频，音色最可控。

CosyVoice 用 `mode` 选择推理方式：`sft`（内置说话人 `spk_id`）、`zero_shot`（`prompt_text` + `prompt_wav`）、
`cross_lingual`、`instruct`、`instruct2`。**它的服务返回裸 PCM**（16 bit 单声道、无 WAV 头），
所以 `sample_rate` 必须填对：CosyVoice2/3 是 `24000`，CosyVoice1 是 `22050`。

#### 用自己训练的音色（实测：GPT-SoVITS v2Pro）

1. `*.pth` 放进整合包的 `SoVITS_weights_v2Pro\`，`*.ckpt` 放进 `GPT_weights_v2Pro\`
   （同轮权重必须配对，混搭会咬字不清）；
2. `config.yaml` 写**绝对路径**，参考音频与它的文本必须逐字一致：

```yaml
tts:
  engine: "gpt_sovits"
  gpt_sovits:
    gpt_weights_path: "E:/GPT-SoVITS/GPT-SoVITS-v2pro/GPT_weights_v2Pro/你的模型.ckpt"
    sovits_weights_path: "E:/GPT-SoVITS/GPT-SoVITS-v2pro/SoVITS_weights_v2Pro/你的模型.pth"
    ref_audio_path: "E:/你的参考音频/ref.wav"
    prompt_text: "参考音频里说的那句话。"
    top_k: 15        # 训练时用的采样参数，别乱改
    top_p: 0.8
    temperature: 0.8
    text_split_method: "cut5"   # 长句必须切
    timeout_seconds: 180        # 首次调用要加载权重并建 CUDA 图
```

3. 先起服务，再起程序。日志里应出现这三行才算真的生效：

```
已切换到 GPT 权重：…
已切换到 SoVITS 权重：…
TTS 初始化完成：配置引擎=gpt_sovits，实际引擎=gpt_sovits，降级=False。
```

实测数据（RTX 5060 Laptop 8GB，v2Pro，输出 32 kHz）：52 字 → 13.36 s 音频、合成 5.4 s；
13 字 → 2.8 s 音频、合成 2.8 s。首次调用含加载权重约 13 s，之后稳定。
注意 GPT-SoVITS 常驻会占约 4 GB 显存，llama-server 会被挤到 CPU 模式，二者要按显存取舍。

> **键名必须写 `gpt_sovits` / `cosyvoice`。** 这两个节是显式 `[YamlMember(Alias=…)]` 绑定的：
> YamlDotNet 的 `UnderscoredNamingConvention` 会把 `GptSoVits` 推成 `gpt_so_vits`，
> 写成那样**不会报错**，但整节会被静默忽略（表现为权重、参考音频全是空的）。

> **版权**：训练权重及其音源归各自权利人，本仓库不附带任何训练好的音色权重。
> 请自行取得合法来源的模型，或只用零样本模式（`ref_audio_path` + `prompt_text`）。
两个引擎都允许在角色音色里逐角色覆盖：`VoiceId` → 说话人，`ReferenceAudioPath` → 参考音频，
`PromptText` / `InstructText` → 对应文本。

> **不用改文件也能换引擎**：设置面板 →「语音」→「合成引擎」下拉框，选中即重建引擎并生效。
> 选择存在 `Assets\appearance.json`（优先级高于 `config.yaml`，且**不会**改动 `config.yaml`，准则 9），
> 删掉该键或用回配置里的值即可恢复。`config.yaml` 仍然决定各引擎的权重、参考音频等细项。

> **CosyVoice3 必须配 `prompt_prefix`。** 它的 LLM 会硬断言输入里含 `<|endofprompt|>`，
> 官方写法是 `"You are a helpful assistant.<|endofprompt|>"`，填在 `tts.cosyvoice.prompt_prefix` 里，
> 引擎会自动补到 zero_shot 的 `prompt_text` 和 cross_lingual 的正文前。
> 不填的话服务端会"合成成功但只有一两个 token"，表现为音频极短甚至报
> `Kernel size can't be greater than actual input size` —— 这个坑踩过，记在这里。
> CosyVoice 1/2 不需要这个前缀，留空即可。

### 语音输入：按住说话（默认）

`asr` 节新增四个键，默认**按住说话**，不再常开监听：

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `push_to_talk` | `true` | 按住按键才录音，松开后整段识别 |
| `push_to_talk_key` | `"Home"` | 触发按键。单键（`Home` / `F2`）、双键（`Alt+Q`）、三键（`Ctrl+Alt+Q`）都支持；修饰键可用 `Ctrl` / `Alt` / `Shift` / `Win` |
| `push_to_talk_min_ms` | `300` | 短于此时长判定为误触，直接丢弃 |
| `push_to_talk_max_seconds` | `60` | 单次录音上限，防止按住不放吃满内存 |

开启按住说话后 `auto_start` 不再生效——两者互斥。原因很实际：
常开监听会把环境音、旁人对话、以及 TTS 自己的回声都收进来；
按住说话把"什么时候在听"交还给用户。

想回到常开监听：`push_to_talk: false` + `auto_start: true`。

实现上是用 60 ms 的 `GetAsyncKeyState` 轮询而不是键盘钩子：
钩子要插进系统输入链，写错会拖慢或吞掉用户按键，风险不对称；
轮询相反——最多 60 ms 延迟（感知不到），不会拦截任何按键，也不需要管理员权限。


另外新增 `llm.layer_count`（0 = 自动从 GGUF 元数据探测 `*.block_count`），
避免把"28 层"硬编码在代码里。

---

## 八、运行与排错

### 交互入口（鼠标）

角色窗口有两种状态，用**右键**在它们之间切换：

| 状态 | Win32 样式 | 表现 | 能做什么 |
| --- | --- | --- | --- |
| **覆盖**（可交互，默认） | `WS_EX_LAYERED` | **完全不透明** | 左键点角色、拖动、右键切换、Ctrl+滚轮缩放 |
| **透明**（鼠标穿透） | `WS_EX_LAYERED \| WS_EX_TRANSPARENT` | **整体 50% 不透明度**，点击穿透到下层 | 角色只是装饰；**鼠标移上去**会自动切回覆盖 |

两种模式不只是点击行为不同，**外观也一眼可辨**：覆盖态恒为完全不透明，透明态整体降到
`ui.transparent_opacity`（默认 0.5）。不透明度作用于整棵可视树，角色、气泡、输入框一起变淡。

- **角色右上角的按钮**（显示「覆」/「透」）→ 切换 透明 ↔ 覆盖。
  它做成**独立窗口**，因此透明态下依然点得到 —— 这是从透明态切回来的可靠入口。
- **右键** → 弹出功能栏：`设置` / `更换外观` / `对话` / `−` / `＋` / `↺`。再右键收起。
- **左键** → 只负责拖动（位移超过 4px 才算拖动），不再触发功能栏。
- **Ctrl + 滚轮** → 调整角色大小（±5%，见下）。
- **透明态下把鼠标移到角色身上** → 悬停 250ms 后自动切回覆盖；鼠标离开 3 秒后自动退回透明。
- **前台有全屏程序时**（游戏、全屏视频）→ 自动切成透明并暂停悬停切换，退出全屏后恢复成全屏前的模式。
  全屏期间点右上角按钮仍然有效，且以你的选择为准。由 `ui.fullscreen_quiet_mode` 控制（默认开）。

> 透明态收不到任何鼠标消息（`WS_EX_TRANSPARENT` 连 `WM_MOUSEMOVE` 都不给），
> 所以悬停检测是"每 100ms 轮询光标 + **整窗**命中判定"。
> 判定范围刻意与窗口矩形一致：早先用像素级 alpha 判定，只把不透明像素算作命中，
> 角色四周的透明留边看着在窗口内却点不到，用户反馈"判定范围不对"。
> 现在根 `Grid` 带 `Background="Transparent"`，**整个窗口矩形都可点**。
>
> **提示气泡会自动消失**：所有气泡都有到期时间，默认取 `ui.bubble_duration_seconds`（5 秒）。
> 不传时长的气泡以前会永不过期、一直挂在界面上，现已修正。时长可在
> **设置面板 →「外观」**页调整，改完立即生效并记到 `Assets\appearance.json`。
>
> 悬停触发需要"**先离开过角色**"才解除待发。否则右键切到透明时光标正压在角色上，
> 250ms 后就被切回覆盖，用户再右键又被切回——模式会来回抽搐（实测 55 秒切了 25 次）。
>
> 角色大小在两种模式下**完全一致**：穿透时输入区用 `Hidden` 而非 `Collapsed`（保留占位），
> 气泡改为叠加在角色之上（不占布局行高）。否则每切一次模式、每弹一个气泡，
> 角色都会被 `Uniform` 拉伸一下——这就是"右键一下大小会变"的成因。

启动时默认是**覆盖**态（`config.yaml` 的 `ui.click_through: false`）。

### 角色大小与 DPI / 分辨率

同一块 2560×1440 屏，**100% 缩放**时逻辑分辨率是 2560×1440，**175% 缩放**时只有 1463×823 ——
一个固定的 340×460 DIP 窗口，占屏比例会差一倍多。因此角色大小是可调的，且默认**按屏幕自动适配**：

| `ui.scale` | 行为 |
| --- | --- |
| `0`（默认） | 自动：角色高度约占工作区 45%，宽度不超过 60% |
| `0.5` – `2.5` | 固定倍率 |

运行中用功能栏的 `−` / `＋` 或 **Ctrl+滚轮**调整，结果记到 `Assets/appearance.json`，
下次启动优先使用；点 `↺` 或删掉该文件即恢复自动适配。倍率上限还会受当前屏幕约束——
放大到窗口超出工作区会被自动收敛，避免角色有一半在屏幕外拖不回来。

大小变化作用于**整棵可视树**（`LayoutTransform`），所以文字、气泡、输入框都与角色等比缩放，
不是只把图片拉大。

程序带 `app.manifest` 声明 **PerMonitorV2** DPI 感知：把窗口拖到不同缩放率的显示器上时，
WPF 按新 DPI 重新布局，不会被 Windows 位图拉伸（变模糊）也不会让命中判定偏移。
每次启动都会把当前 DPI 与分辨率写进日志：

```
显示环境：DPI 175%（1.75×），主屏 1463×823 DIP（物理约 2560×1440 px），工作区 1463×823 DIP
角色大小：80%（来源=按屏幕自动适配）
```

**更换外观**选定的图片与大小倍率存到 `Assets/appearance.json`，**不会修改 config.yaml**（准则 9）。

### 排错

**主程序不弹任何消息框**（准则 4）。所有提示走三个通道：气泡、设置面板、日志。

| 现象 | 查看位置 |
| --- | --- |
| 启动失败 / 依赖缺失 | 主程序气泡 + `logs/app/app-*.log` |
| 配置被兜底的字段 | 设置面板「状态」页的"配置兜底与启动问题"列表 |
| LLM 未就绪 | 设置面板「状态」页 + `logs/app`；LLM 模型缺失属致命缺失，会明确列出路径 |
| 语音不可用 | 设置面板「语音」页显示 ASR 状态与模型路径；文本输入始终可用 |
| 游戏检测断开 | 气泡提示 + 托盘/设置面板的「重连游戏检测」 |
| 待审核记忆 | 设置面板「待审核知识」页；**确认后才会进入正式知识库** |
| 角色只显示纯色方块 | 没有形象素材。`Assets/Default/default.png` 缺失时走三级兜底的最后一级 |

**日志清理**：设置面板「日志」页可查看占用空间、清理应用日志、清理过期聊天日志。
`logs/chat/chat-yyyyMMdd.jsonl` 保存全部原始对话（永不删除），
`logs/chat/chat_log_archive.jsonl` 保存被压缩出窗口的原文。

---

## 九、验证记录

全部证据、复现命令与"尚未验证"清单都在 [VERIFICATION.md](./VERIFICATION.md)（现 31 章）。按章节查：

| 想看什么 | 章节 |
| --- | --- |
| 托管代码 / 原生组件能不能构建 | §1–§2 |
| ASR 桥接层、启动流程、对话管线、OOM 回退、GameMonitor、GPU 推理、VAD | §3、§5–§10 |
| 发现并修复过的缺陷（含对照实验） | §4 |
| 明确**没验证**的部分 | §12 |
| 鼠标交互、DPI、立绘与语音兜底、气泡与外观设置 | §13–§17 |
| Live2D 可行性、合成与命中问题的根因 | §19–§20 |
| 性能与资源（死代码、进程、内存） | §21、§28–§30 |
| TTS / ASR 完善、按住说话、GPT-SoVITS 与 CosyVoice | §22–§23、§27 |
| 打包与分发策略（含 1.6 GB 全家桶安装包） | §24、§26 |
| 两小时连续运行（内存/句柄采样） | §25 |
| 第四轮真机运行：启动 8.4s、退出 0.23s、真实推理 0.7s/16 token、退出崩溃根因 | §31 |
| 自己复现一遍 | §18 |

## 十、打包与分发

### 10.1 一条命令出包

```bat
powershell -ExecutionPolicy Bypass -File tools\pack.ps1
```

> **当前版本 0.9.2**（2026-10-29）：修掉了 `tts.gpt_sovits` / `tts.cosyvoice` 两段配置不生效的问题
> （键名对不上，整段被跳过，程序一直用默认值），打包也改成一律用模板生成 `config.yaml`。
> 版本号从 `AICompanion.App.csproj` 的 `<Version>` 读，目录名、安装包名、`VERSION.txt` 都跟着走。
> 旧包 0.9.0 备份在 `artifacts\dist-backup-0.9.0\`（不进版本库）；出包后可用 `tools\verify_dist.ps1` 异目录验证。

默认是**「带所有环境、但不带需要授权的素材」**的包：

| 开关 | 默认 | 含义 |
| --- | --- | --- |
| `-SelfContained` | `true` | 自带 .NET 8 桌面运行时，目标机不用装任何东西 |
| `-IncludeModels` | `true` | 把 `models\`（LLM 1.04 GB + ASR 141 MB）一起打进去，装完即可对话 |
| `-IncludeLicensedAssets` | 关 | 把 Live2D 的 `Live2DCubismCore.dll` 与 `Hiyori\` 也打进去。**公开分发必须保持关闭** |
| `-ReadyToRun` | `true` | 预编译，启动更快（体积略增） |
| `-ExcludeSymbols` | `true` | 剔除 `.pdb` |
| `-Zip` | `false` | 是否额外生成 zip（有了安装包一般不需要） |
| `-NoCudaRuntime` | 关 | 默认把 CUDA 运行库（`cublas64_13` / `cublasLt64_13` / `cudart64_13`，493 MB）打进 `bin\`，见 10.3 |

> `powershell -File` 传不了布尔值（`-IncludeModels:$false` 会绑定失败），所以另给了几个
> 不带值的开关：`-NoModels`、`-NoLicensedAssets`、`-FrameworkOnly`、`-NoCudaRuntime`。

**分发包里不放需要单独授权的素材。** Live2D 的 Cubism Core 与模型得用户自己按 Live2D 的
条款取得，包里只附一份 `素材说明.md` 写明缺什么、去哪下、放哪里。
缺了不会崩，程序逐级降级（Live2D → 静态立绘 → 纯色块），启动日志写明降到了哪一级。

产物：

```
dist\AICompanion-0.9.2-win-x64\              可直接运行的分发目录（默认含模型与 CUDA 运行库，约 1.90 GB）
dist\AICompanion-0.9.2-win-x64.zip           加 -Zip 才生成
dist\AICompanion-0.9.2-full-setup.exe        安装包（约 1.62 GB；-NoInstaller 则不生成）
```

只出不含模型的轻量包（约 249 MB）：

```bat
powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -NoModels
```

自己用、想把 Live2D 素材一起打包（**别拿去分发**）：

```bat
powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -IncludeLicensedAssets
```

### 10.3 目标机器需要什么（越少越好）

只装安装包的话，**目标机器只需要 NVIDIA 驱动 + 约 4 GB 磁盘**：

| 依赖 | 是否需要 | 说明 |
| --- | --- | --- |
| .NET 8 桌面运行时 | 不需要 | 自包含发布，运行时在包里 |
| CUDA Toolkit | 不需要 | `cublas64_13` / `cublasLt64_13` / `cudart64_13` 已打进 `bin\` |
| Visual C++ 运行库 | 不需要 | 静态链接 |
| Python / venv / torch | 不需要 | 默认 TTS 走 Windows 自带 SAPI；Piper 音色可选 |
| NVIDIA 驱动 | **需要** | GPU 推理、`nvidia-smi` 显存查询都靠它 |
| GPT-SoVITS / CosyVoice | 可选 | 想要更好的音色再单独起服务，App 自动识别、没起就降级 |

**为什么要打 CUDA 运行库**：`bin\ggml-cuda.dll` 依赖 `cublas64_13.dll`。开发机上这些来自
已装的 CUDA Toolkit 13.3 且在 `PATH` 里，所以"本机能跑"并不等于"别人装上能跑"。
实测把 `PATH` 里的 CUDA 目录全删掉再启动打包版，仍然是
`运行模式=GPU 模式（ngl=9/28，空闲显存 6756 MB）` —— 证明目标机不需要 CUDA Toolkit。

体积换来的就是这一条：安装包从 1.26 GB 涨到 1.62 GB（`cublasLt64_13.dll` 单文件 442 MB），
但省掉了"先装 3 GB CUDA Toolkit"这一步。

### 10.2 安装包

安装包由 **Inno Setup 6** 编译（`installer\AICompanion.iss`）。
本机已装 Inno Setup 6.7.3；换机器时先装：<https://jrsoftware.org/isdl.php>，
`pack.ps1` 会自动找到 `ISCC.exe` 并编译，找不到就只输出分发目录并提示。

安装包行为：

- 装到 `%LOCALAPPDATA%\Programs\AICompanion`（按用户安装，不需要管理员）；
- 开始菜单快捷方式 + 可选桌面图标 + 可选开机自启；
- 在「应用和功能」里注册卸载项，卸载时**保留** `logs` 与 `Assets\appearance.json`；
- 自带运行时的包不再检查系统 .NET；依赖系统的包缺失运行时会明确提示而不是让程序闪退；
- 中文安装界面（`installer\ChineseSimplified.isl` 随仓库提供）。

### 10.3 为什么装到 LOCALAPPDATA

程序运行期要在自己目录下写 `logs\` 与 `Assets\appearance.json`。
装到 `Program Files` 会因无写权限失败。按用户安装既避开这个问题，也不需要提权。

万一真被装到受保护目录，`LogRoot` 会按
「当前目录 → 程序目录 → `%LOCALAPPDATA%\AICompanion`」找第一个**可写**位置写日志，日志不会丢。

### 10.4 打包时的三个坑（都已处理）

1. **exe 被占用**：`dotnet publish` 默认往 `src\...\bin\Release\` 写，程序运行时会被锁。
   `pack.ps1` 把发布输出重定向到 `artifacts\pkgbin`，所以**程序开着也能打包**。
2. **GameMonitor 必须与主程序同目录**：`AppHost` 只在 `AppPaths.Root` 与
   `AppContext.BaseDirectory` 找 `GameMonitor.exe`，放进子目录会「启动正常但一直重连失败」。
3. **`powershell -File` 传不了布尔值**：`-IncludeModels:$false` 这种写法在 `-File` 模式下
   会被当成字符串，报 `Cannot convert value "System.String" to type "System.Boolean"`。
   所以脚本另给了不带值的开关（`-NoModels` / `-NoLicensedAssets` / `-FrameworkOnly`），
   cmd 里也能直接写。

> 顺带一条给改脚本的人：本仓库的 `.ps1` 一律存成 **UTF-8 带 BOM**。
> PowerShell 5.1 会把无 BOM 的 UTF-8 按 GBK 读，中文字符串全成乱码、脚本直接语法错误，
> 而 `PSParser` 还报 0 个错误 —— 极容易误判成逻辑 bug。

### 10.5 精简项

| 项目 | 处理 | 收益 |
| --- | --- | --- |
| `.pdb` | 删除 | 约 0.2 MB（另有 `bin\` 里的符号约 24 MB） |
| WPF 卫星程序集 | 只留 `zh-Hans` / `zh-Hant`，删掉 11 种语言 | 约 13 MB |
| 模型 | 默认打包（可用 `-NoModels` 关掉） | 决定安装包量级 |

### 10.6 分发包里有什么

```
AICompanion.exe              主程序（WinExe，带图标与版本信息 0.9.2）
AICompanion.dll / Core.dll   托管程序集
GameMonitor.exe              游戏检测子进程（必须与主程序同目录）
<运行时文件>                  self-contained 的 .NET 8 + WPF 程序集
bin\                         llama.cpp（含 CUDA）/ whisper 桥接 / VAD 原生库
Assets\                      图标、外观设置（第三方立绘不含，见下）
Knowledge\                   知识库目录（热重载）
config.yaml                  配置（用户可改；程序不会改写它）
config.default.yaml          配置模板
models\                      模型（默认含 LLM + ASR）
素材说明.md                  缺哪些需要授权的素材、去哪下、放哪里
install.ps1                  免 Inno Setup 的部署脚本
VERSION.txt                  版本与构建时间
```

**不含**下面这些——它们要按各自的授权自己取得（`素材说明.md` 里写了下载位置）：

```
Live2DCubismCore.dll         Live2D 官方原生库（Cubism SDK for Native）
Hiyori\                      Live2D 示例模型（Live2D 的示例素材不许再分发）
Assets\Default\character.png 第三方立绘
```

缺这些**不影响程序启动**：形象逐级降级（Live2D → 静态立绘 → 纯色块），
启动日志会写明缺什么、降到哪一级、去哪补。

分发包根目录自带 `install.ps1`，做的事与安装包一致（复制、快捷方式、注册卸载项、
生成 `uninstall.ps1`），只是没有图形界面：

```bat
powershell -ExecutionPolicy Bypass -File install.ps1
```

### 10.8 打包后验证

```bat
powershell -ExecutionPolicy Bypass -File tools\verify_dist.ps1
```

它会把分发包复制到临时目录、**换一个工作目录**启动，检查：
必需文件齐全、进程存活、主窗口出现、日志能写出来（`LogRoot` 生效）、日志里没有 `[ERR]`。
这一步专门用来抓「仓库里能跑、发出去跑不了」的问题。

### 10.9 打包前自检清单

- [ ] `dotnet build AICompanion.sln -c Release` 零警告零错误
- [ ] `tools\AsrPttProbe`、`tools\AsrRecoveryProbe`、`tools\AnimationProbe` 全部通过
- [ ] `config.yaml` 里 `llm.model_path` 指向随包分发的模型
- [ ] 打包后在**解压/安装目录**里跑一次，确认不依赖仓库环境

## 十一、许可

本项目采用 **AGPL-3.0 与商业许可双授权**，按你的使用场景任选其一：

| | **AGPL-3.0**（默认，免费） | **商业许可**（付费） |
| --- | --- | --- |
| 适合 | 个人使用、学习研究、开源项目 | 闭源商业软件、不公开源码的 SaaS、OEM 预装 |
| 义务 | 分发或以网络服务形式提供时，须以 AGPL-3.0 提供完整对应源码 | 无开源义务，按合同约定 |
| 费用 | 免费 | 需付费，条款协商 |

- 许可证正文：[`LICENSE`](LICENSE)（GNU AGPL-3.0，仅第 3 版）
- 双授权细则、第三方组件各自的许可、贡献条款：[`LICENSING.md`](LICENSING.md)

> 注意：**Live2D 的 SDK 与模型不在本项目的双授权范围内**，使用它们需自行遵守 Live2D 的许可。
> 模型文件（LLM / ASR / Piper）同样各自独立授权，均不随本仓库分发。

商业授权咨询：**[2075306213@qq.com](mailto:2075306213@qq.com)**（标题注明「AICompanion 商业授权」）；
技术问题、缺陷反馈请走 [GitHub Issues](https://github.com/ZHO2008/AICompanion/issues)。
