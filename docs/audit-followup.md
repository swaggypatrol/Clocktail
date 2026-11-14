# 独立追查：持久化与客户端边界

基线：`9560077`。本轮由主代理独立完成，没有启动子代理。
沿上一轮的配置加载、日志接管及管道调用向外检查；不是全仓库完整审计。

## 确认并修复

### ownership 不能把“读不懂”当作“不欠原值”

- `{}`、null Items/条目、缺字段、未知版本、重复 cell、超限记录均拒绝使用，报告 Foreign。
- 主文件缺失且只有损坏 `.bak` 时，原先会报告 Nothing；现在保留该记录和 sentinel，禁止按“无债务”继续恢复。
- Claim/Release 拒绝覆盖损坏记录，也不能因为 PID 恰好相同就修改另一个 GPU/boot 的记录。需要显式恢复/接管。
- Clear 先删除备份再删主文件。用 Windows 文件共享锁复现了旧顺序的问题：备份删不掉时，主文件却已经消失，旧债务会经备份读取重新出现。
- cell 身份匹配统一忽略大小写。Release 找不到目标 cell 时不再重写日志/刷新磁盘/产生备份。

### 无需驱动写入的恢复也必须正确接管日志

收紧 Release 后，已有两个 Recovery 测试失败，暴露了“原值已在硬件上”分支直接删除前任记录的隐含依赖。
改为先通过既有 AdoptedOriginal 路径明确接管、核对当前欠下的原值，再 Release；正常恢复仍无需 GPU 写入，剩余债务保留正确 AdoptedAt。
原有断言未放宽；新增测试覆盖剩余未知路径债务、其他进程的存活判断，以及损坏备份不得清除 sentinel。

### 坏预设不能拖垮正常邻居

`SavedProfiles.All` 逐项解析，复用 DailyProfile 的结构校验，并检查/规范化名称。
null、标量、错误类型字段、缺失 Writes 或无效 Adopted 的条目会跳过；正常条目仍可列出/找到。
读取本身不重写文件。整个 JSON 语法损坏仍按现有约定返回空列表，不自动猜测或恢复内容。

### 客户端必须先验证参数，再发送请求

- `TryCall` 在连接前验证 connectMs/answerWait。旧实现先连接、启动发送，随后才由 WaitOne 检查时限，可能出现调用报参数错误但请求已发送。
- 用 `Task.WaitAsync` 替代 `IAsyncResult.AsyncWaitHandle`，不再为每次调用主动申请等待句柄；超时后的 I/O 异常仍被观察，管道仍会释放。没有声称测得吞吐提升。
- 响应中的坏 JSON 统一包装成 InvalidDataException，进入 MCP/CLI 已有传输错误处理，而不是泄漏未处理的 JsonException。已发请求的传输失败仍不当作“服务未连接”，不会由本地 fallback 重做。

## 证据与验证

- 先加测试再改实现：第一批 20 项中 **19 项失败、1 项通过**；随后补充的三个错误类型预设用例也均先复现失败。无操作 Release 产生多余备份另有失败复现。
- 最终新增 **29/29** 回归通过（PersistenceBoundaryTests、BrokerClientBoundaryTests、两个 RecoveryTests 用例）。
- `dotnet build Clocktail.slnx --no-restore --nologo`：**0 warnings / 0 errors**，含 Windows MCP 目标。
- `dotnet test tests/Clocktail.Tests/Clocktail.Tests.csproj --no-build --nologo`：**1597/1597 通过**，22 秒。
- `git diff --check`：通过。

测试仅用临时文件、本地测试管道和 FakeDriver；未写真实 GPU、未修改本机服务、未安装或发版。
现有用户未跟踪文档未改动。跨进程原子接管、任意手工并发改文件不在本轮保证范围内；没有借此增加新的保留/删除策略或大规模重构。
