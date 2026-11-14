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
