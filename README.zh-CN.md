# Clocktail

[English](README.md) | 简体中文

**让 AI 助手给你的 RTX 50 系显卡超频，走 MCP 协议。** 你打游戏，AI 调卡，稳不稳由 NVIDIA 自家的压测工具说了算。吵不起来。

![3DMark Speed Way 成绩页：16 182 分，有效成绩，RTX 5090 搭配 Intel Core i9-14900K；同类系统的成绩分布图里，这个分数的柱子在最高那根的右边](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/speed-way-result.jpg)

*Clocktail 起家的那张 RTX 5090 跑 3DMark Speed Way：16 182 分，有效成绩。灰色高柱是同类系统扎堆的地方，橙色那根是这张卡。*

## 恭喜

一张本来就很快的显卡，你还想让它再快一点。懂，不问。

### 安装

把下面这段话原样发给你的编程助手（Claude Code、Codex、Cursor、Trae、Qwen Code、Kimi Code、OpenCode 都行）：

```
用 npx clocktail@latest setup 安装 Clocktail，并接入你自己。先把安装计划给我看，我同意了再装，装完帮我验证一遍。
```

想自己动手也行，装好 Node.js 后在终端里运行：

```
npx -y clocktail setup
```

安装程序会先列出它在你电脑上找到的 AI 助手和要改的配置（改之前都会备份），你点头了才动手。中间 Windows 会弹一次管理员权限，同意就行。装完重启一下 AI 助手。在你开口之前，Clocktail 不会往显卡写任何东西。

它不会自己更新。出了新版，问一句“我的卡现在怎么样”，表格最后一行会提醒你。再跑一遍 `npx -y clocktail@latest setup`（也可以让 AI 替你跑），重启 AI 助手就好，显卡设置和你存的配置都不会动。检查更新时它只去 npm 看一眼最新版本号，你电脑和显卡的信息一概不发。不想让它查，设个环境变量 `CLOCKTAIL_NO_UPDATE_CHECK=1`。

npm 下载慢的话，先换国内镜像：`npm config set registry https://registry.npmmirror.com`

也不一定非得用 Claude。只要模型会好好调用工具就行。下面那份体检报告，是 OpenCode 里的 DeepSeek V4.1 Flash 出的，整段对话花了一美分。本地模型也可以，哪怕就跑在它要汇报的那张显卡上：

![本地模型在 OpenCode 里回答“我的显卡超频状态”：调用 card_report，给出结论、相对出厂改了什么（打 ✅）和当前核心频率](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/local-model-card-report.jpg)

*Qwen3.8 27B 跑在 llama.cpp 里，接 OpenCode，用的正是它在汇报的这张 RTX 5090。显卡给自己写体检报告，倒也坦荡。*

### 模型小？上精简版

```
npx -y clocktail setup --lite
```

完整版有 53 个工具，外加一本手册那么多的说明，你还没开口打招呼，19k 左右的 token 就先没了。上下文只有 32k 的本地模型，塞完 AI 助手自己的提示词就装不下了，而且 Ollama 不会告诉你：它悄悄把对话开头扔掉，最先扔的就是这些说明，AI 聊着聊着就忘了自己在干什么。`--lite` 只给 24 个工具和一页纸的简报，大约 6.5k token。就算用了 lite，也照样给 Ollama 开到显存撑得住的最大上下文（`OLLAMA_CONTEXT_LENGTH`，64k 就很宽裕）。

### 用哪个模型，比用哪个软件要紧

同一个 Clocktail，在 Cursor 里接顶级模型一路顺畅，换成免费的小模型就开始原地转圈。挑你用得起的最好的模型；预算紧的话，OpenCode 里的 DeepSeek V4.1 Flash 就很划算。

有些 AI 助手调用工具从来不问你（比如 Freebuff，或者任何开了 YOLO 模式的）。试验都是小步、有上限、到点自己会撤回的，所以出不了大事。真正会留下来的改动，比如新的日常设置、日常功耗墙，Clocktail 会亲自来问你：AI 助手能弹问题的，就在 AI 助手里问；弹不了的，就弹一个 Windows 对话框。这个框，模型点不了。你要是宁愿全在 AI 助手里批准，就设 `CLOCKTAIL_CONFIRM=off`。

Claude Code 正好反过来，哪怕只是读一下都要先问你。`npx -y clocktail setup --allow-reads` 会放行只读的工具（card_report、gpu_status 这些），写入照样要问。

### 已经在 MSI Afterburner 里调过了？

Clocktail 看得出来。别的软件留在卡上的东西（降压曲线、显存偏移、功耗墙、固定风速），问一句“我的卡现在怎么样”，都会标成 **Not set by Clocktail**，这样 AI 就不会把你的降压曲线当成 NVIDIA 的原厂曲线。接下来你说了算：在它的基础上接着调；清掉（`stock` 回到的是 NVIDIA 的出厂设置；Clocktail 不留备份，所以会先问你）；或者把曲线和偏移交给 Clocktail 接管（`keep_as_daily` 加上 `adopt`，功耗墙用 `set_daily_power_limit` 另设），让 Afterburner 光荣退休。Afterburner 要是还开着，它会直说：两个程序抢着写同一张卡，这架谁也打不赢。NVIDIA App 的自动超频住在驱动里，每次重启都会回来，想让出厂设置一直是出厂设置，就去 NVIDIA App 里把它关掉。

### 然后跟它聊

```
帮我调一下显卡，先上 founders 预设，压测一遍，再看看还能往上加多少。
```

```
我的卡卡在 2970 MHz，加电压也上不去，是被什么限制住了？你打算怎么改？
```

```
我的卡现在怎么样？
```

```
今晚用新曲线打赛博朋克，你全程盯着显卡。要是崩了，就记下来当调参数据。
```

问一句“我的卡现在怎么样”，它会列一张表：每项设置出厂是多少、现在是多少，在 NVIDIA 自家限值以内的打 ✅，超出的打 ⚠️。

![OpenCode 里 DeepSeek V4.1 Flash 调用 card_report：功耗墙、V/F 曲线、显存、两路电压轨、XBAR、风扇，每项都列出原值、现值、改动、✅ 或 ⚠️ 和备注](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/card-report-vs-stock.jpg)

*相对出厂改了五项，四项打 ✅。唯一的 ⚠️ 是 XBAR +560 MHz，超出了它备注里敢担保的范围，它就直说。*

下面再像 btop 那样，用盲文点阵在终端里画出 V/F 曲线，● 标出显卡此刻跑在哪。再往下，是显卡这会儿在干什么。

![盲文点阵画的 V/F 曲线：调过的压在出厂的上面，● 在 3060 MHz、1030 mV；下面是实时读数：16-pin 输入、驱动超时、温度、功耗、PCIe 链路、核心、是什么压着频率、日常设置](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/card-report-curve-now.jpg)

*调过的曲线压在出厂的上面，● 停在 3060 MHz、1030 mV。45 °C，96 W，PCIe 5.0 x16，没有什么压着它。闲着呢。暂时。*

最后是一段大白话结论。

![结论：两处要留意，一是 XBAR 超出了实测范围，二是驱动自己报的 api_triggered；✅ 担保什么、不担保什么；以后要是闪屏或灰屏，先怀疑哪一项](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/card-report-verdict.jpg)

*✅ 的意思是在 NVIDIA 自家限值以内，不是保证不崩，这话它自己会说。哪天真灰屏了先怪谁，它也替你想好了。总得有人背锅。*

电压、频率、功耗墙、显存、风扇，到底是什么卡住了你的频率，都可以直接问它，每一步为什么这么调，它都说得出来。别的软件不太给你看的，它也读：显存温度、16-pin 接口上走了多少瓦、PCIe 链路有没有在重传数据、驱动到底是拿哪条限制压住了频率，还有 Windows 要等多久才会重置卡死的显卡。

你打游戏的时候，它每秒看四次显卡，看到什么就跟你说什么。游戏崩了也不白崩：崩在什么频率、什么电压、多少瓦、多少度，它都记下来，日常设置自动恢复，下一步就把曲线上那一段往下压。不用再开别的软件。

## 几句大实话

- **调参交给 AI。** 一次只动一小步。步子迈大了，显卡容易出现在二手群里。
- **所有开关都给它，没有“专家模式”。** 命令行能调的，AI 都能调：V/F 曲线（一段一段调，或者整条写）、核心偏移、电压加成（两路电压轨的上限）、XBAR 和它跟核心频率的比例、SYS 和 Video、频率锁、显存、功耗墙和电流墙、风扇。每个工具一次只动一点，碰到这张卡的硬上限就停，不让过的时候会告诉你为什么。
- **NVIDIA 自家的压测工具当裁判。** `nvgwls.exe` 是显卡驱动自带的（NVIDIA App 的自动超频用的就是它），每个结果都逐位比对。铁面无私，收买不了。
- **游戏稳定 = 压测稳定再往下退 30–60 MHz。** 压测能摸到边界在哪，但游戏要离边界多远才不崩，只有游戏自己知道，我们这张卡上是 30–60 MHz。所以你打游戏，它盯着，崩一次就多一条数据。这条规矩不是我们定的，是一次次驱动重置撞出来的。
- **游戏测不出来的，交给实验室。** 显存和 uncore 有自己的负载引擎 `clocktail-lab`（StabilityLab 那套，搬了进来），所有结果都让 CPU 逐位核对，顺便测 DRAM 和 crossbar 到底跑多快。你可以让 AI 自己去扫显存偏移，或者自己找 XBAR、比例和电压加成的最佳组合，每一步都跟目前最好的那组按 A B B A 比一遍。赢家还要再跟你的日常设置单独比一次才算数，最后告诉你谁赢了，你不点头，什么都不留。要是有别的东西在用显卡（屏保也算），它会等它走了再测，不拿脏数据糊弄你；测到一半被打扰的那轮，会整轮重测。
- **帧数它自己会看。** 你打游戏时，AI 能直接看到平均帧、1% 和 0.1% 低帧、帧生成时间，用的是 Intel 的 PresentMon，由后台服务来跑，不往游戏里注入任何东西。“调了有没有用”，拿帧数说话，不凭感觉。
- **想存几套就存几套。** “安静”一套、“游戏”一套，一句话就能切。
- **一切都能撤回。** 凡是会留下来的改动，Clocktail 都会亲自先问你一句，AI 助手说了不算。每次写入都会回读核对，随时能退回去（唯一的例外是 `stock` 清掉别的软件留下的设置，这一步同样会先问你）。后台服务记着你的日常设置，每次开机、睡眠唤醒、驱动重置后都会自动恢复，包括你自己作出来的那几次。它还会核对固件是不是真按你设的功耗墙在跑，因为真出过一次：读数全是 600 W，卡却悄悄按 575 W 跑了好几个小时。刷了别的 VBIOS，它会等你点头，不会把旧的偏移直接套到新的出厂曲线上。驱动半路掉了也不用重连，AI 那边接着用。
- **该停的时候它会停。** 显卡看着像卡死了，就不写新的试验。压测到 85 °C 自动停。夜里没人看着跑曲线，崩两次就停手，免得显卡一遍遍卡死也没人知道。会让显卡待机时也顶在高频的频率锁只给测试用，绝不留进日常设置。

![问它拿什么测稳定，它把工具箱摊开：两个裁判 stress_test 和 lab_test，盯真实游戏的 watch 和 live_start，崩溃怎么记，两个常用组合，那条规矩“游戏稳定 = 压测能过的最高频率减 30–60 MHz”，再往下是调 V/F 曲线的工具](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/stability-toolbox.jpg)

*问它拿什么测稳定，它把家底摊开：两个裁判、两种盯游戏的办法、调曲线的工具，还有它奉为圭臬的那条规矩：游戏稳定 = 压测稳定再退 30–60 MHz。它会反复提醒你。*

## 在我们这张卡上测到的

只测过一张卡：RTX 5090 公版，驱动 617.42（后来换了 621.01，见最后三行），Windows 11。你的卡不一样，每张卡都不一样。

| 设置 | 结果 |
|---|---|
| MSVDD REL −50 → 0 mV（NVIDIA 自己的上限） | Blender 快 2 %。不要钱，最划算。 |
| XBAR +300 MHz，电压需求 +20 mV | Blender 比出厂快 6–7 %。 |
| XBAR 超过 +300 左右却不加电压 | MSVDD 掉到底，吃显存的负载最多慢 16 %。别这么干，我们替你干过了。 |
| XBAR +400 MHz / +50 mV，比例 0.95，MSVDD REL +50 / ALT +30 mV | 实验室的 crossbar 测试比这张卡之前的日常设置快 5–6 %，满载和轻载压测都过了，这是它在 617.42 驱动下的日常设置。游戏里还没测。 |
| 在此基础上显存 +1758 MHz | 实验室里一路到 +3000 一个错都没有，但带宽到 +1758 左右就不涨了。3DMark Speedway 比 +358 好一点，+2900 在 Speedway 和 Steel Nomad 里都没更快。过了平台期只剩延迟更低，帧数感觉不到。 |
| 621.01 驱动 | NVVDD 和 MSVDD 的电压写入，它一律回“成功”，然后什么都不改，重启也一样。所以 MSVDD 只能原厂，比例也不再有用（0.925 和 0.95 只会拖慢核心）。XBAR 自己还能出力：比例 0.9 下 +400 MHz / +50 mV，crossbar 测试比原厂快 10 % 左右。Clocktail 会把配置里其余部分照常写上，并告诉你驱动不收哪一部分。 |
| 621.01 下的电压加成 100 % | 621.01 唯一还收的电压开关：每条电压轨的上限 +20 mV，每 25 % 加 5 mV。比例缺的正是它：XBAR +500 MHz / +50 mV、比例 0.95、电压加成 100 %，crossbar 测试比 +400 / 0.9 那组又快 7 %，显存带宽快 4.5 %，实验室和压测在满载、半载下都过了。Clocktail 因此去掉了调 MSVDD 和 NVVDD 偏移的工具。 |
| 621.01 下 XBAR 按 30 MHz 一档细扫 | 从 +320 到 +590，每一档都跟 +500 比：crossbar 测试一路涨到 +560（又快了 1 %），到 +590 又吐回去一点。+560 过了实验室和满载、半载压测，现在就是这张卡的日常设置，截图里的就是它。游戏里还没测。 |

我们一度以为 XBAR 偏移过了 +300 就白给。其实不是，只是它一个人使不上劲：XBAR 被夹在中间，下面是核心频率的 0.9 倍，上面是 MSVDD 电压能撑到的频率，两头只差 30 MHz 左右。只抬一头，纹丝不动；比例和 MSVDD 一起抬，窗口才打开，XBAR 偏移这才开始见效。这是调参工具自己摸出来的，不是我们。数字大不一定就好，有时候它只是缺个帮手。

crossbar 一松开，显存也跟着有了余地：同样的偏移，带宽多了 7.8 %，而且头一回真的跟着显存频率往上涨。

founders 预设里有第二行。第一行原本也在，621.01 不收了，只好拿掉。

![3DMark Steel Nomad 成绩页：15 757 分，有效成绩，RTX 5090 搭配 Intel Core i9-14900K，平均频率 2,878 MHz](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/steel-nomad-result.jpg)

*用 Clocktail 调过的 RTX 5090 跑 3DMark Steel Nomad：15 757 分，有效成绩，平均频率 2,878 MHz。参考卡之前用 founders 预设跑过 15 177 分。*

你的卡要是读出来跟我们的对不上，让 AI 助手生成一份诊断报告（diagnostic report），附在 [GitHub issue](https://github.com/swaggypatrol/Clocktail/issues) 里发给我们。就一个纯文本的 JSON 文件：版本号、显卡、每个开关在你的卡上能不能调、驱动对每次读取是怎么回答的。生成它不会往显卡写任何东西，里面没有用户名、路径或文件，你不发，它哪儿也不去。

## 卸载

```
npx -y clocktail uninstall
```

Clocktail 改过的每一项设置都会改回去，显卡回到装 Clocktail 之前的样子（一般就是出厂设置，除非 Afterburner 那帮软件先来过、而你又没让 `stock` 把它们清掉），服务、文件和 AI 助手里的配置也都会删干净。你的调校记录默认留在 `%LocalAppData%\Clocktail`，加 `--purge` 一起删。显卡不会记得发生过什么。

## GitHub 上的 zip

Clocktail 是围着 AI 助手设计的，不建议手动安装。Releases 页面上的 zip 是给你研究、检查、存档用的：里面是安装程序会装的 MCP 服务器和后台服务，外加一个命令行工具，想翻、想扫毒、想留底都行。

你要是非要自己当机器人，亲手装一遍，步骤如下：

1. 右键 zip → 属性 → 勾选“解除锁定”。不然 Windows 会对几个没签名、从网上下来的程序多问几句，这点它倒也没说错。
2. 解压到 `%LocalAppData%\Programs\Clocktail`。安装程序本来也装在这儿，以后换成 npx 能直接接手。
3. 把 AI 助手连到 `clocktail-mcp.exe`：stdio 方式，不带参数。Claude Code 是：
   ```
   claude mcp add clocktail -s user -- "C:\Users\<你的用户名>\AppData\Local\Programs\Clocktail\clocktail-mcp.exe"
   ```
4. 在那个文件夹里打开终端，运行 `.\Clocktail.Broker.exe install`，同意一次管理员权限。懒得弄也行，AI 助手第一次要写显卡时会自己调用 `install_service`。
5. 重启 AI 助手。

本地小模型的话，在路径后面加上 `--lite`（或者设环境变量 `CLOCKTAIL_TOOLS=lite`）。

升级：先关掉所有 AI 助手（Windows 不让覆盖正在运行的程序；安装程序有办法绕过去，你是手动的，没这待遇），把新 zip 解压覆盖，再运行一次 `.\Clocktail.Broker.exe install`，不然后台服务还是旧版。卸载：依次运行 `.\Clocktail.Broker.exe stock`（把 Clocktail 改过的都改回去）和 `.\Clocktail.Broker.exe uninstall`，从 AI 助手的配置里删掉 clocktail，再删掉文件夹。调校记录在 `%LocalAppData%\Clocktail`，想删得一干二净就连它一起删。

当然，你也可以把上面“安装”那段话贴给 AI 助手，然后去泡杯茶。机器人的活，还是交给机器人干。

## 免责声明

非官方软件。超频可能导致游戏崩溃、画面出错、硬件减寿，风险自负。真出了事，调校日志会一五一十告诉你发生了什么，但不会跟你道歉。
