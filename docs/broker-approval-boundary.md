# Broker 确认锁与配置批准边界

基线：`1541af5`。主代理独立实现，未委派。

## 先复现

新增两项 fake-driver 回归在基线上均失败：

1. 确认回调未返回时，服务 reset recovery、客户端 apply 和 lab candidate 写入不能在 1 秒内完成。
2. 直接写入 450 W 的 Daily JSON，再调用客户端 apply：旧实现返回成功，未询问用户。

## 锁外确认

- 在操作锁内执行现有预检，到授权点抛出专用的内部控制异常，完整退出全部锁作用域后才调用 Windows 确认框；不是手动释放一层可重入锁。
- 批准后重新进入正常 Dispatch 路径。一次性 ticket 绑定 GPU、kind、value、目标 writes、完整应用相关 Daily 状态、trial 和已存配置快照。任何变化均拒绝提交；不沿用等待前的 judge/write-window 判定。
- 10 个 kind（keep/adopt/preset/stock/factory/off/load/wall/save/forget）共用该授权点。嵌套操作不能再开启确认，ticket 不能转给另一操作。关闭 broker 后的迟到 Yes 不能提交。
- 一个 dispatcher 同时只接纳一个确认请求；其余立即失败，不排队弹窗。拒绝、超时或回调异常后有 5 秒冷却。apply/recovery/普通试验写入不受确认队列和冷却影响。

## 批准不再等于用户可写文件

- `IProfileApprovals` 与用户配置 JSON 分离；宿主缺省仅使用进程内、初始为空的批准集合，**不会信任磁盘上的既有文件**。CLI 本地 fallback 同样不能自动信任文件。
- 实际 Windows broker 使用 `%ProgramData%\Clocktail\Approvals\<user-SID>`。GPU/预设名经 JSON 编码和 SHA-256 映射为文件名，不作为路径组件。
- 根目录、子目录和 receipt 的所有者为 Administrators，保护 DACL，仅 Administrators 和 SYSTEM 有 FullControl。创建目录直接使用带安全描述符的原生 CreateDirectoryW；已有目录绝不自动修 ACL。逐层拒绝 reparse point，拒绝非管理员所有者、继承 ACL 和其他授权主体；receipt 文件也检查 ACL/所有者。
- receipt 临时文件以受保护安全描述符及 CreateNew 创建，flush 后原子替换。已有不可信目录/receipt 会失败关闭，不从中导入批准。不会把用户 JSON 内容或环境变量当作保护目录。
- 指纹绑定 GPU、daily/命名预设槽、Writes、Adopted、Suspended、VBIOS、Driver。History/KeptAt 只是记录，不参与授权，故自动追加崩溃说明无需重新批准。
- 所有 Daily 应用统一经过批准检查，包括启动、唤醒、reset、apply-power、back_to_daily、finish-job、试验归还。有效 JSON 指纹不匹配时，不应用其中任何值，尝试归还 Clocktail 持有的原值，并返回 `approval_required` / `stock_restored`；归还本身失败也会明确报告。
- load/preset 的新候选只有刚完成显式确认的路径才能应用/记录。wall/save 不能顺带给一个未经批准的既有 Daily 洗白；需先确认完整配置。
- save/preset 更新命名预设 receipt，forget 撤销它。stock/off/factory 更新暂停状态的批准；即使用户文件已经被删掉，也撤销旧批准，防止旧活动配置被重新放回后复活。
- 签的是经过检查的内存候选，不是批准后再读磁盘来签；文件随后被编辑，不会改变本次已批准候选，下次应用将重新核对。

## 升级/行为变化

没有“第一次启动自动批准”迁移。现有用户首次应用会收到未批准提示，需 `load_daily` 并在 broker 窗口确认完整配置一次；保存预设也要确认后才建立 receipt。

若保护目录已存在但权限/所有者不可信（`%ProgramData%` 下任何账户都能先建同名文件夹），broker 不再因此起不来：它以“批准不可用”方式运行（`UnavailableProfileApprovals`）。此时不应用任何 Daily、不接受 keep/adopt/preset/wall/load/save（在弹确认框之前就拒绝并给出原因），试验写入、recover、back_to_daily 的归还以及 stock/off/factory/forget 照常；`ping` 的 `profile_approvals` 和 `profile show` 的 `approvals_unavailable` 给出原因。

修复放在本来就提权的 `install`（`WindowsProfileApprovals.Repair`）：缺失的目录按受保护描述符创建；已存在但不可信的目录、同名文件或链接改名为 `<原名>.untrusted-<时间>` 后重建，其中内容一律不读、不导入，可信目录原样保留。修复失败时安装继续并说明原因，服务以“批准不可用”运行。**仍然不要把未知 receipt 递归改 ACL 后直接当成真实批准**。

MCP 原聊天确认码仍保留；stock 暂停每日配置仍需确认，这两项交互调整未包含在本次改动。无须新批准的临时归还可用 back_to_daily/recover；已有未批准 Daily 时 back_to_daily 会拒绝它并尝试归还原值。

## 验证与边界

- 新增边界/receipt 测试 **41/41 通过**：每个确认 kind 的锁外并发和锁内复核、12 个并发重复请求、拒绝冷却、缺少/篡改/撤销/重放批准、模拟存储故障、旧配置迁移、持有批准存储的 dispatcher 重启、GPU/槽/字段绑定和 ACL 描述符拒绝规则；等待期间新建 trial/measurement lease 也会被复核，不沿用旧授权覆盖它们。
- 旧功能测试中明确表示“已经批准”的文件夹具，改为同时种下真实 `MemoryProfileApprovals` receipt；没有引入 always-approve 存储替身，没有放宽硬件/拒绝断言。
- `dotnet build Clocktail.slnx --no-restore --nologo`：0 warnings / 0 errors，含 Windows MCP 目标。
- `dotnet test tests/Clocktail.Tests/Clocktail.Tests.csproj --no-build --nologo`：最终 **1638/1638 通过**（23 秒）；前一轮全套也通过。`git diff --check` 通过。
- 未创建或改动本机 ProgramData ACL，未触发提权弹窗或 GPU 实机操作。ACL 测试检查真实 Windows 安全描述符，但**没有执行真实高/中完整性进程间的文件写入拒绝测试**；原生受保护文件创建/重启持久化仍需部署环境手工验收。重启回归使用共享的内存 receipt 存储模拟持久化，不冒充 Windows 文件实测。
- 保护的是 Daily/预设的持久化批准，不是同用户代码沙箱：临时试验写入仍按既有协议可用，其他状态文件（例如 ownership/game recovery）的信任模型不因本次变为管理员存储。归还“原值”也不声称清除所有其他软件设置至固件出厂值。
