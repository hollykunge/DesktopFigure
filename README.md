# DesktopFigure —— VRoid / VRM 桌面宠物

[English](#english)

把你用 VRoid Studio 做的角色（或任何 VRM 模型）放到桌面上：透明、无边框、置顶的 3D 桌宠，可以拖动、缩放、360° 旋转、换装、眨眼、看向鼠标。

基于 Unity 2022.3（Built-in 管线）+ [UniVRM](https://github.com/vrm-c/UniVRM)。

## 功能

- **VRoid / VRM 桌宠**：支持 VRM 0.x 和 VRM 1.0，运行时加载，不需要重新打包。
- **换装**：`Outfits` 文件夹里的每个 `.vrm` 文件就是一套服装，右键菜单或数字键 1~9 切换，会记住上次的选择。
- **透明窗口 + 点击穿透**：只有角色本身能点到，透明区域的点击会穿透到后面的桌面 / 窗口。
- **3D 旋转控制**：
  - 左键拖动角色 = 移动
  - 右键拖动 = 左右旋转（360°）
  - 中键拖动（或 Shift + 右键拖动）= 俯仰视角
  - 滚轮 = 缩放窗口（脚底位置不变）
  - Ctrl + 滚轮 = 镜头远近（macOS 上 Command + 滚轮也可以）
  - 左键双击 = 原地转一圈
- **Windows 11 风格右键菜单**：亚克力质感、圆角、图标、子菜单（衣橱、设置）；可改宠物名称（支持中文输入法）、自动旋转、重置视角、始终置顶、操作说明。
- **绝区零（ZZZ）风格卡通渲染**：右键菜单 → 设置 → 渲染风格，可在「原版（MToon）」和「绝区零风」之间实时切换。
- **托盘图标**：程序不占任务栏，Windows 在系统托盘、macOS 在菜单栏有图标，菜单里可以「显示 / 隐藏」桌宠或退出。
- 窗口位置、大小、视角、服装、名称都会自动保存。

## 下载

到 [Releases](https://github.com/hollykunge/DesktopFigure/releases) 下载：

| 平台 | 文件 | 说明 |
|---|---|---|
| Windows 10 / 11 (x64) | `DesktopFigure-Windows-x64-v1.0.0.zip` | 解压后运行 `DesktopFigure.exe` |
| macOS（Intel / Apple 芯片通用，Universal） | `DesktopFigure-macOS-v1.0.0.zip` | 解压得到 `DesktopFigure.app` |

### Windows

解压到任意文件夹，双击 `DesktopFigure.exe`。服装文件夹是 exe 旁边的 `Outfits`。
程序不在任务栏显示，在右下角系统托盘里找图标（右键：显示 / 隐藏 / 退出）。

### macOS（⚠️ 未经实机测试）

> Mac 版是在 Windows 上交叉编译的，作者手上没有 Mac，**没有在真机上测试过**。透明窗口、点击穿透、菜单栏图标等功能是通过 Objective-C 运行时调用 AppKit 实现的，可能存在问题，欢迎提 Issue / PR。

1. 解压得到 `DesktopFigure.app`（可以拖到「应用程序」文件夹）。
2. 应用没有签名 / 公证，第一次打开时 macOS 会拦截：
   - 在访达里 **右键（或按住 Control 点按）`DesktopFigure.app` → 打开**，在弹出的对话框里再点「打开」；或者
   - 在终端执行：
     ```bash
     xattr -dr com.apple.quarantine /path/to/DesktopFigure.app
     ```
3. 如果在 Apple 芯片的 Mac 上提示「已损坏」或闪退，可以给它做一个本地临时签名：
   ```bash
   codesign --force --deep -s - /path/to/DesktopFigure.app
   ```
4. 程序不显示 Dock 图标，在屏幕顶部的**菜单栏**里有一个小人图标（显示 / 隐藏 / 退出）。

**macOS 的服装文件夹**（右键菜单 → 衣橱 → 打开 Outfits 文件夹 会直接在访达里打开实际使用的文件夹）：

- 如果 `DesktopFigure.app` 旁边有一个 `Outfits` 文件夹，就用它（和 Windows 一样）；
- 否则使用 `~/Library/Application Support/DesktopFigure/DesktopFigure/Outfits`，第一次运行时会把 App 里内置的默认角色复制进去。

## 添加你自己的服装 / 角色

1. 在 [VRoid Studio](https://vroid.com/studio) 里打开角色，换好一套衣服。
2. 右上角 **导出 → 导出为 VRM**（VRM 0.0 或 1.0 都可以）。
3. 把 `.vrm` 文件放进 `Outfits` 文件夹，文件名就是菜单里的服装名（例如 `01_校服.vrm`、`02_便服.vrm`）。
4. 在桌宠上点右键 → 衣橱 → **刷新服装列表**，不用重启。前 9 套可以用数字键 1~9 切换。

也可以使用其它 VRM 模型，但请遵守模型作者的使用条款（VRM 文件里的 meta 许可）。

## 从源码构建

需要 **Unity 2022.3.62f3**（其它 2022.3 版本大概率也可以）。

1. 用 Unity Hub 安装 Unity 2022.3.62f3；要打 Mac 包的话再添加模块 **Mac Build Support (Mono)**（在 Windows 上也可以打 Mac 包）。
2. Unity Hub → Add project from disk → 选择本仓库文件夹。第一次打开会导入 UniVRM（已包含在 `LocalPackages`，不需要联网）并自动：
   - 创建 / 检查场景 `Assets/Scenes/Main.unity`；
   - 设置透明窗口需要的 Player Settings（关闭 Flip Model 交换链、D3D11、窗口大小等）；
   - 把 VRM 运行时用到的 Shader 加入 Always Included Shaders。
3. 菜单 **DesktopFigure → Build Windows exe** / **DesktopFigure → Build macOS app**，或命令行：

```powershell
# Windows -> Build/DesktopFigure.exe（会把 Outfits 复制到 Build/Outfits）
Unity.exe -batchmode -quit -projectPath <仓库路径> -executeMethod BuildScript.BuildWindows -logFile build_win.log
# macOS -> Build/Mac/DesktopFigure.app（Universal；Outfits 复制到 App 内的 Contents/Resources/Outfits，Info.plist 设置 LSUIElement）
Unity.exe -batchmode -quit -projectPath <仓库路径> -executeMethod BuildScript.BuildMac -logFile build_mac.log
```

> Unity Personal 版启动时会先显示 “Made with Unity” 画面，之后窗口才变透明。

### 代码结构

```
Assets/Scripts/TransparentWindow.cs     透明 / 无边框 / 置顶 / 按像素 alpha 点击穿透（Win32；macOS 分支调用 MacNative）
Assets/Scripts/MacNative.cs             macOS：P/Invoke Objective-C 运行时操作 NSWindow（无需原生插件）
Assets/Scripts/MacStatusItem.cs         macOS：菜单栏图标（NSStatusItem + 运行时注册的 Objective-C 类）
Assets/Scripts/TrayIcon.cs              Windows：系统托盘图标（Shell_NotifyIcon）
Assets/Scripts/WindowDragScale.cs       拖动 / 滚轮缩放窗口；窗口 = 角色区域 + 透明边距
Assets/Scripts/FigureViewController.cs  3D 视角：旋转、俯仰、镜头远近、自动旋转
Assets/Scripts/OutfitManager*.cs        Outfits 加载（Vrm10.LoadPathAsync）、右键菜单、宠物名称
Assets/Scripts/MenuSkin.cs              Win11 风格 IMGUI 皮肤（运行时按 DPI 生成纹理；图标字体）
Assets/Scripts/IdleMotion.cs            呼吸、摇摆、眨眼、看向鼠标
Assets/Scripts/NprStyle.cs              绝区零风材质转换；Assets/Shaders/DesktopFigureNPR.shader
Assets/Editor/BuildScript.cs            BuildWindows / BuildMac 打包脚本 + macOS 打包后处理
LocalPackages/                          UniVRM v0.131.3（MIT）
Outfits/                                服装（仓库里只有作者的示例角色）
```

## 许可证

- **源代码**：[MIT License](LICENSE)，Copyright (c) 2026 dodo。
- **第三方组件**：UniVRM（MIT）、Material Icons 图标字体（Apache 2.0）等，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
- 仓库和发布包里**不包含**任何微软字体文件；Windows 上的 Segoe 图标 / 微软雅黑只从系统加载。

### 模型许可

`Outfits/我的角色.vrm`（角色名「裹裹」）由本项目作者使用 VRoid Studio 制作，**不属于 MIT 许可范围**，使用条款以 VRM 文件 meta 中嵌入的许可为准（[VRM Public License 1.0](https://vrm.dev/licenses/1.0/)）。meta 中的设置摘要：

| 项目 | 设置 |
|---|---|
| 作为化身使用（avatarPermission） | 仅作者本人（onlyAuthor） |
| 商业用途（commercialUsage） | 仅限个人非营利（personalNonProfit） |
| 过激暴力表现 / 性表现 | 不允许 / 不允许 |
| 政治或宗教用途 / 反社会或仇恨表现 | 不允许 / 不允许 |
| 署名（creditNotation） | 需要署名 |
| 再分发（allowRedistribution） | 不允许 |
| 修改（modification） | 不允许 |

也就是说：这个模型随本项目一起提供，方便你直接体验程序；请不要把它单独再分发、修改，或用于上述不允许的用途。想用自己的角色，请参照上面的「添加你自己的服装 / 角色」。

---

## English

**DesktopFigure** is a transparent, borderless, always-on-top 3D desktop pet for VRoid / VRM models, built with Unity 2022.3 (Built-in pipeline) and UniVRM.

**Features**: VRM 0.x / 1.0 runtime loading; outfit switching — every `.vrm` in the `Outfits` folder is one outfit (right-click menu or keys 1–9); per-pixel click-through; 3D controls (left-drag move, right-drag rotate, middle-drag or Shift+right-drag pitch, wheel = window size, Ctrl+wheel = zoom, double-click = spin); Windows 11-style right-click menu; ZZZ (Zenless Zone Zero)-style toon shading toggle; tray icon (Windows) / menu-bar icon (macOS).

**Download** from [Releases](https://github.com/hollykunge/DesktopFigure/releases): `DesktopFigure-Windows-x64-v1.0.0.zip` (run `DesktopFigure.exe`) or `DesktopFigure-macOS-v1.0.0.zip` (universal `DesktopFigure.app`).

**macOS (untested!)**: the Mac build is cross-compiled on Windows and has never been run on a real Mac. The app is unsigned: right-click → Open, or run `xattr -dr com.apple.quarantine DesktopFigure.app`. If it is reported as damaged on Apple silicon, run `codesign --force --deep -s - DesktopFigure.app`. The outfits folder is `Outfits` next to the `.app` if it exists, otherwise `~/Library/Application Support/DesktopFigure/DesktopFigure/Outfits` (menu → Wardrobe → Open Outfits folder).

**Build from source**: Unity 2022.3.62f3 (+ "Mac Build Support (Mono)" module for macOS). Use the menu *DesktopFigure → Build Windows exe / Build macOS app*, or `-executeMethod BuildScript.BuildWindows` / `BuildScript.BuildMac` in batch mode.

**License**: source code is MIT (© 2026 dodo). UniVRM is MIT, Material Icons is Apache 2.0 (see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)). No Microsoft font files are included. The bundled model `Outfits/我的角色.vrm` was made by the author with VRoid Studio and is **not** MIT-licensed; its terms are those embedded in the VRM meta (VRM Public License 1.0: avatar use by the author only, personal non-profit use only, credit required, no redistribution, no modification, no violent/sexual/political/religious/antisocial use).
