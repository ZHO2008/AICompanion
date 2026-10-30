# 更新记录

版本号来自 `src/AICompanion.App/AICompanion.App.csproj` 的 `<Version>`，
打包脚本 `tools/pack.ps1` 会把它写进分发目录名、安装包名与 `VERSION.txt`。

---

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
