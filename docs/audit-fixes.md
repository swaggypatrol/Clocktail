# 核心审计修复与验收

基线：`26ab3d52b56dd4b8ef8295e85639f31390666b92`（0.9.6）。
范围：根目录 `AUDIT_FIX_LIST.md` 的六项及三个待确认项。没有扩展为对未覆盖模块的完整审计。

后续发现本轮确认框仍占有操作锁，且批准未覆盖用户可写配置文件；这两项不以本报告的历史测试通过作为完成依据。后续修复、迁移规则及额外验证见 [`broker-approval-boundary.md`](broker-approval-boundary.md)。

## 修复

1. **提权 lab 路径**：`WindowsGameAccess` 仅使用 broker 目录下的 `BrokerInstallBundle.LabName`，忽略 `CLOCKTAIL_LAB_EXE`。MCP 的非提权开发路径不变。源码搜索确认 broker 无其他环境变量启动路径；Core 的 `CLOCKTAIL_GPU_PCI` 仍仅选择受支持设备。Domain 的 `CLOCKTAIL_Q2RTX` 定位器没有 broker 调用点。
2. **每日配置确认**：管道 keep/adopt/preset 强制提供完整预览指纹；broker 原生 Windows 确认默认选择 No，60 秒超时，没有可交互桌面或授权回调则拒绝。确认后重新核验配置/目标值，避免等待期间的变化被悄悄保存。off/load/save/forget 同样经过授权；服务专用恢复命令不能从管道调用，内部恢复不弹框。
   - CLI keep/adopt/preset 和曲线编辑器均适配预览指纹。
   - `apply`（back_to_daily/编辑器撤销）及 `finish-job` 恢复已批准的每日配置，不替换配置、不解除暂停，故无需再次确认；阻塞这些清理操作会把试验设置留在卡上。测试检查恢复值及配置文件逐字不变，并保留 job 身份保护。
   - MCP 仍有自身确认，因此保存/切换每日配置可能出现两层确认；本次没有移除前一层。
   - 没有 broker 时，CLI 本地 fallback 不能更改每日配置。已提权任意代码、手工改配置文件不属于这个管道确认边界的隔离保证。
3. **Adopted 来源**：wall 保留原来源；save/preset 保存、合并仍属于目标配置的来源，去掉已不在配置中的条目。
4. **写入规范化**：在试验规则和原值查找前，用注册路径的 kind 与合法 key 规范写法；拒绝非法键和数字枚举漏洞。POWER/FE 不能绕过功耗墙专用入口，别名不能绕过仅试验分类。
5. **管道**：坏 JSON/帧返回 bad_request；创建失败有可取消重试间隔；读写均有超时，后台 I/O 异常被观察。监听器异常也会等待已接收请求，避免宿主误以为没有原生查询仍在运行。
6. **日志**：datapoints 和 tuning-log 从尾部扫描；broker.log 按 10 MiB 轮转，保留 3 份备份（锁定时继续写入、稍后重试）。不自动删除 datapoints、tuning-log 或 V/F 历史日文件。
7. **已核实的待确认项**：
   - `{}`、null Writes、无效条目的 profile 被视为损坏而不传给下游；合法空配置/旧配置仍可加载。原先启动只尝试一次，不是持续重试。
   - 接管 ownership 时独立记录 AdoptedAt，存活/PID 复用判断不再用前任的 ClaimedAt。
   - broker 跟踪后台任务、事件回调及管道服务，关闭时拒绝新工作、取消并等待；没有静止则不显式卸载 GPU 库。主审另修正了快速完成任务的登记竞态，以及管道请求阻塞时无法进入有界清理的问题。

## 自动化验证

主代理在主目录独立执行，而非仅采用子代理报告：

- `dotnet build Clocktail.slnx --no-restore --nologo`：通过，0 warnings / 0 errors；包含 MCP 的 `net10.0-windows` 构建。
- `dotnet test tests/Clocktail.Tests/Clocktail.Tests.csproj --nologo` / 后续 `--no-build`：最终重新构建后 1568/1568 通过（23 秒）；此前两轮整合全套也通过。
- 新增审计、CLI/编辑器、日志、管道、退出生命周期回归过滤运行：119/119 通过。
- 原始基线独立 worktree 全套：1449/1449 通过。
- `git diff --check`：通过。

**一次非稳定测试观察**：整合首轮 `SoloTests.The_soloist_fits_under_the_wall_holds_and_passes` 得到 breadth=0.25（预期 0.5–0.8），其余 1567 项通过。该项单独复跑通过，随后整合全套通过；基线全套也通过。该 fake 使用独立采样线程、SpinWait 与共享模拟时钟，但没有充分证据认定根因，也未证明这是基线已有问题。未修改测试断言或 Solo 实现来掩盖这次失败。

## 日志测量证据

日志子代理的 Release 合成测量（5 次平均；不是实机 GPU 基准）：

| 操作 | 数据量 | 原耗时/分配 | 改后耗时/分配 |
|---|---:|---:|---:|
| ReadLast(20) | 20 条 / 13.4 KB | 0.437 ms / 77.6 KB | 0.528 ms / 56.2 KB |
| ReadLast(20) | 50,000 条 / 32.69 MB | 178.813 ms / 170,082.6 KB | 0.274 ms / 108.9 KB |
| TuningLog.Tail(15) | 50,000 条 / 9.48 MB | 29.113 ms / 58,854.7 KB | 0.076 ms / 85.6 KB |

主审复跑了语义对照测试：跨块、UTF-8/BOM、CRLF、长行、坏行、删除标记等；未独立复测上述计时。收益针对大记录文件，小文件无性能承诺。请求全部记录仍会读全部内容；单条极长记录仍需对应内存，不宣称固定内存上界。

## 未验证与部署边界

- 未触发实机 GPU 写入、安装/setup、计划任务重启、提权弹窗点击、驱动重置或卸载。
- 原生确认框仅用模拟回调测授权/拒绝/超时/无桌面行为；真实计划任务的高完整性桌面显示和 UIPI 行为仍需用户手工验收。
- 退出任务/回调超时用 fake 验证；真实驱动卡死及 Windows Event Log watcher 释放行为未实测，OS 调用自身仍可能阻塞。
- lab 路径是源码约束测试，不是 `%ProgramFiles%` 中实际子进程启动验收。
- 本机旧服务不会因源码提交自动更新；以后部署新构建需重新 setup。本次只作本地 Git 提交，不推送、不发版。

## 工作材料

子代理说明/完整交接保留在主项目之外：

- `C:/Users/MING/dev/Clocktail-audit-profile/`
- `C:/Users/MING/dev/Clocktail-audit-pipe/`
- `C:/Users/MING/dev/Clocktail-audit-logs/`

这些 worktree 的未提交副本只是工作材料，不会自动进入主目录；主目录以实际整合后的 diff 为准。未把 TASK_BRIEF/TASK_HANDOFF 或用户已有的其他未跟踪文档混入产品提交。
