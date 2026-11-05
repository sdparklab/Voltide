# ROG Omni：同一个接收器，两条电量

在同一个 `0B05:1ACE` 接收器下，Voltide 分别显示已配对的键盘和鼠标。Windows 真机读到了 **STRIX SCOPE II 96 WIRELESS 100%** 和 **HARPE ACE AIM LAB EDITION 3%**。百分比随设备状态变化。

## 原因和代码入口

| 环节 | 代码 | 原有行为 |
|---|---|---|
| HID 枚举 | [HidNative.Enumerate](../src/Interop/HidNative.cs) | 通过 Windows SetupAPI 枚举 HID 集合，获取路径、VID/PID、用途页和报告长度。目录包含接收器的集合。 |
| 识别、配对与读取 | [AsusRogProvider](../src/Providers/AsusRogProvider.cs) | 只匹配 `0B05:1ACE / FF00`，把每个匹配项都标为 AZOTH EXTREME，使用相同 ID `asus-1ACE`。未读配对表，只发送键盘请求 `02 12 01`。 |
| 上报 | [DeviceManager.RefreshCore](../src/Core/DeviceManager.cs) | 每轮共享一次 HID 枚举，为每个发现的设备单独生成 `DeviceSnapshot`。这条链路已有多设备能力。 |
| 面板与托盘 | [MainForm.UpdateSnapshot / CollapseTransports](../src/UI/MainForm.cs)、[CardRenderer](../src/UI/CardRenderer.cs)、[TrayApp.BuildMenu](../src/UI/TrayApp.cs) | 面板按设备 ID 保存卡片状态，并按品牌与型号合并一在线一离线的候选传输。托盘逐条显示快照，休眠值按设备 ID 取回。 |

## 配对与电量协议

先按 Windows `MI_02` 接口实例归组，再查询接收器自己的配对表。不同物理接收器即使 USB PID 相同，也各自读取自己的配对表。

| 通道 | Windows 集合 / 用途页 | 请求头 | 回包数据（索引从 0 开始） |
|---|---|---|---|
| 接收器 | `Col01 / FF02` | `01 A0 00` | `[3]` 为配对数；从 `[5]` 开始，每项四字节：配对 PID（小端）、报告 ID、设备类型 |
| 键盘 | `Col02 / FF00` | `02 12 01` | `[6]` 为百分比，`[9] == 1` 表示充电 |
| 鼠标 | `Col03 / FF01` | `03 12 07` | `[5]` 为百分比，`[10] > 0` 表示充电 |

本机配对回包前 13 字节为 `01 A0 00 02 00 B0 1A 02 04 94 1A 03 05`：

- `B0 1A 02 04`：配对 PID `1AB0`，报告 `02`，键盘。
- `94 1A 03 05`：配对 PID `1A94`，报告 `03`，鼠标。

每条记录使用「接收器实例 + 报告 ID + 配对 PID」作为 `DeviceInfo.Id`，同一个接收器的两条记录共享 `ReceiverId`。`DeviceInfo.ProductId` 记录外设的配对 PID；`0B05:1ACE` 是接收器的 USB VID/PID。面板的传输合并逻辑保留每条配对记录，设备无应答时沿用对应卡片的休眠显示。

每次只接受与请求的报告 ID、命令匹配的回包。越界百分比和无有效数据的空电量回包保留为未知；带有效电压或充电状态的 0% 保留为 0%。有有效配对表时，只查询表中列出的设备。配对通道无响应时保留原来的键盘查询，成功读到电量才显示通用键盘；此时配对 PID 未知。其他适配器、刷新间隔和设置沿用原样，无需新增配置。

## 验证

在项目根目录执行：

```powershell
# 无硬件回放：双设备、不同电量、单设备、休眠和错包、接收器隔离
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\test.ps1

# 连接接收器后，构建并读取真机快照
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\Voltide-Test.exe --test
```

| 用例 | 操作 | 预期 |
|---|---|---|
| Omni 双设备 | 连接已配对一把键盘和一只鼠标的 Omni，两台外设切至 2.4 GHz 并唤醒，运行 `--test`。 | 两条记录共享 `receiver`，各有不同的 `id`、`kind` 和 HID 路径；两条 `online` 均为 `True`。 |
| 面板、托盘与不同电量 | 在两台设备电量不同时，从旧版托盘退出后运行 `.\Voltide.exe --show`。点击面板刷新，再右击新版托盘图标。 | 面板有两个型号各自的卡片，托盘有两行；每个数值和 `--test` 的对应型号一致。面板满电显示 `Full`，托盘在线时显示 `100%`。 |
| 一台设备休眠 | 两台已有读数后让其中一台休眠，再刷新。 | 清醒设备继续更新；休眠设备仍保留自己的卡片，使用灰色历史读数或 `--`。 |
| 单设备回归 | 使用仅配对一台外设的 Omni，或另接一个已支持的单设备接收器（如 G304），运行 `--test`，再查看面板和托盘。 | 该接收器只增加一条对应设备的读数；旧设备按原方式识别与刷新。 |

已通过报文回放与双设备真机 `--test`；面板和托盘按上表做目视验收。单配对 Omni、旧式配对通道回退及其他接收器的枚举回归由回放覆盖。

适配范围为上述 `0B05:1ACE` 接口和帧格式。未收录型号在沿用该协议时可按类型与 PID 显示。如果遇到不同固件下缺设备或读数异常，请记录接收器固件版本、配对的外设型号、`--test` 中的 `kind`、`vid/pid` 和 `raw`；若没有设备记录，附上配对表和电量查询的 USB 报文，并遮盖序列号与 HID 路径的实例部分。

参考：[ASUS Omni FAQ](https://rog.asus.com/support/FAQ/1050878)、[RIGStats 配对查询](https://github.com/dvalfrid/rigstats/blob/cb43edd5b83a050db2a9148c3454b00d91172db0/sensor-sidecar/Control/Lighting/OmniMouse.cs)、[RIGStats 电量回包布局](https://github.com/dvalfrid/rigstats/blob/cb43edd5b83a050db2a9148c3454b00d91172db0/sensor-sidecar/Control/Lighting/Battery.cs)、[G-Helper 型号映射](https://github.com/seerge/g-helper/blob/f62d584d24d4ea12ff902b873f9127edafbcd742/app/Peripherals/PeripheralsProvider.cs)。
