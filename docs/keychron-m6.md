# Keychron M6 1K / M6-A3 适配说明

本分支新增 KeychronM6Provider，并注册到 DeviceManager。

## 实测结果

2026-11-19，在本机 Windows 上通过 3434:D030 接收器查询，配对鼠标为 3434:D03F（M6 1K）。连续三次读取成功，均为 15%，未充电。GUI 与控制台版本编译成功，14 项协议检查通过，覆盖零电量、100%、充电、离线、错误型号、截断报文及缓存身份回复。

本次只实测了上述 2.4G 连接。D031 Type-C 接收器和 D03F 有线连接按同协议实现但尚未实测；蓝牙和 M6 4K/8K 不在本次适配范围内。实际休眠、关机、插线充电切换尚未验证。程序在无线连接位未设置时不显示在线百分比，界面沿用上游保留上次读数的逻辑。

## 查询协议

在 Usage Page 0x008C、Usage 1 上使用 21 字节 Feature Report，包含 Report ID 0x51。仅发送身份与电量查询，不修改设置、重置设备或刷写固件。

- 51 03：配对 VID 位于偏移 3–4，PID 位于 5–6，小端序。接收器被多个型号共用，只有配对身份为 3434:D03F 才匹配 M6。
- 51 06：VID 位于偏移 4–5，PID 位于 6–7；偏移 10 的 bit 3 为无线连接状态；偏移 11 为电量百分比；偏移 12 非零为充电。
- 偏移从 0 开始，包括 Report ID。实测原始状态：

    51 06 01 00 34 34 3F D0 06 01 09 0F 00 00 00 00 00 00 00 00 00

接收器有时会忽略过于紧密的查询，或暂时返回上一条命令的缓冲区。适配器预留事务间隔，并有限轮询匹配的命令回复，同时校验设备身份和百分比范围，超时返回未知状态。

协议参考（独立 C# 实现，未复制 Swift 源码）：

- https://github.com/ernestnurma/engine-for-keychron/blob/main/Sources/KeychronKit/Protocol/Classic/ClassicCommands.swift
- https://github.com/ernestnurma/engine-for-keychron/blob/main/Sources/KeychronKit/Protocol/Classic/ClassicParsers.swift
- https://github.com/ernestnurma/engine-for-keychron/blob/main/Sources/KeychronKit/Catalog/ModelCatalog.swift

## 构建和测试

    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/keychron.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/keychron.ps1 -Hardware

最后一项需要连接 M6，只查询 M6，不查询其他品牌设备。上游仓库未附带 Fonts 目录，本次构建使用系统字体回退，外观可能与官方截图略有不同。
