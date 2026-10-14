# 技嘉 AERO 17-XA 控制中心：重装与故障解决记录

日期：2026-09-15　适用机器：GIGABYTE AERO 17-XA / Intel Core i7-9750H

**实测机型配置**：Intel Core i7-9750H（第 9 代酷睿 H 系列）+ NVIDIA GeForce RTX 2070 Max-Q，控制中心 22.12.20.01 配套 XTU 7.5.3.3。

**适用范围**：同一款机型，或采用同代 CPU（Intel 第 9 代酷睿 H 系列，如 i7-9750H / i7-9850H / i5-9300H / i9-9880H 等）且使用同版本控制中心的技嘉笔记本，都可能遇到同样的问题（根因是控制中心依赖的 Intel XTU 组件版本错配，与具体型号无关）。其他机型请先核对控制中心与 XTU 的配套版本再操作。

**本次有效方案：恢复原厂 Control Center 22.12.20.01，并把 XTU 客户端和服务统一为原厂配套的 7.5.3.3，随后重启 Windows。** 用户反馈启动和键盘均恢复正常。CPU/XTU 调参功能未被禁用。

## 1. 到底是什么问题？

本次“打不开”的直接证据指向 XTU 组件错配，不是 SMV4 服务未启动：

- 原先使用的新版程序出现 `GetSpeedOptimizerState → Tune → CPUOC.CPU.OC` 空引用崩溃。
- 重装原厂控制中心后，程序目录里的 XTU SDK 已是 **7.5.3.3**，但系统后台 XTU 服务仍是 **7.14.2.14**。
- 最新日志出现 `FormatException / SerializationException / NetDispatcherFaultException`，调用栈经过 `IronCity.Core.IXtuTuning.ProposeChange`。这表明 CPU 调参通信的返回数据解析失败。
- 移除 7.14，安装原厂附带的 7.5.3.3，重启后恢复正常。

**结论范围：** XTU 配套修复解决了这次实际故障。键盘也随之恢复正常，但没有做单独的键盘延迟 A/B 测量，因此不能严谨地断言此前所有键盘卡顿都只由 XTU 引起。SMV4 目前正常运行，键盘也正常，所以不应再把“永久禁用 SMV4”当作本机必需修复。

先前制作的 SmartManager 键盘钩子实验补丁已还原，本次成功方案没有使用它。最终安装目录里的 SmartManager.dll 与原始安装包解出的文件 SHA256 完全相同。

## 2. 下次全新重装 Windows：按这个顺序

1. 完成该机型所需的 Windows 和厂商基础驱动安装。
2. 使用本文件夹中的 **ControlCenter_Ver_22.12.20.01.exe**，按默认路径安装到 `C:\Program Files\ControlCenter`。
3. 检查 XTU 是否已安装，以及服务文件和 SDK 是否都是 **7.5.3.3**。可以运行本文件夹中的 `检查安装.cmd`；它只读取信息，不修改系统。
4. 如果原厂安装程序没有成功装上 XTU，单独运行本文件夹中的 **XTUSetup_7.5.3.3_OEM.exe**。这是从上述原厂安装包提取的原文件，仅改了文件名，Intel 签名有效。
5. 接受你主动启动的安装程序所需的 Windows 管理员确认，完成安装。
6. 保存工作，执行 Windows **“重新启动”**。本次安装返回了 3010，意思是安装成功但必须重启，不是安装失败。
7. 从开始菜单或 `C:\Program Files\ControlCenter\ControlCenter.exe` 打开程序。
8. 验证界面、风扇控制、亮度、键盘背光、内置/外接键盘输入，以及日常使用的 CPU 档位功能。

不要把旧备份目录里的 24.12.24.01 主程序与这套 2022 年组件混用。安装包版本与内部各模块版本不完全一样是正常现象，下面列的是本次实测正常组合。

## 3. 已经装了但打不开：修复步骤

### A. 先核对版本，避免重复安装碰运气

运行 `检查安装.cmd`，重点看：

| 项目 | 本次正常版本/状态 |
|---|---|
| 原厂控制中心安装包 | 22.12.20.01 |
| ControlCenter.exe | 22.09.12.01 |
| SmartManager.dll | 22.12.20.01 |
| SMV4_Service.exe | 22.09.12.02 |
| IntelOverclockingSDK.dll | 7.5.3.3 |
| XtuCommon.dll | 7.5.3.3 |
| `C:\Windows\SysWOW64\XtuService.exe` | 7.5.3.3 |
| `SMV4_Service` 服务 | Running / 自动 |
| `XTU3SERVICE` 服务 | Running / 自动 |

如果 SDK 是 7.5.3.3，而后台服务是 7.14.2.14，正好与本次故障一致。**只重装控制中心可能不会移除较新的 XTU，因此这一步不能省。**

### B. 备份，然后恢复配套 XTU

1. 退出控制中心和 XTU 界面。如控制中心没有窗口但后台残留，可在任务管理器中结束 `ControlCenter.exe`；如仍提示 Fusion 正在运行，再结束 `FusionShortcut.exe`。
2. 如有自定义 XTU 配置，先从 XTU 导出配置（若能打开），并备份 `C:\ProgramData\Intel\Intel Extreme Tuning Utility`。旧配置跨版本可能不兼容，保留备份，不要直接覆盖新版本配置。
3. 打开“设置 → 应用 → 已安装的应用”，卸载已确认不配套的 **Intel Extreme Tuning Utility 7.14.2.14**。若有多个相关条目，先核对版本和名称，不要批量删除 Intel 软件。
4. 安装本文件夹的 **XTUSetup_7.5.3.3_OEM.exe**。
5. 如果安装程序明确要求先重启，先保存工作并重启，再继续安装；安装完成后按提示重启。本次实际过程是卸载 7.14 成功，安装 7.5 返回 3010，再重启。
6. 重启后再次运行检查工具，确认服务和 SDK 都为 7.5.3.3，再打开安装目录中的控制中心。

如果卸载入口损坏，不要照抄网上任意 MSI 产品 GUID 或删除服务。本次使用的是系统实际登记的官方卸载程序，不同版本的路径和 GUID 会变化。先检查安装记录或使用同版本官方安装程序修复卸载入口。

### C. 服务名别看错

本机实际使用的是 **XTU3SERVICE**，它的可执行文件名也是 `XtuService.exe`。

另外还残留了一个名字恰好叫 **XtuService** 的服务，指向 `C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Service\XtuService.exe`。该路径本次不存在，这个服务保持停止，而控制中心已经正常。不要为了让所有服务都“运行”而反复启动它；本次修复没有依赖删除这个残留项。

## 4. 如果配套版本正确，重启后仍打不开

打开“事件查看器 → Windows 日志 → 应用程序”，查看本次点击启动时间附近的 `.NET Runtime` 和 `Application Error`：

- 出现 `IronCity`、`ProposeChange`、`TuningLibrary`、`CPUOC`：继续核对 XTU 客户端、服务、驱动及配置；不要只盯着 SMV4。
- 出现其他 DLL、驱动或异常：保存完整错误文本，按新错误排查，不能把所有打不开都套用成本次问题。
- 有进程、没窗口：先检查错误记录和是否存在残留实例，不要把目录下所有 EXE 依次打开。部分 EXE 是安装、服务或辅助工具，不是主程序。

如果安装失败或日志要求重启，先处理安装结果，不要手工混换 SDK DLL、绕过驱动签名或修改系统安全设置来强行运行。

## 5. 防止再次复发

- 把本资料文件夹整体备份到移动硬盘或自己的备份位置，避免重装系统时丢失。
- 对这台机器，这套 OEM 配套组合已实测正常；以后升级 XTU 或控制中心前先保留回退包，再验证整套兼容性。本结论不是要求其他电脑一律降级 XTU。
- 不要单独拿新版 XTU 服务配旧版控制中心 SDK，也不要把 D 盘旧测试目录里的 DLL 覆盖到正常安装目录。
- 无需应用之前的键盘钩子补丁、禁用 SMV4、关闭 CPU 调参或刷 BIOS来重复本次修复。

## 6. 离线资料与校验

本文件夹包含：

- `ControlCenter_Ver_22.12.20.01.exe`：本次原厂安装包，技嘉签名有效。
- `XTUSetup_7.5.3.3_OEM.exe`：原厂安装包中的配套 XTU，Intel 签名有效。
- `检查安装.cmd` / `Check-Setup.ps1`：只读检查版本、服务和安装包完整性。
- `SHA256.json`：安装文件的哈希记录。

SHA256：

```text
ControlCenter_Ver_22.12.20.01.exe
A045FAF771725A9FDAE197D4F6506D2601B84AD3A4F83B9737A2F396B58E53D8

XTUSetup_7.5.3.3_OEM.exe
762005E092AF832C54D6CA9876939D6969B24CAF2691B6C4A9CB733B3ACB0F9A
```

本次恢复后的只读复核：SMV4_Service 和 XTU3SERVICE 均运行；SDK、XTU 服务文件均为 7.5.3.3；用户确认一切正常。未额外进行超频压力测试或物理键盘延迟测量。
