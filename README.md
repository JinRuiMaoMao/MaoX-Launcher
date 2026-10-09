# MaoX Launcher

一个跨平台（Windows / macOS）的 Minecraft 启动器，使用 C# + [Avalonia](https://avaloniaui.net/) 编写。

- 原版、Forge、NeoForge、Fabric、Quilt、OptiFine 一键安装
- Modrinth / CurseForge 模组、资源包、光影、数据包、整合包下载与更新
- 离线、外置登录（authlib-injector）、微软正版账号
- 基于陶瓦联机（Terracotta）的多人联机，邀请码与 HMCL、PCL 社区版互通
- 崩溃分析、版本隔离、整合包导入导出、背景图、自动更新

## 下载

到 [Releases](https://github.com/JinRuiMaoMao/MaoX-Launcher/releases) 下载：

| 系统 | 文件 |
| --- | --- |
| Windows 10/11 (x64) | `MaoX-Launcher-Windows-x64.exe` |
| macOS 14+（M 系列芯片） | `MaoX-Launcher-macOS-arm64.zip` |
| macOS 14+（Intel 芯片） | `MaoX-Launcher-macOS-x64.zip` |

Windows 版是单个 exe，放在哪个文件夹，游戏和配置就保存在哪个文件夹。
Mac 版解压后拖进"应用程序"；首次打开如果提示无法验证开发者，右键点击 → 打开，或到"系统设置 → 隐私与安全性"点"仍要打开"。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```powershell
dotnet run --project src/MaoX          # 直接运行
.\publish.ps1                          # 打包 Windows 版到 dist\
.\publish.ps1 -Mac                     # 同时打包 macOS 版（.tar.gz）
```

在 Mac 上运行 `./publish.sh` 会生成签名好的 `.app` 和 zip。

## 发布新版本

1. 修改 `src/Directory.Build.props` 里的 `Version`
2. 提交并打标签：`git tag -a v2.0.1 -m "更新内容"`，然后 `git push origin main v2.0.1`
3. GitHub Actions 会在 Windows 和 macOS 上打包、自检，并创建 Release；启动器会自动提示更新
