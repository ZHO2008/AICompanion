# 更新记录

版本号来自 `src/AICompanion.App/AICompanion.App.csproj` 的 `<Version>`，
打包脚本 `tools/pack.ps1` 会把它写进分发目录名、安装包名与 `VERSION.txt`。

---

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

---

## 0.9.0 — 2026-10-21

首个公开基线（源码 + 双许可），包含：

- 对话管线（预处理 → LLM → TTS）、上下文滑动窗口与三级摘要、知识库热重载与待审核候选
- 按住说话（Home 键）、WebRTC VAD、whisper.cpp 桥接（`bin/ai_asr.dll`）
- 三条 TTS 后端：Piper / GPT-SoVITS / CosyVoice3，另有 Windows SAPI 兜底
- Live2D 真渲染（Cubism SDK，动作/口型/表情联动）、静态立绘与纯色块三级降级
- GameMonitor 游戏感知与显存让路、OOM 回退与 `ngl` 自动计算
- 自包含打包（含 .NET 8 运行时、模型与 CUDA 运行库；不含需要单独授权的 Live2D 素材）