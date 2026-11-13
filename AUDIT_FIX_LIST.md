# Clocktail 修理清单

来源：2026-11-13 的代码审计，基于 `26ab3d5`（0.9.6）。按重要性排序，先修上面的。

修复状态：本轮已完成下列六项的代码修复/测量，并处理三个待确认项；独立整合验收、未实测边界、兼容性变化见 [`docs/audit-fixes.md`](docs/audit-fixes.md)。下文原始问题与证据保留供追溯；“已修”指源码及自动化验证，不代表已更新本机服务或完成实机验收。

审计只完成了核心部分（broker、调度器、事务与日志、驱动访问层）。没看到的范围列在文末，那里可能还有问题。

每项的"证据"说明它是怎么确认的：

- **测试复现**：写了测试，跑出了错误行为。
- **代码追踪**：读代码走通了整条路径，没有实际运行。
- **推断**：读代码得出，但有一环没有走通。

---

## 1. [高] broker 不再按环境变量决定启动哪个 lab

- [x] 已修
- **位置**：`src/Clocktail.Broker/WindowsGameAccess.cs:39`
- **问题**：提权运行的 broker 优先用环境变量 `CLOCKTAIL_LAB_EXE` 给出的路径启动 `clocktail-lab.exe`，只检查文件存在。这条路径可由管道客户端触发（`src/Clocktail.Domain/Game/GameSession.cs:73-80`），途中没有权限检查。结果是未提权的同用户进程有机会让提权服务启动它指定的程序。
- **为什么算缺陷**：代码自己写明了相反的目标，见 `src/Clocktail.Broker/FrameCapture.cs:13-14` 和 `src/Clocktail.Broker/Installer.cs:207`。
- **怎么修**：
  - 把第 39 行改成只用 broker 同目录的文件：`Path.Combine(AppContext.BaseDirectory, BrokerInstallBundle.LabName)`。
  - `tools/Clocktail.Mcp/TuningTools.Lab.cs:29` 的同名变量不用动，那个进程没有提权。
  - 顺带确认 broker 里没有别的环境变量能指定路径。目前只剩 `src/Clocktail.Core/Gpu/GpuManager.cs:70` 的 `CLOCKTAIL_GPU_PCI`，它只在已支持的显卡里做选择，可以保留。
- **验收**：设了该变量后，game verify 启动的仍然是 `%ProgramFiles%\Clocktail\clocktail-lab.exe`。
- **回归风险**：低。只影响开发时让 broker 指向自编译 lab 的用法，开发可以改为把 lab 复制到 broker 旁边。
- **证据**：代码追踪。没有实际触发；"计划任务进程带有用户级环境变量"这一环依据的是 Windows 的既有行为。
- **备注**：本机装着旧构建的服务，修完发版后要重新跑一次 setup。

## 2. [中] 人工确认要在提权层执行，而不只在 MCP 层

- [x] 已修
- **位置**：`src/Clocktail.Domain/Broker/BrokerDispatcher.cs:421-650`（keep、adopt、stock、factory、wall、preset），`tools/Clocktail.Mcp/Consent.cs:60-80`
- **问题**：broker 对任何同用户进程发来的这些请求都照做。指纹校验是可选的（`BrokerDispatcher.cs:429`，请求不带就不查）。默认 chat 模式下确认码直接交给模型。
- **影响**：对只能调用 MCP 工具的代理有效；对带 shell 的代理只是约定。例如 `Clocktail.Broker.exe stock` 不需要任何确认（`src/Clocktail.Broker/Program.cs:326-344`）。
- **怎么修**，两步可以分开做：
  - 短期：来自客户端（`FromClient`）的 keep、adopt、preset 必须带 `ExpectedProfileFingerprint`，不带就拒绝。
  - 长期：确认框由 broker 自己弹出。它以高完整性运行在交互桌面，未提权进程无法替用户点击。
- **验收**：不带指纹的 keep 被拒；直接发管道请求无法在无人点击的情况下改掉每日配置。
- **回归风险**：中。会改变确认流程；CLI 和卸载流程里的 `stock` 要单独考虑；无桌面会话时需要退路。
- **证据**：代码追踪。这是设计取舍，README 已经承认 chat 模式下模型可以自己回答，但没有说明 shell 可以绕过。

## 3. [低] 设置每日功耗上限会丢掉 Adopted 列表

- [x] 已修
- **位置**：`src/Clocktail.Domain/Broker/BrokerDispatcher.cs:642-643`（wall）；`:1251`（加载已存配置）同样没带
- **问题**：重建 `DailyProfile` 时没有传 `Adopted`。
- **影响**：之前从其他工具接管、之后又调过的设置，在卡报告里会被标成"不是 Clocktail 设的"（`src/Clocktail.Domain/FactoryStock.cs:35`），可能引导用户去清掉自己的设置。正常使用 `set_daily_power_limit` 就会触发。
- **怎么修**：`:643` 的构造加上 `Adopted: profile?.Adopted`。`:1251` 要先决定加载已存配置时是否该保留旧的 Adopted，再照此处理。
- **验收**：先 adopt，再设每日功耗上限，`profile.json` 里的 `Adopted` 还在。
- **回归风险**：很低，一行。
- **证据**：列表被丢是测试复现；报告误标是推断。

## 4. [低] kind 和 key 在入口处规范化

- [x] 已修
- **位置**：`src/Clocktail.Domain/Broker/BrokerDispatcher.cs:829-832`、`:1083-1094`，`src/Clocktail.Domain/Writes/WriteRegistry.cs:216-217`，`src/Clocktail.Domain/Profiles/DailyProfile.cs:64-108`
- **问题**：注册表查找不区分大小写，而试验规则、写入顺序、分组和"仅试验"判断都按原样比较字符串。
- **影响**：直连管道的客户端把功耗墙写成 `POWER` 放进 `writes`，就能绕过"墙必须走 power_mw"。试验记录里没有这面墙，首次 keep 会把试验用的低功耗墙存成每日配置。随附的 MCP 服务端用常量构造 `writes`，不会触发。
- **怎么修**：在 "try" 解析 `writes` 时，把 kind 换成 `WriteRegistry.Find(kind)!.Kind`，key 换成该路径的规范写法，然后再做任何比较。
- **验收**：`kind = "POWER"` 的墙和 `"power"` 一样被拒绝。
- **回归风险**：低。
- **证据**：测试复现。

## 5. [低] 管道服务的健壮性

- [x] 已修
- **位置**：`src/Clocktail.Domain/Broker/BrokerServer.cs:33`、`:54-67`
- **问题和修法**：
  - 非 JSON 的请求得不到应答，服务任务以未观察的异常结束。修法：捕获 `JsonException`，回一个 bad_request。测试复现。
  - 创建管道持续失败时无延迟重试，会占满一个核。修法：重试前加短延迟。代码追踪。
  - 写应答没有超时，一个不读应答的客户端会一直占着线程。修法：给写加超时。代码追踪。
- **影响**：只有同用户进程能触发，后果是日志噪音或资源占用。
- **回归风险**：低。

## 6. [低] 记录文件的增长和整读

- [x] 已测量并修复尾读/轮转；调参记录和 V/F 历史不自动删除
- **位置**：`src/Clocktail.Domain/Profiles/Experiment.cs:201-223`，调用点 `BrokerDispatcher.cs:757`、`:847`；`src/Clocktail.Domain/Profiles/TuningLog.cs:42-50`；`src/Clocktail.Broker/Program.cs:85`
- **问题**：`datapoints.jsonl` 每次查询都整读整解析；`tuning-log.md`、`broker.log` 和 V/F 历史日文件没有看到轮转或清理。
- **怎么修**：先测量再决定。候选做法是从文件尾部倒着读最后 N 条，以及给 `broker.log` 加按大小轮转。
- **证据**：代码追踪，没有测量数字。数据量小的时候不值得动。

---

## 待确认（没有走通，先别当缺陷修）

本轮后续核实：损坏配置、退出释放竞态、接管后的存活判断均已确认并修复。损坏配置原先会导致操作失败，但启动恢复只尝试一次，并非每 5 秒重试。以下保留原审计推断。

- **手改坏的 `profile.json`**：内容是 `{}` 时 `Writes` 为 null，`BrokerDispatcher.Profile` 里可能抛空引用，启动时的应用循环会每 5 秒重试一次。没有追 `DriverResetResponder.ApplyAtStart` 的处理。
- **优雅退出时的释放竞态**：`src/Clocktail.Broker/Program.cs:103` 的 `using var mgr` 可能在 `:207` 的驱动监视任务还在调用原生库时被释放。计划任务是硬终止，所以只影响手动在控制台运行的情况。
- **日志被接管后的存活判断**：`OwnershipJournal.Adopt` 保留旧的 `ClaimedAt`，另一个进程用它判断接管者是否存活时可能误判为已死（`src/Clocktail.Domain/Ownership/OwnershipJournal.cs:118`、`:135`，`src/Clocktail.Domain/Ownership/Host.cs:46`）。只在两个进程同时直接操作日志时才有影响。

## 审计没有覆盖的范围

- `tools/Clocktail.Lab` 全部：负载引擎、CUDA 互操作、内存与 uncore 扫描、C01、游戏测试。
- `tools/Clocktail.Mcp` 的大部分：TuningTools、SettingsTools、LiveWatch、FrameFeed、曲线编辑器。
- 各写路径（Vf、Rails、Clocks、Power、Fan、Boost）以及 `WritePolicy`、Slots。
- `DriverResetResponder`、`FlightRecorder`、`VfHistoryRecorder`。
- CLI、`npm/bin/clocktail.js`、`build/install-local.ps1`。

## 复现测试

第 3、4、5 项的复现测试在临时目录里，重启后可能被清掉，要留就复制出来：

`C:\Users\MING\AppData\Local\Temp\claude\79148871d2169b9c\2efb6941-7e0c-4e07-a383-af5c33a96aaa\scratchpad\repro\AuditDispatcherRepro.cs`

这些测试断言的是当前的错误行为，所以修好之后它们应当失败；搬进 `tests/Clocktail.Tests` 时要把断言反过来。
