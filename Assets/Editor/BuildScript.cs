using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 一键打包：
///   菜单 DesktopFigure/Build Windows exe、DesktopFigure/Build macOS app
///   或命令行：
///     Unity.exe -batchmode -quit -projectPath &lt;项目&gt; -executeMethod BuildScript.BuildWindows -logFile build_win.log
///     Unity.exe -batchmode -quit -projectPath &lt;项目&gt; -executeMethod BuildScript.BuildMac -logFile build_mac.log
/// Windows 输出：&lt;项目&gt;/Build/DesktopFigure.exe，并把 &lt;项目&gt;/Outfits 复制到 exe 旁边（Build/Outfits）。
/// macOS 输出：&lt;项目&gt;/Build/Mac/DesktopFigure.app（Intel + Apple 芯片通用，Mono），
///   &lt;项目&gt;/Outfits 复制到 DesktopFigure.app/Contents/Resources/Outfits（内置默认模型），Info.plist 加 LSUIElement=true（不显示 Dock 图标）。
///   在 Windows 上也能打 Mac 包（需要 Unity Hub 安装 “Mac Build Support (Mono)” 模块）。
/// </summary>
public static class BuildScript
{
    public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    public static string BuildDir => Path.Combine(ProjectRoot, "Build");
    public static string ExePath => Path.Combine(BuildDir, "DesktopFigure.exe");
    public static string MacBuildDir => Path.Combine(BuildDir, "Mac");
    public static string MacAppPath => Path.Combine(MacBuildDir, "DesktopFigure.app");

    [MenuItem("DesktopFigure/Build Windows exe")]
    public static void BuildWindowsMenu()
    {
        BuildWindows();
    }

    [MenuItem("DesktopFigure/Build macOS app")]
    public static void BuildMacMenu()
    {
        BuildMac();
    }

    static void PrepareProject()
    {
        DesktopFigureProjectSetup.ApplyPlayerSettings();
        DesktopFigureProjectSetup.EnsureAlwaysIncludedShaders();
        DesktopFigureProjectSetup.EnsureScene();
        AssetDatabase.SaveAssets();
    }

    public static void BuildWindows()
    {
        try
        {
            PrepareProject();

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
            }

            Directory.CreateDirectory(BuildDir);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { DesktopFigureProjectSetup.ScenePath },
                locationPathName = ExePath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                CopyOutfits(Path.Combine(BuildDir, "Outfits"));
                Debug.Log($"[DesktopFigure] Build succeeded: {ExePath} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime})");
                if (!Application.isBatchMode)
                {
                    EditorUtility.RevealInFinder(ExePath);
                }
            }
            else
            {
                Debug.LogError($"[DesktopFigure] Build failed: {summary.result}, errors: {summary.totalErrors}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            else throw;
        }
    }

    public static void BuildMac()
    {
        try
        {
            PrepareProject();
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX))
            {
                throw new Exception("macOS build support is not installed. Unity Hub → Installs → 2022.3.62f3 → Add modules → Mac Build Support (Mono).");
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX);
            }

            // Intel + Apple 芯片通用二进制（Mono 后端支持）
            SetMacArchitectureUniversal();
            // Retina：渲染分辨率 = point × backingScaleFactor（MacNative.cs 的坐标换算依赖这一点）
            PlayerSettings.macRetinaSupport = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, true);   // Metal
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone, "com.dodo.desktopfigure");

            if (Directory.Exists(MacAppPath)) Directory.Delete(MacAppPath, true);
            Directory.CreateDirectory(MacBuildDir);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { DesktopFigureProjectSetup.ScenePath },
                locationPathName = MacAppPath,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                // Outfits 复制 / Info.plist 修改在 MacPostprocess 里完成（菜单 Build Settings 打包时也会执行）
                Debug.Log($"[DesktopFigure] macOS build succeeded: {MacAppPath} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime})");
                if (!Application.isBatchMode) EditorUtility.RevealInFinder(MacAppPath);
            }
            else
            {
                Debug.LogError($"[DesktopFigure] macOS build failed: {summary.result}, errors: {summary.totalErrors}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            else throw;
        }
    }

    /// <summary>
    /// 等价于 UnityEditor.OSXStandalone.UserBuildSettings.architecture = x64ARM64。
    /// 用反射是因为该类型在 Mac 打包模块的程序集里：没装模块的人编译本脚本时不会因此报错。
    /// </summary>
    static void SetMacArchitectureUniversal()
    {
        Type t = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType("UnityEditor.OSXStandalone.UserBuildSettings", false);
            if (t != null) break;
        }
        var prop = t?.GetProperty("architecture", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (prop != null && prop.PropertyType.IsEnum)
        {
            prop.SetValue(null, Enum.Parse(prop.PropertyType, "x64ARM64"));
            Debug.Log("[DesktopFigure] macOS architecture: " + prop.GetValue(null));
        }
        else
        {
            EditorUserBuildSettings.SetPlatformSettings(BuildPipeline.GetBuildTargetName(BuildTarget.StandaloneOSX), "Architecture", "x64ARM64");
            Debug.LogWarning("[DesktopFigure] OSXStandalone.UserBuildSettings not found; set platform setting Architecture=x64ARM64");
        }
    }

    /// <summary>把 &lt;项目&gt;/Outfits 里的文件（不含 .meta）复制到 dst。</summary>
    public static void CopyOutfits(string dst)
    {
        string src = Path.Combine(ProjectRoot, "Outfits");
        Directory.CreateDirectory(dst);
        if (!Directory.Exists(src)) return;
        foreach (var file in Directory.GetFiles(src))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dst, name), true);
        }
        Debug.Log($"[DesktopFigure] Copied Outfits -> {dst}");
    }
}

/// <summary>
/// macOS 打包后处理：
/// 1) Info.plist 加 LSUIElement=true（菜单栏应用：不显示 Dock 图标、不出现在 Cmd-Tab）；
/// 2) 把 &lt;项目&gt;/Outfits 复制到 DesktopFigure.app/Contents/Resources/Outfits（默认模型，首次运行时复制到用户的 Outfits 文件夹）。
/// 注意：修改 .app 内容会使原有代码签名失效；在 Apple 芯片上若无法启动，可执行 codesign --force --deep -s - DesktopFigure.app（见 README）。
/// </summary>
public class MacPostprocess : IPostprocessBuildWithReport
{
    public int callbackOrder => 100;

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.StandaloneOSX) return;
        string app = report.summary.outputPath;
        if (!app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) app += ".app";
        if (!Directory.Exists(app))
        {
            Debug.LogWarning("[DesktopFigure] macOS postprocess: app bundle not found at " + app);
            return;
        }
        PatchInfoPlist(Path.Combine(app, "Contents", "Info.plist"));
        BuildScript.CopyOutfits(Path.Combine(app, "Contents", "Resources", "Outfits"));
    }

    static void PatchInfoPlist(string plist)
    {
        if (!File.Exists(plist))
        {
            Debug.LogWarning("[DesktopFigure] Info.plist not found: " + plist);
            return;
        }
        string text = File.ReadAllText(plist, Encoding.UTF8);
        if (text.Contains("<key>LSUIElement</key>"))
        {
            Debug.Log("[DesktopFigure] Info.plist already has LSUIElement");
            return;
        }
        int end = text.LastIndexOf("</dict>", StringComparison.Ordinal);
        if (end < 0)
        {
            Debug.LogWarning("[DesktopFigure] Info.plist is not an XML plist; LSUIElement not added");
            return;
        }
        text = text.Substring(0, end) + "\t<key>LSUIElement</key>\n\t<true/>\n" + text.Substring(end);
        File.WriteAllText(plist, text, new UTF8Encoding(false));
        Debug.Log("[DesktopFigure] Info.plist: LSUIElement=true");
    }
}
