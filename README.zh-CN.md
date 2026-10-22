# Clocktail

[English](README.md) | 简体中文

**让 AI 助手给你的 RTX 50 系显卡超频，走 MCP 协议。** 你负责打游戏，AI 负责调卡，稳不稳由 NVIDIA 自家的压测工具说了算。分工明确，各司其职。

## 恭喜

恭喜你决定给一张本来就很快的显卡再超个频。我们尊重你的选择，也不打算问为什么。

### 安装

把下面这段话原样发给你的编程助手（Claude Code、Codex、Cursor、Trae、Qwen Code、Kimi Code、OpenCode 都行）：

```
用 npx clocktail@latest setup 安装 Clocktail，并接入你自己。先把安装计划给我看，我同意了再装，装完帮我验证一遍。
```

想自己动手也行，装好 Node.js 后在终端里运行：

```
npx -y clocktail setup
```

安装程序会列出它在你电脑上找到的 AI 助手，告诉你要改哪些配置（改之前都会备份），等你点头才动手。中间 Windows 会弹一次管理员权限确认，同意就行。装完重启一下你的 AI 助手。显卡在你开口之前一直保持出厂设置。

它不会自动更新。出了新版，问一句“我的卡现在怎么样”，表格最后一行会告诉你；再运行一次 `npx -y clocktail@latest setup`（也可以让 AI 助手替你运行），然后重启 AI 助手就行，显卡设置、日常设置和你存的配置都不会动。检查更新时它只向 npm 读取最新版本号，不会发送你电脑或显卡的任何信息。不想检查，设环境变量 `CLOCKTAIL_NO_UPDATE_CHECK=1`。

npm 下载慢的话，先换国内镜像：`npm config set registry https://registry.npmmirror.com`

也不一定非得用 Claude。只要模型会好好调用工具就行，本地的也可以，哪怕就跑在它要汇报的那张显卡上：

![本地模型在 OpenCode 里回答“我的显卡超频状态”：调用 card_report，给出结论、相对出厂改了什么（打 ✅）和当前核心频率](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/local-model-card-report.jpg)

*Qwen3.8 27B 跑在 llama.cpp 里，接 OpenCode，用的正是它在汇报的这张 RTX 5090。显卡给自己写体检报告，倒也坦荡。*

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

问一句“我的卡现在怎么样”，它会把每一项设置的出厂值、当前值和改动列成一张表，在 NVIDIA 自家限值以内的打 ✅，超出的打 ⚠️，还会像 btop 那样用盲文点阵在终端里画出 V/F 曲线：出厂的、现在的，再用 ● 标出显卡此刻跑在哪。

![出厂状态下的 card_report：每一项设置的原值和现值、标记和备注，下面是盲文点阵画的 V/F 曲线](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/card-report-at-stock.jpg)

*出厂状态：每一项设置原值 → 现值，打个标记，写句备注，下面是曲线。现在和出厂还是同一条线。暂时。*

![Claude Sonnet 用盲文点阵画出调过的 V/F 曲线，现在的压在出厂的上面，● 标出显卡跑的点，并解释曲线的形状](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/vf-curve-braille.jpg)

*调过之后：Claude Sonnet 把曲线画出来，现在的压在出厂的上面，● 是显卡此刻跑的点，顺手讲讲曲线为什么长这样。*

电压、频率、功耗墙、显存、风扇、到底是什么在卡你的频率，也都直接问它，每一步为什么这么调，它都讲得出来。别的软件不怎么给你看的，它也读：显存温度、16-pin 接口的功率和整卡输入电流、PCIe 链路（跑在第几代、几条通道，有没有重传数据）、驱动自己给各个时钟域定的上下限、各由哪条限制决定，还有 Windows 等多久才重置卡死的显卡（TDR）。你打游戏的时候，它每秒看四次显卡，频率、电压、功耗墙、温度，一边看一边跟你说。游戏崩了也不白崩：崩在什么频率、什么电压、多少瓦、多少度，它都记下来，日常设置自动恢复，下一步就把曲线上那一段往下压。没有别的软件要开，也没有界面要找。

## 几句大实话

- **调参交给 AI。** 一次只动一小步。步子迈大了，显卡容易出现在二手群里。
- **所有开关都给它，没有“专家模式”。** 命令行能调的，AI 都能调：V/F 曲线（一段一段调，或者整条写）、核心偏移、NVVDD 和 MSVDD 两路电压、XBAR 和它跟核心频率的比例、SYS 和 Video、频率锁、电压提升、显存、电流墙、功耗墙、风扇停转区间。每个开关写的是什么、一次能动多少、会牵动别的什么，它自带的手册里都写着。每个工具一次只动一小步，碰到这张卡的硬上限（1150 mV、板卡最大功耗、固件自己的范围）就停，不让过的时候会告诉你为什么。
- **NVIDIA 自家的压测工具当裁判。** `nvgwls.exe` 是显卡驱动自带的（NVIDIA App 的自动超频用的就是它），每一帧画面、每一个计算结果都逐位比对。铁面无私，收买不了。
- **游戏稳定 = 压测稳定再往下退 30–60 MHz。** 压测能摸清曲线的走势，每个电压大概能跑多高；但游戏到底在哪儿崩，只有游戏自己知道。所以你打游戏，它盯着，崩一次就是一条数据。这条规矩不是我们定的，是一次次驱动重置撞出来的。
- **帧数它自己会看。** 你打游戏时，AI 能直接看到平均帧、1% 和 0.1% 低帧、帧生成时间，用的是 Intel 的 PresentMon，由后台服务来跑，不往游戏里注入任何东西。“调了有没有用”，拿帧数说话，不凭感觉。
- **想存几套就存几套。** 比如“安静”一套、“游戏”一套，一句话就能切换。显卡同一时间只跑一套，它也乐得清静。
- **一切都能撤回。** 每次写入都会回读核对，随时能退回去。后台服务记着你的日常设置，每次开机、每次睡眠唤醒、每次驱动重置后都会自动恢复，包括你自己作出来的那几次。它还会核对固件是不是真按你设的功耗墙在跑，因为真出过一次：读数全是 600 W，卡却按 575 W 跑了好几个小时。刷了别的 VBIOS，日常设置会等你点头，不会把旧的偏移直接套到新的出厂曲线上。驱动半路掉了也不用重连：Clocktail 会自己把读卡的进程重新拉起来，接着干活。
- **该停的时候它会停。** 显卡看着像卡死了，就不写新的试验。压测到 85 °C 自动停。夜里没人看着跑曲线的时候，崩两次就不再写新的试验，免得显卡一遍遍卡死也没人知道。会让显卡待机时也顶在高频的频率锁（下限、锁死某个 V/F 点、Boost Lock）只给测试用，绝不留进日常设置；只有封顶的那种会留。

![AI 把调参计划列出来：功耗墙、风扇和温度、按电压段摸 V/F 曲线的边界、整理日常曲线，最后进游戏验证](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/tuning-plan.jpg)

*动手之前先把计划摆出来：功耗墙（你来定）、风扇和温度、一段一段摸 V/F 曲线的边界、整理日常曲线，最后拿你最吃配置的游戏验证。*

测试平台只有一张卡：RTX 5090 公版，驱动 617.42，Windows 11。你的卡不一样，每张卡都不一样。

![3DMark Steel Nomad 成绩页：15 757 分，有效成绩，RTX 5090 搭配 Intel Core i9-14900K，平均频率 2,878 MHz](https://raw.githubusercontent.com/swaggypatrol/Clocktail/main/docs/images/steel-nomad-result.jpg)

*用 Clocktail 调过的 RTX 5090 跑 3DMark Steel Nomad：15 757 分，有效成绩，平均频率 2,878 MHz。参考卡之前用 founders 预设跑过 15 177 分。*

你的卡要是读出来跟我们的对不上，让 AI 助手生成一份诊断报告（diagnostic report），附在 [GitHub issue](https://github.com/swaggypatrol/Clocktail/issues) 里发给我们。就一个纯文本的 JSON 文件：版本号、显卡、每个开关在你的卡上能不能调、驱动对每次读取是怎么回答的。生成它不会往显卡写任何东西，里面没有用户名、路径或文件，你不发，它哪儿也不去。

## 卸载

```
npx -y clocktail uninstall
```

显卡恢复出厂设置，服务、文件和 AI 助手里的配置都会删干净。你的调校记录默认保留在 `%LocalAppData%\Clocktail`，加 `--purge` 一起删。显卡不会记得发生过什么。

## GitHub 上的 zip

Clocktail 是围着 AI 助手设计的，不建议手动安装。Releases 页面上的 zip 是给你研究、检查、存档用的：里面是安装程序会装的 MCP 服务器和后台服务，外加一个命令行工具，想翻、想扫毒、想留底都行。

你要是非把自己当机器人，亲手装一遍，步骤如下：

1. 右键 zip → 属性 → 勾选“解除锁定”。不然 Windows 可能会对几个没签名、从网上下来的程序多问几句，这一点它倒也没说错。
2. 解压到 `%LocalAppData%\Programs\Clocktail`。安装程序本来也装在这儿，以后换成 npx 能直接接手。
3. 把 AI 助手连到 `clocktail-mcp.exe`：stdio 方式，不带参数。Claude Code 是：
   ```
   claude mcp add clocktail -s user -- "C:\Users\<你的用户名>\AppData\Local\Programs\Clocktail\clocktail-mcp.exe"
   ```
4. 在那个文件夹里打开终端，运行 `.\Clocktail.Broker.exe install`，同意一次管理员权限。懒得弄也行，AI 助手第一次要写显卡时会自己调用 `install_service`。
5. 重启 AI 助手。

升级：先关掉所有 AI 助手（Windows 不让覆盖正在运行的程序；安装程序有办法绕过去，你是手动的，没这待遇），把新 zip 解压覆盖，再运行一次 `.\Clocktail.Broker.exe install`，不然后台服务还是旧版。卸载：依次运行 `.\Clocktail.Broker.exe stock` 和 `.\Clocktail.Broker.exe uninstall`，从 AI 助手的配置里删掉 clocktail，再删掉文件夹。调校记录在 `%LocalAppData%\Clocktail`，想删得一干二净就连它一起删。

当然，你也可以把上面“安装”那段话贴给 AI 助手，然后去泡杯茶。机器人的活，还是交给机器人干。

## 免责声明

非官方软件。超频可能导致游戏崩溃、画面出错、硬件减寿，风险自负。真出了事，调校日志会一五一十告诉你发生了什么，但不会跟你道歉。
