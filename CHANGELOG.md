# 更新记录

版本号来自 `src/AICompanion.App/AICompanion.App.csproj` 的 `<Version>`，
打包脚本 `tools/pack.ps1` 会把它写进分发目录名、安装包名与 `VERSION.txt`。

---

## 0.9.2 — 2026-10-29

### 修复

- **`tts.gpt_sovits` / `tts.cosyvoice` 整节配置被静默忽略**
  `AppConfig.Tts` 的属性名是 `GptSoVits` / `CosyVoice`，YamlDotNet 的 `UnderscoredNamingConvention`
  会把它们推成 `gpt_so_vits` / `cosy_voice`，与配置里写的 `gpt_sovits` / `cosyvoice` 对不上；
  反序列化器又开了 `IgnoreUnmatchedProperties`，于是整节被丢掉，运行时只看到默认值。
  表现：`config.yaml` 里明明填了权重和参考音频，引擎却报"需要参考音频"、`top_p` 落回 1.0。
  现在给这两个属性加了显式 `[YamlMember(Alias = "gpt_sovits")]` / `[YamlMember(Alias = "cosyvoice")]`。

### 变更

- 分发目录不再带运行期个人状态：`Assets/appearance.json`（窗口尺寸/缩放）、`Knowledge/.pending`（提醒状态）。
- 仓库里最后几处写死的本机绝对路径（`E:\DEEPSEEK`）清掉了：
  `tools/Fetcher`、`tools/probes/ExportProbe`、`tools/probes/OomProbe`、`tools/draw_avatar.ps1`、`tools/soak_test.ps1`
  现在统一按「环境变量 `AICOMPANION_ROOT` → 当前工作目录 → 脚本自身位置」解析，换台机器 clone 下来就能直接跑。
- README 里的目录树与日志示例改用相对写法，不再出现开发机盘符。
- `tools/pack.ps1` 改为**总是**用 `config.default.yaml` 生成分发目录里的 `config.yaml`：
  以前直接复制开发机的根目录 `config.yaml`，会把本机绝对路径（模型、参考音频、端口）打进公开安装包。


- `config.yaml` 不再入库：它含本机路径、音色、端口等个人设置，容易被误提交；
  模板仍是 `src/AICompanion.Core/Configuration/config.default.yaml`，缺文件时程序首次启动会自动生成。
- 研究脚本里写死的本机路径（`E:\cosyvoice-src\…`）改为读环境变量 `COSYVOICE_WORK`。
- README 增加"用自己训练的音色（GPT-SoVITS v2Pro）"一节：权重放置、参考音频与文本、
  必填参数、日志确认点，以及实测耗时（52 字 13.36 s、合成 5.4 s；13 字 2.8 s、合成 2.8 s）。
- 明确写入：`gpt_sovits` / `cosyvoice` 是显式别名，写成 `gpt_so_vits` 会被静默忽略。

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