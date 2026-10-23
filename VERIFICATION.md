# 验证记录

本文档记录**实际执行过的验证**及其原始证据，不是"应该能工作"的推断。
每条都可在本机复现。

验证日期：2026-10-17
开发机：Windows 11 26200 / RTX 5060 Laptop 8GB / 15.3GB RAM / .NET SDK 8.0.425

---

## 1. 托管代码构建

```
dotnet build AICompanion.sln -c Release
```

结果：

```
已成功生成。
    0 个警告
    0 个错误
```

三个工程全部通过：`AICompanion.Core`（net8.0）、`AICompanion.App`（net8.0-windows，WPF）、
`GameMonitor`（net8.0，WinExe）。

---

## 2. 原生组件构建

### 2.1 ai_asr.dll（whisper.cpp 桥接层）

```
build_native.bat asr
```

关键输出：

```
[ 28%] Built target ggml-base
[ 77%] Built target ggml-cpu
[ 85%] Built target ggml
[ 94%] Built target whisper
[ 97%] Building C object CMakeFiles/ai_asr.dir/ai_asr_shim.c.obj
[100%] Linking CXX shared library ai_asr.dll
[100%] Built target ai_asr
[OK] bin\ai_asr.dll
```

产物：`bin\ai_asr.dll`，1.65 MB。

导出符号（`dumpbin /EXPORTS` 实测）：

```
1    0 00006230 ai_asr_init
2    1 000063B0 ai_asr_reset
3    2 000063C0 ai_asr_shutdown
4    3 000063F0 ai_asr_system_info
5    4 00006400 ai_asr_transcribe
```

### 2.2 导出符号的 .NET 侧确认

用 `NativeLibrary.Load` + `TryGetExport` 逐项解析，5 个导出全部 FOUND。
这一步证明的是"主程序将要使用的 P/Invoke 契约成立"，而不只是"dll 编译出来了"。

---

## 3. ASR 功能性验证（tools/probes/ExportProbe）

用与主程序**完全相同**的调用方式（`NativeLibrary` + 函数指针）真实加载 141MB whisper 模型并跑一次推理：

```
=== ai_asr.dll functional verification ===
[OK] ai_asr_system_info -> WHISPER : VITISAI = 0 | COREML = 0 | OPENVINO = 0 | CPU : SSE3 = 1 | SSSE3 = 1 | AVX = 1 | AVX2 = 1 | F16C = 1 | FMA = 1 | AVX512 = 1 | OPENMP = 1 | REPACK = 1 |
[OK] ai_asr_init rc=0, model loaded in 279 ms
[OK] ai_asr_transcribe rc=3 (text: "你")
     2s audio took 1643 ms, RTF=0.822
[OK] ai_asr_reset
[OK] ai_asr_shutdown

=== ALL PASSED: shim ABI and whisper inference path both work ===
退出码 = 0
```

结论：

- **桥接层 ABI 正确**：正因为不在 C# 里手工声明 `whisper_full_params`，
  才能在第一次运行就直接跑通，而不是靠运气对齐结构体。
- 模型加载 279 ms，2 秒音频推理 1643 ms（RTF 0.82，4 线程 CPU）。
- 静音输入被识别成「你」——这是 whisper 在非语音上的典型幻觉，
  恰好印证了 VAD 门控的必要性（本项目在音频线程上已有 VAD 先行切分）。

---

## 4. 发现并修复的缺陷（含对照实验）

### 4.1 卸载原生库导致进程退出时崩溃

**现象**：验证程序所有步骤都打印成功，但进程退出码是 `-1073741819`（`0xC0000005`，访问冲突）。

**对照实验**（可复现，`Probe.exe [dll] [model] withfree`）：

| 行为 | 退出码 |
| --- | --- |
| 调用 `NativeLibrary.Free` | -1073741819（ACCESS_VIOLATION） |
| 不调用（默认，与主程序一致） | 0 |

**根因**：`ai_asr.dll` 静态链接了 C 运行库与 ggml 全局注册表，
卸载动态库会触发静态对象析构，而析构顺序不受控，于是踩到已释放内存。

**修复**：`WhisperCppRecognizer.Dispose()` 不再调用 `NativeLibrary.Free`，
只调用 `ai_asr_shutdown()` 释放 whisper 上下文；库句柄交给进程退出回收。
同时把 `PiperNativeApi.Probe()` 里"探测完立即 Free"的同样写法一并去掉。

### 4.2 启动即 NullReferenceException，且异常无法留痕

**现象**：程序启动后立即失败，气泡显示"启动失败：Object reference not set to an instance of an object."，
但没有生成 `config.yaml`，也没有 `Knowledge` 目录，日志只有一行。

**根因（两处）**：

1. `AppHost.Logger` 到 `StartAsync()` 才赋值，而 `MainWindow` 在 `StartAsync()` **之前**
   就被构造，其 `LoadAvatar()` 调用了 `_host.Logger` → NRE。
2. 因为 `Logger` 本身是 null，`catch` 里的 `_host?.Logger?.Error(...)` 是空操作，
   连异常类型和堆栈都没记下来，只剩下气泡里一句无从排查的提示。

**修复**：`AppHost` 构造函数里就建立引导日志器并赋给 `Logger`；
配置加载后若目录相同则**复用**同一个 sink，不同才切换。
启动异常的日志改为带异常类型与完整堆栈。

### 4.3 日志静默丢失（`FileShare.Read` + 双 sink 写同一文件）

**现象**：修复 4.2 后，日志仍在配置加载处断掉，后续 20 多条启动日志全部消失。

**根因**：引导日志器与正式日志器指向**同一个文件**，
而 `LogSink` 用 `FileShare.Read` 打开——该模式只允许读共享，
第二个写入者打开文件会抛 `IOException`，而日志写入是被 `catch` 吞掉的
（日志绝不能成为崩溃源），于是表现为"日志莫名少一大段"。

**修复**：

1. 文件共享模式改为 `FileShare.ReadWrite`；
2. 配置目录与引导目录相同时复用同一个 `LogSink`，从根上消除两个写入者。

**修复前后对比**：日志行数 5 → 24。

### 4.4 日志行交错

**现象**：两个 sink 同时写同一文件时，出现 `[INF]2026-10-17 ... [INF] [Bubble]` 这种
两行日志粘连、分类标签丢失的畸形行。

**根因**：两个 `StreamWriter` 各自缓冲，刷盘时机不同。

**修复**：同 4.3 的第 2 条（复用 sink）。

### 4.5 GameMonitor 断连事件永不触发

**现象**（代码复查发现，非运行时）：看门狗先置 `_disconnectNotified = true`，
随后调用的 `HandleDisconnect` 见到该标志直接 `return`，
导致 `ClientDisconnected` 事件永远不会触发，托盘的"重连游戏检测"成了摆设。

**修复**：`CheckHeartbeat` 不再预先置位，去重交给 `HandleDisconnect` 自己完成。

### 4.6 批处理文件编码导致脚本被"吃掉字符"

**现象**：`build_native.bat` 报 `'native.bat' is not recognized`、`'lease' is not recognized`
——命令被从中间截断。

**根因**（两个层次）：

1. `.bat` 存为 UTF-8 时，cmd.exe 按 OEM 代码页（936）读取，中文注释破坏语法解析；
2. 改存 GBK 后，GBK 双字节字符的第二字节范围 0x40–0xFE **包含 `^`(0x5E)、`&`(0x26)、
   `|`(0x7C)、`<`、`>`**，cmd.exe 把它们当控制字符，静默吞掉相邻字符。

**修复**：两个 `.bat` 全部改为**纯 ASCII**（已用脚本核对非 ASCII 字节数 = 0），
中文说明移入 README。这也是本文件与 README 存在的原因之一。

### 4.7 CMake 找不到编译器（本机工具链错配）

**现象**：`No CMAKE_C_COMPILER could be found`。

**根因**：本机装了两套 Visual Studio，且各缺一半：

| 安装 | MSVC | Windows SDK |
| --- | --- | --- |
| `D:\VS 2026\project` | 完整（14.44/14.51/14.52） | 有，但在非标准路径 `SDK\ScopeCppSDK\vc15\SDK` |
| `C:\Program Files\Microsoft Visual Studio\2022\Community` | include 残缺（连 `stdio.h` 都没有） | 无 |

CMake 的 "Visual Studio" 生成器按标准路径找 SDK，因此两边都拼不出可用环境。

**修复**：改用 `vcvars64.bat`（它自己知道 SDK 在哪）+ NMake 生成器。
`build_native.bat` 顶部完整记录了这段原因，避免后人重复踩坑。

### 4.8 MSVC 在 936 代码页下无法解析 UTF-8 源文件

**现象**：编译 `ai_asr_shim.c` 报 `C4819`（当前代码页无法表示该字符）
与 `C2001`（字符串字面量中的换行符）。

**根因**：源文件是 UTF-8（无 BOM）且带中文注释，MSVC 默认按系统代码页 936 解析。

**修复**：给目标加 `/utf-8` 编译开关（`third_party/ai_asr/CMakeLists.txt`）。

### 4.9 两个进程写同一个日志文件（跨进程交错）

**现象**：主程序日志里出现畸形行，例如

```
…（兜底修正 0 项，自动生成=False�2026-10-17 13:28:28.592 [INF] [Bubble] [气泡/Info] 知识库为空…
```

两行被拼成一行，且中间有一个被切开的多字节中文字符（`�`）。

**根因**：`LogSink` 的文件名写死为 `app-yyyyMMdd.log`，而 **GameMonitor.exe 与主程序
都写 `logs\app` 目录**。两个进程各持一个 `StreamWriter`，各自缓冲、刷盘时机不同，
于是互相插入。`FileShare.ReadWrite` 允许这种并发写，所以不会报错，只会脏数据。

**修复**：`LogSink` 增加 `filePrefix` 参数；GameMonitor 用 `"gamemonitor"` 前缀。
现在分别是 `app-yyyyMMdd.log` 与 `gamemonitor-yyyyMMdd.log`。

### 4.10 同一进程内两个 sink 写同一个文件

**现象**：修完 4.9 后，主程序日志里**仍有一处交错**，而且 `[Bubble]` 分类的行总是参与交错。

**根因**：`App.OnStartup` 为 `BubbleNotifier` 建了一个 `LogSink`，
`AppHost` 构造函数又建了**第二个**指向同一文件的 `LogSink`（它需要一个"构造阶段就可用"的
日志器给 `MainWindow` 用）。于是同一进程里有两个写入者 —— 之前"配置目录与引导目录相同则复用
sink"的判断只覆盖了 `AppHost` 内部，覆盖不到 `App` 这一侧。

**修复**：`App.OnStartup` 创建**唯一一个** `LogSink`，同时传给 `BubbleNotifier` 与
`AppHost`（新增 `sharedSink` 构造参数）。`AppHost.StartAsync` 里"目录相同则复用"的逻辑保留，
只在配置把日志目录指到别处时才切换到新 sink。

**验证**：重启后逐行检查日志格式（每行必须以时间戳开头）——

```
总行数: 33  格式异常行: 0
[INF] [App] 沿用引导日志目录：E:\DEEPSEEK\logs\app
[INF] [VAD] 使用 WebRTC VAD（libfvad）：E:\DEEPSEEK\bin\libfvad.dll
[INF] [ASR] ASR 初始化完成：…，VAD=webrtc。
[INF] [App] 启动完成：运行模式=GPU 模式（ngl=9/28，空闲显存 6648 MB），LLM 就绪=True…
[INF] [GameMonitor] GameMonitor 已连接：127.0.0.1:62917
```

---

## 5. 启动流程验证（真实运行）

清空 `logs/`、`config.yaml`、`Knowledge/` 后启动 `AICompanion.exe`（工作目录为项目根），
运行 30 秒后终止。日志全文（24 行，节选并标注）：

```
[INF] [Bootstrap] 已生成默认配置文件：E:\DEEPSEEK\config.yaml          ← §5.1 自动生成
[INF] [Bubble] [气泡/Warning] 已生成默认配置文件，请按需修改后重启。
[INF] [Bubble] [气泡/Info] 有 1 项配置缺失或非法，已使用默认值…        ← 逐字段兜底上报
[INF] [Bubble] [气泡/Info] 知识库为空，角色将以通用设定运行。…          ← §6 知识库为空提示
[ERR] [LlamaServer] LLM 模型文件缺失或路径错误：…\qwen2.5-7b-q4_k_m.gguf ← §6 致命缺失
[INF] [Bubble] [气泡/Error] LLM 模型文件缺失或路径错误，无法启动对话服务。请在设置面板检查 llm.model_path。
[INF] [Bubble] [气泡/Warning] 未找到 Piper 语音模型，语音合成暂不可用。…  ← 可降级缺失
[INF] [TTS] TTS 初始化完成：配置引擎=piper，实际引擎=piper，降级=False。
[INF] [ASR] whisper.cpp 已加载：WHISPER : … AVX512 = 1 | OPENMP = 1 | …  ← 真实加载原生库
[INF] [ASR] whisper 模型已加载：…\models\asr\ggml-base.bin（线程 0，CPU 模式）
[INF] [VAD] WebRTC VAD 不可用，使用能量 VAD：未找到 …libfvad.dll       ← 三级兜底生效
[INF] [ASR] ASR 初始化完成：模型=…，VAD=energy。
[WRN] [Health] LLM 服务 探活失败第 1 次：由于目标计算机积极拒绝…(127.0.0.1:8080)
[INF] [Health] 健康监控已启动（间隔 30 秒，离线阈值 2 次）。          ← §3.8
[INF] [GameMonitor] GameMonitor 通信服务已监听 127.0.0.1:47777。       ← §3.9 端口
[INF] [GameMode] 游戏模式协调器已接入 GameMonitor 事件。               ← 决策 Q11
[INF] [Idle] 空闲调度器已启动（轮询 500 ms，空闲阈值 5 分钟，CPU 上限 50%）。 ← §3.6
[INF] [App] 启动完成：… TTS=piper（降级=False），ASR=Stopped，知识库 0 条，启动问题 2 项。
```

同时确认生成物：

| 项 | 结果 |
| --- | --- |
| `config.yaml` | 已生成（带完整中文注释，来自嵌入资源） |
| `Knowledge/README.md` | 已生成（说明文件格式与"程序绝不自动写入"约定） |
| `Knowledge/.pending/` | 已创建（待审核目录） |
| `logs/app/app-*.log` | 已写入 |
| 进程存活 | 30 秒内未崩溃、未退出 |

这一轮验证覆盖了：配置自动生成与逐字段兜底、知识库初始化、日志、TTS 引擎选择、
ASR 真实加载、VAD 三级兜底、健康探活、GameMonitor 监听、空闲调度、致命/可降级缺失的区分处理。

---

## 6. 对话管线端到端验证（tools/probes/PipelineProbe）

在核心层直接驱动对话管线（不经过 GUI，避免"点一下看看"这种不可复现的验证），
真实启动 llama-server、真实调用模型推理。**16 项检查全部通过，退出码 0。**

### 6.1 llama-server 启动（决策 Q5：主程序持有进程）

```
--- 启动 llama-server（主程序持有进程，决策 Q5）---
  结果      : Started
  就绪耗时  : 3.7 秒
  ngl       : 13
  运行模式  : CPU 模式（llama-server 未编译 GPU 支持，-ngl 被忽略，已改用 CPU 推理）
  ngl 推导  : 空闲 7338 MB − 预留 5120 MB = 2218 MB，× 0.9 = 1,996 MB 保守可用；
              ÷ 150 MB/层 = 13 层；层数上限 28（从 GGUF 元数据探测）⇒ ngl=13
  [PASS] llama-server 已就绪  —— Started
  [PASS] HTTP /health 探活成功  —— status=200 body={"status":"ok"}
```

这一条同时验证了三件事：

1. **手册 §3.3 的 ngl 公式逐项算对**：预留 4096+1024、系数 0.9、每层 150MB 全部生效；
2. **层数 L=28 是从 GGUF 元数据真实探测出来的**（`qwen2.5-1.5b-instruct-q4_k_m.gguf`），
   不是硬编码——这正是 `GgufMetadataReader` 存在的意义；
3. **决策 4 的"如实上报"生效**：本次用的是 CPU 版 llama-server，
   它打印 `warning: no usable GPU found, --gpu-layers option will be ignored`，
   主程序识别后把运行模式改回 **CPU 模式**，而不是谎报"GPU 模式（ngl=13）"。

### 6.2 全部检查项

```
--- 测试 1：预处理知识命中（手册 §4 步骤 3-4）---
  [PASS] 热重载后能查到条目  —— count=1
  [PASS] Query 命中预处理条目
  [PASS] 回复等于模板  —— 这是预处理知识回复。
  [PASS] 标记为来自知识库  —— True
  [PASS] 取出动画指令  —— wave
  [PASS] 预处理延迟 <650ms（手册 §9）  —— 39 ms

--- 测试 2：LLM 生成（手册 §4 步骤 5）---
  回复      : "我是一个AI助手，可以回答各种问题。"
  [PASS] LLM 生成本次成功  —— ok
  [PASS] 回复非空  —— 18 字
  [PASS] 未误判为知识库回复  —— False
  端到端耗时: 654 ms

--- 测试 3：上下文与原文留存（手册 §3.2）---
  [PASS] RecentMessages 已累积  —— 4 条
  [PASS] BuildContext 非空  —— 73 字符
  [PASS] 已写入当日聊天日志（原文永久保留）  —— chat-20261017.jsonl

--- 测试 4：锚点记忆（手册 §3.2）---
  [PASS] 锚点记忆已记录  —— 1 条
  [PASS] 锚点内容进入上下文  —— 含【必须牢记的锚点记忆】段

--- 关闭 llama-server ---
  让出显存耗时: 36 ms（手册 §9 要求 <5s）
  [PASS] 显存已让出（进程已终止）  —— IsRunning=False

============================================================
  全部通过
退出码 = 0
```

### 6.3 手册 §9 验收指标的实测值

| 指标（手册 §9） | 要求 | 实测 | 结论 |
| --- | --- | --- | --- |
| 预处理命中端到端延迟 | < 650 ms | **39 ms** | ✅ 达标 |
| LLM 生成回复延迟 | < 2 s | **654 ms** | ✅ 达标（1.5B 模型 / CPU 推理） |
| 语音故障后托盘恢复 | < 5 s | 未测（Piper 未提供） | ⏸ |
| 让出显存耗时 | 15 s 内完成 | **36 ms** | ✅ 达标 |

> 说明：LLM 延迟是在 **CPU 推理 + 1.5B 模型**下测得的。部署机（RTX 3060 12GB + 7B 模型 + CUDA 构建）
> 的数值需要另行测量，不能直接套用。

### 6.4 顺带验证的降级路径

- **TTS 失败不影响对话**：Piper 模型与可执行文件都缺失，`SynthesizeAsync` 抛出后
  被 `TTSManager` 吸收为失败结果 + 气泡「语音合成失败」，
  管线继续走到写入上下文并正常返回（准则 4 与手册 §3.4 崩溃处理）。
- **知识库热重载**：运行中写入条目后立即 `Query` 命中，无需重启。
- **原文归档**：`logs/chat/chat-20261017.jsonl` 已生成。

---

### 6.5 停止生成（断流取消，决策 Q6）

llama.cpp 没有 `/stop` 端点，官方取消方式是客户端断开连接（PR #9679）。
这一项验证"取消后能立刻返回，且服务端没被弄坏"：

```
--- 测试 5：停止生成 / 断流取消（决策 Q6）---
  进入推理中耗时: 0 ms（IsGenerating=True）
  [PASS] 管线进入「推理中」状态  —— stage=Thinking
  返回      : Success=False，Error=已取消。
  取消耗时  : 15 ms
  [PASS] 取消后本次对话判定为未完成  —— 已取消。
  [PASS] 取消原因标注为已取消  —— 已取消。
  [PASS] 取消耗时 <5s（手册 §9）  —— 15 ms
  [PASS] 取消后服务仍然健康（未被打坏）  —— ok
  [PASS] 取消之后仍能正常生成新回复  —— "记住"
```

最后一项是关键证据：取消一次生成之后**还能正常生成新回复**，
说明释放响应流没有把 llama-server 留在半死状态。

### 6.6 日志轮转、清理与面板接口（手册 §5.1 / 阶段 3 第 6 项）

设置面板的「日志」页依赖 `LogSink` 的这几个接口，因此逐项验证：

```
--- 测试 0：日志轮转、清理与面板接口（手册 §5.1 / 阶段3 第6项）---
  [PASS] 按日生成日志文件名 app-yyyyMMdd.log  —— app-20261017.log
  [PASS] 日志内容已落盘  —— 135 字符
  [PASS] Debug 级别被过滤（minimum=Info）  —— 已过滤
  [PASS] RecentSnapshot 返回最新在前  —— [ERR] [LogTest] …
  [PASS] GetDirectorySizeBytes 返回正数（面板展示占用）  —— 156 字节
  [PASS] CleanupOldFiles 删除超期日志（保留 3 天）  —— 已删除
  [PASS] CleanupOldFiles 不误删当日日志  —— app-20261017.log
  [PASS] PurgeAll 返回删除数量  —— 0 个（当日文件被占用属正常）
```

> `PurgeAll` 返回 0 是**预期**的：当日文件正被本进程持有，删除会失败并被跳过。
> 面板上的"清理应用日志"因此只清理历史文件，不会把正在写的那份弄坏。

### 6.7 上下文摘要压缩与归档（手册 §3.2）

往 `ContextManager` 灌入消息使其超出 30 轮窗口，再触发压缩：

```
--- 测试 7：上下文摘要压缩与归档（手册 §3.2）---
  注入后 RecentMessages = 48（阈值 30）
  [PASS] 注入后消息数超出窗口  —— 48 > 30
  压缩结果 = True，耗时 774 ms
  压缩后 RecentMessages = 30
  摘要栈条目数 = 1
  [PASS] 压缩被触发  —— True
  [PASS] 窗口被收敛到阈值以内  —— 30 <= 30
  [PASS] 摘要栈产生了近期摘要（L0）  —— L0(34tk)
  [PASS] 摘要内容非空  —— 30 字
  归档文件新增 18 行（压缩移出 18 条）
  [PASS] 被压缩的原文已归档（不丢数据）  —— +18 行
  [PASS] 归档行数与移出窗口的消息数一致  —— 归档 18 vs 移出 18
  [PASS] 归档是合法 JSONL（每行可独立反序列化）  —— 18 行可解析，0 行失败
  [PASS] 每个归档行恰好对应一条消息  —— 18 条消息 / 18 行
  [PASS] 未超窗时压缩直接返回 false  —— False
```

摘要由本机 llama-server 真实生成（774 ms）。1.5B 模型的摘要质量一般，
偶尔会出现复述原文的情况；这是模型能力问题，换成 7B 会明显改善。

#### 这一轮测试暴露的缺陷：`.jsonl` 文件其实不是 JSONL

| 项 | 内容 |
| --- | --- |
| **现象** | 压缩移出 18 条消息，归档文件却新增 **116 行** |
| **根因** | `ContextManager` 的 `ArchiveAsync` 与 `AppendChatLog` 用了 `KnowledgeFileStore.JsonOptions`，而它是 `WriteIndented = true`。于是一个对象被展开成 6 行左右的漂亮 JSON，"一行一个对象"的前提失效。 |
| **影响** | `ChatMessage` 日志与归档都不是合法 JSONL。`ConversationRefiner` 逐行反序列化成 `ChatMessage` 时几乎全部抛 `JsonException`，而失败被逐行 `catch` 掉 —— 表现为"整理器永远认为聊天记录太少、直接跳过"，**静默失效**，从现象上完全看不出来。 |
| **修复** | 新增 `KnowledgeFileStore.JsonLineOptions`（与 `JsonOptions` 一致，仅 `WriteIndented = false`），并让两处 JSONL 写入改用它。 |
| **验证** | 新增两条断言守住契约：「每行可独立反序列化」（18 行 0 失败）与「每个归档行恰好对应一条消息」（18 / 18）。 |

修复前后对比：归档 18 条消息 → **116 行（不可解析）** 变成 **18 行（全部可解析）**。

---

## 7. OOM 回退循环验证（手册 §3.3，tools/probes/OomProbe + tools/probes/FakeLlamaServer）

显存不足在正常环境下很难按需复现，因此写了一个**假 llama-server**：
它前 N 次启动会向 **stderr** 打印 `CUDA out of memory` 后退出（与真实 CUDA 版行为一致），
之后的启动才真的在指定端口上提供 `{"status":"ok"}`。
这样就能精确走一遍"检测 OOM → 减 4 层 → 等待 → 重启 → 再检测"。

### 7.1 场景 A：回退两次后成功恢复

```
--- 场景 A：前 2 次 OOM，第 3 次成功（应回退 28 → 24 → 20）---
    [气泡/Warning] 显存不足，已把 GPU 推理层数从 28 降到 24 后重试（共可重试 2 次）。
    [气泡/Warning] 显存不足，已把 GPU 推理层数从 24 降到 20 后重试（共可重试 2 次）。
  结果     : Started
  最终 ngl : 20
  运行模式 : GPU 模式（ngl=20/28，空闲显存 6437 MB）
  ngl 序列 : 28,24,20（共 3 次启动）
  [PASS] 场景 A 最终启动成功  —— Started
  [PASS] 场景 A 最终 ngl 为 20（回退两次各 -4）  —— ngl=20
  [PASS] 场景 A 的 ngl 序列为 28,24,20  —— 28,24,20
  [PASS] 场景 A 只启动了 3 次  —— 3 次
  [PASS] 场景 A 告知用户已降层重试
  [PASS] 场景 A 不误报「LLM 服务已离线」
  [PASS] 场景 A 运行模式反映真实 ngl（20 而非计划值 28）
```

假服务记录的原始 ngl 序列（`fake_ngl_log.txt`）：`1:ngl=28`、`2:ngl=24`、`3:ngl=20`
—— 回退步长与手册要求的"减少 4、最小为 1"完全一致。

### 7.2 场景 B：重试耗尽后放弃

```
--- 场景 B：每次都 OOM（重试 2 次后应放弃）---
    [气泡/Error] 显存不足，请关闭其他程序后重试。
  结果     : OutOfMemory
  最终 ngl : 20
  消息     : 显存不足，回退 2 次后仍失败（ngl=20）。
  ngl 序列 : 28,24,20（共 3 次启动）
  [PASS] 场景 B 判定为显存不足  —— OutOfMemory
  [PASS] 场景 B 命中重试上限（只启动 3 次 = 1 次 + 2 次重试）  —— 3 次
  [PASS] 场景 B 提示用户关闭其他程序
```

### 7.3 场景 C：显存不足以承载任何一层（决策 Q6）

手册字面写的是 `ngl = max(1, ...)`，但显存不足时强行上 1 层同样会 OOM
（KV Cache 与上下文仍占显存），只会把"可解释的 CPU 降级"变成"不可解释的启动失败"。
因此按决策 Q6 改为直接 `ngl=0` 并切 CPU。本场景把预留量设成远大于空闲显存来验证：

```
--- 场景 C：显存不足以承载任何一层（应 ngl=0 并切 CPU，决策 Q6）---
  空闲显存   : 6437 MB
  保守可用   : -85,128 MB
  计划 ngl   : 0
  运行模式   : Cpu
  说明       : 空闲显存 6437 MB 扣除预留 101024 MB 后不足，已切换为 CPU 模式
  [PASS] 显存不足时 ngl 置为 0  —— ngl=0
  [PASS] 显存不足时运行模式切为 CPU  —— Cpu
  [PASS] 显存不足时告知用户
  [PASS] 未按 max(1,…) 强行上 1 层  —— ngl=0
```

### 7.4 这一轮测试暴露并修复的三个缺陷

| # | 现象 | 根因 | 修复 |
| --- | --- | --- | --- |
| 1 | 回退期间弹出「LLM 服务已离线，请手动重启程序」 | 显存不足的进程会**自己退出**，`OnProcessExited` 在 `StopProcessAsync` 之前就触发了，此时 `_intentionalStop` 还是 false | 新增 `_handlingOom` 标志，在检测到 OOM 的瞬间就置位（早于退出事件）；退出处理器据此跳过误报 |
| 2 | 降层后**没有任何提示**，用户不知道程序静默变慢了 | 原代码只在重试**耗尽**时才气泡，回退过程中只写日志 | 每次回退都气泡说明"层数从 X 降到 Y" |
| 3 | 运行模式仍显示 `ngl=28/28`，而服务实际跑在 20 层 | `RuntimeModeState` 只在启动前由 `GPUMemoryManager` 写入一次，回退不更新它 | 启动成功后若 `ngl != 计划值`，把**真实 ngl** 写回运行模式 |

> 另外一个"顺手修掉"的问题：`StartProcess` 原本用
> `StopProcessAsync().GetAwaiter().GetResult()` 同步等待，而它是在 WPF 的 UI 线程上被调用的
> （OOM 回退会连续重启进程，正是高频路径），属于准则 1 明令禁止的"UI 线程阻塞"。
> 已改为真正的 `async` 方法。

---

## 8. GameMonitor 协议与冷却期验证（手册 §3.9 + 决策 Q11，tools/probes/MonitorProbe）

用的是**真实的** `MonitorServer` 与 `MonitorClient`（后者正是 GameMonitor.exe 内部使用的那个），
通过回环 TCP 真跑一遍。

### 8.1 协议编解码与冷却期表

```
--- 协议编解码 ---
  [PASS] 往返 ping / unload / restore / cooldown
  [PASS] 非法 JSON 被拒绝且不抛异常

--- GameCooldownTracker 语义 ---
  [PASS] 刚设置的进程处于冷却期  —— GameA
  [PASS] 大小写不敏感  —— gamea
  [PASS] 不同进程不受影响（手册明确要求）  —— GameB
  [PASS] 剩余冷却时间为正  —— 04:59
  [PASS] 已过期的冷却自动失效  —— GameC（已过期）
```

### 8.2 真实 TCP 往返（127.0.0.1:47777）

```
--- 真实 TCP 往返（127.0.0.1:47777）---
  [PASS] 服务端已开始监听 47777  —— TcpListener.Start 成功
  [PASS] GameMonitor 客户端已连接  —— 115 ms
  [PASS] 服务端收到 ClientConnected 事件  —— 1 次
  [PASS] 心跳（ping）已刷新最后收信时间  —— 0.1 秒前
  [PASS] unload 触发 UnloadRequested  —— GenshinImpact
  [PASS] unload 携带了游戏进程名  —— GenshinImpact
  [PASS] 冷却期内的同进程 unload 被拒绝（决策 Q11 双保险）  —— 已拒绝
  [PASS] 冷却期不影响其它进程的 unload  —— starrail
  [PASS] 客户端收到 cooldown 并写入本地表  —— 04:59
  [PASS] 客户端不会误判无关进程  —— GenshinImpact
  [PASS] restore 触发 RestoreRequested  —— GenshinImpact
  [PASS] restore 携带最后退出的进程名（决策 Q11 的绑定依据）  —— GenshinImpact
```

其中两项直接对应决策 Q11 的**双保险**设计：

- **服务端拒绝**：即使 GameMonitor 因为版本不一致而没有执行冷却期，
  主程序在收到同一进程的 `unload` 时也会拒绝 —— 实测"已拒绝"；
- **不同进程不受影响**：手册明确要求"如果检测到的是不同的游戏进程名，则应正常触发 unload"
  —— 实测 `starrail` 正常触发。

### 8.3 心跳看门狗

```
--- 心跳看门狗（静默客户端应被判为断连）---
    [气泡/Warning] 游戏检测服务已断开。可点"重连游戏检测"重新启动它。
  [PASS] 静默客户端已接入  —— 0 ms
    [看门狗] 判定断连：5 秒未收到心跳
  [PASS] 静默客户端被看门狗判定断连  —— 5063 ms
  [PASS] 断连时给出了气泡提示
  [PASS] 断连判定未过早（不早于阈值 3 秒）  —— 5063 ms
```

用"连上后什么都不发"的裸 TCP 客户端验证：阈值 3 秒 + 看门狗 5 秒轮询 → 5063 ms 判出，
既不误判也不漏判，并给出气泡与"重连游戏检测"入口。

---

## 9. GPU 推理验证（CUDA 构建）

### 9.1 为什么改用 Ninja 构建

第一次 CUDA 构建用的是 `build_native.bat` 里的 NMake 路径，结果是**串行**的：
30 分钟只到 26%，全量约需 2 小时。llama.cpp 的 CUDA 后端要编译数百个 `.cu`，
其中大量是重模板化的 fattn 实例，非常适合并行。

本机 8 物理核 / 16 逻辑核 / 15.3 GB 内存。因此新增 `tools/build_llama_cuda.bat`：
取来便携版 Ninja（285 KB，v1.13.2），用 Ninja 生成器 + **6 路并行**
（nvcc 每进程吃 1-2 GB 内存，16 路会有换页风险）。整个构建在 20 分钟量级完成。

### 9.2 构建产物

```
bin\ggml-cuda.dll        54.18 MB    ← 真正的 CUDA 后端
bin\llama-server.exe                （llama.cpp 0.7.0-dev）
bin\cpu-backup\           16 个文件  ← 原 CPU 版完整备份，便于回退
```

配置阶段实测：`Found CUDAToolkit … 13.3.73`、`Using CMAKE_CUDA_ARCHITECTURES=120a`
（`a` 后缀是 Blackwell 的架构变体，比裸 120 更合适）。

### 9.3 GPU 真的在用的三重证据

**证据一：显存占用变化**

| 时刻 | 显存占用 |
| --- | --- |
| 启动前基线 | 1090 MB |
| 以 `-ngl 99` 加载模型后 | **2242 MB**（+1152 MB） |
| 终止 llama-server 后 | 1070 MB（回到基线） |

1.5B Q4_K_M 模型全量上卡约 1.1 GB，与实测增量吻合。

**证据二：GPU 计算进程列表（决定性）**

`nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv`：

```
启动前   15 个进程，无 llama-server
启动后   28044, E:\DEEPSEEK\bin\llama-server.exe   ← 出现在 GPU 计算进程里
终止后   15 个进程，llama-server 消失
```

llama-server 被驱动登记为 GPU 计算客户端。这直接证明它在用 GPU，
而不是"GPU 版二进制但实际跑在 CPU 上"。

**证据三：日志与运行模式**

CPU 版会打印 `warning: no usable GPU found, --gpu-layers option will be ignored`；
CUDA 版**没有**这条警告。`/health` 返回 `{"status":"ok"}`，应用层上报：

```
运行模式  : GPU 模式（ngl=10/28，空闲显存 6838 MB）
ngl 推导  : 空闲 6836 MB − 预留 5120 MB = 1716 MB，× 0.9 = 1,544 MB 保守可用；
            ÷ 150 MB/层 = 10 层；层数上限 28（从 GGUF 元数据探测）⇒ ngl=10
```

> `ngl` 从之前的 13 降到 10，是因为测试时 GPU 上还有其它进程占显存
> （空闲从 7338 MB 降到 6836 MB）。这正好说明按"实时空闲显存"计算 ngl 是必要的。

### 9.4 性能对比（同一台机器、同一个 1.5B 模型）

| 推理后端 | LLM 端到端耗时 |
| --- | --- |
| CPU 构建，ngl 被忽略 | 654 ms |
| CUDA 构建，ngl=10/28（部分卸载） | **436 ms** |

仅 10/28 层上卡就快了 33%；若显存允许全量卸载，提升会更大。

### 9.5 部署注意（重要）

`bin\` 里**没有** `cublas64_*.dll` / `cudart64_*.dll`。CUDA 运行时是通过系统
CUDA 工具包（`C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin`，已在 PATH）
解析的。因此：

- **本机**可以正常运行；
- **换到没装 CUDA 工具包的机器上会因缺 DLL 而启动失败**。

分发时需要把 CUDA 运行时 DLL 一起复制到 `bin\`，或要求目标机安装对应版本的 CUDA 运行时。

---

## 10. WebRTC VAD 验证（libfvad 真实构建）

### 10.1 构建

libfvad 只产出**静态库**，且 `include/fvad.h` 里没有任何 `__declspec(dllexport)`，
直接共享构建出来的 DLL 在 Windows 上**零导出符号**。

因此新增 `third_party/fvad_shim`：把 libfvad 的 13 个 `.c` 直接编进一个 DLL，
用 `fvad_exports.def` 精确导出 6 个公开函数，`build_fvad.bat` 一键构建。
产物 `bin\libfvad.dll`（22 KB）。`dumpbin /EXPORTS` 实测：

```
1 fvad_free      2 fvad_new           3 fvad_process
4 fvad_reset     5 fvad_set_mode      6 fvad_set_sample_rate
```

### 10.2 发现并修复的严重缺陷：`fvad_reset` 会把采样率打回 8kHz

这是本轮最有价值的发现，**会导致语音输入静默完全失效**。

**现象**：接上 libfvad 后，8.43 秒真实语音的 421 帧里，**0 帧**被判为语音。

**定位过程**：

1. 给 `FvadDetector` 增加 `LastRawResult`（暴露 `fvad_process` 的原始返回值）——
   "判定为 false"有两种完全不同的原因：真的非语音（0）、帧长非法（-1），只看 bool 无法区分；
2. 实测分布：`-1 → 421 帧`，**全部是帧长非法**；
3. 读 `third_party/libfvad/src/fvad.c`，找到根因：

   ```c
   void fvad_reset(Fvad *inst) {
       int rv = WebRtcVad_InitCore(&inst->core);
       inst->rate_idx = 0;      /* ← 把采样率重置回默认 8kHz */
   }
   ```

4. `fvad_set_sample_rate` 只是设置 `rate_idx`，而 `fvad_process` 用
   `valid_length(rate_idx, length)` 校验帧长。8kHz 下 320 样本 = 40ms，属非法 →
   一律返回 -1 → `result == 1` 永远为假 → **永远判定为非语音**。

**影响面**：`ASRManager` 的音频线程在每个语音段结束后都会调用 `vad.Reset()`。
真实运行时：第一段语音还能工作，之后 VAD 就永久失效 ——
麦克风看着在采集，却永远不会触发识别。这类故障极难从现象上定位。

**修复**（`src/AICompanion.Core/Asr/VadDetectors.cs`）：

1. `FvadDetector` 保存 `set_sample_rate` / `set_mode` 的委托与参数，新增 `ApplyConfiguration()`；
2. `Reset()` 改为 `fvad_reset()` **之后立刻重新应用**采样率与模式；
3. 启动自检从"构造后"移到"一次 `Reset()` 之后"，复现运行期真实状态；
   自检失败时打印该实例实际接受的帧长列表，并回退能量 VAD（而不是静默失效）。

### 10.3 修复后的实测结果

```
--- 测试 6：VAD 选择与判定（手册 §3.5 的 WebRTC VAD）---
  实际使用的 VAD: webrtc，帧长 320 采样
  [PASS] auto 模式下使用 WebRTC VAD（而非能量兜底）  —— webrtc
  [PASS] 帧长为 20ms（320 采样 @16kHz）  —— 320
  [PASS] 静音帧判为非语音  —— False
  [PASS] 极低幅噪声判为非语音  —— False
  [信息] 300Hz 满幅正弦 @激进度2 → 语音（不作断言）
  样本：134880 采样（8.43s），峰值 0.640
  fvad_process 原始返回值分布：0 → 92 帧, 1 → 329 帧
  [PASS] 真实语音被识别出语音帧  —— 329/421 帧判为语音（78%）
  [PASS] 语音帧占比处于合理区间（10%-95%）  —— 78%
  激进度对比：mode0=341 帧，mode2=329 帧，mode3=324 帧
  [PASS] 激进度越高检出越少（fvad_set_mode 生效）  —— mode0=341 → mode3=324
  [PASS] libfvad 缺失时自动回退能量 VAD  —— energy
```

**测试样本**：用 Windows SAPI（Microsoft Huihui Desktop，zh-CN）合成的真实中文语音，
16kHz / 16bit / 单声道 / 8.43 秒，保存在 `tools/probes/testdata/speech.wav`。

**激进度单调性**（341 → 329 → 324）是一条比"某信号应该被判成什么"更稳健的断言：
它证明 `fvad_set_mode` 确实生效，而不是被忽略。

### 10.4 两次写错的测试期望（如实记录）

值得保留，因为它说明"用真实数据验证"与"按想象断言"的区别：

1. **第一次**：断言"纯正弦音应被判为语音"。实测 0/25 帧 —— 但那是因为采样率 bug，
   所有输入都返回 -1，并非 VAD 的真实判断；
2. **第二次**：修好采样率后，反过来断言"纯正弦应被判为非语音"。实测**它就是语音**。

最终结论：WebRTC VAD 用的是各频带对数能量的 GMM，强低频音确实可能落进语音分布，
它不是语音识别器。因此这条改为**信息性输出**，不作断言 ——
为了让测试变绿而反复调参数或改期望，等于把测试变成装饰。

---

## 11. 资源获取

本机网络受限（GitHub 直连约 22 KB/s、HuggingFace 完全不可达），
因此写了 `tools/Fetcher`（多镜像轮换 + HTTP Range 断点续传 + 停滞检测）。

实测可用通道：

| 通道 | 速率 |
| --- | --- |
| `hf-mirror.com`（HuggingFace 镜像） | 0.8–0.9 MB/s |
| `ghfast.top` / `gh-proxy.com`（GitHub 代理） | 0.3–1.0 MB/s |
| nuget.org | 3.7 MB/s |
| GitHub 直连 | ~0.02 MB/s |
| HuggingFace 直连 | 不可达 |

已获取（含校验）：

| 资源 | 大小 | 校验 |
| --- | --- | --- |
| `tools/toolchain/cmake`（便携版 CMake 4.4.4） | 51.9 MB | ZIP 魔数 + 可执行验证 `cmake version 4.4.4` |
| `third_party/whisper.cpp` | 10.2 MB | ZIP 魔数 + 头文件存在 |
| `third_party/llama.cpp` | 38.3 MB | ZIP 魔数 + CMakeLists 存在 |
| `models/asr/ggml-base.bin` | 141.1 MB | 体积 + 真实加载推理（见 §3） |
| `models/llm/qwen2.5-1.5b-instruct-q4_k_m.gguf` | 1065.6 MB | 体积 + **GGUF 魔数**校验 |

> 关于模型选择：手册基准是 Qwen2.5-7B Q4_K_M（约 4.7GB）。
> 按决策 Q7，本轮用 1.5B 测试模型打通链路；`config.yaml` 的 `llm.model_path`
> 当前指向 1.5B，换回 7B 只需改这一行。

---

## 12. 尚未验证的部分（如实列出）

| 项 | 原因 |
| --- | --- |
| **真实游戏进程的让路/恢复端到端** | 协议层已用真实 TCP 往返全面验证（§8），但**没有真的启动一个游戏**来看 GameMonitor 的进程扫描 + GPU 使用率双条件是否按预期触发。这需要人工开一局游戏。 |
| Piper 语音合成 | Piper C 库与模型未提供（决策 Q9 后置）。 |
| Live2D 真渲染 | ~~决策 Q4 降级为静态图~~ **已补齐**：真实 Cubism 渲染跑通，见 §19–§20；下方降级路径的实测记录保留。 |
| 长对话压力测试（≥50 轮）与 12 小时泄漏测试 | 属手册阶段 4。摘要压缩本身已在 §6.7 实测，但"长时间连续对话 + 内存/显存不增长"需要长跑观察。 |
| GameMonitor 内存 <10MB | 已确认为 .NET 栈的固有限制（决策 Q13）。 |
| 休眠唤醒处理 | `SystemEvents.PowerModeChanged` 的订阅与 `HandlePowerResumeAsync` 已实现，但无法在自动化验证中真实触发系统休眠。 |
| 目标部署机（RTX 3060 12GB）上的 GPU 性能 | 本轮 GPU 验证在开发机（RTX 5060 Laptop 8GB）上完成，且是部分卸载（ngl=10/28）。12GB 全量卸载的表现需在部署机测。 |

### 12.1 已在后续轮次补齐的项

以下各项最初列为"代码完成但未运行时验证"，现均已补齐实测：

| 项 | 补齐情况 | 见 |
| --- | --- | --- |
| 手册 §3.3 的 OOM 回退循环 | ✅ 用假 llama-server 注入 OOM，验证"回退后恢复"与"重试耗尽"两条路径 | §7 |
| `ngl ≤ 0` 切 CPU | ✅ 预留 100GB 强制触发，验证 `ngl=0` + CPU 模式 | §7.3 |
| 「停止生成」的断流取消 | ✅ 实测取消耗时 15 ms，且取消后服务仍可正常生成 | §6.5 |
| GameMonitor 的 TCP 往返与冷却期 | ✅ 真实 TCP 往返 26 项断言全通过 | §8 |
| **GPU 推理（CUDA 构建）** | ✅ Ninja 并行构建成功；显存 +1152MB、nvidia-smi 计算进程列表、应用层 GPU 模式三重证据 | §9 |
| **WebRTC VAD** | ✅ libfvad 真实构建；并修掉了会让语音输入静默失效的 `fvad_reset` 缺陷 | §10 |
| **上下文摘要压缩** | ✅ 灌到 48 条触发压缩，窗口收敛到 30，摘要入栈，18 条原文归档；并修掉 `.jsonl` 不是合法 JSONL 的缺陷 | §6.7 |
| **日志轮转与面板接口** | ✅ 按日文件命名、级别过滤、环形缓冲顺序、目录占用、超期清理、当日文件不误删 | §6.6 |
| **Live2D 真渲染** | ✅ Cubism SDK 真实渲染跑通（Hiyori 完整显示）；动作/口型/表情联动 46 项断言全过；并定位修复了合成与命中判定问题 | §19–§20 |
| **按住说话的语音输入（PTT）** | ✅ 27 项断言 + 真实语音端到端（真实 whisper 识别 1559ms，RTF 0.33）；并把"按住没收到音频"与"误触"区分开 | §23 |
| **两小时连续运行**（非手册要求的 12 小时） | ✅ 122 分钟：内存 456→188MB 无单调上升、句柄 1047↔1060、0 崩溃；期间两次麦克风设备异常被自愈机制自动恢复 | §25 |

---

## 13. 鼠标交互验证（左键功能栏 / 右键模式切换）

### 13.1 两种窗口状态（Win32 扩展样式实测）

用 `GetWindowLong(GWL_EXSTYLE)` 读真实样式，而不是看代码推断：

```
[1] 启动后角色窗口
    exStyle=0x80008  layered=True  transparent=False   ← 覆盖（可交互）
[2] 右键一次
    exStyle=0x80028  layered=True  transparent=True    ← 透明（鼠标穿透）
```

`0x80000` = `WS_EX_LAYERED`，`0x00020` = `WS_EX_TRANSPARENT`。
两者的差别正是"能否收到鼠标事件"，所以这张表就是左键/右键交互能否工作的前提。

> 为什么默认必须是覆盖态：`WS_EX_TRANSPARENT` 会让窗口**完全忽略鼠标**，
> 穿透状态下左键点角色、右键切换都收不到事件。因此 `ui.click_through` 的默认值
> 从 `true` 改成了 `false`，配置文件里同步加了说明。

### 13.2 功能栏位置（左键弹出）

```
角色窗口: rect=(225,34)-(565,494)   → 底边 y=494，水平中心 x=395
功能栏  : rect=(259,501)-(531,553)  → 顶边 y=501（下方 7px），水平中心 x=395
```

功能栏确实**贴在角色正下方并水平居中对齐**。再点一次角色，功能栏变为不可见。

定位逻辑还处理了屏幕边界：下方放不下时自动翻到角色上方（`TaskBarWindow.ShowBelow`）。

### 13.3 功能栏按钮（UI Automation 实测）

一开始我用"按宽度百分比估算坐标"去点按钮，结果点到了「更换外观」，
弹出了文件选择对话框 —— 估算不可靠。改用 UI Automation 直接按名称定位并调用：

```
功能栏内按钮数: 4
  - 名称='设置'      位置=(595,891) 尺寸=81x51
  - 名称='更换外观'  位置=(686,891) 尺寸=123x51
  - 名称='对话'      位置=(819,891) 尺寸=81x51
  - 名称='↺'         位置=(910,891) 尺寸=57x51
```

按钮接线验证（用 `InvokePattern.Invoke()` 真正触发）：

| 按钮 | 结果 |
| --- | --- |
| 对话 | 日志出现 `[气泡/Info] 在角色下方的输入框里输入内容，按回车发送。` ✅ |
| 设置 | 打开了标题为 `AI 伴侣 · 设置` 的窗口，并成功通过 `WindowPattern.Close()` 关闭 ✅ |
| 更换外观 | 弹出标题为 `选择角色形象图片` 的文件对话框 ✅ |
| ↺ | 日志出现 `用户恢复默认外观（已删除 Assets\appearance.json 中的覆盖）。` ✅ |

### 13.4 顺带修掉的启动误报

第一次启动后日志里每次都有一条 `[气泡/Warning] 未找到任何角色形象素材，已使用纯色背景显示。`，
**即使素材其实存在**。原因是 `MainWindow` 构造时要先加载一次形象（否则启动早期窗口空白），
而此时 `config.yaml` 还没读、`AppPaths` 仍指向 exe 目录，找不到素材是必然的。

修复：`LoadAvatar(bool provisional)` —— 构造阶段以 `provisional: true` 调用，
只记 Debug、不弹气泡；配置加载后 `OnHostReady` 再做正式加载。
修复后启动日志里不再有这条假告警。

### 13.5 占位形象

项目原本没有任何形象素材，角色显示为纯色方块。生成了一张 512×512 的占位 PNG
（`Assets/Default/default.png`，13.3 KB，带 alpha），走三级兜底的第一级：

```
[App] 角色形象：使用角色图片 E:\DEEPSEEK\Assets\Default\default.png
```

### 13.6 透明态悬停切换（双向实测）

穿透态下窗口收不到 `WM_MOUSEMOVE`，所以悬停是"每 100ms 轮询 `GetCursorPos` +
像素级 alpha 命中判定"实现的。实测日志形成完整闭环：

```
13:52:08.765  交互模式切换为透明（鼠标穿透），来源=手动    ← 右键
13:52:09.185  交互模式切换为覆盖（可交互），来源=悬停      ← 光标停在角色上 420ms 后自动切回
13:52:24.880  交互模式切换为透明（鼠标穿透），来源=悬停    ← 光标移开 3 秒自动退回
13:52:27.169  交互模式切换为覆盖（可交互），来源=悬停      ← 光标移回再切回
13:52:31.441  交互模式切换为透明（鼠标穿透），来源=悬停    ← 再移开再退回
```

同步读取 `GWL_EXSTYLE` 交叉验证，`WS_EX_TRANSPARENT` 的置位/清除与日志一一对应。

**只对悬停触发的切换自动回退**：用户手动右键切过去的模式不会被自动改掉。

### 13.7 排查过程中踩的两个坑（都值得记下来）

**坑一：穿透态下连悬停事件都没有。**
最初想用 `MouseEnter`/`MouseLeave`，但 `WS_EX_TRANSPARENT` 会让窗口对鼠标完全透明，
这些事件永远不会触发。改成轮询 + `WindowFromPoint` 级别的判定才可行。
最终用的是"光标屏幕坐标 → 窗口 DIP → 图片元素坐标 → 像素 alpha"这条链。

**坑二：DPI 坐标空间不一致，导致自动化验证连续失败。**
开发机是 2560×1440 @ **175%**，逻辑分辨率只有 1463×823。
我的验证脚本（`powershell.exe`）是 **DPI-unaware** 进程，`SetCursorPos` 用的是虚拟化坐标；
而应用是 DPI 感知的，`GetCursorPos` 返回物理坐标。两边差 1.75 倍，
表现为"明明把光标放到角色上了，程序却认为它在窗口外"：

```
我设 SetCursorPos(606, 816)  →  程序看到 cursor=1061,1428（= 606×1.75, 816×1.75）
```

最后改成**不猜坐标**：在我的坐标空间里对窗口做栅格扫描（每点停留 400ms > 250ms 阈值），
一次就定位到了能触发悬停的位置。这条经验对以后写 GUI 自动化验证同样成立。

### 13.8 三处"抽搐 / 大小跳变"缺陷（均为实测定位）

用户报告"拖动时 UI 抽搐""右键按一下大小会变"。这是四个独立成因，都已修复并量化验证。

#### 缺陷 A：拖动用了相对坐标，形成反馈回路

**根因**：位移用 `e.GetPosition(this)` 计算 —— 那是**相对于窗口**的坐标。
拖动时窗口自己也在动，于是"移动窗口 → 相对坐标随之变化 → 又算出新的位移"，
形成反馈回路。典型的自参照坐标系错误。

**修复**：改用 `GetCursorPos()` 的**屏幕物理坐标**做唯一参照系，
按 `VisualTreeHelper.GetDpi` 换算成 DIP 后累加。位移变成绝对量，回路被切断。

**量化验证**（逐帧采样窗口位置）：

```
逐帧 X 位移: -8,-8,-8,-8,-8,-8,-8,-8,-8,-8,-8,-8,-8,-8
方向反转次数: 0
```

位移完全均匀、零反转；修复前会出现 ±1~2 px 的来回跳动。

#### 缺陷 B：拖动时每帧 `Activate()` 抢焦点

**根因**：拖动每帧调用 `_taskBar.ShowBelow(this)`，而它内部有 `UpdateLayout()` + `Show()` + `Activate()`。

**修复**：拆出 `MoveBelow(owner)` —— 只设置 `Left/Top`，不 Show、不 Activate、不做布局；
拖动期间调它，拖动结束才调 `ShowBelow`。

边界钳制同时改为"拖动中软钳制"：`ClampKeepVisible()` 只保证窗口至少 30% 在工作区内。
修复前是"拖动中不钳制、松手才全量钳回"，贴边拖动时松手瞬间窗口会**跳**一下。

#### 缺陷 C：`Collapsed` 让角色被拉伸 —— "右键一下大小会变"

**根因**：`ApplyClickThrough` 在穿透时把输入区设为 `Visibility.Collapsed`。
输入区位于 Grid 最后一行（`Height=Auto`），`Collapsed` 使该行高度归零，
于是角色所在的 `*` 行变高 44 DIP，图片是 `Stretch="Uniform"`，跟着从 328 缩放到 372。

**修复**：改用 `Visibility.Hidden` —— 保留占位、只是不绘制。

**实测证据**（新增布局日志，仅在切换模式时输出一行）：

```
00:24:52.496  布局：角色图片元素 328×328，输入区显示（高 44），气泡区高 120。
00:24:53.476  布局：角色图片元素 328×328，输入区隐藏但占位（高 44），气泡区高 120。
```

两种模式下图片元素**都是 328×328**，输入区高度都是 44。

#### 附带：气泡不再挤动角色

气泡行原来是 `Height="Auto"`，**每弹一个气泡都会改变角色行高度**，角色随之缩放 ——
这也是"抽搐"的一部分。改为把气泡 `ScrollViewer` 放进 `Grid.Row=1` 与角色**同格叠加**
（`Panel.ZIndex=1`、`VerticalAlignment=Top`、`IsHitTestVisible=False`），行高固定为 0。
`IsHitTestVisible=False` 是必须的，否则气泡会挡住对角色本身的左键/右键。

#### 缺陷 D：悬停切换与手动切换互相打架（模式震荡）

**现象**：日志实测 55 秒内切换 **25 次**（13 次透明 / 12 次覆盖），其中 11 次"手动"。
每次切换都改窗口扩展样式并弹气泡，视觉上就是剧烈抽搐。

**根因**：右键切到透明时，光标通常正压在角色身上；250ms 后悬停逻辑立刻切回覆盖；
用户再右键、又被切回…… 无限对抗。

**修复**：新增 `_hoverArmed`。切到透明时置 false，**必须先在角色外被观测到一次**才置 true，
之后移回角色上才允许悬停触发切换。

**验证**：

```
[1] 右键切到透明（成功）
[2] 光标保持不动 3.5 秒 → 模式=透明，期间新增切换 0 条   ✓ 稳定，无震荡
[3] 移开 1.2 秒再移回 → 覆盖（悬停触发）                 ✓ 悬停依然有效

完整序列仅 2 条：
  00:21:40.088  透明（来源=手动）
  00:21:45.563  覆盖（来源=悬停）
```

---

## 14. DPI / 分辨率适配与角色大小

### 14.1 环境事实（这台机器）

```
显示环境：DPI 175%（1.75×），主屏 1463×823 DIP（物理约 2560×1440 px），
          工作区 1463×823 DIP，虚拟屏 1463×823 DIP
```

也就是说：**物理 2560×1440，逻辑只有 1463×823**。基准 340×460 DIP 的角色窗口，
在这台机器上占工作区高度的 **56%** ——固定尺寸必然在某一类机器上不合适，
这正是"大小可调"的必要性来源。

### 14.2 高 DPI 感知

新增 `src/AICompanion.App/app.manifest` 声明 **PerMonitorV2**，并在 csproj 里通过
`<ApplicationManifest>` 引用。验证清单确实被嵌进 exe：

```
含 PerMonitorV2: 是
含 dpiAwareness: 是
```

> 构建时 WinForms 分析器报 `WFAC010`，建议改用 `ApplicationHighDpiMode`。
> 该属性只对 WinForms 的 `ApplicationConfiguration.Initialize` 生效，
> 本程序入口是 WPF `Application`（WinForms 只用于托盘的 `NotifyIcon`），
> 因此保留清单并用 `<NoWarn>` 抑制该警告，理由写进了 csproj 注释。

### 14.3 大小可调与自动适配

`ui.scale`：`0` = 按屏幕自动适配（默认），`0.5`–`2.5` = 固定倍率。
自动适配的目标是"角色高度约占工作区 45%、宽度不超过 60%"。
本机算得 **80%**，窗口实际变为 **272×368 DIP**（368 / 823 = 44.7% ✔）。

缩放作用于整棵可视树（`LayoutTransform` + 代码创建的 `ScaleTransform`），
文字、气泡、输入框与角色等比变化。

> 变换对象刻意不在 XAML 里内联：**XAML 里声明的 `Freezable`（含 `ScaleTransform`）
> 会被 WPF 冻结**，之后改 `ScaleX` 会抛 "Cannot modify a frozen object"。
> 必须 `new ScaleTransform(1, 1)` 在代码里创建并挂到 `LayoutTransform`。

### 14.4 功能栏 −/＋ 与持久化实测

用 UI Automation 按名称定位并 `Invoke()` 真实点击（按钮顺序：
`设置` / `更换外观` / `对话` / `−` / `＋` / `↺`）：

```
[PASS] ＋ 两次：80% → 85%（窗口 289×391）→ 90%（窗口 306×414），均「已记住」
[PASS] − 一次：90% → 85%（窗口 289×391），「已记住」
[PASS] 写入 Assets\appearance.json：{ "imagePath": null, "scale": 0.85, ... }
[PASS] 重启后：角色大小：85%（来源=用户上次调整（Assets\appearance.json）），窗口 289×391
```

`Assets\appearance.json` 是用户偏好，**不改 `config.yaml`**（准则 9）；
`↺` 会同时清除外观与大小两项覆盖，回到"config.yaml / 自动适配"。

### 14.5 上限受屏幕约束

倍率上限不只是常量 2.5，还要满足"窗口不超出工作区"：

```
maxByScreen = min(工作区宽 × 0.95 / 340, 工作区高 × 0.95 / 460)
```

本机工作区高 823 DIP → 上限约 1.70 倍。超出时会自动收敛，
并在气泡里注明"已受屏幕尺寸限制"，避免角色被放大到一半在屏幕外、用户又拖不回来。

---

## 15. 角色立绘、语音兜底与文件整理

### 15.1 角色立绘与表情差分（tools/draw_avatar.ps1）

> 图标：早期 `Assets\app.ico` 由民间立绘裁切而来，公开仓库版本改为 `tools/make_icon.ps1` 现场生成的几何图标（256×256 PNG-in-ICO）。期间踩到两个坑：目录项少写 2 字节会导致 CSC 报 CS7065「Unable to read beyond the end of the stream」；小尺寸帧必须用 BMP 而非 PNG。

项目原本没有任何形象素材，角色显示为纯色方块。新增 `tools/draw_avatar.ps1`，
用 GDI+ 在 1024×1024 透明画布上生成：

| 文件 | 用途 |
| --- | --- |
| `Assets/Default/default.png` | 默认立绘 |
| `Assets/Default/Expressions/wave.png` | 挥手（额外画了抬起的手与袖子） |
| `.../happy.png` `nod.png` `smile.png` | 开心 / 点头 / 微笑 |
| `.../sad.png` `angry.png` `shy.png` `thinking.png` | 难过 / 生气 / 害羞 / 思考 |

文件名必须与 LLM 输出的动画指令一致（`ApplyExpression` 按 `<动画名>.png` 查找）。

**实测**：9 张全部生成，单张 46–67 KB；首个像素采样确认**背景透明**
（四角 `A0`，中心 `A255`），不透明像素占比 43.7% —— 这个比例也决定了悬停命中区域的大小。

制作过程中修掉两个脚本缺陷：
1. XAML 里内联的 `ScaleTransform` 会被冻结（与本文件 §14.3 同源）；
2. 用 `AddLine([single]$pt[0], [single]$pt[1])` 时 PowerShell 会把重载解析到
   `AddLine(Point, Point)` 并报转换失败 —— 改为显式 `PointF[]` + `AddLines`。

### 15.2 语音合成：从"永远不出声"到可用

**问题**：TTS 配置为 piper，但 Piper 的 `piper.exe` 与 `.onnx` 模型都不存在，
原实现只弹一条"语音合成暂不可用"就结束 —— 结果是程序**从头到尾不会说一句话**。

**修复**：新增 `SapiEngine`（Windows 内置语音），并在 `TTSManager` 里加一级降级：
Piper 不可用 → SAPI。日志与气泡都标明降级，不是静默替换。

```
[TTS] 按配置创建 TTS 引擎：piper
[TTS] Piper 不可用，降级为 Windows 内置语音（SAPI）。
[TTS] TTS 初始化完成：配置引擎=piper，实际引擎=sapi，降级=True。
[App] 启动完成：… TTS=sapi（降级=True）…
```

**实现过程中踩的两个坑（都记下来，避免重复）**：

1. **.NET 8 的 `System.Speech` 包调用 `SpeechSynthesizer.Speak` 必抛
   `NullReferenceException`** —— 线程池 MTA 与自建 STA 线程都一样；
   而同一条 SAPI 能力在 PowerShell（.NET Framework）里完全正常。
2. 最终改为**直接驱动 SAPI COM**（`SAPI.SpVoice` + `SAPI.SpFileStream`，经 `Type.GetTypeFromProgID`
   与 `dynamic` 晚期绑定），既绕开该缺陷，又省掉一个 NuGet 依赖。
   选音色用"配置音色名匹配音色描述子串 → 系统默认"的兜底。

**实测**（`tools/probes/PipelineProbe` 测试 8）：

```
[PASS] SAPI 引擎报告可用  —— True
  合成耗时 64 ms，WAV 153878 字节
[PASS] 合成返回非空 WAV
[PASS] WAV 头正确（RIFF/WAVE）  —— RIFF....WAVE
[PASS] 工厂能把引擎类型 sapi 解析为 SapiEngine
```

### 15.3 表情自动回退

表情图是**整张替换**立绘，原实现一旦切到某个表情就永不回退 —— 说过"挥手"之后
角色会一直保持挥手姿势。新增 `ui.expression_hold_seconds`（默认 8 秒，0 = 不回退）：
到期后走默认加载链恢复，且**优先恢复用户自选的外观**，不会被 `ui.pet_image_path` 覆盖掉。

### 15.4 文件整理

清理前 **2.62 GB / 17199 个文件**，清理后 **1.54 GB / 14858 个文件**，释放 **1096.7 MB**。

| 删除项 | 大小 | 理由 |
| --- | --- | --- |
| `third_party/llama.cpp/build` | 346.2 MB | CPU 构建中间产物，可由 `build_native.bat` 重新生成 |
| `third_party/llama.cpp/build-cuda` | 527.9 MB | CUDA 构建中间产物，可由 `build_llama_cuda.bat` 重新生成 |
| `third_party/ai_asr/build` | 31.7 MB | CMake 中间产物 |
| `third_party/fvad_shim/build` | 0.4 MB | CMake 中间产物 |
| `tools/src` | 101.6 MB | 已解压的源码压缩包（删前确认 `whisper.h` 未被任何构建引用） |
| `src/*/{bin,obj}`、`tools/*/{bin,obj}` | 89 MB | .NET 构建产物 |
| `tools/bootstrap-download.ps1` | — | 已被 `tools/Fetcher` 取代，无任何引用 |
| 空目录 | 11 个 | CMake 残留与占位目录 |

删除前逐条校验了绝对路径（必须位于 `E:\DEEPSEEK\` 下），并确认：
`build_native.bat` 被 4 处文档引用（保留）、`tools/src/whisper.h` 未被 ai_asr 使用、
`bootstrap-download.ps1` 无引用。`bin/`、`models/`、`third_party` 源码全部保留。

### 15.5 默认形象改为"目录驱动"（不绑定文件名与格式）

**用户反馈**：`Assets\Default` 里的图就是默认图，不该要求它叫 `default.png`。

**改动前**：形象来源写死为 `ui.pet_image_path`（默认值 `./Assets/Default/default.png`）。
用户把自己截的图丢进目录、名字任意（如 `1792415130246.jpg`）时，程序**完全找不到**，
只能退化成纯色背景 —— 日志报"角色形象全部缺失"。

**改动后**的四级链路：

| 级别 | 来源 | 说明 |
| --- | --- | --- |
| 0 | `Assets\appearance.json` | 用户通过功能栏"更换外观"显式选择的图（不写 config.yaml） |
| 1 | `ui.pet_image_path` | 配置指定且文件确实存在时生效 |
| 2 | **扫描 `Assets\Default`** | **新增**：任意文件名、任意受支持格式 |
| 3 | `live2d.default_texture_path` | 全局默认贴图 |
| 4 | 纯色背景 + 角色名 | 最后兜底 |

扫描规则（多张时保证结果确定且可解释）：
1. 优先名字以 `default` 开头的（兼容老约定）；
2. 否则取**最后修改时间最新**的那张；
3. 命中文件与取舍理由写进日志。

支持格式：`.png` `.jpg` `.jpeg` `.bmp` `.gif` `.tif` `.tiff`。
**WebP 不支持**（WPF 无内置解码器），因此没有列入，避免"文件在目录里却被跳过"的困惑。

**实测**：

```
[测试 A] 目录: 我的角色图.png + 说明.txt（干扰文件）
  → 角色形象：使用 Assets\Default 中的图片 ...\我的角色图.png（目录中唯一的图片）

[测试 B] 目录: character.png
  → 角色形象：使用 Assets\Default 中的图片 ...\character.png（目录中唯一的图片）
```

中文文件名、任意名字都生效；`.txt` 被正确忽略。

**顺带处理的素材问题**：用户放进来的是扩展名为 `.jpg`、实际为 PNG
（魔数 `89 50 4E 47`）、且**无 alpha 通道**（`Bgr32`，1922×2182，纯白背景）的图。
直接当桌面宠物会是一块白底矩形，因此：

1. 改名修正扩展名；
2. 用"从四边泛洪 + 中性白判定 + 2 遍边缘羽化"抠掉背景，产出 `Assets\Default\character.png`
   （移除 50.9% 像素；四角 `A=0`、中心 `A=255`、不透明占比 48.6%）；
3. 原始图保留在 `Assets\source\`（程序不读取），不删除用户素材。

---

## 16. 模式可辨识性、气泡到期与外观设置

用户反馈三点：覆盖/透明切换看不出区别、提示信息不消失、秒数要能在设置里改。三条都已实现并实测。

### 16.1 两种模式用不透明度区分

**改动前**：覆盖与透明只差 `WS_EX_TRANSPARENT` 一个样式位，**视觉上完全一样** ——
用户必须点一下才知道自己在哪种模式。

**改动后**：不透明度作用于整棵可视树（`RootGrid.Opacity`）——

| 模式 | 不透明度 |
| --- | --- |
| 覆盖 | 恒为 `1.0`（完全不透明） |
| 透明 | `ui.transparent_opacity`，默认 `0.5` |

实测日志（同一进程内来回切换）：

```
21:13:46.451 鼠标穿透已开启 → 整体不透明度 50%
21:13:48.102 鼠标穿透已关闭 → 整体不透明度 100%
21:13:52.056 鼠标穿透已开启 → 整体不透明度 50%
21:14:01.241 鼠标穿透已关闭 → 整体不透明度 100%
```

截图对比同样成立：透明态能清楚看到她后面的桌面内容，覆盖态完全不透明。
配置校验把该值限制在 `0.1–1.0`（0 会让角色彻底看不见）。

### 16.2 气泡永不过期（缺陷修复）

**现象**：界面上长期挂着几条旧提示，几小时前的"已切换为「透明」…"还在。

**根因**：`BubbleNotifier.Show(key, message, level, duration = null)` 在不传时长时把
`ExpiresUtc` 设为 `null`，而 `IsExpired` 的定义是
`ExpiresUtc.HasValue && DateTime.UtcNow >= ExpiresUtc.Value` —— **对 null 恒为 false**，
于是这类气泡永远不会被清理。

**修复**：`BubbleNotifier` 增加 `DefaultDuration`（默认 5 秒，来源 `ui.bubble_duration_seconds`），
`Show` 在未显式给时长时使用它，保证**所有气泡都有到期时间**。

**实测**：启动后 35 秒再截图 —— 5 条启动气泡（含"可用显存不足""Piper 降级""交互提示"）
全部消失，界面无残留。

### 16.3 设置面板新增「外观」页

| 控件 | 作用 | 持久化 |
| --- | --- | --- |
| 气泡显示时长滑块（1–30 秒） | 所有提示信息的显示秒数 | `Assets\appearance.json` 的 `bubbleSeconds` |
| 透明态不透明度滑块（0.1–1.0） | 透明模式下的整体不透明度 | 同文件的 `transparentOpacity` |

点「保存并应用」立即生效：气泡时长直接改 `BubbleNotifier.DefaultDuration`，
不透明度通过回调让主窗口重新套一次当前模式。**不修改 config.yaml**（准则 9），
删掉 `Assets\appearance.json` 即回到配置默认值。

**实测**（UI Automation 真实操作）：

```
功能栏按钮: 设置 / 更换外观 / 对话 / − / ＋ / ↺
设置面板页签: 状态 / 待审核知识 / 语音 / 游戏检测 / 外观 / 日志
外观页滑块: [0] 当前=5  范围=1-30      [1] 当前=0.5  范围=0.1-1
把滑块[0]改为 3 并点「保存并应用」后：

  21:15:39.688 [App] 外观设置：气泡 3 秒，透明态不透明度 50%
  21:15:39.688 [App] 设置面板已保存外观：气泡 3 秒，透明态不透明度 50%（保存成功）

  appearance.json → { "bubbleSeconds": 3, "transparentOpacity": 0.5, ... }
```

测试产生的偏好已删除，重启后回到配置默认（气泡 5 秒 / 不透明度 50%）。

### 16.4 排查记录：一次"点不动"的误导

验证过程中连续几次点击都没反应，一度怀疑功能栏坏了。实际原因是
**当时窗口正处于「透明」模式**，点击本就该穿透过去 —— 日志最后一条正是
`交互模式切换为透明（鼠标穿透），来源=悬停`。把光标移回角色上（悬停切回覆盖）后，
一次点击就打开了功能栏。

另外截图里一度看到"两个角色窗口"，经全系统枚举确认**只有一个**
（`pid=23428 title='AI 伴侣'`）；下面是分层窗口移动后桌面未重绘干净的残留像素，
不是第二个实例。

### 16.5 去掉模式切换气泡（用户反馈"多余"）

**改动前**：每次切模式都弹一条气泡（如"已切换为「覆盖」：可以点击、拖动角色。左键点我打开功能栏。"）。

**为什么多余**：切换已有三个可视化反馈 —— 右上角按钮文字「覆」↔「透」、
整体不透明度 100% ↔ 50%、以及点击行为本身；而悬停切换在鼠标进出角色时相当频繁，
气泡会不断刷屏。何况那条文案里"左键点我打开功能栏"已经过时（功能栏改成了右键）。

**改动**：删除 `_bubbles.Show("mode-switch", …)`；切换事实仍写日志
（`交互模式切换为…，来源=手动/悬停`），排障不受影响。

**实测**：点右上角按钮切到透明，新增日志只有三行（穿透开启 / 布局不透明度 50% / 模式切换），
**气泡计数保持 5 条不变**（都是启动期的那几条），确认不再新增。

### 16.6 文字可读性：UI 一律放在实心面板上

**现象**：角色立绘是浅色（白发 + 白裙），窗口里所有文字都"看不清"。

**根因**：状态行、输入框、发送按钮是**直接浮在角色图上的**，背景只是
`#33FFFFFF`（20% 白）+ 白色文字 —— 白字压在浅色衣服上等于隐形。
气泡底色是 `#E61E1E28`（90% 深色），但在浅色图上仍然偏灰、笔画发糊。

**改动**（对齐参考桌宠的做法：文字只放在实心卡片上，与底图无关）：

| 位置 | 改动 |
| --- | --- |
| 输入区 | 用 `Border` 包起来：背景 `#F2141720`（≈95% 不透明深色）、圆角 10、1px 浅边 |
| 状态文字 | `#CCFFFFFF` 11px → `#FFE6EBF5` 12px |
| 输入框 | 前景改为纯白，边框 40% 白 |
| 气泡 | 背景 `#E61E1E28` → `#F5141720`；文字 12px → **13px** + `LineHeight=19` |

**实测**（截图核对）：气泡与输入面板均变成实心深色圆角卡片，
在浅色立绘上文字清晰可读 —— 启动气泡（5 条）与状态行
`运行模式: GPU · TTS: sapi · ASR: Stopped` 都能一眼看清。

> 这条经验值得记住：**不要在任意底图上直接叠半透明文字**。
> 角色立绘的颜色不可控，任何"半透明底 + 浅色字"的组合迟早会在某个模型上看不清。

### 16.7 缩放一致性：以底边中点为锚 + 右上角按钮同步缩放

用户反馈过三轮，最终方案如下。

**第一轮现象**：点 `−`/`＋` 时功能栏整体位移，按钮从光标下面跑掉，连续缩放得追着点。
当时错误地用了"冻结功能栏"（缩放期间既不改尺寸也不改位置）——按钮确实不动了，
但功能栏会和角色脱节，放大几次就被角色压在下面。

**第二轮尝试（失败）**：改成"以光标为中心缩放功能栏"。结果**错位**——
锚点取的是光标位置，而光标不总在按钮上（Ctrl+滚轮时在角色身上），
功能栏会被拽到光标所在处。实测功能栏跑到 y=1293，而角色底部只有 938，离了几百像素。

**最终方案**：不动功能栏的定位逻辑，改**角色自己的缩放锚点**。

1. 角色以**底边中点**为锚缩放：
   `bottomCenterX = Left + Width/2`、`bottomCenterY = Top + Height` 先记下，
   改完 `Width/Height` 再反推 `Left/Top`。于是**底边 Y 与中心 X 恒定**；
2. 功能栏照常 `ShowBelow(角色)` —— 因为角色底边没动，重算出来的位置和之前一样，
   视觉上功能栏纹丝不动，且**始终紧贴角色**，不会再错位；
3. 右上角「覆/透」按钮是独立窗口，**不会**自动跟随角色的 `LayoutTransform`，
   所以显式加 `ApplyScale`：尺寸与字号同倍率缩放（下限 18px / 9.5px，太小小到点不准）。

**一个必须修的细节**：`MoveBelow` 原来用 `owner.ActualHeight`。而 `ApplyScale` 是
"先设 Height、紧接着调 ShowBelow"，此时布局还没更新，`ActualHeight` 还是旧值 ——
功能栏会按旧高度定位，表现同样是错位。改为优先用显式 `Width/Height`。

**实测**（连续放大 2 次、缩小 4 次；功能栏"贴下方"= 栏顶 ≈ 角色底 + 9px）：

```
初始  角色 170x230  功能栏 180x26 @(1063,369) 贴下方=True   角标 18x18
＋1   角色 187x253  功能栏 297x43 @(1580,554) 贴下方=True   角标 28x28
＋2   角色 204x276  功能栏 324x47 @(1567,554) 贴下方=True   角标 31x31
−1   角色 187x253  功能栏 297x43 @(1580,554) 贴下方=True   角标 28x28
−2   角色 170x230  功能栏 270x39 @(1594,554) 贴下方=True   角标 27x27
−3   角色 170x230  （已到 50% 下限，不再变化）
−4   同上
```

三条结论：
- **功能栏每一次都紧贴角色下方**（`贴下方=True` 全为真），不再错位；
- 功能栏 Y 坐标恒为 554，缩放过程中**纵向完全不动**；
- 角标随倍率变化：18 → 28 → 31 → 28 → 27 像素，与角色同步。

> 踩过的两个坑值得记下来：
> 1. **别用光标当缩放锚点**。它会随用户操作到处跑，用它定位界面元素必然错位；
>    锚点应该是"被缩放对象自己的某个几何特征"（这里是底边中点）。
> 2. **布局属性有滞后**。改完 `Width/Height` 立刻读 `ActualWidth/ActualHeight` 拿到的是旧值，
>    同一帧内要用就去读显式设置的值。

---
### 16.8 接入真实 Live2D 模型（Hiyori）：只做了「能力解析」，没做渲染

用户放入 `E:\DEEPSEEK\Hiyori\`（官方示例模型）。实际清点：

```
Hiyori.model3.json        1.7 KB   主文件
Hiyori.moc3             433.2 KB   模型数据
Hiyori.physics3.json     25.5 KB   物理
Hiyori.cdi3.json          9.4 KB   参数/部件显示名（70 个参数）
Hiyori.2048\texture_0{0,1}.png     1.7 MB + 2.4 MB   贴图（2048²）
motions\Hiyori_m01..m10.motion3.json   10 个动作（1.6 ~ 8.6 秒）
```

**关键发现：这个模型只有 2 个动作组**

```
Idle     : m01 m02 m03 m05 m06 m07 m08 m09 m10   （9 个）
TapBody  : m04                                    （1 个）
```

没有 `think` / `listen` / `talk` / `question` 这类专用动作 ——
这正是「逻辑动作 + 角色包映射」要解决的问题：映射表把它们收敛到已有的动作组上。

`AvatarProfile.json`（放在模型同目录）据此填写：

| 逻辑动作 | 映射到 | 说明 |
| --- | --- | --- |
| Idle / Listen / Think / Speak / Sad | `Idle` | 模型没有对应专用动作 |
| Confused / Happy | `TapBody` | 唯一带反应的组 |
| Fallback | `Idle` | 兜底 |

参数取自 `Groups`：`LipSync → ParamMouthOpenY`、`EyeBlink → ParamEyeLOpen/ParamEyeROpen`；
另按 cdi3 补了 `ParamMouthForm`、`ParamAngleX/Y/Z`、`ParamBodyAngleX/Z`、`ParamBreath`。

**代码改动**：
1. 新增 `Live2dModelInfo`：解析 `*.model3.json` 的 `FileReferences.Motions`（动作组名）
   与 `Groups[]`（LipSync / EyeBlink 参数）—— **把之前的 TODO 换成了真实现**；
2. `AnimationManager` 的 `motionExists` 现在接的是 `modelInfo.HasMotion`，
   映射到模型里不存在的组会真的降级到 Fallback 并记 Warning；
3. `AvatarProfile` 改为**优先从模型所在目录加载**，换模型只换文件夹；
4. `config.yaml` / `config.default.yaml` 的 `live2d.default_model_path` 指向
   `./Hiyori/Hiyori.model3.json`。

**实测（运行日志）**：

```
Live2D 模型已解析：Hiyori.model3.json，动作组 [Idle, TapBody]，口型参数 ParamMouthOpenY
已加载角色包配置：E:\DEEPSEEK\Hiyori\AvatarProfile.json（角色=Hiyori，动作 8 项，参数 12 项）
检测到 Live2D 模型文件：E:\DEEPSEEK\Hiyori\Hiyori.model3.json（当前为静态图降级方案，决策 Q4）
```

**探针新增第五组断言（15 项，全部读真实模型文件）**：动作组 = [Idle, TapBody]、
`HasMotion(Think)=false`、口型参数 = ParamMouthOpenY、8 个逻辑动作全部映射到真实存在的组、
`Think → Idle`、`Confused → TapBody`、映射到不存在的组时降级 Fallback(Idle) 且记 Warning。
合计 **46 项断言，0 失败**。

> **必须说清楚的差距**：目前只做到「读得懂模型」——能解析动作组、参数、口型名，
> 并按映射调度。**画面仍然是静态 PNG**（决策 Q4 的降级方案），
> 动作与口型参数没有真正作用到 `.moc3` 上。
> 要显示动态模型，需要一个 Cubism 运行时（Cubism Native SDK / Live2DSharp 之类）
> 把模型渲染出来，这是独立的一大步，不是本次改动能覆盖的。

---
## 17. 交互模型对齐参考桌宠（右上角按钮 / 右键功能栏 / 整窗判定）

用户要求照桌面上另一个桌宠（实测为 **VPet-Simulator**，`pid 24328`）的交互改造。先通过窗口枚举与
真实点击观察了它的结构：

```
pid=24328 (VPet-Simulator.Windows)  title='PetHelper'   rect=(1437,711)-(1462,736) 25x25
pid=24328 (VPet-Simulator.Windows)  title='MainWindow'  rect=(1161,736)-(1411,986) 250x250
```

即：**本体窗口** + **右上角一个 25×25 的独立小窗口（PetHelper）**。实测它处于透明态时，
在本体上右键会穿透到桌面（弹出的是桌面图标菜单），说明按钮必须独立于本体才始终可点。
切换到覆盖态后右键本体，弹出的是按键栏（投喂 / 面板 / 互动 / 系统）。

据此做了三处改造：

### 17.1 右上角模式切换按钮（独立窗口）

新增 `CornerButton.xaml`（34×34，置顶、不显示在任务栏）：
- 贴在本体右上角，显示「覆」或「透」表示当前模式；
- **做成独立窗口**，不带 `WS_EX_TRANSPARENT`，因此**透明态下依然可点** ——
  这是从透明态切回覆盖态的可靠入口（在此之前只能靠悬停或托盘）；
- 随本体移动：拖动时调 `MoveTo`（不激活），切换模式/改大小时调 `ShowAt`。

**实测**（点击按钮后的日志）：

```
鼠标穿透已开启。
布局：角色图片元素 328×372，输入区隐藏但占位（高 44），气泡区高 0，整体不透明度 50%。
交互模式切换为透明（鼠标穿透），来源=手动
```

再次点击切回覆盖：

```
鼠标穿透已关闭。
布局：… 整体不透明度 100%。
交互模式切换为覆盖（可交互），来源=手动
```

### 17.2 右键弹出功能栏

原来是"左键开功能栏、右键切模式"，现改为与参考一致：
- **右键** → `ToggleTaskBar()`（功能栏：设置 / 更换外观 / 对话 / − / ＋ / ↺）
- **左键** → 只负责拖动，单击不再弹功能栏（避免误触）
- 模式切换统一走右上角按钮

**实测**：在角色上右键后 `foreground_window` 变为 `功能栏`
（`class=HwndWrapper[AICompanion;;...]`，即功能栏窗口），确认弹出成功。

### 17.3 判定范围改为整窗

**问题**：早先的悬停判定是**像素级 alpha**——只把图片里 alpha>32 的像素算作"碰到角色"。
角色四周有大量透明留边，视觉上明明贴着窗口边缘，鼠标移上去却没反应；而且点击命中也只落在
具体元素上，窗口空白处会被 WPF 判定为"没有元素"，事件直接落到桌面。

**改动**：
1. `IsCursorOnCharacter()` 从像素级改为**整窗矩形判定**；
2. 根 `Grid` 加 `Background="Transparent"`，让整个窗口矩形都参与命中测试；
3. 删除不再需要的 BGRA 像素缓存（`_avatarPixels` 等）与 Uniform 绘制区计算；
4. 日志里的"角色命中区域"改报**窗口矩形**，与用户看到、能点到的范围一致。

> 附带一个教训：验证期间我按"虚拟坐标 ×1.5"换算点击位置，结果有一半的自动化点击落在窗口外，
> 一度误判为"点不动"。实际是**外部用 `SetWindowPos` 搬动窗口后，WPF 的 `Left/Top` 会与实际位置脱节**，
> 我的坐标推算因此偏了；点击角色本体（而非推算的边缘点）立刻就命中了。
> 这也提醒：这类验证应以截图读出的真实像素为准，不要用换算坐标。

---

## 18. 复现方式

> 本节曾被一次文档编辑误删（没有 git，无法回滚），以下按当前实际工具重建。

### 18.1 环境前提

- Windows 10/11 x64，.NET SDK 8.0（本项目在 8.0.425 上验证）
- 显卡驱动支持 OpenGL 3.3（Live2D 渲染需要）
- Live2D 模型目录（含 `*.model3.json` 与 `*.moc3`）
- `Live2DCubismCore.dll`（x64）—— 放在 `E:\DEEPSEEK\` 下，构建时会自动复制到 PoC 输出目录

### 18.2 构建主程序

```bat
cd /d E:\DEEPSEEK
dotnet build AICompanion.sln -c Release
```

> 构建前先杀掉 `AICompanion` / `GameMonitor` / `llama-server`，否则文件被占用会报 MSB3021。

### 18.3 探针一览

| 探针 | 验证内容 | 运行命令 |
| --- | --- | --- |
| `ExportProbe` | 资源导出 / 语音 / 配置 | `tools\ExportProbe\bin\Release\net8.0\Probe.exe` |
| `PipelineProbe` | 管线四合一（日志、ASR、压缩、SAPI） | `tools\PipelineProbe\bin\Release\net8.0\PipelineProbe.exe` |
| `OomProbe` | 显存不足时的降级 | `tools\OomProbe\bin\Release\net8.0\OomProbe.exe` |
| `MonitorProbe` | GameMonitor 协议 | `tools\MonitorProbe\bin\Release\net8.0\MonitorProbe.exe` |
| `AnimationProbe` | 动作调度 / 角色包映射 / 口型同步（46 项断言） | `tools\AnimationProbe\bin\Release\net8.0-windows\AnimationProbe.exe` |
| `Live2DPoc` | Live2D 渲染 / 透明 / 口型（人工目视） | `tools\Live2DPoc\bin\Release\net8.0-windows\Live2DPoc.exe "E:\DEEPSEEK\Hiyori"` |

探针都必须在**项目根目录**运行（它们按工作目录查找 `config.yaml`）。

### 18.4 Live2D PoC

```bat
REM 不透明模式（看模型是否渲染、口型是否生效）
tools\Live2DPoc\bin\Release\net8.0-windows\Live2DPoc.exe "E:\DEEPSEEK\Hiyori"

REM 透明模式（看背景是否真透）
tools\Live2DPoc\bin\Release\net8.0-windows\Live2DPoc.exe "E:\DEEPSEEK\Hiyori" --transparent
```

日志写到 `%TEMP%\live2dpoc.log`（WinExe 没有控制台，**不要**用 stdout 看）。

### 18.5 启动

```bat
start_all.bat
```

日志在 `logs\app-yyyyMMdd.log`。

## 19. Live2D 渲染可行性验证（PoC）

技术选型见 §16.8 之后的调研结论。这一步只回答一个问题：**这套东西在本机 .NET 8 + WPF 上到底能不能跑**。

### 19.1 选型

| 候选 | 结论 |
| --- | --- |
| **Live2DCSharpSDK.OpenGL.WPF 1.0.0** | **采用**。TFM `net8.0-windows7.0`，依赖 OpenGL 后端 + OpenTK 4.8.2 + OpenTK.GLWpfControl 4.3.1 |
| VPet.Live2DAnimation 1.0.10 | 不用：它内部就打包了同一套 SDK，但额外拖 `VPet-Simulator.Core` 整个桌宠框架 |
| Live2d_For_WPF | 不用：作者已标注"已过时"，且走 WindowsFormsHost |
| Sparkle.Live2DView | 不用：只支持 Avalonia |
| 自己写 C++/OpenGL | 按要求排除 |

原生库 `Live2DCubismCore.dll`（x64）不在任何 NuGet 包里，需单独放到 exe 同目录。

### 19.2 验证结果（tools/probes/Live2DPoc）

```
Live2DPoc.exe "E:\DEEPSEEK\Hiyori" [--transparent]
```

| 验证项 | 结果 | 证据 |
| --- | --- | --- |
| 模型渲染 | 通过 | Hiyori 完整显示（Cubism Core 5.0.0000） |
| 连续帧 | 通过 | FPS≈160~172 |
| 口型参数 | 通过 | `写入 0.65 / 回读 0.65`（SetParameterValue 后立即 GetParameterValue） |
| 点击命中 | 通过 | 日志 `hit area: [Body]` |
| WPF 叠层 | 通过 | 诊断文字正常盖在 GL 画面之上 |
| **透明背景** | **通过** | 桌面内容直接透出，**不需要色键** |
| 通用路径 | 通过 | 传目录 / 传 model3.json 均可；错路径有明确提示 |

透明成立的条件（三者缺一不可）：
`AllowsTransparency=True` + `Background=Transparent` + `GLWpfControlSettings.TransparentBackground=true` + `GL.ClearColor(alpha=0)`。

### 19.3 踩到的五个坑

1. **`RenderContinuously = true` 不生效**：实测渲染回调只进 3 帧就停（模型静止）。
   必须用 `CompositionTarget.Rendering += _ => gl.InvalidateVisual()` 主动驱帧。
2. **别用 `Console.WriteLine` 做 Cubism 日志回调**：WinExe 无控制台，stdout 被重定向后句柄无效，
   写一次就抛 `IOException` 把程序带崩。改成写文件。
3. **`LAppModel` 没有 `GetModel()`**：模型数据在继承来的 `Model` 属性上（`CubismModel`），
   参数接口是 `SetParameterValue(index|id, value, weight)` / `GetParameterValue(...)`。
4. **枚举名带前缀**：`MotionPriority.PriorityNormal`，不是 `Normal`。
5. **`Color4` 在 `OpenTK.Mathematics`**，不在 `OpenTK.Graphics`。

### 19.5 模型水平位置：默认居中 + 用户可调

**需求**：任何模型开箱即居中；用户可自己微调。

**做法**：

1. 渲染器的水平平移走**模型矩阵**（`ModelMatrix.TranslateRelative`），不动 WPF 视觉；
2. 角色包 `AvatarProfile.json` 提供该模型的**实测默认值** `horizontal_offset`（Hiyori = 0.103）；
3. 设置面板「外观 → 模型水平位置」提供滑块（-0.30 ~ +0.30），存入 `Assets\appearance.json` 的 `horizontalOffset`，
   保存后即时生效；滑块归零 = 回到模型默认值（即居中）。

**实测**：

| 场景 | 水平中心 | 说明 |
| --- | --- | --- |
| 无偏移 | 73.4% | 画布居中、角色偏右 |
| 角色包默认 0.103 | **48.4%** | 开箱居中（目标 50%） |
| 用户滑块 0.25 | **17.3%** | 明显左移，说明用户设置生效 |

占高稳定 88.5%，说明平移不影响缩放比例。

> **踩坑**：曾试图"读回画面像素自动测量包围盒再自动居中"，但在这个宿主里行不通 ——
> `GLWpfControl` 通过共享纹理合成，`GL.ReadPixels` 读默认帧缓冲拿不到画面
> （绑 FBO 0 后仍全透明，Cubism 自己的离屏遮罩也读不出内容），
> 读到的假数据给出 50.9% 而实际是 73.9%。最终改为"角色包默认值 + 用户滑块"。

> 另一个坑：给 `GLWpfControl`（D3DImage 宿主）加 `RenderTransform` 会让模型在桌面上消失、
> 那块区域变成点击穿透 —— `PrintWindow` 仍拍得到内容，说明画面画了但 alpha 合成丢了。
> 平移必须用模型矩阵实现。

### 19.4 接入主程序前还剩的风险

1. **缩放**：主窗口用 `LayoutTransform` 缩放整棵可视树，GL 画面不会跟着缩放
   （它按控件像素尺寸渲染）。接入时要么用模型矩阵缩放，要么把 GL 控件尺寸按倍率设置。
2. **点击穿透**：主程序透明态用 `WS_EX_TRANSPARENT`，对 GL 子窗口应当同样有效，但**未实测**。
3. **叠加顺序**：GL 用 D3D 共享表面，理论上会盖住 WPF 同层的其它元素；
   实测诊断文字能盖在 GL 之上，说明正常合成，但边缘/半透明区域仍需观察。

---


## 20. Live2D 合成与命中问题的根因（已解决）

**现象**：Live2D 模型在桌面上看得见，但 `WindowFromPoint` 在模型处返回桌面，
即这块区域**点不到**（点击穿透到下层）。`PrintWindow` 能拍到内容，说明画面确实画了。

**根因**：WPF 的 `Border` 在没有 `Background`（或 `Background="Transparent"`，alpha=0）时，
在分层窗口里贡献的是 **alpha=0**；而里面的 `D3DImage`（GLWpfControl 的合成方式）
**不会**自行补上 alpha。于是整块区域的合成 alpha 为 0 —— 输入命中判不出来，
DWM 侧的呈现也依赖旧帧，才会有"看得见却点不到"的错觉。

**修复**：给 GL 宿主 `Border` 一个 alpha=1/255 的近透明背景：

```xml
<Border x:Name="Live2DHost" Background="#01000000" Visibility="Collapsed" />
```

1/255 肉眼不可见，但足以让这块参与命中测试与合成。

**验证（物理坐标，DPI 感知进程）**：

```
覆盖态：exStyle=0x80008 TRANSPARENT=False   模型处命中 → 'AI 伴侣'
透明态：exStyle=0x80028 TRANSPARENT=True    模型处命中 → 下层窗口（点击穿透成立）
```

**排错过程中的几个错误结论（记录下来避免重犯）**：

1. 曾怀疑 `LayoutTransform`、`RootGrid.Background="Transparent"`、`RenderTransform`，
   逐个排除后都不是（`RenderTransform` 确实是独立问题，见 §19.5）；
2. 用 `SetWindowPos` 从外部搬动窗口会让分层窗口的呈现异常，**据此得出的"窗口不可见"结论不可信**；
3. 判断命中是否成立，最可靠的是**透明的 PoC 窗口做对照**：
   PoC 命中的是模型所在窗口、空白处穿透，说明测量方法本身没问题。



## 21. 性能与资源优化（清理死代码 / 进程 / 内存）

### 21.1 删除的死代码

| 项 | 原因 |
| --- | --- |
| `AI_NO_LAYOUT_SCALE` 诊断开关 | 定位合成问题时临时加的，问题已解决（§20） |
| `MainWindow._mouthOpen` | 只写不读 |
| `Live2DRenderer._renderedFrames` | 只写不读（自动居中改成像素回读后废弃，回读方案也移除了） |
| `LipSyncDriver.PushAmplitude` | 无人调用（流式播放器的扩展点，当前播放器不适用） |
| `Live2dModelInfo.ParameterGroups` | 仅类内使用，降为 private |

### 21.2 进程检查

启动脚本会拉起 3 个进程，均为必需：

```
AICompanion   WPF 主程序
GameMonitor   游戏检测（独立进程，只读窗口标题/进程名）
llama-server  LLM 推理服务（由主程序托管）
```

检查结果：无残留探针进程（Live2DPoc / AnimationProbe / Probe / FakeLlamaServer 均已退出）。
日志目录累计 8.3 KB / 3 个文件，无异常增长。

### 21.3 内存与 CPU 实测（三组对照）

| 组 | 配置 | 时长 | 工作集 | 私有 | 句柄 | CPU |
| --- | --- | --- | --- | --- | --- | --- |
| A | Live2D，`RenderContinuously=true`，约 170 fps | 3 分钟 | **+4.8 MB** | +0.9 MB | **+53** | **65.6%** |
| B | 静态图（模型路径指向不存在） | 2 分钟 | −1.1 MB | −4.3 MB | −12 | **0.5%** |
| C | Live2D，限帧 + `RenderContinuously=false` | 3 分钟 | **−9.5 MB** | −0.2 MB | **−1** | **12%** |

结论：

1. 增长**只出现在 Live2D 渲染路径**；静态图模式内存与句柄都是持平或回落 ——
   说明气泡、定时器、ASR、SAPI 这些都没有泄漏；
2. A 组的句柄增长（+53 / 3 分钟）与工作集增长（+1.6 MB/分钟）是同一件事：
   GL/D3D 的每帧资源churn；
3. **限帧本身不降 CPU**（170→30 fps 后仍是 65%），真正的元凶是 `RenderContinuously=true`：
   控件自己开了一条**满速**渲染循环，不受我们的 `InvalidateVisual` 节流控制；
4. 关掉它、改由 `CompositionTarget.Rendering` 按 30 fps 主动驱动后：
   **CPU 65.6% → 12%，工作集由 +4.8 MB 转为 −9.5 MB，句柄由 +53 转为 −1**。

### 21.4 顺带加的诊断

渲染器首帧会打印 OpenGL 环境（Vendor / Renderer / Version）。本机结果：

```
Vendor=NVIDIA Corporation，Renderer=NVIDIA GeForce RTX 5060 Laptop GPU/PCIe/SSE2，Version=3.3.0 NVIDIA 617.42
```

如果是软件实现（GDI Generic / llvmpipe），代码会额外打一条 Warning ——
那种情况下 Live2D 会烧掉一个核，属于环境问题而不是代码问题。


### 21.5 Live2D 画面发糊：GL 表面按 DIP 渲染导致（已修）

**现象**：模型能显示，但明显发糊、细节丢失。

**根因**：之前为了让"视口与 SDK 认知一致"，把 GL 表面设成了 `UseDeviceDpi = false` ——
表面按 **DIP 尺寸**（328×382）渲染，再由 WPF 按 150% DPI 放大到 492×573 显示，
等于**先降采样再放大**，必然糊。

当时这么改是为了绕开"模型偏位"，但那个问题的真凶是宿主 Border 的 alpha=0（见 §20），
与 DPI 无关 —— 两个 bug 被混在一起，导致这个错误设置留了下来。

**修复**：

```csharp
UseDeviceDpi = true,          // 表面按物理像素创建
// 视口同样用物理像素，否则模型会被按比例缩小
var dpi = VisualTreeHelper.GetDpi(_control);
var width  = (int)Math.Round(_control.ActualWidth  * dpi.DpiScaleX);
var height = (int)Math.Round(_control.ActualHeight * dpi.DpiScaleY);
GL.Viewport(0, 0, width, height);
```

**实测**：

| 项 | 修复前 | 修复后 |
| --- | --- | --- |
| 渲染视口 | 328×382 像素 | **492×573 像素**（DIP 328×382 × 1.5） |
| 像素总量 | 125,296 | **281,916**（2.25 倍） |
| 观感 | 头发/领结/毛衣纹理糊成一片 | 发丝、纽扣、针织纹理清晰可辨 |
| 命中 | 正常 | 正常（模型处仍返回「AI 伴侣」） |

> **MSAA 不能用**：顺手试了 `Samples = 4`（想让轮廓更干净），结果进程**直接崩溃**，
> 日志里没有任何异常记录 —— 典型的原生层崩溃（GL/D3D 互操作表面不支持多重采样）。
> 已在代码注释里标注，不要重开。


### 21.6 仍然发糊的第二个原因：贴图没有 mipmap（已修）

把表面改回物理像素后，视口已经是 1:1（492×573 = 屏幕实际像素），但观感仍偏软。
排查掉渲染链路后（WPF Tier 2 硬件、GL 硬件、视口一致），问题落在**贴图采样**上：

- Hiyori 的贴图是 2048²，而屏幕上模型只有约 630 像素高（约 3 倍降采样）；
- Cubism 的 OpenGL 渲染器默认**不生成 mipmap**，只做线性过滤。
  3 倍降采样 + 无 mipmap 时 GPU 每次只采几个纹素，表现为"整体发糊 + 边缘抖动"。

**修复**：模型画过第一帧（贴图 id 有效）后，对每张贴图做一次
`GL.GenerateMipmap` + 三线性过滤（`LinearMipmapLinear`）：

```csharp
foreach (var id in renderer.GetBindedTextures().Values) {
    GL.BindTexture(TextureTarget.Texture2D, id);
    GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
    GL.TexParameter(..., TextureMinFilter, (int)All.LinearMipmapLinear);
    GL.TexParameter(..., TextureMagFilter, (int)All.Linear);
}
```

**实测**：日志输出「已为 2 张模型贴图生成 mipmap 并启用三线性过滤」，
1:1 截图里发丝、水手领条纹、针织纹理、裙褶都清晰可辨；性能无明显变化。

**至此，"糊"的三个层次都处理了**：

1. 表面按 DIP 渲染（§21.5）—— 像素总量少 2.25 倍；
2. WPF 再放大 4%（LayoutTransform 1.04）—— 影响很小；
3. 贴图降采样无 mipmap（本节）—— 观感上的主要来源。

## 22. TTS / ASR 完善与打包准备（本轮）

### 22.1 ASR：从"能跑"到"好用"

本轮补了四件事（都经过实测）：

**① 系统音频回环采集**（`AudioCaptureLoopback.cs`）

原来只有 `WaveInEvent` 麦克风采集。新增 WASAPI 回环采集，可以直接识别电脑正在播放的声音
（视频、直播、通话），也让"放一段视频验证识别"成为可能。

配置：`asr.capture: microphone | loopback`。

回环拿到的是 32 位浮点、设备原采样率（本机 48 kHz / 8 声道），whisper 要 16 kHz 单声道，
所以做了下混 + 盒式平均重采样：

```
48000 Hz / 8 声道 / float32  →  16000 Hz 单声道（每块约 20 ms）
```

**② 启动即监听**（`asr.auto_start`）

之前 `ASR=Stopped` 是常态，必须手动点托盘才开始。现在配置开了就自动进入 Running，
实测日志：

```
[ASR] ASR 已开始监听。
[App] ASR 已按配置自动启动（采集源=microphone），状态=Running。
```

**③ 繁体转简体**（`AsrTextNormalizer.ToSimplified`）

whisper 中文经常输出繁体。实测第一次识别：

```
识别成功："今天天氣不錯,我們出去走吧"     ← 修复前
识别成功："今天天气不错,我们出去走吧"     ← 修复后
```

实现上踩了一个坑：`Microsoft.VisualBasic.Strings.StrConv` 在 .NET Framework 下可用，
**在 .NET 8 上会抛**

```
ArgumentException: This system does not contain support for the Traditional Chinese locale.
```

改为直接 P/Invoke Windows 的 `LCMapStringEx` + `LCMAP_SIMPLIFIED_CHINESE`（0x02000000），
系统自带的简繁映射，零维护、无依赖。

**④ 回声抑制**（`MainWindow.SuspendAsrForPlayback`）

这是回环模式暴露出来的真问题：TTS 念出来的回复会被回环重新采集，
形成「识别 → 回复 → TTS → 再识别」的自激循环。实测几秒内就会无限自问自答。

处理方式：`PlaybackStarted` → `StopListening()`；`PlaybackStopped` → 延迟 400 ms 后
`StartListening()`（400 ms 等扬声器余音散去）。日志：

```
[App] TTS 播放中：已暂停 ASR（回声抑制）。
[App] TTS 播放结束：已恢复 ASR。
```

### 22.2 TTS 现状与取舍

| 引擎 | 状态 | 说明 |
| --- | --- | --- |
| Piper | **不可用** | 二进制能下载（gh-proxy 可拿到 21 MB 的 Windows 版），但中文语音模型（zh_CN-huayan-medium.onnx，约 63 MB）所在的 HuggingFace 镜像本轮全部超时/无法连接 |
| SAPI | 可用（降级） | 使用系统音色 `Microsoft Huihui Desktop - Chinese (Simplified)`，中文正常，音质朴素 |

因此当前实际引擎是 SAPI，配置里 `engine: piper` 会在启动时优雅降级并提示一次。
语音模型可手动放到 `models/piper/zh_CN/`，无需改代码即可启用 Piper。

已删除下载下来的 `tools/piper`（21 MB，没有语音模型就是死重量）。

### 22.3 打包准备

| 文件 | 作用 |
| --- | --- |
| `tools/pack.ps1` | 一条命令出包：publish + 资源拷贝 + zip +（有 Inno Setup 时）编译安装包 |
| `installer/AICompanion.iss` | Inno Setup 6 脚本，含 .NET 8 运行时检查、快捷方式、卸载项 |
| `installer/install.ps1` | 免安装工具部署（复制、快捷方式、注册卸载项），随分发包一起发 |
| `Assets/app.ico` | 由角色立绘生成的应用图标 |
| `csproj` 版本元数据 | `Version=0.9.0`、产品名"AI 桌面伴侣"、图标；实测已进 exe |

打包相关的两个**真实障碍**及处理：

1. **日志路径依赖 CWD**：原来写 `Environment.CurrentDirectory\logs\app`，
   装到受保护目录或从别处启动就会失败。新增 `LogRoot`：
   按「当前目录 → 程序目录 → `%LOCALAPPDATA%\AICompanion`」找第一个**可写**位置（写探针文件验证）。
2. **安装位置**：装到 `%LOCALAPPDATA%\Programs\AICompanion`（按用户、免提权、目录可写），
   避开 Program Files 不可写导致的日志/外观设置失败。

模型（LLM 1.07 GB + ASR 141 MB）默认不进安装包，`-IncludeModels` 可一起打。
## 23. 语音输入改为「按住说话」（push-to-talk）

### 23.1 为什么改

原先 ASR 是常开监听 + VAD 静音切句。实测暴露两个问题：

1. **收不该收的声音**：环境音、旁人对话都会被当成输入；TTS 回复还会被回环采集形成自激
   （上一轮靠"播放时暂停 ASR"绕过，但那只是打补丁）。
2. **不知道什么时候在听**：用户没有明确的"我要说话了"这个动作，交互上是模糊的。

改成按住说话后，「什么时候在听」由用户按键决定，上面两个问题从根上消失。

### 23.2 实现

| 位置 | 内容 |
| --- | --- |
| `AsrConfig` | 新增 `push_to_talk` / `push_to_talk_key` / `push_to_talk_min_ms` / `push_to_talk_max_seconds` |
| `ASRManager` | 新增 `BeginPushToTalk()` / `EndPushToTalk()`；PTT 模式下音频块直接进缓冲，**不经过 VAD** |
| `PushToTalkMonitor`（App） | 60 ms 轮询 `GetAsyncKeyState` 做全局按键检测 |
| `AppHost` | PTT 与 `auto_start` 互斥，启动时二选一 |
| `MainWindow` | PTT 模式下跳过回声抑制与"播放结束恢复监听" |

默认按键 **`Home`**（可在 `config.yaml` 的 `push_to_talk_key` 改）。解析支持单键（`Home`、`F2`）、
双键（`Alt+Q`）、三键（`Ctrl+Alt+Q`）；修饰键可用 `Ctrl` / `Alt` / `Shift` / `Win`，主键支持字母、数字、`F1`–`F24`、
`Space`、`Enter`、`Tab`、方向键与常用符号键，默认 `Ctrl+Space`。

两条保护：
- 短于 `push_to_talk_min_ms`（默认 300 ms）判定误触，丢弃；
- 单次录音超过 `push_to_talk_max_seconds`（默认 60 秒）不再累积，避免按住不放吃满内存。

### 23.3 为什么用轮询而不是键盘钩子

`WH_KEYBOARD_LL` 钩子要插进系统键盘输入链，任何一个实现疏漏都可能拖慢或吞掉用户按键，
对一个"顺带听一下"的桌面伴侣来说风险不对称；而且容易被安全软件当成键盘记录器。

轮询的代价是最长 60 ms 响应延迟（感知不到），换来的是：不拦截任何按键、不需要管理员权限、
天然能判断"当前是否按住"，不用自己拼按下/抬起事件。

### 23.4 验证

新增 `tools/probes/AsrPttProbe`（纯假对象，27 项断言）：

| 用例 | 断言 | 结果 |
| --- | --- | --- |
| 按住 1.2 秒 | 状态 Running→Stopped、采集启停、识别回调、音频长度合理、繁体转简体、日志有按下/松开 | 10/10 通过 |
| 只按 100 ms | 不触发识别、不调用识别器、日志判定误触 | 3/3 通过 |
| 连续两次按住 | 两次都识别、结束后状态 Stopped（无残留） | 2/2 通过 |
| 按住 800ms 但只收到 40ms 音频 | 不触发识别、日志指出没收到音频、弹警告气泡、不误报误触 | 4/4 通过 |
| 按键解析 | `Home` / `F2` / `Alt+Q` / `Ctrl+Alt+Q` / `Ctrl+Shift+Home` 均可解析；`NotAKey`、空值、未知修饰键被拒绝 | 8/8 通过 |

```
=== 按住说话（PTT）探针 ===
  [用例 1] 按住 1.2 秒后松开 ... 10/10 通过
    繁体应转成简体：今天天氣不錯,我們出去走吧 → 今天天气不错,我们出去走吧
  [用例 2] 只按 100 毫秒 ... 3/3 通过
  [用例 3] 连续两次按住（状态不残留） ... 2/2 通过
  通过 27 项，失败 0 项。
```

### 23.5 退回常开监听

```yaml
asr:
  push_to_talk: false
  auto_start: true
```

两者互斥：`push_to_talk: true` 时 `auto_start` 被忽略（否则会同时存在常开监听与按键录音，
既费电又会重复采集）。
## 24. 打包：自带运行时 + 含模型的安装包

### 24.1 目标变化

最初只打算出「框架依赖 + 不含模型」的轻量包；后来要求变成**带所有环境**：
目标机器不装任何东西，装完就能对话。于是默认值改为：

| 选项 | 默认 | 影响 |
| --- | --- | --- |
| `-SelfContained` | `true` | 内置 .NET 8 桌面运行时（分发目录 184.5 MB / 492 文件） |
| `-IncludeModels` | `true` | LLM 1.04 GB + ASR 141 MB 一起进包 |
| `-ReadyToRun` | `true` | 预编译，启动更快 |

实测产物：分发目录 **1.43 GB**，安装包 **1.27 GB**（lzma2 压缩，模型本身不可压缩）。

### 24.2 三个工程问题

**① 发布时 exe 被运行中的程序锁住**

`dotnet publish` 默认写 `src\AICompanion.App\bin\Release\net8.0-windows\AICompanion.exe`，
程序一开着就 MSB3021/MSB3027。解法：把 `OutputPath` 重定向到 `artifacts\pkgbin`，
于是**程序运行中也能打包**——这一点很关键，否则每次打包都要中断长测试。

**② GameMonitor 必须与主程序同目录**

`AppHost` 只查 `AppPaths.Root` 与 `AppContext.BaseDirectory`。最初把它发布到
`app\GameMonitor\` 子目录，结果会是「AICompanion 起来了、但一直说未连接」。
现在与主程序同一个 `-o` 目录。

**③ 日志路径依赖工作目录（部署后会失败）**

原实现写 `Environment.CurrentDirectory\logs\app`。装到 `Program Files` 或从任意目录启动时：
前者无写权限、后者会把日志散落到各处。新增 `LogRoot`：

```
当前目录 → 程序目录 → %LOCALAPPDATA%\AICompanion
```

按顺序找第一个**真能写**的位置（建目录 + 写探针文件验证，见 `Services\LogRoot.cs`）。

### 24.3 Inno Setup 的落地细节

- 本机原本没装 Inno Setup，winget 下载卡在 GitHub；改走 `gh-proxy` 拿到 10.1 MB 安装包，
  `/VERYSILENT` 静默装好 6.7.3。
- 中文安装界面需要 `ChineseSimplified.isl`，Inno 不内置（属"unofficial"）。
  直接写 `C:\Program Files (x86)\Inno Setup 6\Languages\` 需要管理员权限，于是放进仓库
  由 `.iss` 通过 `/DIslPath` 引用。
- 压缩级别从 `lzma2/max` 降到 `lzma2/normal`：体积多约 5%，但重打包从 6.3 分钟降到 4 分钟左右，
  CPU 占用也更平缓。

### 24.4 精简项（实测）

| 项目 | 数量 | 节省 |
| --- | --- | --- |
| `.pdb` | 3 个（分发目录）+ `bin\` 内符号 | 约 24 MB |
| WPF 卫星程序集 | 删 11 种语言，只留 `zh-Hans`/`zh-Hant` | 13.3 MB |

### 24.5 版本信息

`csproj` 加了 `Version=0.9.0`、`Product=AI 桌面伴侣`、`ApplicationIcon=Assets\app.ico`
（图标由角色立绘缩放生成，40.1 KB）。实测已进程序集：

```
产品名: AI 桌面伴侣
版本:   0.9.0.0
```

### 24.6 打包后验证

`tools\verify_dist.ps1` 把分发包复制到临时目录、**换一个工作目录**启动，检查
必需文件 / 进程存活 / 主窗口 / 日志可写 / 无 `[ERR]`——专抓「仓库里能跑、发出去跑不了」。
### 23.6 端到端验证：真实语音 + 真实 whisper

`tools/probes/AsrPttRealProbe`：用 SAPI 合成一段 4.66 秒中文语音写到 WAV（**只写文件、不播放**，
不打扰人），再按 20 ms 一块喂进 `ASRManager` 的 PTT 缓冲——走的是和真机完全相同的路径。

```
音频：4.66 秒，74628 个样本，RMS 0.0394
  [通过] 初始化后 Stopped
  [通过] 识别器就绪
  [通过] 按下后 Running
  [通过] 音频全部进入采集
  [通过] 松开后 Stopped
识别结果："今天天气不错,我们出去走吧"
引擎耗时：1,559 ms（RTF 0.33）
  [通过] 应触发识别回调 / 识别结果非空 / 识别内容与原文相关
通过 8 项，失败 0 项。
```

原文是「今天天气不错，我们出去走走吧。」——whisper base 把句末「吧」丢了，属模型能力范围。

### 23.7 实测踩到的三个真实缺陷

**① DispatcherTimer 建在了线程池线程上 → 定时器永不触发**

`PushToTalkMonitor` 在 `AppHost.StartAsync` 的续体里创建，那里已经因为
`ConfigureAwait(false)` 跑在线程池上。`new DispatcherTimer(...)` 不传 Dispatcher 时用的是
`Dispatcher.CurrentDispatcher`——在线程池线程上会**新建一个永远不会被 pump 的 Dispatcher**。

表现极具迷惑性：日志有「按住说话监听已启动」，`Start()` 也调了，但按键一次都不响应。
修法：显式传 `Application.Current?.Dispatcher`。

**② 按键状态毛刺会把录音截断**

单次读到"未按住"就收尾，实测会把一段话切成几十毫秒。改为**连续 2 个 tick（120 ms）**
都读到松开才结束。代价是尾部多 120 ms，听感上反而更自然。

**③ 按住很久却没收到音频，被误报成「误触」**

原来只比较音频长度，只要短就记「误触」。但「按了 3 秒却只收到 40 ms」和
「手抖碰了一下」是两回事：前者是麦克风被占用/驱动异常，必须提示用户。
现在两种日志分开，后者还会弹一条警告气泡。

### 23.8 验证手段的边界（如实记录）

按键模拟这条路走不通，已确认原因：

| 手段 | 结果 |
| --- | --- |
| `keybd_event` 按住 | Windows 约 60–100 ms 后就把注入的按键当"已放开"（`GetAsyncKeyState` 的 0x8000 位消失） |
| 按住期间每 40 ms 重发 | 系统认为"已按下重复"，不再产生新的 down 转换，反而一次都读不到 |
| `SendInput` + 扫描码 | 返回 0（被 UIPI 拒绝，无法向更高完整性级别注入） |

因此 `tools/ptt_smoke.ps1` 只能验证到「按下 → 采集启动 → 松开 → 采集停止 → 时长判定」
这一层，**无法模拟数秒长按**。补上的是：

- `tools/probes/AsrPttRealProbe`：真实语音 + 真实引擎走完整 PTT 路径（见 23.6）；
- `tools/probes/MicProbe`：确认真实麦克风在程序用的同一套 NAudio 参数下持续供数据
  （实测 2 秒收到 1988 ms，100 块，RMS 0.00189）。

**剩下唯一需要真人做的一步**：按住 Home 说一句话，确认能出字。
这东西没法替用户按——但除了"手指"以外的每一环都已经单独验证过。
## 25. 两小时连续运行测试（2026-10-21）

### 25.1 结果

`tools/soak_test.ps1`：每分钟采样一次，共 122 分钟。

| 指标 | 起始 | 30 分钟 | 60 分钟 | 结束 | 峰值 |
| --- | --- | --- | --- | --- | --- |
| 工作集内存 | 456.3 MB | 618.6 MB | 339.2 MB | 187.7 MB | 656.3 MB |
| 句柄数 | 1047 | 1197 | 1101 | 1060 | 1246 |
| 线程数 | — | 67 | 64 | 61 | 67 |
| CPU | — | 16–27% | 24–28% | 28% | 34% |

进程结束后仍存活，**无崩溃、无泄漏迹象**。

### 25.2 怎么读这些数字

- **内存没有单调上升**：456 → 656 → 339 → 188 MB。峰值出现在打包编译那几分钟
  （publish 占用了同一台机器），之后随 GC 回落。真正的判据是"结束值低于起始值"，
  这条满足。
- **句柄在 1047–1246 之间来回**：波动来自 30 秒一次的健康检查（每次 `nvidia-smi` 子进程
  会短暂多出句柄），不是泄漏。结束时 1060，与起始 1047 基本重合。
- **CPU 25% 左右**：Live2D 以 30fps 渲染 + 空闲调度器 500ms 轮询，属预期。

### 25.3 采样脚本自身的一个坑（已修）

脚本原本每秒抓一次日志做统计，但中途有个变量被 UIAutomation 的 `RootElement` 覆盖了，
导致第 5 分钟之后 `errors/warns/log_kb` 三列全是 0。**不能拿这三列当作"没有错误"的证据。**

正确的做法是直接读日志文件（程序持有写句柄，要用 `FileShare.ReadWrite` 打开）：

```
[ERR] 2 条  录音设备异常终止：NoDriver calling waveInPrepareHeader（随后自动恢复成功）
[WRN] 1 条  ASR 进入故障状态：DeviceError
```

两次都发生在反复重启程序、麦克风被上一实例占用的时候，且 **ASR 自愈机制都成功恢复**
（`ASR 自动恢复：重新打开音频设备 → 成功`）。这正是之前做设备自愈的目的，
这次在真实运行里验证了它确实管用。

### 25.4 长时间运行后再做的验证

测试结束后用同一台机器的最终代码重跑了全部探针：

| 探针 | 断言 | 结果 |
| --- | --- | --- |
| `AsrPttProbe`（按住说话） | 27 | 全部通过 |
| `AsrRecoveryProbe`（设备自愈） | 12 | 全部通过（Stopped→Running→Faulted→Stopped→Running→Stopped） |
| `AnimationProbe`（形象动作） | 46 | 全部通过 |

---

## 26. 分发策略：需授权的素材不由我们分发（2026-10-21）

起因：用户明确要求"有些需要授权的你不放进去，让他们自己下载，比如 live2d 的 dll"。
仓库里早就排除了这些，但**打包脚本没排除** —— `dist\` 里照样有 `Live2DCubismCore.dll`、
`Hiyori\`、`Assets\source` 与 `Assets\Default\character.png`。也就是说"仓库干净、
发出去的二进制不干净"，这个口子必须堵。

### 26.1 改了什么

| 位置 | 改动 |
| --- | --- |
| `tools/pack.ps1` | 新增 `-IncludeLicensedAssets`（默认关）；关着时不打 `Hiyori\`、`Live2DCubismCore.dll`，并从 `Assets\` 里剔掉 `source\` 与 `Default\character.png`；包里生成 `素材说明.md` |
| `tools/pack.ps1` | 新增不带值的开关 `-NoModels` / `-NoLicensedAssets` / `-FrameworkOnly`（原因见 26.3） |
| `tools/verify_dist.ps1` | 断言包里**没有**那四样东西，且必须有 `素材说明.md`；`Live2DCubismCore.dll` 从"必需文件"降级为"用户自备" |
| `src/AICompanion.App/MainWindow.xaml.cs` | 缺 `Live2DCubismCore.dll` 或缺模型时各给一条 WRN，写清"授权原因 + 去哪取 + 见素材说明.md"，不再只记一句"创建失败" |

关键一点：`Live2DCubismCore.dll` 是 **publish 自动带进来的**（`AICompanion.App.csproj` 里有复制规则），
光"不主动拷"没用，必须显式从产物里删掉。第一版就漏在这里 —— 体积表上看不出来，
是逐项核对 `dist` 内容才发现的。

### 26.2 四次打包实验（都真跑）

| 命令 | 结果 |
| --- | --- |
| `-NoModels -NoLicensedAssets -NoInstaller` | 249.0 MB；四样授权素材全不在；`素材说明.md` 在 |
| `-NoLicensedAssets -NoInstaller`（默认+模型） | 1.42 GB；LLM/ASR 已打进去；授权素材仍不在 |
| `-NoModels -IncludeLicensedAssets -NoInstaller` | 262.6 MB；DLL 与 Hiyori 都进了包，证明开关方向正确 |
| 回到干净状态重打 | 1.42 GB；`verify_dist` 通过 |

`tools\verify_dist.ps1` 的输出：

```
[OK] 未包含需单独授权的素材
[OK] 含 素材说明.md
[OK] 错误行：0
启动完成：… LLM 就绪=True，TTS=sapi（降级=True），ASR=Stopped，知识库 0 条，启动问题 0 项。
=== 验证通过 ===
```

轻量包首启会多两行 `[ERR]`（缺 LLM / ASR 模型）——这是**没带模型的必然结果**。
所以校验脚本改成：模型不在包里时这两行归为「预期」，不计入错误行；否则官方轻量包
每次首启都像坏了。实测输出 `[OK] 错误行：0` 加一行 `[预期] 另有 2 行属于"模型未随包附带"`。

另外手动跑了一次不带 DLL 的包，确认日志有可照做的提示：

```
[INF] [App] 未解析到 Live2D 模型（…\Hiyori\Hiyori.model3.json），动作存在性检查降级为「只看映射是否存在」。
[WRN] [App] 未启用 Live2D：既没有模型文件，也没有 Live2DCubismCore.dll。二者都需按 Live2D 的
            授权自行取得（不随包分发），放到程序目录后重启即可启用；缺省会用静态立绘或纯色背景
            代替，功能不受影响。详见同目录的「素材说明.md」。
[WRN] [App] 角色形象全部缺失（…），已降级为纯色背景。
[INF] [Bubble] [气泡/Warning] 未找到任何角色形象素材，已使用纯色背景显示。把图片放进 Assets\Default 目录即可。
```

错误行为 0 —— 缺授权素材是**预期状态**，不是故障。

### 26.3 顺带修掉的两个坑

1. **`powershell -File` 传不了布尔值。** 文档里那句 `-IncludeModels:$false` 一跑就报
   `Cannot convert value "System.String" to type "System.Boolean"`。实测四种写法，
   只有 `-Command "& script -IncludeModels $false"` 可行。与其在文档里教转义，
   不如给脚本加不带值的开关（`-NoModels` 等），cmd 与 `-File` 都能直接写。
2. **`verify_dist.ps1` 漏杀 `llama-server`。** 它只杀主程序与 GameMonitor，
   而 llama-server 是主程序的子进程、不随父进程退出。清理时发现**堆了 4 个**
   临时目录里的 llama-server 还在跑，各占一份模型与显存。已补：按 `$tempApp` 前缀清理并打印数量。

### 26.4 一个自己踩的坑（记下来防复发）

改 `.ps1` 时用工具直接编辑，工具写回的是**无 BOM 的 UTF-8**；Windows PowerShell 5.1
按系统代码页（GBK）读无 BOM 文件，中文全成乱码，脚本直接语法错误 —— 而且
`PSParser` 报 0 个错误、`powershell -File` 却报一堆，很容易误判成逻辑问题。
规矩：**改完 `.ps1` 必须确认前三字节是 `EF BB BF`**。

### 26.5 仍未做的

- `dist\AICompanion-0.9.0-full-setup.exe`（1.27 GB，旧包）里**还带着** Live2D 素材，
  是这次变更之前打的。没有删（属于本机产物），但它**不能拿去分发**；
  要发布就用 `-NoLicensedAssets` 重打。
- Inno Setup 那条路径这次没重跑，只验证了分发目录。`.iss` 是整目录递归打包
  （`Source: {#StageDir}\*`），所以目录干净它自然干净。

### 26.4 安装包"装完即用"：把 CUDA 运行库打进去（含 Inno 全流程实测）

**问题**：第一次打出来的 1.26 GB 安装包在开发机上 GPU 正常，但它能跑 GPU 只是因为**这台机器
装了 CUDA Toolkit 13.3 而且在 `PATH` 里**——`bin\ggml-cuda.dll` 依赖 `cublas64_13.dll`，
目标机没有就静默退回 CPU。也就是说"本机能跑"骗了人。

**改法**：`pack.ps1` 自动在 CUDA 安装目录里找 `cublas64_13.dll` / `cublasLt64_13.dll` /
`cudart64_13.dll` 复制进 `bin\`（Windows 优先从 exe 所在目录加载 DLL），加 `-NoCudaRuntime`
可跳过。三个文件共 493 MB，其中 `cublasLt64_13.dll` 一个就 442 MB。

**验证（每次都真跑）**：

| 步骤 | 结果 |
| --- | --- |
| 重打安装包 | 分发目录 1.90 GB，`full-setup.exe` **1621.5 MB**，Inno 编译 212 秒 |
| 静默安装到临时目录 | 退出码 0，**69 秒**，744 个文件，1460 MB，`unins000.exe` 就位 |
| 运行安装版 | 进程存活、正常启动 |
| 静默卸载 | 退出码 0，目录清空（`logs` 与 `appearance.json` 按设计保留） |
| **删掉 PATH 里所有 CUDA 目录后启动打包版** | `运行模式=GPU 模式（ngl=9/28，空闲显存 6756 MB）` |
| 同一份包里 TTS | `配置引擎=piper，实际引擎=sapi，降级=True`（Piper 音色未随包，走系统语音） |

倒数第二行是关键：`PATH` 里已经没有任何 CUDA 目录，程序仍然进了 GPU 模式，说明
**目标机器只需要 NVIDIA 驱动，不需要装 CUDA Toolkit**。这就是"环境不要太复杂"的落点。

顺带修的坑：用编辑器改 `pack.ps1` 后文件丢了 UTF-8 BOM，PowerShell 5.1 按 ANSI 读，
里面的中文全变乱码，报 `方法调用中缺少")"`。以后改 `.ps1` 都要 `ReadAllBytes` 查 BOM 再补。

---

## 27. 语音合成后端：GPT-SoVITS 与 CosyVoice（2026-10-22）

### 27.1 为什么要做

手册 §3.4 只规定了 Chatterbox 与 Piper。这一轮要把两种**训练好的音色**接进来：
GPT-SoVITS 整合包（`E:\GPT-SoVITS\...v2pro`）与 CosyVoice 3。二者都是"本地 HTTP 服务"形态，
于是新增 `IHttpTtsEngine` 抽象，把探活 / 超时 / 软重启 / 连接池重建从 Chatterbox 里提取出来复用——
否则每加一个引擎就要在 `TTSManager` 里再复制一遍降级逻辑。

### 27.2 新增与改动

| 文件 | 内容 |
| --- | --- |
| `Abstractions/IHttpTtsEngine.cs` | 外部 HTTP 引擎接口：BaseUrl / Port / Uptime / 探活 / 软重启 |
| `Tts/HttpTtsEngineBase.cs` | 公共骨架 + `WavFromPcm16`（裸 PCM → WAV） |
| `Tts/GptSoVitsEngine.cs` | `POST /tts`；`/set_gpt_weights`、`/set_sovits_weights` 切换训练模型 |
| `Tts/CosyVoiceEngine.cs` | 5 种推理模式；多部分表单；按配置采样率封装 PCM |
| `Tts/TTSEngineFactory.cs` | 新增 `gpt_sovits` / `cosyvoice` 两个标识 |
| `Tts/TTSManager.cs` | 降级、软重启、空闲判断泛化到 `IHttpTtsEngine`；新增 `EngineLabel` |
| `Configuration/AppConfig.cs` | `GptSoVitsConfig` / `CosyVoiceConfig` |
| `Configuration/ConfigLoader.cs` | 引擎白名单 + 端口/路径/采样率兜底 |
| `Monitor/GameModeCoordinator.cs` | 按"当前 HTTP 引擎的端口"结束进程，不再写死 5000 |
| `Core/Models/Knowledge.cs` | `VoiceConfig.PromptText` / `InstructText`（逐角色覆盖） |
| `tools/probes/TtsBackendsProbe` | 新探针，30 项断言 |

### 27.3 真实验证（GPT-SoVITS）

服务：本地整合包 `api_v2.py`，CUDA，模型 `s1v3.ckpt` + `s2Gv2ProPlus.pth`（v2ProPlus）。

| 指标 | 实测 |
| --- | --- |
| 参考音频 | 4.8 秒 / 32 kHz（SAPI 生成，`models\tts\ref_zh.wav`） |
| 输出 | WAV 32 kHz、4.04–4.80 秒、253–307 KB（每句不同） |
| 单句耗时 | 2.0–3.7 秒（首次含 CUDA 预热为 44 秒） |
| 连续合成 | 第二次、第三次均成功 |
| 探针结果 | 49 通过 / 0 失败（引擎 + 管理器合计） |

### 27.4 CosyVoice：Windows 原生跑通，但踩了两个坑

先说结论：**不需要 WSL**。`requirements.txt` 里已经没有 `pynini` / `WeTextProcessing`，
新版换成了纯 Python 的 `wetext`，所以只要有个 Python 3.10，Windows 上就能装。

实际安装路径（全部实测）：

| 步骤 | 命令 / 结果 |
| --- | --- |
| Python | `python-3.10.11-amd64.exe` 静默装到 `E:\py310`（华为镜像 27.7 MB，3 秒） |
| torch | `torch==2.3.1` + `torchaudio==2.3.1`（CPU 版，约 10 分钟） |
| 依赖 | 分组装，镜像按 清华 → 阿里 → PyPI 回退；`openai-whisper` 要 `--no-build-isolation` |
| 源码 | GitHub 的 `git clone` 被连接重置，改用 `codeload` 的 zip（1.54 MB / 78 秒）+ Matcha-TTS zip |
| 权重 | ModelScope 4.13 GB（llm/flow/hift/campplus/tokenizer_v3）+ BlankEN 945 MB |

**坑一：CosyVoice3 的 `<|endofprompt|>`。** 第一次合成"成功"，但音频只有 3 帧，
HiFiGAN 的 f0 预测器报 `Kernel size can't be greater than actual input size`。根因在
`cosyvoice/llm/llm.py:479`：`assert 151646 in text`，而 151646 就是 `<|endofprompt|>`。
官方 `example.py` 的写法是把它放在指令前：

    inference_zero_shot(tts_text, 'You are a helpful assistant.<|endofprompt|>' + 参考文本, prompt_wav)

引擎为此加了 `tts.cosyvoice.prompt_prefix`，自动补到 zero_shot 的 `prompt_text` 与
cross_lingual 的正文前；已经带标记就不重复加。

**坑二：上游 `server.py` 自相矛盾。** 它先 `load_wav(prompt_wav.file, 16000)` 读成 tensor，
再把 tensor 传给 `inference_zero_shot`，而 `frontend.load_wav` 只接受路径，必然抛
`TypeError: Invalid file: tensor([[...]])`。已核对上游 main 分支，同样是这个写法。
本机副本就地打了补丁：上传内容落成临时文件、传路径进去，让前端按 24 kHz（频谱）与
16 kHz（token / speaker）各自正确重采样，流式响应结束后删文件。补丁在 `server.py`
的 `_save_upload` / `_stream_and_cleanup`，有注释标明"本地补丁（非上游代码）"。

修复后的真实结果：

| 指标 | 实测 |
| --- | --- |
| 模型加载 | 11.4 秒（4.13 GB + 945 MB，从本地磁盘） |
| 输出 | 24 kHz 单声道 WAV、5.80 秒、272 KB，PCM 非全零 |
| 单句耗时 | 38.9 秒（CPU，RTF 7.2） |
| 服务端 | `uvicorn` + `python-multipart`，`/docs` 200 |

CPU 上 RTF 7.2，慢得没法当默认引擎。GPU 后来补上了，见下。

启动服务用 `tools\start-cosyvoice.ps1`（默认指向 `E:\py310` 与
`E:\cosyvoice\pretrained_models\Fun-CosyVoice3-0.5B`，端口被占时会提示而不是再起一个；
脚本会自动优先挑带 CUDA 的 `.venv-gpu`）。

**坑四：GPU 版 torch 的 3.1 GB 轮子下不下来。** 本机的 torch 是 CPU 版，
RTX 5060 是 sm_120，必须 `torch 2.7.1+cu128` 才认。做法是建一个
`--system-site-packages` 的隔离 venv（`E:\cosyvoice\.venv-gpu`），只让它覆盖 torch/torchaudio，
CPU 环境原样保留：

    E:\py310\python.exe -m venv --system-site-packages E:\cosyvoice\.venv-gpu
    # pip 的下载器会在 download-r2.pytorch.org 上卡住（TLS 拦截 + 大文件流式），
    # 改用 curl 断点续传下到本地再离线装：
    curl.exe -L -C - -o torch-2.7.1+cu128-cp310-cp310-win_amd64.whl <wheel-url>
    .venv-gpu\Scripts\python.exe -m pip install --no-deps --no-index <两个 whl>

实测 5.6 MB/s，3.1 GB 约 10 分钟。装完验证：

    torch 2.7.1+cu128 | cuda available: True | cuda build: 12.8
    device: NVIDIA GeForce RTX 5060 Laptop GPU | sm_120, 7.9 GB, 26 SMs
    matmul ok: True

GPU 与 CPU 的实测对比（同一句 20 字，输出约 4.2 秒音频）：

| 项目 | CPU（py310，torch 2.3.1） | GPU（.venv-gpu，torch 2.7.1+cu128） |
| --- | --- | --- |
| 模型加载 | 11.4 秒 | 10.4–13.9 秒（fp32）/ 9.9–10.7 秒（fp16） |
| 首句（无预热） | 38.9 秒 | 23–26 秒 |
| 稳定态单句 | 约 39 秒（RTF 7.2） | **4.7–5.2 秒（RTF 1.0–1.1）** |
| 显存 | 0 | 3.2–3.4 GB |

#### 精度对比：为什么停在 fp16，为什么不用 fp4

| 精度 | 可行性 | 单句（约 4.2 秒音频） | RTF |
| --- | --- | --- | --- |
| fp32（上游默认） | — | 5.5–5.7 秒 | 1.30–1.34 |
| fp16（`--fp16 1`，autocast） | 可用，已设为 GPU 默认 | 4.2–4.7 秒 | **1.00–1.11** |
| bf16 | 可用（把 autocast 的 dtype 换成 bf16） | 5.5–5.7 秒 | 1.30–1.33 |
| fp8 | **不可用** | `RuntimeError: "addmm_cuda" not implemented for 'Float8_e4m3fn'` | — |
| fp4 | **不可用** | torch 2.7.1 里没有 `float4_e2m1fn_x2` 这个 dtype | — |

fp4 在 Blackwell 上确实是硬件能力（sm_120 有 FP4 tensor core），但 PyTorch eager 里没有对应
dtype 和算子，能吃它的只有 TensorRT 10.x 的 FP4 engine 或 torchao 的 NVFP4（还要逐层量化 + 校准）。
而且这里是 batch=1、26 个 SM 的**延迟**瓶颈，FP4 省的是带宽与吞吐，落到单句上收益很小，音质风险
却是实打实的。fp16 是这条链上唯一"改一行、零风险、有正收益"的档位，所以默认开它——
收益也只有约 20%，说明瓶颈在自回归解码的 kernel 启动与访存，不在算力。

#### 预热：把一次性开销挪到启动时（DirectStorage 式思路）

每个进程的第一次推理要多花 20–40 秒（CUDA kernel 编译 + onnxruntime EP 初始化）。做法不是让
用户等，而是**启动时先合成一句丢掉**（`server.py --warmup`，默认开）：

    21:04:36 INFO use wetext frontend
    21:05:29 INFO yield speech len 5.44, rtf 7.63      ← 这句是预热，输出被丢弃
    21:05:29 INFO 预热完成，用时 42.7s
    INFO:     Uvicorn running on http://0.0.0.0:50000

| | 无预热 | 有预热 |
| --- | --- | --- |
| 服务开始接受请求 | 12–17 秒 | 42–89 秒 |
| **服务就绪后的第一句** | 23–26 秒 | **5.0 秒** |
| 稳定态 | 4.7–5.2 秒 | 4.7–5.2 秒 |

代价是启动慢 30–70 秒，换来"第一句话不卡"。`--warmup 0` 可关掉。

#### 显存/内存的真实边界（8 GB 显卡 + 16 GB 内存）

- 两个服务同开：显存 6.4 / 8.1 GB 仍能跑，但 CosyVoice 单句劣化到 8–14 秒（独占时 4.8–5.2 秒）。
- 真正的坑在**内存**：两个 python 服务各提交 8.4–8.9 GB（工作集很小，CUDA 运行时预留的提交量很大），
  合计约 17 GB，而物理内存只有 15.3 GB → 页面文件涨到 8 GB、Memory Compression 压到 3.8 GB，
  任务管理器显示 98%。这时合成会劣化到 12–30 秒；关掉一个服务后**立即**回到 5 秒。
- 边打游戏边合成同样会劣化（游戏占显存，驱动回退共享内存）：实测 9–24 秒，游戏退出即恢复。
- `NVIDIA Overlay`（NVIDIA App 浮层）自己占约 1.25 GB 共享显存，关掉能多让出 1 GB。
- 结论：**8 GB 显卡 + 16 GB 内存的机器，同一时间只该跑一个 TTS 服务**。没在跑的那个引擎由
  App 自动降级到 Piper，不影响出声。`tools\start-cosyvoice.ps1` 启动时若发现 9880 在监听，
  会直接打出这条提醒和结束命令。

#### 语音 tokenizer 上 GPU（onnxruntime-gpu）

装了 `onnxruntime-gpu 1.23.2`，CUDA EP 可用，`frontend_zero_shot` 从 0.98 秒降到 **0.11 秒**；
代价是首次调用前要付 66 秒的 EP 初始化（正好被上面的预热吃掉），并且显存/内存都上涨。
8 GB 卡上"两个服务同开"会因此爆显存，所以默认仍走 CPU，只有设 `COSYVOICE_ORT_GPU=1` 才启用。

#### TRT / vLLM 评估（都没有落地）

- **vLLM**：PyPI 上只有 sdist（`vllm-0.31.0.tar.gz`），Windows 没有二进制轮子，源码编译要全量 CUDA
  工具链，上游也不支持 Windows。放弃。
- **TensorRT**：CosyVoice 里有 `load_trt` 路径（只换 flow 的 `decoder.estimator`，需要 onnx 导出 +
  engine 构建）。关掉 Steam++（它装了系统代理和根证书，等于中间人）之后 `pypi.nvidia.com` 能直连了，
  两个版本都装过：
  - TRT **11.4**：装上了，`trt.DataType.FP4` 存在（说明工具链认得 FP4），但 TRT 11 把
    `BuilderFlag.FP16/FP8/FP4` 全删了（改成"强类型网络 + Q/DQ 决定精度"），而上游
    `file_utils.convert_onnx_to_trt` 里是 `config.set_flag(trt.BuilderFlag.FP16)`，在 TRT 11 上直接
    AttributeError。**上游这条 TRT 路径在 TRT 11 上跑不了。**
  - TRT **10.16**：装上了、API 对得上，但上游只覆盖 CosyVoice1/2 的 `ConditionalDecoder` 形状
    （`x/mask/mu/cond`，80 通道）；本机 CosyVoice3 的 flow 是 `CausalMaskedDiffWithDiT`，
    estimator 是 DiT，输入约定不同，模型目录里也没有预导出的
    `flow.decoder.estimator.fp32.onnx`，仓库里没有导出脚本。**要在 v3 上用 TRT 得自己写
    onnx 导出 + 适配 DiT 的输入，等于一次移植，没有现成路径。**
  - FP4 更远一层：TRT 的 FP4 不是"加个 flag"，要网络里带 NVFP4 的 Q/DQ（权重按 16 元素分组、
    E4M3 缩放、E2M1 尾数），即先用 ModelOpt 做 PTQ 再建 engine；对"LLM 自回归解码 + flow + hifigan"
    这种混合管线，等于整条链的 Triton/TRT 移植。
- **torchao（torch 侧 NVFP4）**：0.18 需要 torch ≥ 2.8（用了 `torch.nn.functional.ScalingType`），
  装进本机 torch 2.7.1 的环境后，`transformers` 会自动探测并导入 torchao，结果 **整个 CosyVoice 都
  import 不起来**（`cannot import name 'ScalingType'`）。已卸载并验证环境恢复。要在 torch 侧量 FP4，
  必须单独装 torch 2.9.1+cu128（另建 venv，不动现有的 2.7.1）。
  试完已把 `tensorrt-cu12` / `onnxruntime-gpu` 从 `.venv-gpu` 卸载（省 1.5 GB 依赖），回归到
  `torch 2.7.1+cu128 + onnxruntime 1.18(CPU)` 这套已验证的组合。

#### 流式合成：试过，短句上反而更慢

上游 `server.py` 所有端点都是 `stream=False`，必须整句合成完才吐第一个字节。本机加了
`/inference_*_stream` 变体（`stream=True`），并把"第一块到达时间"逐块记下来：

| | 首块到达 | 首块音频 | 整句结束 |
| --- | --- | --- | --- |
| 非流式 | 4.75 秒 | 4.08 秒（整句） | 4.77 秒 |
| 流式 | **4.05 秒** | 1.04 秒 | 6.91 秒 |

模型层（不走 HTTP）的同样对照：首块 3.05 秒 / 1.04 秒音频，三块分别在 3.05、5.29、5.97 秒。

结论是"能流，但不划算"：LLM 要先产出 `token_min_hop_len`（50 个 token ≈ 2 秒音频）才做第一块
flow，而分块 flow 推理比整段推理效率低，**短句总延迟反而从 4.8 秒涨到 6.9 秒**。所以 App 侧
没有接入流式播放，端点保留给长文本试验（长文本时首块收益才明显）。

#### 想靠环境变量压低那 8.5 GB 提交量：压不动

`CUDA_MODULE_LOADING=LAZY` + `PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True` 重启后，
提交量 8947 MB → 8810 MB（噪声级），合成速度不变。说明这 8.5 GB 是 torch/CUDA 上下文的固有
开销，不是缓存造成的，所以"两个服务同开"的内存压力没法靠调参解决。

**坑三：wetext 的 FST 要联网拉。** 前端初始化时会 `snapshot_download("pengzhendong/wetext")`，
本机 TLS 被拦截（`SSL: CERTIFICATE_VERIFY_FAILED`），于是 `text_frontend` 退化成空字符串——
数字不念出来，比如"我有 3 个苹果"会读成"我有三个苹果"之外的怪结果。
修法是把 FST 直接落到本地（`en/tn`、`zh/tn` 各约 0.5–6 MB，PathScope 能正常下），
再让 `frontend.py` 优先读本地目录（`WETEXT_DIR` 或 `<repo>/pretrained_models/wetext`），
找不到才回退上游行为。验证：

    输入：我有3个苹果，电话是13800138000。
    输出：我有三个苹果，电话是幺三八零零幺三八零零零。
    输入：价格是12.5元。  →  价格是十二点五元。

服务日志里现在会打印 `use wetext frontend`。

### 27.5 设置面板可以直接切引擎

原来"语音"页只有一行只读的"配置引擎"，换引擎得去改 `config.yaml` 再重启。
现在加了下拉框（Piper / Chatterbox / GPT-SoVITS / CosyVoice），选中即生效：

- 选择结果写 **`Assets\appearance.json`** 的 `ttsEngine`，**不写 config.yaml**（准则 9）；
- 切换时立即 `TTSManager.ResetAsync()` 重建引擎，并在气泡里提示"已切换为 X"；
- 启动时 `AppHost.ApplyTtsEnginePreference()` 用这个偏好覆盖配置值（只在内存里改），
  优先级：`appearance.json` > `config.yaml`；值非法时记一条 WRN 并按配置运行；
- 下拉框右侧有小字提示该引擎要不要先起服务（GPT-SoVITS 9880 / CosyVoice 50000），
  服务没起时按原设计降级到 Piper 并弹气泡，不会静默换引擎。

### 27.6 instruct2 的 endofprompt 自动补全

`instruct` / `instruct2` 的指令文本也要求带 `<|endofprompt|>`，但位置和 zero_shot 相反：
标记必须落在**指令末尾**，模型用它切"指令"与"要读的正文"。让用户手写容易忘，
所以引擎在配了 `prompt_prefix`（说明用的是 CosyVoice3）且指令缺标记时自动补一个：

    [INF] instruct_text 缺少 <|endofprompt|> 标记，已按 prompt_prefix 的配置补在末尾。
    [INF] CosyVoice 合成完成（instruct2，24000 Hz，PCM 433920 字节 → WAV 433964 字节）：16 字。

探针第 6 项专门验证这条：指令写"用四川话说这句话"（不带标记），
得到 9.04 秒音频而不是"1 个 token 的假成功"。产物见 `artifacts\tts\cosyvoice_instruct2.wav`。

### 27.7 管理器级集成（不只是引擎）

前几项都直接调引擎，绕过了 `TTSManager`。但 App 里走的是管理器：它要读配置选引擎、
探活、失败时按三级降级、把异常吸收成失败结果并弹气泡。所以补了第 7 项：

    [INF] 按配置创建 TTS 引擎：gpt_sovits
    [INF] gpt_sovits 服务就绪：http://127.0.0.1:9880
    [INF] TTS 初始化完成：配置引擎=gpt_sovits，实际引擎=gpt_sovits，降级=False。
    [OK]  初始化后按配置选中 gpt_sovits
    [OK]  未降级到 Piper
    [OK]  ActiveHttpEngine 暴露端口 9880
    [OK]  SynthesizeAsync 成功（无错误）  ← 1.6 秒，322 604 字节 WAV
    [OK]  空文本被管理器拦下（不抛异常）

这一项确认了"设置面板切到 GPT-SoVITS 后，管理器确实把请求交给了它，而不是悄悄用 Piper"。

同一项里还模拟了设置面板的**运行时切换**（下拉框做的事 = 改配置 + `ResetAsync`）：

    [INF] 收到"重启语音服务"请求，正在重建 TTS 引擎。
    [INF] 按配置创建 TTS 引擎：cosyvoice
    [INF] TTS 初始化完成：配置引擎=cosyvoice，实际引擎=cosyvoice，降级=False。
    [OK]  ResetAsync 后切到 cosyvoice
    [OK]  切换后仍能合成（27 字，24000 Hz，222 764 字节）

### 27.8 App 启动时按界面偏好覆盖引擎（真进程验证）

单测之外，把 `Assets\appearance.json` 写成 `"ttsEngine": "gpt_sovits"`（config.yaml 里仍是
`piper`），然后真跑一次 `AICompanion.exe`，日志里能看到覆盖生效、并按新引擎初始化：

    17:56:32 [INF] [App] 按界面偏好把 TTS 引擎由 config.yaml 的 "piper" 覆盖为 "gpt_sovits"
                    （来源 Assets\appearance.json，未改动 config.yaml）。
    17:56:35 [INF] [TTS] 按配置创建 TTS 引擎：gpt_sovits
    17:56:35 [INF] [TTS] TTS 初始化完成：配置引擎=gpt_sovits，实际引擎=gpt_sovits，降级=False。
    17:56:35 [INF] [App] 启动完成：… TTS=gpt_sovits（降级=False）…

验证完已把 `appearance.json` 还原（没有留下测试痕迹），config.yaml 全程未被修改。

### 27.9 仍未闭合

- fp4 只能用 TensorRT / torchao NVFP4 这条量化路线，本机都拿不到轮子（见上），所以"fp4 对比"
  只到"可行性 + fp8 报错"这一层，没有真实 FP4 数字。
- `COSYVOICE_ORT_GPU=1` 只在大批量、单独跑 CosyVoice 时值得开；没做"根据剩余显存自动决定"的逻辑。
- GPT-SoVITS 的 `/control` 接口未使用，软重启只重建本地连接池。
- 下拉框只覆盖四个引擎标识，各自的细项（权重路径、参考音频、指令文本）仍然要改 `config.yaml`。

### 27.10 FP4 实测（关掉 Steam++ 后真做了一遍）

用户要求"必须试验 fp4"。上一轮被 TLS 挡在门外，这轮 Steam++ 关掉后能装到真东西，
于是建了一个**独立环境** `.venv-fp4`（`torch 2.9.1+cu128` + `torchao 0.14` + `tensorrt 10.16`），
不碰能用的 `.venv-gpu`。三条路线都试了：

**一、torch 侧的 NVFP4（torchao）**

- `float4_e2m1fn_x2` 只在 torch ≥ 2.8 里有（2.7.1 没有）；torchao **0.18 实际要求 torch ≥ 2.11**
  （报错原文 `Please upgrade to torch >= 2.11.0`），0.14 才能在 2.9.1 上导入，但会
  `Skipping import of cpp extensions`，原生内核不可用。
- 0.14 的 `NVFP4Tensor.to_nvfp4()` 能用，`F.linear(a_bf16, w_nvfp4)` 跑通，**相对误差 9.5%**（W4A4 为 13.4%）。
- 但看源码 `nvfp4_tensor.py:722 nvfp4_linear` 就是 `weight.to_dtype()` + `F.linear`：
  **先反量化回 bf16 再算**，不是 FP4 张量核。
- 实测（RTX 5060 Laptop, sm_120, M/K/N=4096）：

  | 方式 | 时延 | 算力 | 相对 bf16 |
  | --- | --- | --- | --- |
  | bf16 | 4.98 ms | 27.6 TFLOPS | 1.00× |
  | NVFP4 权重-only | 12.88 ms | 10.7 TFLOPS | **0.39×（更慢）** |
  | NVFP4 权重+激活 | 21.43 ms | 6.4 TFLOPS | 0.23×（更慢） |

- 直接调 `torch._scaled_mm` 传 FP4 一律 `Invalid scaling configuration`（各种打包/缩放布局都试过，
  含 e4m3 缩放与 e8m0+block32 的 MXFP4），说明 **torch 2.9.1 这个 cu128 构建在 sm_120 上没有 FP4 GEMM 路径**。
  同一接口的 **fp8 是 53.6 TFLOPS**（比 bf16 快 1.9×），证明 `_scaled_mm` 本身很快，问题只在 FP4。

**二、TensorRT 10.16 的 NVFP4（真 FP4 引擎）**

ONNX 里 `FLOAT4E2M1` 权重 + 每 16 元素一个 fp32 块缩放 → `DequantizeLinear(axis=1, block_size=16)`
→ `Transpose` → `MatMul`，TRT 编译出 engine（fp16 版 33.6 MB，fp4 版 12.6 MB）。

踩到四个坑，都记下来：

1. **ONNX 的 FP4 是沿最后一维打包的**。先按第 0 维打包，小规模测（K=64,N=2、所有 nibble 相同）
   居然"通过"，到 4096×4096 就变成 141% 误差 —— 因为小样例里怎么打包都一样。改成
   `[N,K]` 沿 K 打包 + 转置后，误差降到 13%。
2. `IBuilderConfig` 没有 `create_optimization_profile()`，要用 `builder.` 那个。
3. 事件计时必须 record 在**同一个 stream** 上，否则测出 4837 TFLOPS 这种假数字。
4. e4m3 缩放会被拒绝（`Scale coefficients must all be positive`），fp32 缩放才收。

结果（同一张图、同一套计时）：

| M（批量） | fp16 | fp8(E4M3) | NVFP4 | NVFP4 误差 |
| --- | --- | --- | --- | --- |
| 4096 | 9.17 ms / 15.0 TF | 9.01 ms / 15.3 TF | 8.16 ms / 16.9 TF | 13.0% |
| 512 | 0.93 ms / 18.5 TF | 1.06 ms / 16.2 TF | 1.34 ms / 12.8 TF | 13.0% |
| **1** | **0.087 ms** | 0.101 ms | **0.268 ms（3.1× 慢）** | 12.9% |
| 数值误差 | 0.2% | 2.7% | 13.0% | — |

**结论（回答"为什么不用 fp4"）**：

- FP4 在**大批量**（M=4096）只是和 fp16 打平、略好一点点，代价是 13% 数值误差；
  fp8 在同场景误差只有 2.7%、吞吐与 fp4 相当 —— **要省显存/提吞吐，fp8 才是这一代的正解**。
- 语音合成是 **batch=1 的自回归解码**，正好落在 FP4 最差的那一档：**比 fp16 慢 3.1 倍**。
  把 CosyVoice 换成 FP4 只会更慢。
- 这台 8 GB 显存 / 16 GB 内存的机器上，真正有效的优化是把 fp16 用对（已做：RTF 1.30→1.00）、
  只跑一个服务、开启动预热 —— 不是换数据类型。

### 27.11 FP8 实测（清掉 FP4 环境后）

fp4 研究环境（`.venv-fp4` 7.7 GB + 下载的轮子 2.7 GB）已删除，回到只有
`torch 2.7.1+cu128 + onnxruntime 1.18(CPU)` 的最小环境，再试 FP8。

**一、微基准（`torch._scaled_mm`，fp16 对照）**

| 形状 | fp16 | fp8（tensorwise） | 倍数 |
| --- | --- | --- | --- |
| 4096×4096×4096 | 5.25 ms | 2.88 ms | **1.82×** |
| M=512 FFN 上行 (896→4864) | 0.159 ms | 0.087 ms | **1.83×** |
| M=1 FFN 上行 | 0.031 ms | 0.024 ms | 1.3× |
| M=1 FFN 下行 | 0.026 ms | 0.032 ms | 0.8×（更慢） |
| M=1 attn 投影 | 0.018 ms | 0.019 ms | 持平 |

- **per-row（逐 token）缩放不可用**：`RuntimeError: Per-row scaling is not supported for this platform!`
  —— sm_120 上这个 torch 构建只支持 per-tensor（标量）缩放。
- 这很关键：LLM 量化必须逐 token 缩放才不掉精度，per-tensor 对 outlier 无能为力。

**二、端到端（真模型）**

- 同进程、fp16 基线：**2.79 秒合成 1.88 秒音频**（单句、非服务态）。
- 把 LLM 里 **169 个 Linear** 全换成 per-tensor FP8 的 `_scaled_mm` 后：
  `RuntimeError: CUDA error: device-side assert triggered` —— 量化噪声直接把自回归采样打崩。
- 分阶段耗时（同一句、fp16）：

  | 阶段 | 耗时 | 占比 |
  | --- | --- | --- |
  | LLM + 前端 | ≈2.5 s | **80%** |
  | flow（扩散解码） | 0.52 s | 17% |
  | hift（声码器） | 0.11 s | 4% |
  | 合计 | 3.13 s | — |

**结论**：FP8 的 1.8× 只在 **M≥512**（prefill 那一小段）成立；而 80% 的时间花在
**batch=1 的自回归解码**上，这一档 FP8 持平或更慢，加上 sm_120 只给 per-tensor 缩放、
精度撑不住 LLM。所以 FP8 在 CosyVoice 上同样不可用 —— **保持 fp16** 是这台机器的最优解。

脚本：`E:\cosyvoice-src\bench_fp8_torch.py`（微基准）、`cosy_fp8.py`（端到端尝试）、
`cosy_profile_stages.py`（分阶段）。

脚本留在 `E:\cosyvoice-src\`（`trt_prec3.py` 三方对比、`trt_fp4_bench4.py` FP4 单测、
`bench_nvfp4.py` torchao 版），`.venv-fp4` 只是研究环境，**不进安装包**。

---

## 28. 清理与收尾（2026-10-23 凌晨）

用户要求"删掉测试数据、整理项目、排查内存泄漏和死代码，做完关机"。以下是这一轮做了什么、
依据是什么。全部是**静态检查 + 构建**，没有跑模型推理。

### 28.1 删掉的测试数据与中间产物（约 6.4 GB，另加之前的 10.4 GB）

| 目标 | 体积 | 为什么 |
| --- | --- | --- |
| `artifacts\pkgbin` | 248.6 MB | `pack.ps1` 的发布中间产物，随时可重生成 |
| `dist\AICompanion-0.9.0-win-x64` | 1949 MB | 分发目录副本，安装包已包含全部内容 |
| `artifacts\cosyvoice-campplus.onnx` | 27 MB | 研究时下载的模型副本，正本在 `E:\cosyvoice` |
| `artifacts\tts`、`artifacts\pilot-screenshots` | 9 MB | 探针合成的 wav、试点截图 |
| `artifacts\speech_zh.wav`、`icon-preview.png`、`app.ico.orig`、`cosyvoice-*.{py,md}`、`wsl-install.ps1` | 0.2 MB | 一次性产物与抄来的上游文件 |
| `logs\` | 1.9 MB | 运行日志 |
| 32 个 `obj\` / `bin\` | 229 MB | 编译中间产物（顶层 `bin\` 是原生库，保留） |
| `E:\cosyvoice-src` | 3983 MB | TRT 的 `.plan` / `.onnx` 与实验草稿 |

保留：`dist\AICompanion-0.9.0-full-setup.exe`（1621.5 MB，交付物）、
`artifacts\repo-full-history.bundle`（14.8 MB，完整历史备份）。

研究脚本没丢：`trt_prec3.py`、`trt_fp4_bench4.py`、`bench_fp8_torch.py`、
`cosy_profile_stages.py` 已归档到 `tools\research\`，并附了 `README.md` 说明依赖与结论。

### 28.2 内存泄漏排查（逐个资源看，没有发现需要改的）

| 资源 | 检查结果 |
| --- | --- |
| `HttpClient` | 全仓库只有 4 处创建（`HealthMonitor`、`LlamaClient`、`ChatterboxEngine`、`HttpTtsEngineBase`），都是**长生命周期字段**，且各自在 `Dispose()` 里 `_http.Dispose()`；没有"每请求 new" |
| 定时器 | `LipSync`、`HealthMonitor`、`MonitorServer`、`IdleTaskScheduler`、`ASRManager` 的 `Timer` 都在 `Dispose()` 里 `?.Dispose()`；`MainWindow`/`BubbleNotifier`/`PushToTalkMonitor` 的 `DispatcherTimer` 随 Dispatcher 存活，`PushToTalkMonitor.Dispose()` 还额外退订了 Tick |
| `CancellationTokenSource` | 只有两处 `new`：`ASRManager._listenCts`（在 `Stop`/`Dispose` 里释放）、`IdleTaskScheduler`（`finally` 里 `cts.Dispose()`） |
| 会增长的集合 | `ContextManager` 有分层摘要 + `MaxRecentRounds`；`BubbleNotifier` 有过期清理（`PurgeExpired`）；`ASRManager._pttSamples` 有 `MaxSegmentSeconds` 上限；`GameCooldownTracker` / `HealthMonitor` 的字典按进程/探针名索引，规模有限 |
| 引擎重建 | `TTSManager.ResetAsync()` / 降级路径都是先 `_engine.Unload()` 再建新引擎，`Unload()` 会释放 `HttpClient` |

唯一"不完美但可接受"的：`BubbleNotifier` 没实现 `Dispose`（它的 `DispatcherTimer` 与窗口同生命周期，
且 Tick 里只做清理，不持有外部对象）。为它加 `IDisposable` 只是在 `App` 退出时省一次 Tick，不值得改动。

### 28.3 死代码扫描

- `dotnet build AICompanion.sln -c Debug` → **0 警告**（没有 CS0169 / CS0414 这类未使用字段警告）。
- 额外做了一次启发式扫描：把 `src` 下所有 `private`/`internal` 成员名拎出来，在
  `.cs` + `.xaml` + `.json` 全文里按词边界计数，**只被声明一次**的只有
  `GameMonitor\Program.cs:26 Main` —— 那是入口点，属于误报。
- 结论：没有可安全删除的死代码。这一条如实记下来，不为了"看起来整理了"而乱删。

### 28.4 最终状态

- `git status` 干净；构建 0 警告；交付物 `dist\AICompanion-0.9.0-full-setup.exe` 在。
- E 盘剩余空间 65.1 GB（清理前约 55 GB）。
- 收尾后执行关机（用户要求）。
---

## 29. 逐行死代码 / 内存泄漏审计（第二轮）

### 29.0 先纠正 §28 的结论

§28.2 / §28.3 当时写的是"没有泄漏、没有可删的死代码"。那一轮只做了构建 + 词边界计数两种机器扫描，
**不足以发现"写了但没接线""Dispose 了但对象不对""异常路径漏释放"这类问题**。
这一轮按用户要求逐行读（`src` 下 78 个 `.cs`、约 1.96 万行，分 6 个子任务并行通读并互相 grep 复核），
实际找到 **30 处**问题，其中 6 处是真实资源泄漏、4 处是原生层风险（会崩或会死锁）。
§28 的结论作废，以本节为准。

### 29.1 已修：ASR / 音频采集

| 文件 | 问题 | 修法 |
| --- | --- | --- |
| `ASRManager.cs` `StopListening()` | 状态不是 `Running` 时直接 `return`（Faulted/Idle 态都算），导致采集不停、事件不退订、CTS 不取消、队列不 `CompleteAdding` → 状态机卡在"正在监听"且资源不回收 | 改为按"曾经在跑"和"有资源可收"两个条件收尾，不再依赖当前状态 |
| `ASRManager.cs` `ResetAsync()` | 先 `_vad?.Dispose()` 再停音频线程；音频线程还在 `ProcessFrame` 里用 VAD → 原生 use-after-free | 顺序改为：停采集 → `_audioThread.Join(2s)` → 等识别任务 → `_recognizer.Reset()` → 释放 VAD |
| `ASRManager.cs` `Dispose()` | 不等识别任务就 `_recognizer.Dispose()`；whisper 的 shutdown 会释放全局 ctx，与在飞的 `whisper_full` 并发是原生崩溃（不可 catch） | 新增 `WaitRecognitionTask(TimeSpan)`，释放前限时等 2 秒；`_capture` / `_recognizer` 各自独立 try，互不连坐 |
| `AudioCapture.cs` `Start()` | 启动抛异常时 `IsCapturing` 仍是 false，于是 `Stop()` 在 `if (!IsCapturing)` 处提前返回 → waveIn 原生句柄永久泄漏 | catch 里自行 `Dispose()` 并置 null |
| `AudioCapture.cs` `OnRecordingStopped` | 设备侧停录后不复位 `IsCapturing`，上层自愈再 `Start()` 会被 `if (IsCapturing) return` 挡掉 → "显示正在监听但收不到声音" | 复位 `IsCapturing`，无异常时也记一条 Warn |
| `AudioCaptureLoopback.cs` `Stop()` / `Start()` 失败路径 | 在 `lock (_sync)` 内调 `StopRecording()`：`StopRecording` 要等采集线程退出，而采集线程在 `OnDataAvailable` 里正等同一把锁 → 自锁死 | 锁内只摘引用（`_capture = null`），`StopRecording/Dispose` 全部挪到锁外；`CleanupCapture` 改为接参数 |
| `AudioCaptureLoopback.cs` | 只写不读的 `_ratio`、`_emitted` | 删除 |

### 29.2 已修：音频播放

| 文件 | 问题 | 修法 |
| --- | --- | --- |
| `WaveOutPlayer.cs` `PlayAsync` | 顺序反了：先 `ReleasePin()` 再 `PlaySound(..., SndPurge)`。Win32 还在读这块内存时 pin 已经解掉，GC 可以移动/回收它 | 先 purge（停掉引用），再解 pin |
| `WaveOutPlayer.cs` `PlayAsync` | 正常播完不解 pin：只在下一次 Play/Stop/Dispose 时才释放，最后一次播放的 WAV 会一直被 `GCHandle` 钉在固定堆上 | 播放结束后立即在锁内 `ReleasePin()` |
| `ObservableAudioPlayer.cs` | 没实现 `IDisposable`，于是 `AppHost` 里的 `(Audio as IDisposable)?.Dispose()` **是永远不成立的分支**，内层 `WaveOutPlayer` 从不被释放 | 实现 `IDisposable`，先 `Stop()` 再转发给内层 |

### 29.3 已修：TTS

| 文件 | 问题 | 修法 |
| --- | --- | --- |
| `SapiEngine.cs` `Speak` | `stream = null;` 之后才 `ReleaseCom(stream)` —— RCW 引用计数永远不归零，每次合成漏一个 COM 对象 | 删掉置空，直接 `ReleaseCom(stream)` |
| `SapiEngine.cs` `TrySelectVoice` | 每次合成重新枚举全部 voice token（COM 对象泄漏 + 明显变慢） | 进程内解析一次并缓存（`_voiceToken` / `_voiceResolved`），`ISpeechObjectTokens` 在 `finally` 里释放 |
| `PiperEngine.cs` `SynthesizeAsync` | 异常路径不 `TryKill` 子进程；`stderr`/`stdout` 读任务不观察（异常被丢、管道句柄不关）；临时文件删除失败完全静默 | `TryKill` 移入 `finally`；读任务限时 `WhenAny(WhenAll, Delay(500))`；删除失败记 Debug |

### 29.4 已修：GameMonitor / LLM / 外部进程

| 文件 | 问题 | 修法 |
| --- | --- | --- |
| `MonitorServer.cs` | 新连接接入时只 `Dispose` 旧 `_client`，旧 `_writer` 不释放 | 换连接前先 `_writer?.Dispose()` |
| `MonitorServer.cs` | 旧读循环的 `finally` 无条件 `HandleDisconnect`：新客户端接入后，旧循环收尾会把**刚建好的新连接和新 writer** 一起关掉（"重连后立刻又断开"） | `finally` 里先 `ReferenceEquals(_client, client)` 确认自己仍是当前连接 |
| `MonitorServer.cs` `Stop()` | 程序自己退出时仍弹"游戏检测服务已断开，可点重连"的告警气泡 | `HandleDisconnect(reason, notify: false)`，只记日志 |
| `MonitorClient.cs` `ConnectOnceAsync` | 连接失败时 `_client` 还没赋值，局部 `TcpClient` 无人释放 → 每 5 秒漏一个 socket | 失败分支自己 `Dispose()` 后再抛 |
| `GameMonitor\Program.cs` | `client.Start()` 之后从不 `Stop()/DisposeAsync()`：`TcpClient`、心跳 CTS、后台任务全部活到进程结束 | 退出路径统一 `DisposeAsync` |
| `GameMonitor\Program.cs` | 引导日志器与正式日志器**目录、前缀完全相同**（发布后都是 `logs\app\gamemonitor-yyyyMMdd.log`），同进程两个 `StreamWriter` 写同一文件 → 行交错（主程序里已实测过） | 用显式 `LogSink` 建引导日志器，正式日志器一就绪就关掉引导句柄 |
| `GameMonitor\Program.cs` | `_lastExitedGame ??= null;` 是无操作语句 | 删除 |
| `LlamaServerController.cs` `StartAsync` | 启动被取消（用户退出/重启）时已拉起的 llama-server 变孤儿进程，**继续占着显存且无人能管** | `catch (OperationCanceledException)` 里 `StopProcessAsync(CancellationToken.None)` 再抛出 |
| `LlamaServerController.cs` `StartProcessAsync` | `Process.Start()` 返回 false 时 `_process` 仍指向该对象且不 `Dispose` | 清字段 + `Dispose` |
| `LlamaServerController.cs` | `_intentionalStop` 在 `Process.Exited` 事件线程上无锁读 | 加 `volatile` |
| `LlamaServerController.cs` | `_stderrHistory` 跨尝试累积，OOM 重试时诊断串混入上一次进程输出 | 每次尝试前清空 |
| `ProcessRunner.cs` | 超时用的 `Task.Delay(timeout, ct)` 在进程正常结束时不会取消 → 每次调用留一个活到 `timeout`（nvidia-smi 是 10 秒）的计时器 | 用 linked CTS，成功路径 `Cancel()`；kill 后限时收掉两个读流任务 |
| `LlamaClient.cs` `TryEraseSlotsAsync` | `HttpClient.Timeout` 是无限（对话要能长跑），清槽请求没有自己的超时 → llama-server 卡住时永久挂住整条流程 | `CancelAfter(5s)` |

### 29.5 已修：进程生命周期与窗口关闭

| 文件 | 问题 | 修法 |
| --- | --- | --- |
| `AppHost.cs` `DisposeAsync` | 四个组件挤在一个 `try`：ASR 抛一次异常，TTS / 知识库 / 音频全部跳过释放 | 每个组件独立 `try` |
| `AppHost.cs` | `PushToTalkMonitor` 全生命周期不释放：退出后 60ms 的 `DispatcherTimer` 仍在轮询键盘，还可能对已释放的 ASR 调 `BeginPushToTalk` | 在释放 ASR **之前** `Dispose()` 并置 null |
| `AppHost.cs` | `IdleTaskScheduler` 的 `Dispose()` 从未被调用 | 补调用 |
| `MainWindow.xaml.cs` `OnClosed` | `_hoverTimer` / `_expressionTimer` / `_asrResumeTimer` 从不 `Stop()`；`_taskBar` / `_cornerButton` 从不 `Close()`；`AnimationManager` 的 hold 定时器不停 | 全部补上 |
| `AnimationManager.cs` | 每次动作都 `new DispatcherTimer`，旧 timer 只 `Stop()` 不退订 → Tick 委托（连同捕获的请求）在 Dispatcher 上越堆越多 | 复用单个 timer + `Dispose()`；`MainWindow.OnClosed` 调用 |
| `LipSync.cs` `Start` | 包络在后台线程算完就无条件 `new Timer`；期间若已 `Stop`/`Dispose`（播放被打断、窗口关闭），就留下一个没人持有的定时器，每 50ms 空转到进程结束 | `_disposed` 双重检查（任务入口 + 锁内） |
| `VadDetectors.cs` `Dispose` | 不幂等：`InitializeAsync` 是"先 Dispose 再重建"，重建若中途失败，下一次 Dispose 会二次 `free` 同一个 fvad（原生堆破坏） | 释放后把句柄清零，相关字段改为可写 |

### 29.6 已修：正确性与安全

| 文件 | 问题 | 修法 |
| --- | --- | --- |
| `PortProcessLocator.cs` | 只按端口号匹配就返回 PID 并杀进程，不校验监听地址；同端口号在 `0.0.0.0` / `[::]` 上可能是别的程序 | 只认 `127.0.0.1` / `[::1]`，与类注释承诺一致 |
| `GgufMetadataReader.cs` `SkipValue` | 数组嵌套无深度限制，损坏/恶意 GGUF 可用 `Array<Array<...>>` 打爆线程栈（`StackOverflowException` 无法捕获，直接杀进程） | 深度上限 32 |
| `ContextManager.cs` `AddMessage` | `_recent` 没有硬上限；压缩只在空闲维护里跑，用户连续对话时会无限增长 | 硬上限 `max(64, MaxRecentRounds * 4)`，裁掉的消息仍在聊天日志里 |
| `GameCooldownTracker.cs` | `PruneExpired()` 全仓零调用，字典只增不减（长驻进程慢泄漏） | 设冷却时先清理过期项 |
| `SettingsWindow.xaml.cs` | 2 秒定时器每轮发起显存查询，每次查询起 2 个 `nvidia-smi` 子进程，且无防重入 → 查询慢于 2 秒就并发叠加 | `_gpuQuerying` 门控 |
| `MainWindow.xaml.cs` | 配置项 `ui.always_on_top` 从未生效（死配置）：主窗口、功能栏、角按钮都没设 `Topmost` | 三处都按配置设置 |

### 29.7 死代码：删除 13 项，接线 1 项

删除（全仓按词边界计数，含 `tools/probes`，均只剩声明本身）：

- `TTSManager`：`CurrentEngine`、`LastRequestUtc`、`IsHttpEngineActive`
- `MonitorMessageTypes.Pong`、`MonitorMessage.IsPing`
- `GameModeCoordinator.ChatterboxWasStopped`
- `SystemMetrics.IsOnBattery()`（连同只被它使用的 `SYSTEM_POWER_STATUS` / `GetSystemPowerStatus`）、`SuspendDetector.MarkResumed()`
- `ASRManager.FaultMessage`、`ASRManager.RecognizerStatus`
- `CornerButton._clickThrough`（只写不读）
- `TaskBarWindow.xaml` 的 6 个零引用 `x:Name`（只为生成无用字段）
- `Live2dModelInfo.ParameterGroups`（私有属性，构造函数直接用参数）
- `GameMonitor\Program.cs` 的 `_lastExitedGame ??= null;`

接线而非删除：

- `MainWindow.ClampIntoWorkArea()`：注释承诺"改大小时把窗口拉回工作区"，但从来没人调用。
  在 `ApplyScale()` 末尾接上——这是漏接线，不是多余代码。

### 29.8 复核后**保留**的（附证据，避免下一轮又当成 bug）

| 位置 | 为什么保留 |
| --- | --- |
| `WhisperCppRecognizer` 不调 `NativeLibrary.Free` | 实测会触发 `0xC0000005`（whisper 内部仍有线程/回调引用该模块） |
| `MonitorServer` 用 `_listener.Stop()` 而非 `Dispose()` | 二者等效，`Stop()` 还能让 `AcceptTcpClientAsync` 正常抛出而不是 `ObjectDisposedException` |
| `LlamaServerController.StopProcessAsync` 的 `process.Dispose()` | 在 `finally` 里，扫描工具看不见 → 误报 |
| `LipSync` 用 `Timer.Stop()` 而非 `Dispose()` | 其 `Dispose()` 路径就是 `Stop()` + 置空引用，再加一层没有收益 |
| `BubbleNotifier` / `LogSink` 的 `DispatcherTimer` / `StreamWriter` | 与进程同生命周期；为省一次 Tick 去改接口不划算。但 GameMonitor 的**双 sink 同文件**是真 bug，已修 |
| `tools/probes` 引用的公开成员（`ComputeRms`、`EyeBlinkParameters`、`LastRawResult` 等） | 探针不在解决方案里，但属于可运行验证资产，删了会破坏探针编译。**注**：本条原还列了 `IHttpTtsEngine.BaseUrl`，第三轮复核发现探针对它 0 引用，已删除该成员（见 §30.12 第 13 项） |

### 29.9 本轮**未修**的（如实登记原因）

1. `Live2DRenderer.TryCreate` / `Dispose` 的异常路径不释放 GL 上下文、lapp、control（正常路径正确）。
   要改必须重排 GL 线程上的释放顺序，风险高于收益；等有真实复现再动。
2. `TrayIcon` / `MainWindow` 的事件订阅与 `ContextMenuStrip` 不退订、不 `Dispose`。
   两者寿命 = 进程寿命（`BubbleNotifier` 是单例），退订不改变实际释放行为，只是增加空引用风险。
3. `MainWindow` 的 8 个 lambda 事件订阅没有 `-=`：同上，窗口关闭即退出。
4. `GameListProvider.ReloadIfChanged` 的热重载分支不可达（只在构造时 `force: true` 调用）。
   这是"未实现能力"（要接 `FileSystemWatcher`），不是清理项。
5. `LogSink` 没有单文件大小上限（只按天轮转 + 保留天数）。属功能增强，不是泄漏。
6. `ConversationPipeline` 在构造锚点前覆盖 `_lastUserMessage`：属行为时序问题，要改对话上下文构造，本轮不动。
7. `ContextManager.MaxRecentRounds` 的"轮 vs 条"语义不一致：已用硬上限兜底，语义重命名留待下一轮。
8. 未使用的配置键 `tts.gpt_sovits.launch_command`、`tts.cosyvoice.launch_command`、`ui.idle_expression_seconds`：
   配置校验是严格的，删键会让用户现有的 `config.yaml` 报"未知键"，保留并在此登记。
9. `KnowledgeImporter` 子系统（约 390 行）目前零消费者：它是知识导入的设计入口，删掉会丢失功能面，
   登记为"未接线"，等真正接上再决定去留。

> **第三轮已全部处理**：以上 9 项在 2026-10-23 上午逐一落地（第 8 项改为“实现”而不是“弃用”，
> 因为复核发现 `ConfigLoader` 使用 `IgnoreUnmatchedProperties()`，并不存在“删键会报未知键”的校验）。
> 逐项对应关系与证据见 §30.0；其中第 6 条已修，第 8 条的前提判断在 §30.0 末尾纠正。

### 29.10 最终状态

- `dotnet build AICompanion.sln -c Debug` → **0 Warning / 0 Error**。
- 改动 30 个文件；行尾统一（ASRManager 恢复 CRLF，其余保持 LF），没有混入 EOF 换行噪声。
- 本轮全部为静态代码修改，**没有跑任何模型推理**（遵守"低负载"要求）。
- 机器**没有关机**：用户中途回来继续提要求，关机前需再确认。


## 30. 第三轮：§29.9 登记项全部落地（2026-10-23 上午）

### 30.0 与 §29.9 的对应关系

| §29.9 登记项 | 本轮状态 | 详见 |
| --- | --- | --- |
| 1 `Live2DRenderer` 异常路径不释放 GL/lapp/control | 已修 | §30.1 |
| 2 `TrayIcon` 事件与 `ContextMenuStrip` 不退订、不释放 | 已修 | §30.2 |
| 3 `MainWindow` 8 个 lambda 订阅没有 `-=` | 已修（实际 9 条，含 `Bubbles.Changed`） | §30.2 |
| 4 `GameListProvider.ReloadIfChanged` 热重载分支不可达 | 已修（接上，未新增 `FileSystemWatcher`） | §30.8 |
| 5 `LogSink` 没有单文件大小上限 | 已修（32 MB 滚动一代） | §30.7 |
| 6 `ConversationPipeline` 锚点前覆盖 `_lastUserMessage` | 已修 | §30.6 |
| 7 `ContextManager.MaxRecentRounds` 轮/条语义 | 已修 | §30.5 |
| 8 三个未使用的配置键 | 已实现（保键不删，见说明） | §30.4 / §30.10 |
| 9 `KnowledgeImporter` 零消费者 | 已接线（设置面板选文件夹导入） | §30.11 |

§29.9 第 8 项当时写的是"配置校验严格、删键会让用户现有 `config.yaml` 报未知键"。本轮复核发现这个前提**不成立**：
`ConfigLoader` 用的是 `IgnoreUnmatchedProperties()`（`ConfigLoader.cs`），仓库里根本没有"未知键/废弃键"检查，
所以既没有"删键会报错"的风险，也没有现成的忽略名单可用。两条路都能走通后，选择了**实现**而不是**弃用**——
把键变成真实功能，比删掉更能兑现手册里"外部语音服务可被程序拉起"的设计意图。

### 30.1 Live2D 异常路径（`Animation/Live2DRenderer.cs`）

`TryCreate` 原来是"正常路径正确、失败路径全漏"：

- `control.Start()` 已经建好 GL 上下文与渲染线程、`new LAppDelegateOpenGL()` 已经建好 Cubism 资源之后，
  任何一步失败都只写一行日志就走人 —— 泄漏整套原生资源（含渲染线程）。
- 修法：把 `control` / `lapp` / `model` 提升为 `try` 外的局部变量，加 `handedOver` 标志；
  `model is null`、任意异常、**以及"就绪日志本身抛异常"**三条失败路径都走 `finally` 里的释放。
- 释放助手 `ReleaseNativeResources(model, lapp, control, logger)` 按 model → lapp → control 顺序，**每步各自 try/catch**。
  顺序不能反（lapp 持有模型用到的贴图与离屏表面，control 提供 lapp 依赖的 GL 上下文），
  也不能挤在一个 try 里（`model.Dispose()` 抛一次就会漏掉最贵的 control）。

`Dispose` 原来是"四行挤一个 try"：`_model.Dispose()` 抛异常就跳过 lapp 与 control。改为先单独 try 退订
`CompositionTarget.Rendering` / `_control.SizeChanged` / `_control.Render`（避免释放过程中合成器还在回调），
再调同一个释放助手。

其余：删掉 `_startedUtc`（只写不读）；`ApplyViewport` 的视口日志由 `Info` 降为 `Debug`
（`SizeChanged` 高频触发，而 `LogSink` 每行 Flush，拖窗口会持续写盘）。`_frameworkStarted` 有意不复位
（Cubism 的 `StartUp` 全进程只允许一次，失败后复用比再来一次安全），已加注释说明。

行尾：该文件原有 2 行散落 CRLF，被编辑工具归一为 LF。这是本轮唯一一处行尾差异
（`git diff --ignore-cr-at-eol` 后 +81/-11，正常 diff 为 +83/-13）。

### 30.2 窗口 / 托盘 / 动作 / 口型的事件与释放

**MainWindow**：新增 `private readonly List<Action> _unsubscribe`，所有对外部服务的订阅都登记一条退订动作，
`OnClosed` 里逐条 try/catch 执行后 `Clear()`，且**排在所有释放动作之前**（后台服务在窗口关闭后仍会触发）。
登记 9 条：`Bubbles.Changed`、`Pipeline.StatusChanged/ReplyReady/UserMessageAccepted`、`Asr.SpeechRecognized`、
`Asr.StateChanged`、`ObservableAudio.PlaybackStarted/PlaybackStopped`、`Tts.EngineFellBack`。
`Asr.StateChanged` 原来挂了两个处理器（一个更新文字、一个请求 `Listen` 动作），合并成一个
（先 `UpdateStageText()`，再只在 `Running` 时请求动作），退订只算一次。

顺带修掉三处零碎问题：

- `_expressionTimer` 初始化里的 `Interval = 8s` 是死赋值（必然被 `ScheduleExpressionRevert` 的
  `Stop → Interval → Start` 覆盖），删除。
- 口型：`_lipSync.Start(wav)` 改为只在 `_live2d is not null` 时启动。静态图下 `ApplyMouthOpen` 只写 Debug、
  没有写回路径，每秒 20 次 `BeginInvoke` 纯属空转。
- 悬停诊断改为惰性：判定节奏仍是 2 秒一次，但拼接字符串只在"这一 tick 真的要写日志"时做
  （原来每 100 ms 拼一次）；`IsCursorOnCharacter(bool collectDiag = false)` 收集参数可选。
- 静态图表情查找统一到 `ApplyExpression`（`.png`/`.jpg` 的探测只剩一处），删掉过期 TODO。

**TrayIcon**：删 `_hostReady` 字段（连同只为消除"未使用"告警而写的 `_ = _hostReady;`）；
删无效闪烁分支（`BubbleLevel.Error` 时赋的还是构造时同一个 `_icon` 实例，没有任何视觉变化）；
`Bubbles.Changed` 处理器存字段并在 `Dispose` 里退订；`ContextMenuStrip` 存字段并在 `Dispose` 里
`Visible=false → ContextMenuStrip=null → Dispose()`，最后释放菜单。
没有逐个字段保存 9 个菜单项：`ToolStrip.Dispose()` 会连带释放 `Items`，逐个持有属于重复引用、收益为零。

**AnimationManager**：删 `Current` 属性、`ApplyProfile(AvatarProfile)`、`Request(AnimationRequest)` 重载
（enum 版 `Request` 内联构造请求对象）。删 `ApplyProfile` 的前提是确认"换模型"能力**本身不存在**：
模型路径只在 `OnHostReady` 里从 `config.live2d.default_model_path` 读一次并建渲染器，
`ChangeAppearance` 只换静态图，设置面板没有任何 Live2D 模型选择/重建入口 —— 详见 §30.15 第 1 条。
另清理两处过期 TODO 注释：`_motionExists` 与 `SafeExists` 上写着"接 Live2D 后替换为真实检查"，
但 `MainWindow` 早在 `OnHostReady` 里就注入了真实的 `Live2dModelInfo.HasMotion`（按 model3.json 的
Motions 组名判断），注释已改成描述现状。

**LipSync**：`_lastReported` 的读与写都挪进 `lock (_sync)`（原来只有写在锁内），
平滑基准不再可能与 `Stop()` 的清零竞争；`ReportToUi` 仍在锁外。

### 30.3 水平微调：托盘入口缺回调（本轮新发现）

主窗口打开设置面板时会把 `ApplyUserAppearanceSettings` 作为回调传进去，托盘菜单打开时**没有传**——
从托盘改"水平微调"并保存后，偏移要重启才生效。已在 `TrayIcon` 打开设置面板处补上同一个回调
（`ApplyUserAppearanceSettings` 本来就是 public，没有改可见性）。

接线的完整形态（`MainWindow`）：

- 字段 `_defaultHorizontalOffset` 记下角色包自带偏移（`profile.HorizontalOffset`）；
- 创建渲染器时用 `AppearanceStore.LoadHorizontalOffset() ?? _defaultHorizontalOffset`；
- `ApplyUserAppearanceSettings()` 里 `_live2d?.SetUserOffset(...)`，滑块归零（存 null）时回到角色包默认值，
  而不是硬编码 0。

### 30.4 TTSManager 并发保护 + `launch_command` 生效

**引擎替换加锁**：新增 `SemaphoreSlim _engineLock`，所有引擎字段替换都走 `InstallEngineAsync`：
持锁 → 先换字段 → 再 `Unload` 旧引擎（`ReferenceEquals` 防同实例双卸，`Unload` 异常只 Warn）→ 释放锁。
持锁期间不 await 任何外部代码，所以不会死锁。`Dispose` 也用同一把锁（同步方法只能 `Wait()`）。
这样消除了"取到引擎引用后、调用前被 Unload"的窗口。

**合成路径取快照**：`var engine = _restarting ? GetPiperFallback() : _engine;` + `engine is null` 检查，
后续（含 catch 里的 `engine.Name`）全用快照。

**`launch_command` 从死配置变真功能**：`LaunchCommandFor` 只认 `gpt_sovits` / `cosyvoice`
（`chatterbox` 故意返回 null —— `AppHost` 已经负责拉起，两处都拉会起两个进程）。
拉起方式与 `AppHost` 同构：`cmd /c <命令>`、`WorkingDirectory = AppPaths.Root`、每个引擎**每进程只试一次**、
空命令跳过、失败只 Warn（绝不因此让启动失败）。拉起后 2 秒 × 3 次探活（≤6 秒），
拿到了就继续用，拿不到仍按手册降级到 Piper —— 但用户可以在服务起来后点"重启语音服务"切回。

另删掉一处悬空 XML 注释（没有挂在任何成员上，是 CS1587 的来源）。

### 30.5 ContextManager 轮/条语义

原来 `MaxRecentRounds` 被当成"条数"用：配置 N 轮实际只保留 N 条消息 = N/2 轮（一轮 = 用户 + 回复两条）。
改为 `MaxRecentMessages => Math.Max(2, MaxRecentRounds * 2)`，压缩阈值改按条数比较，
日志改为 `（X 条 > Y 条 = Z 轮）`；`AddMessage` 的硬上限同步成 `Math.Max(64, MaxRecentMessages * 2)`。

摘要栈原来无上限（长跑进程会一直长）：新增 `MaxSummaryEntries = 200` + `TrimSummaryStackLocked()`
（丢最旧、记 Debug），锚点摘要与压缩摘要两处追加点都过一遍。

### 30.6 ConversationPipeline 锚点时序

原顺序是"先覆盖 `_lastUserMessage` 再处理锚点指令"，于是"记住这个"把指令本身当成被记住的内容。
改为先取 `previousUserMessage` 快照、再覆盖（仍在 `UserMessageAccepted` 之前，事件顺序不变），
快照传给 `HandleAnchorCommandAsync`。依赖检查：`_lastUserMessage` 的唯一读者就是该方法；
`GenerateReplyAsync` 用的是参数 `userText`，不读该字段，所以没有其它地方依赖旧顺序。

### 30.7 LogSink 单文件上限（32 MB，滚动一代）

`RollIfTooLarge`：写盘并 Flush 之后检查长度 → 超限则 Flush+Dispose writer → `File.Move(x.log, x.log.1, overwrite: true)`
→ 重开当日文件 → 用统一的 `FormatLine` 往**新**文件写一条 Info 通知（滚动本身要留痕）。

关键细节是 `_rollRetryAfterLength` 闸门：文件被别的进程独占时 `File.Move` 会失败，
若每写一行都重试，就会每行都做一遍"关闭 → 改名 → 重开"，把日志变成 I/O 风暴。
现在失败后记下当时的长度，等文件再长 8 MB 才重试一次。所有 IO 异常照旧吞掉，日志本身不成为崩溃源。

按天清理、`PurgeAll`、目录大小统计没有改动。两个已知取舍登记在 §30.15。

### 30.8 MonitorServer / GameMonitor / GameListProvider

- `MonitorServer.StartAsync` 不再是 `async`（原来结尾只有一句 `await Task.CompletedTask;`）。
- `_acceptTask` 选择"保留 + 在 `Stop` 里限时等待 1 秒"，而不是删字段：接受循环必须活到 Stop，
  删字段只消除告警、不解决退出时后台线程还在跑。等待顺序是 `Cancel CTS → Listener.Stop()（打断 Accept）
  → 再 Wait(1s)`，反过来必等满超时。
- 加了等待之后引出一个**新回归**：读循环的 finally 现在必定抢在 `Stop` 的 `notify:false` 之前，
  正常退出会弹一句"游戏检测服务已断开"。新增 `volatile bool _stopping`，`HandleDisconnect` 改为
  `notify ?? !_stopping`；读循环的通用 catch 在停止期降为 Debug（此时强关 client 属预期）。
- `GameProcessScanner.GetRunningGames()` 开头调 `_list.ReloadIfChanged(force: false)` ——
  热重载分支第一次真正可达（外部编辑 `game_list.txt` 后下一次扫描即生效；是否读盘由写时间把关）。
  重载失败保留上一次的列表、只 Warn、不抛。
- `Program.cs`：`Main` 建 `CancellationTokenSource`，`ProcessExit` 里 `cts.Cancel()`，
  `RunAsync(..., cts.Token)`。原 `CancellationToken.None` 让所有取消分支是死代码；
  现在取消能立刻打断 `Task.Delay`（空闲间隔 30 秒，最多省 30 秒）。协议里没有 offline 消息类型，
  断连信号就是 socket 关闭：`RunAsync` 返回后 finally 的 `DisposeAsync()` 关连接，主程序读循环 EOF。
  退出时**刻意不发 restore**（避免与主程序退出流程抢状态回滚）。
- `TickAsync` 每个检测周期调一次 `_cooldown.PruneExpired()`：冷却字典只在"被查询"时惰性删除，
  设过一次就再没被查过的进程名会一直留着（长驻进程的慢泄漏）。同时把 `GameCooldownTracker` 里
  重复的两份清理实现合并成一份（`PruneExpiredLocked` 统一返回清理条数）。

### 30.9 AppHost / App 生命周期

- 删 `_dispatcher`（只赋值不读；UI 封送都在 `BubbleNotifier` / `AnimationManager` 内部）。
  构造参数保留以免改所有调用方，加注释 + `_ = dispatcher;` 说明。
- 两处拉起外部服务改用 `using var process = Process.Start(...)`，并新增 `IsProcessRunning` 去重：
  GameMonitor 按进程名；Chatterbox 改按**健康状态**判断（`cmd /c <任意命令>` 的真实进程可能是
  python/node/xxx.exe，按进程名会把用户自己的 python 误当成服务而跳过真正需要的启动）。
- 待审核提醒的 fire-and-forget 任务改为 `_backgroundCts` + `Task? _pendingNotifyTask` 跟踪；
  `DisposeAsync` 一开始就 Cancel + `Wait(2s)`（超时 Warn），**先于**所有组件拆解与最后的 `LogSink.Close()`。
- `BubbleNotifier` 新增 `SetLogger(IAppLogger)`，`AppHost` 在两个日志分支里改绑：
  否则切换 `app_log_dir` 之后，任何一条气泡日志都会把引导目录的文件重新打开（分流 + 多一个句柄）。
- `App.OnExit`：`_host.DisposeAsync().AsTask()` + `Wait(10s)`，超时不再静默（`Debug.WriteLine`，
  刻意不写日志文件 —— 此刻 sink 很可能已关，写进去正好是上面要避免的分流）。
- 兜底托盘：`_host` / `_mainWindow` / `_bubbles` 三者都非 null 才建 `TrayIcon`，否则 `Shutdown(1)`。
  选择"不创建"而不是安全占位：`TrayIcon` 对这三个对象全无空校验，占位只能把 NRE 藏起来；
  没有主窗口就没有可托管界面，直接退出比留一个点了就崩的托盘更合理。

### 30.10 闲置表情 `ui.idle_expression_seconds`

`ExpressionHoldSeconds` 是"每次表情播完固定等几秒就退"，配置成 0 就永不回退；`IdleExpressionSeconds`
是"最后一次交互之后 N 秒，无论表情何时贴上的一律退"。本轮把后者从死配置接成真规则：

- `NoteUserInteraction()` 记录时刻，调用点：表情变化、发送消息、左键按下（点击/拖动起点）、右键开功能栏；
- `CheckIdleExpression()` 挂在已有的 `_hoverTimer` tick（100 ms）顶部，**先于**透传模式判断，
  所以关掉悬停切换也生效；不新增定时器；
- `0` 表示关闭；当前没有表情时直接返回。

`AppConfig` 与 `config.default.yaml` 的注释同步改成"闲置兜底"语义（原来写的是"待机时每隔多少秒播放一次随机表情"，
与实际行为不符）。

### 30.11 KnowledgeImporter 接线（390 行从零消费者变真入口）

设置面板"待审核知识"页新增"从文件夹导入角色包"按钮：

- `OpenFolderDialog` 选目录（导入器的参数是**角色包目录**，内含 `personality.json`），
  默认从知识库目录开始找；
- `new KnowledgeImporter(_host.Config, _host.Logger, _bubbles, _host.Knowledge)`
  → `ImportCharacterPackAsync(dir, ImportStrategy.Append)`；
- `Append` 是唯一"只新增、绝不改动已有条目"的策略（`Merge` 会按 Id/触发器改写，`Overwrite` 会备份后替换整库）；
- `Warnings` 逐条 Warn，成功刷新候选列表并提示条数，失败显示 `Error`，异常记 `Error(ex)`。

### 30.12 本轮删除的死代码（27 项）

| # | 位置 | 判据 |
| --- | --- | --- |
| 1 | `AppPaths.EnsureDirectory` | 全仓 1 处（声明本身） |
| 2 | `AppearanceStore.Model.SavedAt`（+ 唯一写入） | 只写不读 |
| 3 | `AnimationRequest.CreatedUtc` | 只有初始化赋值 |
| 4 | `KeyCombo.Modules` | 0 引用 |
| 5 | `KeyCombo.IsValid` | 0 引用（`ConfigLoader` 直接调 `TryParse`） |
| 6 | `IdleTaskScheduler.IsTaskRunning` | 0 读取（内部仍用 `_taskRunning`） |
| 7 | `PiperEngine._config` | 只写不读（9 处 `_config` 匹配其实是 `_configuredModelPath`） |
| 8 | `PiperEngine._bubble` + 构造参数 | 未使用，3 个调用点同步 |
| 9 | `PiperEngine.NativeLibraryStatus` / `NativeLibraryMessage` | 公开属性 0 读取；后者降为私有字段（错误文案仍在用） |
| 10 | `PiperEngine.ConfiguredModelPath` | 0 引用 |
| 11 | `TtsSynthesisResult.UsedVoice` | 只写不读 → 位置参数与 `Fail` 的 voice 参数一并去掉 |
| 12 | `AppConfig.FillWarnings`（+ `ConfigLoader` 赋值） | 只写不读，兜底信息仍由 `ConfigLoadResult.Warnings` 返回 |
| 13 | `IHttpTtsEngine.BaseUrl` | 接口成员 0 消费 → 删；`HttpTtsEngineBase` 的降 private；`ChatterboxEngine` 的实现删掉 |
| 14 | `LlamaClient.BaseUrl` | 0 引用（内部走 `_baseUrl`） |
| 15 | `TrayIcon._hostReady` | 只写不读（含 `_ = _hostReady;`） |
| 16 | `TrayIcon` 的 Error 闪烁分支 | 赋的是同一个 `_icon` 实例，无视觉变化 |
| 17 | `AnimationManager.Current` | 0 读取 |
| 18 | `AnimationManager.ApplyProfile` | 0 调用（换模型能力不存在，见 §30.15） |
| 19 | `AnimationManager.Request(AnimationRequest)` | 0 调用 |
| 20 | `Live2DRenderer._startedUtc` | 只写不读 |
| 21 | `MonitorServer` 的 `await Task.CompletedTask;` | 空操作 |
| 22 | `AppHost._dispatcher` | 只赋值不读 |
| 23 | `MainWindow._expressionTimer.Interval` 初始化 | 死赋值 |
| 24 | `SettingsWindow` 3 处恒假 null 守卫 | `InitializeComponent()` 之后恒非 null |
| 25 | `GameCooldownTracker` 重复的清理实现 | 两套等价逻辑合并成 `PruneExpiredLocked` |
| 26 | `ContextManager.GetAnchorMemories()` | 0 调用（`AnchorMemory` 属性本身有用，这个包装是多余的） |
| 27 | `ContextManager.RemoveAnchorMemory(DateTime)` | 0 调用，也没有任何 UI 能删单条锚点 |

每个名字都按词边界全仓计数（`src` + `tools/probes`，排除 `obj/bin`），确认只剩声明本身才动。
`tools/probes` 对以上成员 0 引用，已逐名核对。

### 30.13 接线而非删除（5 项）

| 位置 | 之前 | 现在 |
| --- | --- | --- |
| `ServiceHealthSnapshot.Describe()` | 只写日志，界面看不到 | 设置面板"服务健康"列表读 `Health.Snapshots` |
| `BubbleNotifier.Statuses` | 写入后没人读 | 设置面板"状态栏标记"列表（`tts-engine` 等） |
| `GameCooldownTracker.PruneExpired()` | 0 调用 | `TickAsync` 每周期调用 |
| `BubbleNotifier.SetLogger` | 不存在（日志器绑死在引导 sink） | `AppHost` 两个日志分支改绑 |
| `GameProcessScanner.ReloadIfChanged` | 分支不可达 | `GetRunningGames()` 开头调用 |

### 30.14 复核后**保留**的（新增证据）

| 位置 | 为什么保留 |
| --- | --- |
| `MonitorMessage.TimestampUtc` | 只有一个构造点，但它是 JSON 线格式字段（`System.Text.Json` 序列化所有公开属性），删了会改变协议 |
| `PendingReviewService.DisplayText` | 源码里 0 引用，但 `CandidateList` 用 `DisplayMemberPath="DisplayText"` 走反射绑定；grep 看不见这类使用 |
| `MonitorServer._acceptTask` / `_readTask` | 前者在 `Stop` 里限时等待，后者在 `AcceptLoop` 内 `await`，都属真实使用 |
| `PiperEngine._nativeLibraryMessage` | 公开属性已删，但"为什么不可用"的错误文案要用它，降为私有字段 |
| `HttpTtsEngineBase` 软重启时重建 `HttpClient` | 与在飞请求之间确实存在窗口，但异常会被上层 catch 并降级；加锁会把合成路径串行化，代价大于收益 |
| `WhisperCppRecognizer` 不 `NativeLibrary.Free` | 同 §29.8：实测 `0xC0000005` |

### 30.15 仍未做 / 新登记

1. **换模型能力本身未实现**：没有"重载/切换 Live2D 模型"的入口（`AppHost` 与设置面板都没有），
   模型路径只在 `OnHostReady` 读一次。所以 `AnimationManager.ApplyProfile` 是被删掉而不是接线；
   将来若要支持换模型，需要在重建 `Live2DRenderer` 之后重建 `AnimationManager`（或恢复一个 `ApplyProfile` 并接上）。
2. **知识导入会出现两条气泡**：导入器内部自己弹 `import-done` / `import-rejected`，设置面板再补一条。
   功能上无害，未改（改要动导入器的提示职责划分）。
3. **`GameListProvider` 重载失败会每轮 Warn**（约 12 条/分钟）：换来"文件被临时占用后自愈"。
   若嫌吵，可在 catch 里也更新 `_lastWriteUtc`，本轮未改。
4. **`.log.1` 是否会被按天清理的 `"*.log"` 枚举命中**：取决于 Win32 对 3 字符扩展名的兼容匹配，
   按"不动现有清理逻辑"的要求未验证、未改。
5. **滚动通知那条 Info 不按 `minimum_level` 过滤**：需求是"滚动本身必须留痕"，故保留。
6. **`AppHost` 构造参数 `dispatcher` 保留**（用 `_ = dispatcher;` 标注）：删参数要改所有调用方，收益为零。
7. **`Live2DRenderer.cs` 的 2 行 CRLF 被归一到 LF**：见 §30.1 末尾。
8. **用户的 `config.yaml` 不改**（准则 9）：`idle_expression_seconds` 的新语义只写在模板
   `config.default.yaml` 的注释里，用户现有文件里的键值与行为仍然一致（20 秒）。

### 30.16 最终状态

- `dotnet build AICompanion.sln -c Debug` → **0 Warning / 0 Error**（本轮每批改动后都跑，最后一次在全部改动之后）。
- 改动 **34 个文件**（33 个代码 + 本文档），相对第二轮提交 `9b6abb4`：`+1663 / -530`；忽略行尾后 `+1437 / -304`。
- 行尾：代码文件里只有 `Live2DRenderer.cs` 的 2 行 CRLF 归一为 LF（详见 §30.1）；本文档原有 224 行 LF-only，
  追加 §30 时统一成 CRLF。逐文件用 `--ignore-cr-at-eol` 对比过，除上述 2 行外没有行尾噪声。
- 全部为静态代码修改 + 文档，**没有跑任何模型推理、没有运行程序**（遵守"低负载"要求）。
- 没有改 `config.yaml`（准则 9），没有关机。

## 31. 第四轮：真机运行验证（2026-10-23 白天）

第三轮结束时我明确记了"没有运行程序（遵守低负载要求）"（§30.16）。用户随后说"继续啊，已经第二天了，
正常完成"，低负载限制解除，于是这一轮改成**真跑**：启动 `AICompanion.App`、看日志、走退出，
把前三轮靠读代码得出的结论逐条对现实核验。结果抓到一个**每次退出必崩**的真问题（§31.3），
修掉并复验通过（§31.5）。

### 31.1 跑之前先核对前置条件

- `src\AICompanion.App\bin\Debug\net8.0-windows\AICompanion.exe` 存在（10-23 08:19 构建）。
- `config.yaml` 里 `llm.server_executable_path=bin/llama-server.exe`、模型
  `models/llm/qwen2.5-1.5b-instruct-q4_k_m.gguf`（1.04 GB）、`asr.model_path=models/asr/ggml-base.bin`
  （141 MB）、`bin/ai_asr.dll` + `bin/libfvad.dll`、`Hiyori/Hiyori.model3.json` 均存在。
- **`models/piper/` 是空目录**：没有 `model.onnx`，也没有 `bin/piper.exe`。所以 TTS 必然降级 SAPI，
  这是环境事实，不是代码缺陷（见 §31.9）。
- 启动方式：`Start-Process -WorkingDirectory E:\DEEPSEEK`。`ConfigLoader.ResolveConfigPath` 先看
  当前目录再看 `AppContext.BaseDirectory`，`AppPaths.Root` 由 config.yaml 所在目录决定
  （`AppPaths.Configure`），因此在根目录起就能正确解析所有相对路径。

### 31.2 启动链路实测（10:33:51 → 10:33:59，8.4 秒到"启动完成"）

日志 `logs/app/app-20261023.log` 原文摘录（时间戳保留）：

```
10:33:53.286 [INF] [Bootstrap] 配置已加载：E:\DEEPSEEK\config.yaml（兜底修正 0 项，自动生成=False，解析错误=无）
10:33:53.338 [INF] [Knowledge] 知识库已就绪：E:\DEEPSEEK\Knowledge，条目数 0。
10:33:53.347 [INF] [TTS.Piper] 未找到 Piper 动态库：...\third_party\piper\build\piper.dll 将使用进程外 piper.exe 兜底。
10:33:53.532 [INF] [Gpu] 从 GGUF 探测到模型层数 L=28。
10:33:53.534 [INF] [Gpu] GPU 计划：mode=Gpu, ngl=8, free=6565MB, L=28, conservative=1,300MB | ...
10:33:56.656 [INF] [LlamaServer] llama-server 已就绪（ngl=8）。        ← 3.1 秒
10:33:56.658 [WRN] [TTS] Piper 不可用，降级为 Windows 内置语音（SAPI）。   ← 全程唯一 WRN
10:33:56.839 [INF] [TTS] TTS 初始化完成：配置引擎=piper，实际引擎=sapi，降级=True。
10:33:57.049 [INF] [ASR] whisper 模型已加载：models\asr\ggml-base.bin（线程 0，CPU 模式）
10:33:57.069 [INF] [VAD] 使用 WebRTC VAD（libfvad）：bin\libfvad.dll
10:33:57.070 [INF] [PTT] 按住说话监听已启动：按住 Home 录音，松开识别。
10:33:57.074 [INF] [Health] 健康监控已启动（间隔 30 秒，离线阈值 2 次）。
10:33:57.079 [INF] [GameMonitor] GameMonitor 通信服务已监听 127.0.0.1:47777。
10:33:57.079 [INF] [Idle] 空闲调度器已启动（轮询 500 ms，空闲阈值 5 分钟，CPU 上限 50%）。
10:33:57.080 [INF] [App] 启动完成：运行模式=GPU 模式（ngl=8/28，空闲显存 6565 MB），LLM 就绪=True，TTS=sapi（降级=True），ASR=Stopped，知识库 0 条，启动问题 0 项。
10:33:58.812 [INF] [App] Live2D 渲染器已就绪：Hiyori，动作 10 个，参数 70 个，口型=ParamMouthOpenY(index=19)
10:33:58.926 [INF] [Live2D] OpenGL 环境：Vendor=NVIDIA Corporation，Renderer=RTX 5060 Laptop GPU/PCIe/SSE2，Version=3.3.0 NVIDIA 617.42
10:33:58.963 [INF] [Live2D] 已为 2 张模型贴图生成 mipmap 并启用三线性过滤（改善降采样清晰度）。
10:33:58.997 [INF] [App] 显示环境：DPI 150%（1.50×），主屏 1707×1067 DIP（物理约 2560×1600 px）
10:33:59.050 [INF] [App] 角色大小设为 104%，窗口 353×479 DIP（未持久化）
```

对照第三轮改动的结论，全部成立：配置零兜底、GPU 规划与 `ngl` 计算一致、TTS 降级有气泡、
ASR/VAD/PTT 三件套就绪、Live2D 参数树与口型索引正确、DPI 150% 下窗口尺寸合理。
`llama-server` 子进程命令行与日志一致（`-ngl 8 -c 4096`），`/health` 返回 HTTP 200 `{"status":"ok"}`。

### 31.3 发现（真问题）：**每次关窗都崩，并且把 llama-server 留成孤儿**

第一轮测试我用 `Process.CloseMainWindow()` 关窗，返回 `False`；`taskkill /PID` 也没退。原因是
`App.xaml:4` 的 `ShutdownMode="OnExplicitShutdown"`——关窗**本来就不退出**，真正的退出只在托盘
"退出"→`ExitApplication()`→`Application.Current.Shutdown()`。所以改用
`EnumWindows` + `PostMessage(WM_CLOSE)` 精确投递到 `HwndWrapper[AICompanion...]` 窗口再观察。

进程确实"没了"，但**不是正常退出**，事件日志里两条硬证据：

| 尝试 | 最后一次日志 | 系统事件 | 现场 |
| --- | --- | --- | --- |
| A：按原顺序 `model → lapp → control` 释放 | `10:38:35 [Live2D] model 已释放` | `10:38:35 Application Error`：`ntdll.dll`，异常码 **0xC0000374**（STATUS_HEAP_CORRUPTION） | 进程被内核杀掉，`llama-server` PID 5092 仍在跑，占着显存 |
| B：改成只 `lapp → control` | `10:41:00 [Live2D] lapp 已释放` | `10:41:01 Application Error`：**`nvoglv64.dll`**（NVIDIA OpenGL 驱动），异常码 **0xC0000005**，偏移 `0xa3c36c`；`.NET Runtime`：`The process was terminated due to an unhandled exception ... c0000005` | 同上，`llama-server` 变孤儿 |

两条推论：

1. **A 的崩因不是"顺序不对"**，而是 `LAppModel` 归 `LAppDelegate` 的 `Live2dManager` 所有，
   单独 `model.Dispose()` 再让 lapp 释放一遍 = 同一批渲染器/贴图二次释放（堆破坏）。
2. **B 的崩因更根本**：`lapp.Dispose()`（删贴图）与 `control.Dispose()`（毁上下文）都要求
   **GL 上下文在调用线程上为当前上下文**，而 GL 上下文只活在 `GLWpfControl` 自己的渲染线程上。
   在 WPF UI 线程（`OnClosed` 就是在 UI 线程）调这些原生释放，等于拿一个属于别的线程的上下文
   去 `glDeleteTextures`，驱动层直接访问违例——`nvoglv64.dll` 的崩溃地址说明得很清楚。

连带后果比"崩一下"严重：`OnClosed` 崩掉后 `App.OnExit` 根本轮不到执行，
`AppHost.DisposeAsync` 里"正在关闭 → 终止 llama-server"整段被跳过，于是每次退出都留下一个
**占 1.4 GB 显存的孤儿 `llama-server`**，而日志里连一句"退出"都没有（`OnExit` 里只有超时才写
`Debug.WriteLine`，这也是当时没察觉的原因之一）。

> 这一条推翻了我第三轮的两处记录：§29.9 第 1 条把"`model → lapp → control` 三步各自 try"记为
> 正确释放顺序、§30.13 据此把它保留下来。实测两种顺序都会崩，**问题不在顺序与 try 粒度，
> 而在"在错误的线程上释放原生对象"**。下面 §31.4 的改法取代该结论。

### 31.4 修复：关窗只"停止使用"，不在 UI 线程释放原生对象

`Animation/Live2DRenderer.cs`：

- 新增 `private void StopRenderLoop()`：退订 `CompositionTarget.Rendering`、`_control.SizeChanged`、
  `_control.Render`，置 `_rendering = false`。这是**唯一**在关窗时做的清理。
- `Dispose()` 改为调用 `StopRenderLoop()`，然后记一行
  `Live2D 渲染已停止（原生上下文交由进程退出回收）。`；不再碰 `_lapp` / `_control` / `_model`。
- 删除 `ReleaseNativeResources(...)`（原先三步释放的辅助方法）——留着就是留一个随时会被再次调用的坑。
- `TryCreate` 的失败路径同样不再释放：改为记一条
  `Live2D 创建失败，原生上下文交由进程退出回收（不在 UI 线程释放，避免驱动崩溃）。`
  这是**明知取舍**：失败路径会漏一次 GL 上下文到进程退出，但换来"绝不因清理而崩"。
  该路径只有模型文件损坏/加载异常才会走到。

`MainWindow.xaml.cs` 的 `OnClosed`：顺序改为
退订外部事件 → 停三个定时器 → `_animations.Dispose()` → `_live2d.Dispose()` + `_live2d = null`
→ **把控件从可视树上摘下来**（`Live2DHost.Child = null; Live2DHost.Visibility = Collapsed;`）
→ `_lipSync.Dispose()` → 关两个子窗口 → `base.OnClosed(e)`。

摘控件这一步是必要的：渲染循环已停，若控件还挂在可视树上，窗口销毁过程中 WPF 还会再量一次、
画一次它，等于拿一个没有当前上下文的控件走合成管线。

### 31.5 复验：干净退出（无崩溃、无孤儿）

托盘"退出"要鼠标点，没法在脚本里点（`NotifyIcon` 的菜单是 WinForms 自绘，UIA 拿不到）。
于是临时在 `App.OnStartup` 末尾加了一个 5 行的验证钩子：读环境变量
`AICOMPANION_EXIT_AFTER_SECONDS`，到点调用 `Shutdown()`——与托盘"退出"**完全同一条路径**。
验证完已删除（`grep` 确认无残留，重新构建 0/0）。实测日志：

```
10:46:57.387 [INF] [App] [退出诊断] 测试钩子生效：25 秒后调用 Shutdown()。
10:47:22.399 [INF] [Live2D] Live2D 渲染已停止（原生上下文交由进程退出回收）。
10:47:22.412 [INF] [App] 正在关闭 AI 伴侣…
10:47:22.588 [INF] [LlamaServer] 正在终止 llama-server（PID=7496）...
10:47:22.625 [INF] [App] 已退出。
```

退出后核对：`AICompanion` 进程消失、`llama-server` 进程消失、Windows 事件日志**没有任何**
`Application Error` / `.NET Runtime` 错误级条目。整段退出 0.23 秒。

再用**去掉钩子的生产版**复跑一次：20 秒到"启动完成"、`WM_CLOSE` 关窗后进程存活（`OnExplicitShutdown`
的预期行为）、日志停在 `Live2D 渲染已停止`、事件日志无崩溃记录。测试实例随后手工清理。

### 31.6 顺手清理：`Knowledge\.pending` 里 403 条测试候选

启动气泡报"有 403 条新记忆待确认"，查 `Knowledge\.pending`：404 个文件
（403 个 `candidate_*.json` + 1 个 `.notify_state.json`），时间集中在 10-19（1 个）与 10-21（402 个），
`source` 全是 `Candidate`，66 种 `replyTemplate` 全是"作为一个AI助手…""好的，有什么可以帮你的吗？"
这类兜底回复——是之前自动化测试聊天留下的产物，与第一轮"删除测试数据"的要求一致。

处理：403 个候选打包到 `artifacts\pending-candidates-backup-20261023.zip`（229 KB，可随时回滚），
原文件删除；保留 `.notify_state.json`（只是通知时间去重状态，删不删都会自动重建）。
删除后 `.pending` 只剩 1 个状态文件，下次启动不会再报 403 条。`artifacts/`、`logs/`、
`Knowledge/.pending/` 都在 `.gitignore` 里，不污染仓库。

### 31.7 实测中另外几条观察（不是缺陷，但值得记）

1. **`logs/app/*.log` 用 `FileShare.ReadWrite` 打开**，所以排查时必须同样以读写共享方式读
   （`[System.IO.File]::Open($p,'Open','Read','ReadWrite')`）。用 `File.ReadAllLines` 会报
   "文件正由另一进程使用"——这是共享模式互斥，不是日志写坏了。
2. **`Assets\appearance.json` 里还留着 `savedAt` 键**（10-21 写下的 179 B 文件）。当前
   `AppearanceStore` 已经不再读写该字段，反序列化忽略未知键，下次用户调整外观时会被自然覆盖掉；
   不对它做写操作（准则 9 只管 config.yaml，但没必要为一行陈旧键去改用户文件）。
3. **`ngl=8/28` 不是缺陷**：`gpu.reserved_mb=4096` + `kv_cache_reserved_mb=1024` 的保守策略
   在 6565 MB 空闲显存下算出 1300 MB 可用、8 层。想要更快可以调小 `reserved_mb`，但那是用户的
   配置选择（不改 config.yaml）。
4. **`Process.MainWindowHandle` 对这个窗口恒为 0**：`ShowInTaskbar=false` 让窗口带
   `WS_EX_TOOLWINDOW`，.NET 枚举主窗口时会跳过它。自动化测试要用 `EnumWindows` 自己找。

### 31.8 第三轮记录的两处修正

- §29.9 第 1 条 / §30.13：Live2D 释放顺序与 try 粒度的结论作废，见 §31.3–§31.4。
- §30.16 "没有运行程序"：本轮已真跑，并据此修掉退出崩溃。

### 31.9 遗留（诚实清单）

- `models/piper/` 是空目录，TTS 永远降级到 SAPI。要用 Piper 得放 `model.onnx` + 语音文件；
  当前 SAPI 能说话，所以功能不残缺，只是音质一般。
- 关窗（非退出）后经托盘"显示/隐藏角色"重开窗口，**Live2D 不会恢复**（`_live2d` 已置空）。
  正常用户路径是托盘退出/重开程序，不受影响；要支持"重开窗口还原角色"得给渲染器加重启能力，
  属于新功能，不在本轮范围。
- `TryCreate` 失败路径漏一次 GL 上下文（§31.4 的取舍），直到进程退出。
- 本轮**没有改 `config.yaml`**（准则 9），**没有关机**。

### 31.10 本轮最终状态

- `dotnet build AICompanion.sln -c Debug` → **0 Warning / 0 Error**。
- 改动：`Animation/Live2DRenderer.cs`（`StopRenderLoop` 取代三步释放、失败路径改为不释放）、
  `MainWindow.xaml.cs`（`OnClosed` 摘控件、`_live2d`/`_lipSync` 置空）、`App.xaml.cs`
  （临时钩子加入后删除，净改动为 0 行）、本文档。
- 运行证据：启动 8.4 秒到"启动完成"、退出 0.23 秒、两次崩溃已消除、事件日志干净、
  `llama-server` 不再残留。

### 31.11 补一条 LLM 实跑（同一个 llama-server 进程）

启动应用、等它自己拉起 `llama-server`，直接对 `127.0.0.1:8080/v1/chat/completions` 发一次短请求：

```
POST /v1/chat/completions  {"messages":[{"role":"user","content":"用一句话介绍你自己。"}],"max_tokens":16,"temperature":0.2}
→ 0.7 秒返回 16 token（约 23 tok/s），usage prompt=34 / completion=16
→ 内容："我是阿里云的超大规模语言模型，我叫通义千问。"
```

这证明"GPU 规划 → 起服务 → 真出词"整条链路是通的（这一次 GPU 规划给了 `-ngl 5`，
空闲显存比 §31.2 那次低，说明 `ngl` 跟随实时显存变化，符合 `GpuPlan` 的设计）。
1.5B Q4_K_M 在 CPU/GPU 混合下 23 tok/s，交互可接受。

**没做到的**：本机没有可用截图能力（当前模型不接受图像输入），所以"发一条消息 → 气泡显示 →
口型同步 → SAPI 出声"这条**纯视觉/听觉**链路没有用人眼确认过；托盘"退出"菜单也没有真的点过，
退出验证走的是与它完全相同的 `Shutdown()` 路径（§31.5）。这两点属于验证手段的限制，
不是没验证就宣称通过。

## 32. 0.9.1 打包（2026-10-23 中午）

第四节修完退出崩溃后，`dist\AICompanion-0.9.0-full-setup.exe` 就成了"装上去会崩"的旧包。
按用户指示：**先备份旧包，再重打，版本号提到 0.9.1**（推送远端的事按用户回复先不做）。

### 32.1 版本号从 0.9.0 提到 0.9.1

| 位置 | 改动 |
| --- | --- |
| `AICompanion.App.csproj` | `Version`、`AssemblyVersion`、`FileVersion` → `0.9.1` / `0.9.1.0` |
| `installer\AICompanion.iss` | 注释里的 `/DMyVersion=0.9.1`、`#define MyVersion`、兜底 `StageDir` |
| `installer\install.ps1` | 卸载信息里读不到 `VERSION.txt` 时的兜底 `$version = '0.9.1'` |
| `tools\verify_dist.ps1` | 用法示例里的包目录名 |
| `README.md` | §10.2 产物清单、§10.6 的 exe 版本号 |

`pack.ps1` 的版本是从 csproj 现读的（`<Version>`），所以只要 csproj 改了，分发目录名、zip 名、
安装包名、`VERSION.txt` 全都会跟着变——这也是为什么只改一处 `Version` 就够。

### 32.2 打包过程（`tools\pack.ps1`，默认参数）

```
版本：0.9.1   自带运行时：True   ReadyToRun：True   含模型：True   含授权素材：False
+ Assets\ (8.7 MB)                  + bin\ (18 个原生库, 101.2 MB)
+ CUDA 运行库 493 MB                来自 CUDA v13.3 的 cublas64_13 / cublasLt64_13 / cudart64_13
+ models\llm (1.04 GB)              + models\asr (141.1 MB)     + models\tts (0.3 MB)
- models\piper 是空目录，跳过        - Hiyori\ / character.png / Live2DCubismCore.dll 未打包（授权素材）
    精简语言资源省 13.3 MB，排除 3 个 .pdb 省 0.2 MB
    分发目录体积：1.90 GB
    ISCC 编译：Successful compile (233.4 秒)
```

产物：

```
dist\AICompanion-0.9.1-win-x64\          1.90 GB（可直接运行的分发目录）
dist\AICompanion-0.9.1-full-setup.exe    1621.4 MB（安装包）
```

exe 版本信息核对：`FileVersion=0.9.1.0`，`ProductVersion=0.9.1+c87e9ee0a757...`（提交号自动进版本串）。
`VERSION.txt` 内容首行 `AICompanion 0.9.1`，构建时间 `2026-10-23 11:42:22`。

旧包没有删，移到了 `artifacts\dist-backup-0.9.0\AICompanion-0.9.0-full-setup.exe`（1621 MB），
要回退直接搬回来即可。

### 32.3 异目录启动验证（`tools\verify_dist.ps1`）

把分发目录整份复制到 `%TEMP%`，用**另一个工作目录**启动，跑 45 秒后检查：

```
[OK] 必需文件 7/7（exe / dll / Core.dll / GameMonitor.exe / config.yaml / bin\llama-server.exe / bin\ai_asr.dll）
[OK] 未包含需单独授权的素材（Live2DCubismCore.dll / Hiyori / character.png / Assets\source 都不在包里）
[OK] 含 素材说明.md
[OK] 进程存活，内存 359.9 MB，句柄 766
[OK] 主窗口已出现
[OK] 日志：...\app\logs\app\app-20261023.log   错误行：0
     11:47:54 启动完成：运行模式=GPU 模式（ngl=10/28，空闲显存 6811 MB），LLM 就绪=True，TTS=sapi（降级=True）
[OK] 清理：已停 1 个 llama-server 子进程，删除临时目录
=== 验证通过 ===
```

这一次顺带证明了"包是自洽的"：缺 Live2D 素材时按设计降级（日志里没有 ERR），
GPU 规划在干净环境里照样算到 `ngl=10/28`，退出时子进程被清掉。

### 32.4 途中发现并修掉的一个真缺陷：**PS 脚本丢了 UTF-8 BOM**

第一次跑 `verify_dist.ps1` 直接语法报错：

```
+     Write-Host "    宸叉竻鐞?$($strays.Count) 涓?llama-server 瀛愯繘绋?
The string is missing the terminator: ".
```

原因不是脚本内容坏了，是**文件编码**：Windows PowerShell 5.1 用 `-File` 执行脚本时按
"有没有 BOM"判断编码——有 BOM 当 UTF-8，没有就按系统 ANSI（本机 GBK）解。`verify_dist.ps1`
被文本编辑器改过一次后 BOM 丢了，中文注释被当 GBK 解成乱码，连引号都配不上，整份脚本解析失败。

同一批检查还发现 `installer\install.ps1` 也丢了 BOM（它的中文提示会同样乱码）。
两个文件都已补回 BOM，并用 PS 5.1 的语法解析器逐个验证 `tools\*.ps1` 与 `installer\*.ps1`：
**8 个脚本，语法错误 0**。

> 教训记在这里：本项目的 `.ps1` 必须存成 **UTF-8 with BOM**（`pack.ps1`/`soak_test.ps1`/
> `ptt_smoke.ps1`/`make_*.ps1`/`start-cosyvoice.ps1` 本来就有 BOM）。`.bat` 与 `.cs` 没有这个要求
> （`.bat` 里全 ASCII，C# 由 Roslyn 按 UTF-8 读）。

### 32.5 当前分发状态

- 本地 `dist\` 只有 0.9.1 两个产物；0.9.0 的安装包在 `artifacts\dist-backup-0.9.0\` 备份。
- `dist/`、`artifacts/` 都在 `.gitignore` 里，不进版本库。
- 远端未推送（用户选择先不推）：本地 `main` 领先 `origin/main` 18 个提交。
- `artifacts\repo-full-history.bundle` 已重打到 `main@c87e9ee`（bundle verify 通过）。

### 32.6 这次改动带来的行尾归一（记录，不掩盖）

`git diff` 里 `csproj` 与 `.iss` 会显示十几行"改动"，其实多数是行尾被归一，用
`git diff --ignore-cr-at-eol` 看只剩版本号那几行：

| 文件 | 改前 | 改后 | 忽略行尾后的真实改动 |
| --- | --- | --- | --- |
| `AICompanion.App.csproj` | 56 CRLF + 10 LF（混排） | 66 CRLF | 3 行（三个版本号） |
| `installer\AICompanion.iss` | 139 LF + 4 CRLF（混排） | 143 LF | 3 行（`MyVersion`、`StageDir`、注释） |
| `installer\install.ps1` | 194 LF | 194 LF（未变） | 1 行（兜底版本号，另补回 BOM） |
| `tools\verify_dist.ps1` | 246 CRLF | 246 CRLF（未变） | 1 行（用法示例，另补回 BOM） |

两个混排文件各自被统一成"占多数的那种"，算是顺手清掉了行尾噪声；`.ps1` 的 BOM 与行尾是两件事，
BOM 决定 PS 5.1 能不能解析（§32.4），行尾只影响 diff 观感。