using System.IO;
using System.Linq;
using DesktopFigure;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 打开项目时自动：
/// 1) 设置透明窗口需要的 PlayerSettings；
/// 2) 把 VRM 运行时用到的 Shader 加进 Always Included Shaders（否则打包后运行时加载的模型会变粉/不显示）；
/// 3) 若 Assets/Scenes/Main.unity 不存在则自动创建并加入 Build Settings。
/// </summary>
[InitializeOnLoad]
public static class DesktopFigureProjectSetup
{
    public const string ScenePath = "Assets/Scenes/Main.unity";
    public const int WindowWidth = 345;
    public const int WindowHeight = 460;

    /// <summary>运行时加载 VRM 时 UniVRM 会通过 Shader.Find 查找的 Shader。</summary>
    static readonly string[] RuntimeShaders =
    {
        "VRM10/MToon10",
        "VRM/MToon",
        "UniGLTF/UniUnlit",
        // glTF PBR / 默认材质（例如 VRM 官方示例 Seed-san 含有此类材质）。缺少时运行时加载会报 "Value cannot be null. Parameter name: Shader"
        "Standard",
        // 绝区零风卡通渲染（运行时用 Shader.Find 生成材质，必须打进包里）
        "DesktopFigure/NPR",
    };

    static DesktopFigureProjectSetup()
    {
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            ApplyPlayerSettings();
            EnsureAlwaysIncludedShaders();
            EnsureScene();
        };
    }

    [MenuItem("DesktopFigure/Apply Project Settings (透明窗口设置)")]
    public static void ApplyAllFromMenu()
    {
        ApplyPlayerSettings();
        EnsureAlwaysIncludedShaders();
        EnsureScene();
        Debug.Log("[DesktopFigure] Project settings applied.");
    }

    public static void ApplyPlayerSettings()
    {
        PlayerSettings.companyName = "DesktopFigure";
        PlayerSettings.productName = "DesktopFigure";

        // 透明窗口关键设置：必须关闭 Flip Model 交换链，否则 DWM 透明无效（背景会变黑）
        PlayerSettings.useFlipModelSwapchain = false;

        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultIsNativeResolution = false;
        PlayerSettings.defaultScreenWidth = WindowWidth;
        PlayerSettings.defaultScreenHeight = WindowHeight;
        PlayerSettings.resizableWindow = false;
        PlayerSettings.allowFullscreenSwitch = false;
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.forceSingleInstance = true;

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

        // 透明窗口用 D3D11 最稳
        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64) ||
            apis.Length != 1 || apis[0] != GraphicsDeviceType.Direct3D11)
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        }

        // VRoid / MToon 推荐 Linear 色彩空间（只在不同的时候改，避免触发全量重导入）
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
        }
    }

    public static void EnsureAlwaysIncludedShaders()
    {
        var graphicsSettings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
        if (graphicsSettings == null) return;

        var so = new SerializedObject(graphicsSettings);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        if (arr == null) return;

        bool changed = false;
        foreach (var name in RuntimeShaders)
        {
            var shader = Shader.Find(name);
            if (shader == null)
            {
                Debug.LogWarning($"[DesktopFigure] Shader not found (UniVRM not imported yet?): {name}");
                continue;
            }
            bool exists = false;
            for (int i = 0; i < arr.arraySize; i++)
            {
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                int idx = arr.arraySize;
                arr.InsertArrayElementAtIndex(idx);
                arr.GetArrayElementAtIndex(idx).objectReferenceValue = shader;
                changed = true;
            }
        }
        if (changed)
        {
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log("[DesktopFigure] Added VRM shaders to Always Included Shaders.");
        }
    }

    public static void EnsureScene()
    {
        if (!File.Exists(ScenePath))
        {
            CreateMainScene();
        }

        var scenes = EditorBuildSettings.scenes;
        if (scenes.Length == 0 || scenes[0].path != ScenePath || !scenes[0].enabled)
        {
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) }
                .Concat(scenes.Where(s => s.path != ScenePath))
                .ToArray();
        }
    }

    static void CreateMainScene()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 相机：纯色背景 RGBA(0,0,0,0)
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        camGo.AddComponent<AudioListener>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.allowHDR = false;
        cam.fieldOfView = 20f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 50f;
        camGo.transform.position = new Vector3(0f, 0.85f, 4.8f);
        camGo.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

        // 平行光：从相机一侧照向角色
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
        light.shadows = LightShadows.None;
        lightGo.transform.rotation = Quaternion.Euler(30f, 160f, 0f);

        // 主对象
        DesktopFigureBootstrap.CreateAppObject();

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f, 1f);
        RenderSettings.skybox = null;

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.Refresh();
        Debug.Log("[DesktopFigure] Created " + ScenePath);
    }
}
