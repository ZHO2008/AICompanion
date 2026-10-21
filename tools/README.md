# tools —— 开发与验证工具

这个目录分三块，按"拿来就能用"的程度排：

```
tools\
├── *.ps1 / *.bat      日常入口脚本，直接运行
├── probes\            验证探针，每个都是独立控制台工程，dotnet run 即可
└── toolchain\         本机构建工具（CMake / Ninja），不入版本库
```

## 一、日常脚本

| 脚本 | 用途 | 典型用法 |
| --- | --- | --- |
| `pack.ps1` | 打包：发布主程序 + GameMonitor、复制资源与模型、调 Inno Setup 出安装包。默认**不打包需授权的 Live2D 素材**（`-IncludeLicensedAssets` 才打） | `powershell -File tools\pack.ps1 [-NoModels \| -NoLicensedAssets]` |
| `verify_dist.ps1` | 分发包校验：复制到临时目录、**换工作目录**启动，查进程/窗口/日志/错误行；并断言包里**没有**需授权的素材、清理 llama-server 子进程 | `powershell -File tools\verify_dist.ps1` |
| `soak_test.ps1` | 长时间连续运行采样：每分钟记内存、句柄、线程、CPU，并做 UI 交互压测 | `powershell -File tools\soak_test.ps1` |
| `ptt_smoke.ps1` | 按住说话冒烟：模拟按住按键，按时间戳筛日志，判断是"出字/误触/没收到音频" | `powershell -File tools\ptt_smoke.ps1 -HoldMs 1500` |
| `make_speech_wav.ps1` | 用 SAPI 合成中文语音到 WAV（**只写文件、不播放**，不吵人），供真实语音探针使用 | `powershell -File tools\make_speech_wav.ps1` |
| `make_icon.ps1` | 生成 `Assets\app.ico`（纯几何图形，不依赖任何第三方素材） | `powershell -File tools\make_icon.ps1` |
| `draw_avatar.ps1` | 生成角色立绘与表情差分到 `Assets\Default` | `powershell -File tools\draw_avatar.ps1` |
| `build_llama_cuda.bat` | Ninja + CUDA 并行构建 `bin\llama-server.exe` | 需要 `toolchain\cmake`、`toolchain\ninja` |
| `build_fvad.bat` | 构建 `bin\libfvad.dll`（WebRTC VAD） | 同上 |
| `Fetcher\` | 多镜像断点续传下载器，用来拉 CMake/模型等大文件 | `dotnet run --project tools\Fetcher -- <url>` |

## 二、验证探针（probes）

每个探针都是自包含的控制台程序，断言失败会返回非 0 退出码，可以直接接进 CI。

| 探针 | 验证什么 | 断言数 | 运行 |
| --- | --- | --- | --- |
| `AnimationProbe` | 形象/动作/口型：动作映射、降级、优先级、口型曲线 | 46 | `dotnet run --project tools\probes\AnimationProbe -c Release` |
| `AsrPttProbe` | 按住说话：按下→缓冲→松开→识别、误触、连续按、按键解析、无音频告警 | 27 | 同上 |
| `AsrPttRealProbe` | 真实语音 + 真实 whisper 走完整 PTT 路径 | 8 | 先 `make_speech_wav.ps1`，再运行 |
| `AsrRecoveryProbe` | ASR 设备异常后的自愈（退订/重连/单订阅） | 12 | 同上 |
| `ExportProbe` | `ai_asr.dll` 的 ABI 与功能 | — | 需要 `bin\ai_asr.dll` |
| `MicProbe` | 麦克风诊断：列设备、逐台录 2 秒，报告实测时长与 RMS | — | 直接运行，会占用麦克风 2 秒/台 |
| `MonitorProbe` | GameMonitor 协议、冷却期、重连 | — | 需要 GameMonitor 可执行 |
| `OomProbe` | LLM 显存不足时的 ngl 回退循环 | — | 配合 `FakeLlamaServer` |
| `PipelineProbe` | 对话管线 + VAD + 语音合成端到端 | — | 需要模型与 LLM |
| `Live2DPoc` | Live2D 最小可行性验证（渲染/口型/命中/透明） | — | `Live2DPoc.exe "<模型目录>"` |
| `FakeLlamaServer` | 假 llama-server，用于注入 OOM / 慢响应 | — | 被 `OomProbe` 调用 |
| `testdata` | 测试语音样本（`speech.wav`，SAPI 合成） | — | 数据目录 |

约定：

- 探针只依赖 `src\AICompanion.Core`（`AnimationProbe` 额外依赖 App），不反向影响产品代码；
- 断言失败必须返回非 0，否则"通过"没有意义；
- 需要的模型/语音样本缺失时，探针应**跳过并说明原因**，而不是崩掉。

## 三、toolchain（不入库）

`toolchain\cmake`（便携版 CMake 4.4.4）与 `toolchain\ninja`（1.13.2）是原生组件构建用的本地工具，
体积大、可重新下载，已在 `.gitignore` 中排除。缺失时 `build_*.bat` 会明确提示，
用 `Fetcher` 拉一份解压到对应路径即可。

## 四、目录约定

- **产物不落在源码树里**：发布输出走 `artifacts\pkgbin`，最终产物在 `dist\`，日志在 `logs\`；
- **探针的 bin/obj 可以随时删**：`git clean -xdf tools\probes` 之后重新 `dotnet build` 即可；
- 新增探针请放进 `probes\`，并在上表登记一行——否则半年后没人知道它是干什么的。
