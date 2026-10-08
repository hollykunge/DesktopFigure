using UnityEngine;
using UnityEngine.Rendering;

namespace DesktopFigure
{
    /// <summary>
    /// 保险措施：场景加载后如果缺少相机 / 灯光 / 主对象，就在运行时自动创建，
    /// 所以即使场景是空的，程序也能正常运行。
    /// </summary>
    public static class DesktopFigureBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            // 环境光：MToon 的暗部会用到
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f, 1f);

            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
                camGo.transform.position = new Vector3(0f, 0.8f, 4.5f);
                camGo.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                cam.fieldOfView = 20f;
            }
            TransparentWindow.SetupCamera(cam);

            if (Object.FindObjectOfType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.0f;
                light.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.Euler(30f, 160f, 0f);
            }

            if (Object.FindObjectOfType<OutfitManager>() == null)
            {
                CreateAppObject();
            }
        }

        public static GameObject CreateAppObject()
        {
            var go = new GameObject("DesktopFigure");
            go.AddComponent<TransparentWindow>();
            var root = new GameObject("CharacterRoot").transform;
            root.SetParent(go.transform, false);
            var drag = go.AddComponent<WindowDragScale>();
            drag.characterRoot = root;
            go.AddComponent<IdleMotion>();
            var view = go.AddComponent<FigureViewController>();
            view.characterRoot = root;
            var outfit = go.AddComponent<OutfitManager>();
            outfit.characterRoot = root;
            return go;
        }
    }
}
