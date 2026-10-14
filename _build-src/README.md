# 构建源码说明（供后续更新版本用）

本目录包含一键修复安装器的完整构建源码，以后如需更换组件版本（例如技嘉发布新控制中心、或调整 XTU 版本）可重新生成安装器。

## 文件

| 文件 | 作用 |
|---|---|
| `AERO17Fix.cs` | 安装器 C# 源码（向导 UI + 检测/卸载/安装逻辑 + 内嵌数据解析） |
| `app.manifest` | 管理员权限声明（requireAdministrator） |
| `build.ps1` | 一键构建脚本：编译 → 拼接内嵌组件 → 输出 SHA256 |

## 如何重新构建

1. 把 `ControlCenter_Ver_22.12.20.01.exe` 和 `XTUSetup_7.5.3.3_OEM.exe` 放到本目录（或作为参数传入）
2. 运行：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

3. 生成 `AERO17_ControlCenter_Fix_Setup.exe`，脚本会打印 SHA256

如需更换 XTU 版本：
1. 修改 `AERO17Fix.cs` 中的 `TARGET_VER` 常量
2. 修改 `build.ps1` 中的内嵌文件名（`name1/name2` 与 `$xtu` 路径）保持与源码 `CC_NAME/XTU_NAME` 一致

## 内嵌数据格式

```
[exe] [CC包] [XTU包] [manifest]
manifest = "A17FIXPK"(8B) + int32条目数 + 每项(int32文件名长 + 文件名UTF8 + int64偏移 + int64长度)
```
