【服装文件夹 Outfits】

把在 VRoid Studio 里导出的 .vrm 文件放到这个文件夹，每个 .vrm 文件就是一套服装。

做法：
1. 在 VRoid Studio 里打开你的角色，换好一套衣服。
2. 右上角“导出”→“导出为 VRM”（VRM 0.0 或 VRM 1.0 都可以），
   保存到这个 Outfits 文件夹。
3. 换另一套衣服，再导出一次，用不同的文件名保存。

文件名就是菜单里显示的服装名，例如：
    01_校服.vrm
    02_便服.vrm
    03_睡衣.vrm
（菜单按文件名排序；前 9 个可以用数字键 1~9 直接切换。）

在桌宠上点右键 → 衣橱 →“刷新服装列表”即可看到新放入的文件，无需重启。

注意：
- Windows 版读取的是 exe 旁边的 Outfits 文件夹（Build\Outfits）。
- macOS 版：DesktopFigure.app 旁边有 Outfits 文件夹时用它，否则用
  ~/Library/Application Support/DesktopFigure/DesktopFigure/Outfits
  （右键菜单 → 衣橱 →“打开 Outfits 文件夹”会直接打开实际使用的文件夹）。
- 在 Unity 编辑器里运行时读取的是项目根目录的 Outfits 文件夹（就是这里）。
- 打包时（BuildScript）会自动把这里的文件复制到 Build\Outfits（Windows）
  或 DesktopFigure.app/Contents/Resources/Outfits（macOS）。
- 我的角色.vrm 是作者的示例角色，使用条款见项目 README 的“模型许可”。
