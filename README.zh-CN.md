# wiisl — Why is it so laggy?

[English](README.md) · **简体中文**

[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white)](#环境要求)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](#从源码构建)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> 一个 Windows 桌面工具：在游戏运行时盯着它，然后告诉你**为什么卡**——用数字说话，而不是靠猜。

选择任意进程（或让 wiisl 自动找到你装的游戏），实时采集 CPU / GPU / 内存 / 磁盘指标，只读解析游戏自己的画质配置，最后生成 **Markdown + PDF + CSV + JSON** 四份诊断报告，结论按严重度排序。

> **安全承诺：本工具只读游戏文件，从不写入或修改任何游戏文件、配置或存档。**
> 所有优化建议都只针对系统层面（电源计划、显卡驱动设置、后台进程）或游戏内画质选项，是否采纳由你自己决定。

---

## 为什么要做这个

大多数「优化大师」让你关服务、碰运气。wiisl 反过来：先测量，然后在数据不足以支撑结论时**拒绝下结论**。传感器不可靠就如实说明，而不是给你一个看起来很自信的错误数字。见[已知限制](#已知限制实测得出不是猜测)——里面每一条都是在真实硬件上测出来的，其中好几条是 wiisl 故意「什么都不报」的情况。

## 功能

- **任意进程，或整个游戏库** — 从实时进程列表里挑 PID，或自动扫描 Steam / Epic / Battle.net / Ubisoft / GOG / WeGame / Xbox 游戏库。
- **12 项实时读数 + 曲线图** — CPU 占用、真实频率、温度、封装功耗；GPU 占用、频率、温度、功耗；进程内存；系统可用内存；磁盘；页面文件。
- **读取游戏画质配置** — 解析 INI / JSON / XML / VDF 配置文件，把阴影质量、环境光遮蔽这类设置翻译成性能影响评级。
- **排序后的结论，而不是一堆数字** — 每条结论都带证据、解释、具体建议动作和预期收益。
- **四种输出格式** — Markdown 便于阅读，PDF 便于分享，CSV/JSON 便于自己分析。
- **定时或手动采样** — 可设定时长，也可手动开始/停止；目标进程退出时会自动结束采样。
- **优雅降级** — 每个采集器都会先自检。取不到就如实报「不可用」，绝不编造。

## 环境要求

- Windows 10 或 Windows 11（x64）
- **用免安装版的话，其它什么都不需要** —— 它自带 .NET 运行时
- 只有[从源码构建](#从源码构建)时才需要 .NET 10 SDK
- 管理员权限是**可选的** —— 见[数据来源与权限](#数据来源与权限)

## 快速开始

### 方式 A — 免安装版（不用装东西，不用装 .NET）

从 [Releases](../../releases) 把两个文件下载到**同一个文件夹**，然后运行 `wiisl.exe`：

| 文件 | 说明 |
|---|---|
| `wiisl.exe` | 主程序（自包含单文件，约 78 MB，自带 .NET 10 运行时） |
| `QuestPDF.Fonts.Lato.br` | QuestPDF 的字体资源，**缺了它 PDF 就生成不出来** |

> ⚠️ **这两个文件必须放在一起，不能只拷 exe。**
> 实测只拷 exe 时，Markdown/CSV/JSON 仍正常，但 PDF 会失败并报
> `The text "..." uses font families that are not available: 'Lato'`。
> 此时 wiisl 以退出码 `5` 结束，并**删掉生成失败留下的 0 字节空文件**，不会留给你一个打不开的假 PDF。

### 方式 B — 从源码构建

```bash
git clone https://github.com/chioplet/Why-is-it-so-laggy_.git
cd Why-is-it-so-laggy_
dotnet build wiisl.slnx -c Release
```

可执行文件生成在 `src/Gpd.App/bin/Release/net10.0-windows/win-x64/wiisl.exe`。

想产出免安装的单文件版本：

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

## 图形界面

| 区域 | 作用 |
|---|---|
| 左侧「任意进程」 | 列出所有进程（有窗口的排在前面），可按名字 / 窗口标题 / PID 过滤 |
| 左侧「游戏库」 | 自动扫描 8 个平台的游戏库；选中游戏会自动关联它正在运行的进程 |
| 右侧「实时监控」 | 设置采样间隔与时长（时长留空 = 手动停止），实时看 12 项读数与曲线 |
| 右侧「诊断报告」 | 选输出目录与格式，一键生成报告，并直接列出全部诊断结论 |

## 无界面模式

```bash
wiisl.exe --cli (--pid <PID> | --process <进程名>) [选项]
```

| 选项 | 说明 |
|---|---|
| `--interval <秒>` | 采样间隔，默认 `1.0`（最小 `0.2`） |
| `--seconds <秒>` | 采集时长；不写表示一直采到目标进程退出 |
| `--out <目录>` | 报告输出目录，默认「我的文档\wiisl 报告」 |
| `--formats <列表>` | `md,pdf,csv,json` 的任意组合，默认全部 |
| `--fps` | 启用帧率模块（需要管理员权限；没有权限时自动降级） |
| `--scan-games` | 扫描游戏库并自动关联目标进程对应的游戏（含画质配置解读） |
| `--game <名字>` | 直接指定游戏名（会先扫描游戏库） |
| `--no-stop-on-exit` | 目标进程退出后不自动结束采样 |
| `--help` | 显示帮助 |

示例：

```bash
wiisl.exe --cli --process cs2 --seconds 60 --scan-games --out D:\reports
```

**退出码：** `0` 成功 ｜ `2` 参数或目标进程错 ｜ `3` 采到 0 个样本 ｜ `4` 一个文件都没写出来 ｜ `5` 部分格式生成失败。

> 程序是 WinExe（没有自己的控制台），只有在调用方重定向了 stdout 时才看得到文字输出。

## 报告内容

| 章节 | 说明 |
|---|---|
| 一、结论速览 | 瓶颈判定 + 结论表（按严重度排序）+「最该先做的一件事」 |
| 二、硬件与系统 | CPU / 内存 / 显卡 / 电源计划 / 游戏模式 / HAGS / 驱动版本 |
| 三、采样概况 | 采样点数、时长、间隔、停止原因 |
| 四、汇总指标 | CPU、GPU、内存、页面文件、磁盘、帧率的平均 / 最大 / 最小 |
| 五、时间序列 | 逐样本明细（超过 200 点时按首末保留抽样） |
| 六、诊断结论 | 每条结论的证据、解释、建议动作、预期收益 |
| 七、画质解读 | 从游戏配置文件里读出的画质项与性能影响评估 |
| 八、逐核快照 | 每个逻辑处理器的平均占用与最高温度 |
| 九、数据来源与可信度 | 每个采集器是否可用、受限原因 |

## 数据来源与权限

| 指标 | 来源 | 是否需管理员 |
|---|---|---|
| CPU 占用 / 逐核占用 | 性能计数器 `Processor Information` | 否 |
| CPU 实际频率 | 性能计数器 `Actual Frequency` | 否 |
| CPU 封装功耗 | 性能计数器 `Energy Meter → RAPL_Package0_PKG` | 否 |
| GPU 占用 / 频率 / 温度 / 功耗 / 显存 | `nvidia-smi`（CSV 通路） | 否 |
| 进程级 GPU 占用与显存 | 性能计数器 `GPU Engine` / `GPU Process Memory` | 部分受限 |
| 内存 / 提交内存 / 页面文件 / 磁盘 | 性能计数器 `Memory`、`Paging File`、`PhysicalDisk` | 否 |
| 帧率 / 帧时间 / 卡顿 | PresentMon（ETW） | **是** |
| 硬件与系统信息 | WMI + 注册表 + `powercfg` | 否 |

**帧率是可选的。** PresentMon 需要 ETW 内核会话，因此需要管理员权限。没有权限时帧率模块会自动降级为「不可用」，其余指标不受影响。想要帧率数据，用窗口右上角的「以管理员身份重新启动」按钮提权后再勾选启用帧率模块。

应用清单用的是 `asInvoker` 而不是 `requireAdministrator`：在标准用户账户上，`requireAdministrator` 会让程序**根本打不开**，而主要数据源本来也不需要提权。

## 已知限制（实测得出，不是猜测）

1. **CPU 封装温度读不到。** 开发机上 `Thermal Zone Information\_TZ.TZ0` 实测是**主板 ACPI 热区**，不是 CPU 封装温度：16 线程满载时它读 82.7 ℃，反而**低于**空闲时的 85.1 ℃。真实的 CPU 温度不可能满载下降，所以报告不会拿这个数字判断 CPU 是否过热，而是输出一条「CPU 封装温度不可用，未做 CPU 过热判断」的提示。想要准确读数需要 HWiNFO64 这类通过 MSR/DTS 读取的工具——本工具出于「不安装内核驱动」的定位不会自行安装。
   程序会自动做负载相关性判定：只有温度确实随负载上升（高负载组平均比低负载组高 ≥3 ℃，且两组各有 ≥3 个样本）时才把该热区当作 CPU 温度使用。
2. **`Processor Frequency` 计数器是静态标称基频，不能当当前频率用。** 实测它在空闲和满载时都恒定不变（`_Total` 恒 2250 MHz，P 核恒 2400 MHz，E 核恒 1800 MHz）。真正可用的是 `Actual Frequency`。
3. **虚拟显示器会干扰 GPU 计数器解读。** 开发机上存在 `GameViewer Virtual Display Adapter`，`GPU Engine` 的实例分属两个 LUID。nvidia-smi 取到的整卡数据不受影响，但按 pid 归因的进程级 GPU 计数器需要按 LUID 甄别。报告会在硬件表里标注虚拟显示器。
4. **NVIDIA 驱动 617.14 的 nvidia-smi 不支持 `--format=xml`**（会返回 exit=2 和 `Format modifier is not recognized.`），本工具走 CSV 通路；`voltage.gpu` 字段该驱动也不认，且**一个无效字段会让整条查询失败并把错误打到 stdout**，所以字段可用性是运行时探测的。该机器上 `power.limit` 和 `fan.speed` 都是 `[N/A]`。
5. **Epic / Battle.net / GOG / Xbox 的非空分支没有真机样本。** 开发机没有安装这些平台的游戏，只验证了「无游戏时给出中文说明且不抛异常」。Steam（5 个）与 WeGame（1 个）走的是真实数据。
6. **画质项的「性能影响(0-5)」是经验估值**，不是实测帧数差；报告里已注明。
7. 有 9 个 Steam 库目录残留（Aim Lab、PUBG 等）实测没有任何 .exe，属于卸载残留，报告为告警而不是伪造出游戏条目。

## 工程结构

```
wiisl.slnx
├── src/Gpd.Core/                 类库（无 UI 依赖，可单独引用）
│   ├── Contracts.cs              全部数据契约
│   ├── Collect/                  采集器：CPU / GPU / 内存 / 帧率 / 机器信息 / 采样调度
│   ├── Games/                    游戏库扫描（8 个平台）+ 配置解析（INI/JSON/XML/VDF）+ 画质解读
│   ├── Analysis/                 汇总统计 SummaryCalculator + 诊断规则 DiagnosisEngine + 自测
│   └── Reporting/                Markdown / CSV / JSON 报告写出
└── src/Gpd.App/                  WPF 应用
    ├── MainWindow.xaml(.cs)      界面与交互
    ├── CliRunner.cs              无界面模式
    ├── ReportBuilder.cs          GUI 与 CLI 共用的报告组装（保证两条路径结论一致）
    └── Reporting/PdfReportWriter.cs   QuestPDF 中文报告（内嵌字体子集）
```

> `Gpd` 是最初的内部代号（Game Performance Doctor），保留作为命名空间前缀；对外产品名是 `wiisl`。

构建：

```bash
dotnet build wiisl.slnx -c Release
```

## 参与贡献

欢迎提 Issue 和 PR。如果反馈的是性能分析结论有误，请附上生成的 `report.json` —— 里面包含得出结论所依据的原始采样数据。

## 许可证

[MIT](LICENSE)
