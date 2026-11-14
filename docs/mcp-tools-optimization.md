# Clocktail 55 MCP Tools — Algorithmic & Code Optimization Log

本文档记录对 Clocktail 全部 55 个 MCP 工具及其支撑底层模块分阶段（Phase 1 – Phase 4）实施的算法与代码级优化、边界修复及自动化回归验证结果。

---

## Phase 1：算法正确性修复与高频热路径复杂度降维

### 1. `lab_test` 驱动重置事件日志等待漏判修复
- **位置**：[`tools/Clocktail.Mcp/TuningTools.Lab.cs`](../tools/Clocktail.Mcp/TuningTools.Lab.cs)（`RunLabTest`）
- **问题**：当 `clocktail-lab.exe` 在 CUDA 负载中遭遇驱动重置（`CUDA_ERROR_LAUNCH_FAILED` / `DeviceLost`）时，子进程会写出 `verdict: "fail"` 并以退出码 `4`（`LabResult.ExitCode`）退出，而 Windows 系统日志中的 `nvlddmkm` Event 153 往往延迟 1–2 秒才落盘。原代码将 `code == 4` 映射为 `0` 传入 `Nvgwls.DriverResets`，跳过了 5 秒系统日志轮询等待，导致 `RecordCrash` 在 Broker 尚未收到事件前抢先写入手工 Crash 记录。
- **修改**：在调用 `Nvgwls.DriverResets` 前提取 `labVerdict` 与 `reasons`；当 `code == 4` 且失败原因包含 GPU 异常（`GPU fault` / `DeviceLost`）或 `labVerdict == "none"` 时，传入 `2` 触发最长 5 秒的 Event 153 等待。

### 2. V/F 曲线造型、钳位、7.5 MHz 半步对齐与二分查找
- **`VfShapes.ShiftCurrent` 越界钳位与真实 `Landing` 对齐**（[`src/Clocktail.Domain/Vf/VfWrites.cs`](../src/Clocktail.Domain/Vf/VfWrites.cs)）：
  - 在将 `Undervolt(asStock, ...)` 计算出的增量叠加回原有 `s.Points[i].OffsetKhz` 后，显式对总偏移执行固件上下限 `[lo, hi]` 钳位，并基于真实 `s.Points[i].BaseKhz` 重新执行 7.5 MHz 半步奇偶去重（`Landing(BaseKhz, total - StepKhz) == Landing(BaseKhz, total)`）及 `NewKhz` 重算，防止累计偏移越过固件范围或产生无效半步。
- **`VfShapes.Steps.Stock(long uv)` 从 $O(N)$ 线性扫描降为 $O(\log N)$ 前缀最大值二分**（[`src/Clocktail.Domain/Vf/VfWrites.cs`](../src/Clocktail.Domain/Vf/VfWrites.cs)）：
  - 在 `Steps` 构造时预计算前缀最大频率数组 `MaxF`，使 `Undervolt` 内层对 `Stock(uv)` 的调用复杂度由 $O(C \cdot N^2)$ 降至 $O(N \log N)$。
- **`TuningTools.AdjustCurve` 单调抬升半步对齐**（[`tools/Clocktail.Mcp/TuningTools.cs`](../tools/Clocktail.Mcp/TuningTools.cs)）：
  - 右邻单调性抬升循环改用 `VfShapes.Landing(pts[k - 1].BaseKhz, next[k - 1])` 与 `VfShapes.Landing(pts[k].BaseKhz, candidate)` 判断真实驱动落点，避免奇数半步基频处多抬或少抬一个 7.5 MHz 步进。
- **`TuningTools.FlattenPlan` 左邻峰值污染修复**（[`tools/Clocktail.Mcp/TuningTools.cs`](../tools/Clocktail.Mcp/TuningTools.cs)）：
  - 当某点在 Regional 模式下被左侧更高电压点的峰值单调拉平（`p.EffectiveKhz <= points[i - 1].EffectiveKhz && (long)p.EffectiveKhz - p.OffsetKhz > p.BaseKhz`）时，采用真实的 `p.BaseKhz` 作为无偏移基线，避免左侧尖峰压平后把右邻点错误下压成凹坑。
- **`TuningTools.Nearest` $O(\log N)$ 二分查找**（[`tools/Clocktail.Mcp/TuningTools.cs`](../tools/Clocktail.Mcp/TuningTools.cs)）：
  - 利用解析阶段已保证的 `VoltageUv` 单调性，将线性 `.MinBy(...)` 替换为二分查找（并保持等距时优先选较小索引的语义）。

### 3. `lab_test` `Burn` 阶段 CPU 重算停顿消除与错误早停
- **位置**：[`tools/Clocktail.Lab/Engine.cs`](../tools/Clocktail.Lab/Engine.cs)、[`tools/Clocktail.Lab/LabRun.cs`](../tools/Clocktail.Lab/LabRun.cs)
- **修改**：
  - 利用 `fma_rot` 核函数按 `gid` 独立计算的数学性质（`Reference.RotationThread` 仅依赖 `gid` 与 `iterations`，不依赖启动总线程数 `n`），将 `CudaEngine.RunCompute` 的参考复用条件由 `_referenceThreads == n` 放宽为 `_referenceThreads >= n`，并仅在 `threads > maxRefThreads` 时刷新参考缓冲；`Burn` 阶段每 500 ms 调功增减线程数不再触发同步 1024 线程 CPU 重算与整块 `Buffer.BlockCopy`。
  - 移除 `CudaEngine.VerifySubset` 中的 `_hostOut` 全数组 `.ToArray()` 拷贝，直接基于切片 Span 校验。
  - 在 `LabRunner.Run` 中一旦任一阶段出现 `ComputeMismatches > 0`、`MemoryErrors > 0` 或 `ChainErrors > 0`，立即早停后续阶段。

### 4. `FlightRecorder.Hang` 与 `FrozenFrom` 从 $O(N^2)$ 切片分配优化为 $O(N)$ 零分配
- **位置**：[`src/Clocktail.Domain/Profiles/FlightRecorder.cs`](../src/Clocktail.Domain/Profiles/FlightRecorder.cs)
- **修改**：新增 `FrozenFrom(IReadOnlyList<FlightSample> window, int count)` 重载，避免 `Hang` 每次循环调用 `looks.GetRange(0, i + 1)` 分配子列表，并在单次向后扫描中同步追踪 `tailMax`，取代 `.Skip(i).Max(...)`。

### Phase 1 验证结果
- 新增单元测试：
  - `VfWritesTests.Shifting_the_current_curve_clamps_total_offsets_within_the_firmware_limits`
  - `FlattenTests.A_point_lifted_by_a_left_neighbor_spike_uses_its_own_base_when_flattened`
- 构建：`dotnet build Clocktail.slnx --no-restore --nologo` — **0 warnings / 0 errors**。
- 测试：`dotnet test tests/Clocktail.Tests/Clocktail.Tests.csproj --no-build --nologo` — **1660/1660 通过**。

---

## Phase 2：驱动读合并、重复序列化消除、零分配遥测与单次排序分位数

### 1. 驱动层读取与锁合并（`Card` / `RailsReader` / `SettingsTools` / `TuningTools`）
- **合并 Rails 电压读取并复用缓存**（[`src/Clocktail.Domain/Rails/RailsModel.cs`](../src/Clocktail.Domain/Rails/RailsModel.cs)、[`tools/Clocktail.Mcp/Card.cs`](../tools/Clocktail.Mcp/Card.cs)）：
  - 在 `RailsReader` 新增 `ExtractPresentUv` 与 `PresentUvPair`：`Card.Read()` 直接从已获取的 `RailsReader.Read` 原始状态缓冲提取 `NVVDD`/`MSVDD` 当前电压（仅异常时单次 `PresentUvPair` 回退），并顺带回填 `_railMask`、`_monitorLayout` 与 `_monitorAt`，避免紧随其后的 `ReadSample()` 在 5 秒内重复读取电流限制表；`Card.ReadSample` 则以单次 `PresentUvPair` 替代两次独立 `VoltVoltRailsGetStatus` 驱动调用。
  - 为 `Card.ReadSample(bool includeExtended = true)` 增加轻量模式：`limiter`（[`tools/Clocktail.Mcp/SettingsTools.cs`](../tools/Clocktail.Mcp/SettingsTools.cs) `WhatLimits`）传入 `includeExtended: false`，跳过每次采样多余的时钟表、`GpcLocked()` 与显存温度驱动读取（单次 `limiter` 节省 15 次驱动读取）。
- **批处理状态与时钟域读取**（[`tools/Clocktail.Mcp/Card.cs`](../tools/Clocktail.Mcp/Card.cs)、[`tools/Clocktail.Mcp/TuningTools.cs`](../tools/Clocktail.Mcp/TuningTools.cs)、[`tools/Clocktail.Mcp/SettingsTools.cs`](../tools/Clocktail.Mcp/SettingsTools.cs)）：
  - 新增 `Card.ReadStatusExtras()` 在单次 `DriverLane` 加锁周期内完成 `Gpc`、`Clocks`、`ClockLocks` 与 `MemoryC` 读取，`gpu_status` 节省 3 次加锁排队。
  - `SettingsTools.Domain` 将拓扑、控制与 Rails 三次 `WithDriver` 合并为单次锁内执行。
- **试探读取复用**（[`tools/Clocktail.Mcp/TuningTools.Trials.cs`](../tools/Clocktail.Mcp/TuningTools.Trials.cs)）：
  - `set_clock_lock` 在查找目标曲线点时改用 $O(\log N)$ 二分查找 `Nearest` 并把已取得的 `Card.Look` 传入 `Trial(..., beforeLook)`，消除紧随其后的重复全卡 `card.Read()`。
- **正则与位掩码快路径**（[`tools/Clocktail.Mcp/Card.cs`](../tools/Clocktail.Mcp/Card.cs)）：
  - `Card.IsRtx50` 改用编译期 `[GeneratedRegex]`（`Rtx50Regex()`）；`PerfDecreaseReasons(0)` 直接返回空集合，非零位掩码遍历静态数组避免每次分配元组数组与 LINQ 委托。

### 2. JSON 序列化、并发与杂项计算消冗
- **消除二次文本解析与并行版本检查**（[`tools/Clocktail.Mcp/SettingsTools.cs`](../tools/Clocktail.Mcp/SettingsTools.cs)、[`tools/Clocktail.Mcp/SettingsTools.Report.cs`](../tools/Clocktail.Mcp/SettingsTools.Report.cs)）：
  - `card_settings` 在需附加 `daily_differs_on_card` 时直接以 `JsonObject.Create(doc.RootElement)` 包装已解析的 `JsonElement`，移除第二次全量 JSON 字符串解析（`JsonNode.Parse`）。
  - `SettingsTools.Curve` 单遍扫描 `offsets` 统计 `nonZero`/`min`/`max` 并利用 `VoltageUv` 升序对 `flat` 检查增加 $O(1)$ 右邻快路径；`FanCurves` 复用单次 `Band(snap.ControlRaw, p.Index)`。
  - `card_report` 在执行 `CardSettings()`/`WhatLimits()` 前通过 `Task.Run(UpdateCheck.Latest)` 并发检查版本；`Arr` 移除多余 `.ToList()` 分配。
- **进程快照合并**（[`tools/Clocktail.Mcp/OtherTools.cs`](../tools/Clocktail.Mcp/OtherTools.cs)）：
  - `OtherTools.Running()` 改为单次 `Process.GetProcesses()` 构建进程名集合并在 `finally` 中逐个释放句柄，将 9 次全系统进程枚举合并为 1 次。

### 3. 单次排序多分支分位数与 `LiveWatch` 热路径优化
- **统一复用缓冲区单次排序**（[`tools/Clocktail.Mcp/Card.cs`](../tools/Clocktail.Mcp/Card.cs) `SampleQuantiles`、[`tools/Clocktail.Mcp/TuningTools.cs`](../tools/Clocktail.Mcp/TuningTools.cs)、[`tools/Clocktail.Mcp/TuningTools.Trials.cs`](../tools/Clocktail.Mcp/TuningTools.Trials.cs)、[`tools/Clocktail.Mcp/LiveWatch.cs`](../tools/Clocktail.Mcp/LiveWatch.cs)、[`tools/Clocktail.Mcp/SettingsTools.cs`](../tools/Clocktail.Mcp/SettingsTools.cs)）：
  - 引入 `SampleQuantiles.P10P50P90` / `P50` / `Avg`，复用单个 `List<double>` 原地 `.Sort()`：
    - `stress_test` 由 59 次 LINQ 排序降至 17 次原地排序，直接读取 `StressStat p50` 生成摘要；
    - `watch` 由 50 次 LINQ 排序降至 10 次原地排序，消除对 `WatchStats` 与 `Pct` 的重复调用；
    - `LiveWatch`（`WindowLine`/`Totals`/`SummaryLine`/`Line`）统一采用复用缓冲原地排序；`SettingsTools.Median` 改为原生 `List<double>.Sort()`（保持偶数长度中位均值语义）。
- **`LiveWatch` 运行期缓存与锁一致性**（[`tools/Clocktail.Mcp/LiveWatch.cs`](../tools/Clocktail.Mcp/LiveWatch.cs)）：
  - `CurveTop` 上移至窗口刷新阶段按窗计算一次（不再每 500 ms 重算），`ToPoint` 改用 $O(\log N)$ 二分 `NearestIndex`，`WindowLine`/`PointAt` 优先使用 $O(1)$ 下标访问；
  - `Edge` 缓存 `datapoints.jsonl` 路径、文件 `LastWriteTimeUtc` 与曲线形态，仅在文件修改或曲线形态变化时调用 `Experiment.ReadLast`；
  - `Read`/`Stop`/`Dispose` 统一 `lock (run)` 读写 `Ended`/`EndReason` 并用 `List.GetRange` 替代 `.Skip().ToList()`。

### 4. 零分配 CSV 解析、历史读取与策略微优化
- **PresentMon 帧统计零分配单遍解析**（[`src/Clocktail.Domain/Fps/FrameStats.cs`](../src/Clocktail.Domain/Fps/FrameStats.cs)、[`tools/Clocktail.Mcp/FrameFeed.cs`](../tools/Clocktail.Mcp/FrameFeed.cs)）：
  - `Frames.Add(ReadOnlySpan<char>, Frames? also)` 直接对 UTF-8 解码行切片按逗号遍历列字段，使用 `_series.GetAlternateLookup<ReadOnlySpan<char>>()` 查询已有 swap chain（零 per-frame 字符串分配），并在 `FrameFeed.Lines` 中由 `_total` 一次解析同时驱动 `_window`，消除双倍解析与 `line.Split(',')`。
- **Residency Span 解析与 $O(1)$ 前缀清理**（[`tools/Clocktail.Mcp/Residency.cs`](../tools/Clocktail.Mcp/Residency.cs)）：
  - `ResidencyLog.Between`/`Parse` 优先用 `ReadOnlySpan<char>` 解析首列时间并于时间窗口外立即早退；`FlushHeld` 以单调计数 `_trimmed` 跟踪前缀修剪并调用 `RemoveRange(0, removeCount)` 取代 `ToHashSet` + `RemoveAll`；`LastLoadMedian` 去除 LINQ 分配。
- **曲线编辑器与历史文件 I/O 优化**（[`tools/Clocktail.Mcp/TuningTools.Editor.cs`](../tools/Clocktail.Mcp/TuningTools.Editor.cs)、[`src/Clocktail.Domain/Vf/VfHistory.cs`](../src/Clocktail.Domain/Vf/VfHistory.cs)、[`src/Clocktail.Domain/Limiter.cs`](../src/Clocktail.Domain/Limiter.cs)）：
  - `TuningTools.Editor` 合并 `EditorData` 两次 `residency.Between` 为单次时间跨度读取；
  - `VfHistoryStore.WithoutInvalidTail` 使用 `stackalloc` 返回 `(End, LastMs)` 省略末尾时间戳二次 seek/read，`ReadFile` 改用 `ArrayPool<byte>.Shared` 租借块读取缓冲；
  - `Limiter.Holding` 复用静态 `IdleOnly`/`NoneOnly`，消除单元素列表分配。

### Phase 2 验证结果
- 新增单元测试：
  - `FrameStatsTests.Shared_span_parse_across_two_accumulators_matches_individual_reads`
  - `ResidencyLogTests.Between_filters_by_time_window_and_flush_handles_overflow_prefix_trim`
  - `LastLoadTests.Last_load_median_handles_unordered_ticks_and_cutoff_intersection`
  - `PerfDecreaseTests.IsRtx50_and_sample_quantiles_match_expected_values`
- 构建：`dotnet build Clocktail.slnx --no-restore --nologo` — **0 warnings / 0 errors**。
- 测试：`dotnet test tests/Clocktail.Tests/Clocktail.Tests.csproj --no-build --nologo` — **1664/1664 通过**。

---

## Phase 3（已完成 — `c01_test` SIMD / `tune_uncore` MSVDD 悬崖早停 / `game_test` 亚秒步长轮询）

### 1. `c01_test` 按类并行 SIMD Oracle
- **YMM 8 类并行 Reference + 预分配复用缓冲区**（[`tools/Clocktail.Lab/C01/C01Fp32Fma.cs`](../tools/Clocktail.Lab/C01/C01Fp32Fma.cs)）：
  - 在 `C01Kernel` 新增 `ReferenceAllClasses`，在支持 `Fma` 与 `Avx2` 且 `classes % 8 == 0`、`nacc in {8, 16, 32}` 时，按 8 个类为一组将各累加器状态转置为 `Vector256<float>` 列向量并参数化统一 `hashInterval == 32` 与 `hashInterval == 8` 的检查点/归一化循环，通过 `Avx2.MultiplyLow` + `Avx2.Add` 在寄存器内并行计算全部 8 类的 FNV 风格哈希，消除 `Bits.GetElement` 标量提取与逐类堆分配。
  - 在 `C01Fp32Fma.Prepare` 预分配 `_refBits` / `_refH` 并在 `RunBatch` 中复用，移除每批次 `new uint[...]` 与 32 任务 `Parallel.For` 调度开销。

### 2. `tune_uncore` MSVDD 悬崖首轮早停
- **`b1` 降频悬崖检测并立即恢复 incumbent**（[`tools/Clocktail.Lab/UncoreTune.cs`](../tools/Clocktail.Lab/UncoreTune.cs)）：
  - 在 `Compare` 完成 `b1` 后，若 `a1.MemSmMhz > 0` 且 `b1.MemSmMhz < a1.MemSmMhz * 0.85`（MSVDD/FCLK 跌落超 15% 悬崖），立即通过 `Write(a)` 将 incumbent 写回显卡、跳过 `b2`/`a2`，并返回 `Outcome = Comparison.NotBetter`、`Veto = Comparison.Cliff`，在保留 `UncoreSearch.Xbar` 提压重试语义的同时削减 50% 无效 ABBA 运行。

### 3. `game_test` 亚秒 Dwell 自适应轮询
- **子秒步长紧凑采样间隔**（[`tools/Clocktail.Lab/Game/GameRunner.cs`](../tools/Clocktail.Lab/Game/GameRunner.cs)）：
  - `GameRunner.Dwell` 在 `want < 1.0` 时将单次 `Sleep` 上限收紧至 `Math.Clamp(want / 4.0, 0.05, 0.25)` 秒（保持 `>= 1.0 s` 步长原有的 `1.0 s` 轮询），确保 `0.25 s` 等短时突发步能够完成多次帧采样而不受单次过长休眠或单帧读取延迟影响。

### Phase 3 验证结果
- 新增单元测试：
  - `AriaOracleTests.Class_parallel_oracle_agrees_bit_for_bit_with_per_class_reference`
  - `UncoreWorkflowTests.B1_msvdd_cliff_aborts_abba_early_and_restores_incumbent`
  - `GameTests.Sub_second_dwell_polls_at_sub_second_intervals`
- 构建：`dotnet build Clocktail.slnx --no-restore --nologo` — **0 warnings / 0 errors**。
- 测试：`dotnet test tests/Clocktail.Tests/Clocktail.Tests.csproj --no-build --nologo` — **1667/1667 通过**（0 skipped, 0 failed）。

## Phase 4：Partial 类模块化与事务边界细节优化

### 1. `BrokerDispatcher` 职责拆分为 Partial 类
- 将原本 2143 行的 [`src/Clocktail.Domain/Broker/BrokerDispatcher.cs`](../src/Clocktail.Domain/Broker/BrokerDispatcher.cs) 按职责拆分为核心调度文件与 3 个功能明确的 `partial` 文件（均保留完整 XML 文档注释与原有锁/并发语义）：
  - [`src/Clocktail.Domain/Broker/BrokerDispatcher.cs`](../src/Clocktail.Domain/Broker/BrokerDispatcher.cs)（545 行）：核心状态、锁（`_operation`、`_writeWindow`、`_experiment`、`_settle`）、`Dispatch` / `DispatchOne` / `FromClient`、`JudgeNow`、`Write` / `Prove` / `Power` / `Clock` / `FpsOp` 等基础入口与共享辅助。
  - [`src/Clocktail.Domain/Broker/BrokerDispatcher.Profile.cs`](../src/Clocktail.Domain/Broker/BrokerDispatcher.Profile.cs)（793 行）：`Profile` 请求分发、`KeepWrites`、`ToFactory`、`LoadSaved`、`ApplyProfile`、`ApplyOnce` 等 daily / preset 配置管理逻辑。
  - [`src/Clocktail.Domain/Broker/BrokerDispatcher.Experiment.cs`](../src/Clocktail.Domain/Broker/BrokerDispatcher.Experiment.cs)（509 行）：`ExperimentOp`、`Back` / `LeaveBack`、`CloseTrial` / `CrashHeld`、`Gone`、`Leave` 等试验会话生命周期逻辑。
  - [`src/Clocktail.Domain/Broker/BrokerDispatcher.Recovery.cs`](../src/Clocktail.Domain/Broker/BrokerDispatcher.Recovery.cs)（344 行）：`SettleAll` / `Settle`、`Ownership`、`Recover`、`HandBack`、`CellsToPutBack`、`CurveAside`、`GivenBack`、`CrashedAt` 等启动结算、归属接管与异常恢复逻辑。

### 2. `TuningTools` 按领域拆分为 Partial 类与 JSON Node 直接合并
- [`tools/Clocktail.Mcp/TuningTools.cs`](../tools/Clocktail.Mcp/TuningTools.cs)（1349 行精简至 561 行）与已有 [`TuningTools.Trials.cs`](../tools/Clocktail.Mcp/TuningTools.Trials.cs) 协同，新增 3 个 `partial` 文件：
  - [`tools/Clocktail.Mcp/TuningTools.Curve.cs`](../tools/Clocktail.Mcp/TuningTools.Curve.cs)（261 行）：V/F 曲线工具与计划逻辑（`CurveAround`、`AdjustCurve`、`FlattenPlan`、`FlattenCurve`、`SetCurve`、`ShiftCurve`、`CurveTrial`、`CurveShift` 等）。
  - [`tools/Clocktail.Mcp/TuningTools.Profiles.cs`](../tools/Clocktail.Mcp/TuningTools.Profiles.cs)（224 行）：日常配置与预设管理工具（`BackToDaily`、`Stock`、`LoadDaily`、`PreviewDaily`、`KeepAsDaily`、`ListPresets`、`SaveProfile`、`DeleteProfile`、`LoadPreset`、`Changes`、`Fingerprint`），并将 `Merge` 改为直接通过 `JsonSerializer.SerializeToNode(more, Json)` 将属性写入现有 `JsonObject`，消除 `more` 的中间字符串序列化开销。
  - [`tools/Clocktail.Mcp/TuningTools.Session.cs`](../tools/Clocktail.Mcp/TuningTools.Session.cs)（444 行）：游戏会话与 V/F 数据统计工具（`BeginSession`、`MarkStable`、`MarkCrash`、`LogNote`、`ReadLog`、`EndSession`、`DataPoints`、`PointRecordsFrom`、`HistoryTimeline`、`CrashAt`、`WithoutFrozenTail` 等）。

### 3. `Transaction` 与 `SavedProfiles` 路径细节优化
- **硬件寄存器逐字节比较快速路径**（[`src/Clocktail.Domain/Transaction.cs`](../src/Clocktail.Domain/Transaction.cs)）：
  - 在 `Transaction.FirstDifference` 长度检查之后加入 `if (a.SequenceEqual(b)) return null;`，在两个缓冲区完全相同时利用向量化 `Span<byte>.SequenceEqual` 直接返回 `null`，避免逐字节循环与 `volatileRanges` 遍历。
- **预设删除单次读取与解析**（[`src/Clocktail.Domain/Profiles/SavedProfiles.cs`](../src/Clocktail.Domain/Profiles/SavedProfiles.cs)）：
  - `SavedProfiles.Delete` 改为在规范化名称后通过单次 `All(path)` 读取并解析 JSON，使用 `FindIndex`（`StringComparison.OrdinalIgnoreCase`）定位并用 `RemoveAt` 移除后再写回，消除原先 `Find` + `All` 的两次文件读取与反序列化。

### Phase 4 验证结果
- 新增单元测试：
  - `PersistenceBoundaryTests.Saved_profiles_delete_removes_case_insensitively_and_preserves_siblings`
  - `RailWritesTests.First_difference_fast_paths_identical_spans_and_respects_volatile_ranges`
- 构建：`dotnet build Clocktail.slnx --no-restore --nologo` — **0 warnings / 0 errors**。
- 测试：`dotnet test tests/Clocktail.Tests/Clocktail.Tests.csproj --no-build --nologo` — **1669/1669 通过**（0 skipped, 0 failed）。
- 格式检查：`git diff --check` — 通过（exit code 0）。
