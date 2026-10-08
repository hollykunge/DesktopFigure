using System.Collections;
using UnityEngine;
using UniVRM10;

namespace DesktopFigure
{
    /// <summary>
    /// 3D 视角控制：
    /// - 右键拖动（水平）：角色绕竖直轴旋转（360°，平滑）
    /// - 右键单击（移动 &lt; 5px）：打开菜单
    /// - 中键拖动 / Shift+右键拖动：镜头上下环绕（俯仰 -30°..+60°）
    /// - Ctrl+滚轮：镜头远近
    /// - 左键双击：原地转一圈
    /// - 自动旋转（转台）
    /// 镜头按模型实际包围体（摆好姿势后烘焙网格测量）自动取景，任何角度都能完整显示全身。
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class FigureViewController : MonoBehaviour
    {
        public static FigureViewController Instance { get; private set; }

        public Transform characterRoot;
        public float cameraFov = 20f;

        [Header("灵敏度")]
        public float yawDegPerPixel = 0.45f;
        public float pitchDegPerPixel = 0.3f;
        public float minPitch = -30f;
        public float maxPitch = 60f;
        public float minZoom = 0.35f;
        public float maxZoom = 2.0f;
        public float autoRotateSpeed = 25f; // 度/秒
        public float clickThresholdPx = 5f;

        const string KeyYaw = "DF_Yaw";
        const string KeyPitch = "DF_Pitch";
        const string KeyZoom = "DF_Zoom";
        const string KeyAuto = "DF_AutoRotate";

        public bool AutoRotate { get; private set; }
        public bool IsRotatingByUser => _dragMode != DragMode.None && _dragMoved;

        // 当前 / 目标值
        float _yaw, _targetYaw;
        float _pitch, _targetPitch;
        float _zoom = 1f, _targetZoom = 1f;

        // 模型包围体（模型本地坐标，绕原点竖直轴的圆柱）
        float _minY = 0f, _maxY = 1.6f, _radius = 0.35f;
        float _facing = 1f;
        Vrm10Instance _model;
        Coroutine _measure;
        Light[] _lights;

        enum DragMode { None, Yaw, Pitch }
        DragMode _dragMode = DragMode.None;
        int _dragButton = -1;
        bool _dragMoved;
        Vector2 _pressPos, _lastPos;

        float _lastLeftClickTime = -10f;
        Vector2 _lastLeftClickPos;

        void Awake()
        {
            Instance = this;
            _targetYaw = _yaw = PlayerPrefs.GetFloat(KeyYaw, 0f);
            _targetPitch = _pitch = Mathf.Clamp(PlayerPrefs.GetFloat(KeyPitch, 0f), minPitch, maxPitch);
            _targetZoom = _zoom = Mathf.Clamp(PlayerPrefs.GetFloat(KeyZoom, 1f), minZoom, maxZoom);
            AutoRotate = PlayerPrefs.GetInt(KeyAuto, 0) != 0;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // 模型 / 取景
        // ------------------------------------------------------------------

        public void SetModel(Vrm10Instance model, float facing)
        {
            _model = model;
            _facing = facing >= 0f ? 1f : -1f;

            // 先用骨骼粗略估计，等姿势生效后再精确测量
            _minY = 0f;
            _maxY = 1.6f;
            _radius = 0.35f;
            if (model != null && model.TryGetBoneTransform(HumanBodyBones.Head, out var head) && head != null)
            {
                float headY = model.transform.InverseTransformPoint(head.position).y;
                if (headY > 0.2f) _maxY = headY * 1.12f + 0.08f;
                _radius = Mathf.Max(0.3f, _maxY * 0.2f);
            }
            if (_measure != null) StopCoroutine(_measure);
            _measure = StartCoroutine(MeasureAfterPose(model));
        }

        IEnumerator MeasureAfterPose(Vrm10Instance model)
        {
            // 等待 IdleMotion 把手臂放下、ControlRig 应用到骨骼
            for (int i = 0; i < 4; i++) yield return null;
            if (model == null || model != _model) yield break;
            MeasureBounds(model);
            _measure = null;
        }

        void MeasureBounds(Vrm10Instance model)
        {
            var root = model.transform;
            float minY = float.MaxValue, maxY = float.MinValue, r2 = 0f;
            bool any = false;
            var baked = new Mesh();
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr == null || smr.sharedMesh == null) continue;
                bool ok = false;
                try
                {
                    smr.BakeMesh(baked, false);
                    var verts = baked.vertices;
                    if (verts != null && verts.Length > 0)
                    {
                        var t = smr.transform;
                        // useScale=false：顶点相对于渲染器的位置和旋转（不含缩放）
                        var m = root.worldToLocalMatrix * Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                        int step = verts.Length > 60000 ? 4 : (verts.Length > 20000 ? 2 : 1);
                        for (int i = 0; i < verts.Length; i += step)
                        {
                            Vector3 p = m.MultiplyPoint3x4(verts[i]);
                            if (p.y < minY) minY = p.y;
                            if (p.y > maxY) maxY = p.y;
                            float d = p.x * p.x + p.z * p.z;
                            if (d > r2) r2 = d;
                        }
                        ok = true;
                        any = true;
                    }
                }
                catch (System.Exception)
                {
                    ok = false;
                }
                if (!ok)
                {
                    // 退化：用世界 AABB 的 8 个角
                    var b = smr.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var corner = new Vector3(
                            (c & 1) == 0 ? b.min.x : b.max.x,
                            (c & 2) == 0 ? b.min.y : b.max.y,
                            (c & 4) == 0 ? b.min.z : b.max.z);
                        Vector3 p = root.InverseTransformPoint(corner);
                        minY = Mathf.Min(minY, p.y);
                        maxY = Mathf.Max(maxY, p.y);
                        r2 = Mathf.Max(r2, p.x * p.x + p.z * p.z);
                    }
                    any = true;
                }
            }
            Destroy(baked);
            foreach (var mr in model.GetComponentsInChildren<MeshRenderer>())
            {
                var b = mr.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? b.min.x : b.max.x,
                        (c & 2) == 0 ? b.min.y : b.max.y,
                        (c & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 p = root.InverseTransformPoint(corner);
                    minY = Mathf.Min(minY, p.y);
                    maxY = Mathf.Max(maxY, p.y);
                    r2 = Mathf.Max(r2, p.x * p.x + p.z * p.z);
                    any = true;
                }
            }
            if (!any || maxY - minY < 0.2f) return;

            _minY = Mathf.Min(minY, 0f);
            _maxY = maxY;
            // 留一点余量给呼吸摆动和头发摆动
            _radius = Mathf.Sqrt(r2) * 1.05f + 0.02f;
        }

        // ------------------------------------------------------------------
        // 公共操作（菜单用）
        // ------------------------------------------------------------------

        public void SetAutoRotate(bool on)
        {
            AutoRotate = on;
            Save();
        }

        public void ResetView()
        {
            // 走最短路径转回正面
            _targetYaw = Mathf.Round(_targetYaw / 360f) * 360f;
            _targetPitch = 0f;
            _targetZoom = 1f;
            Save();
        }

        public void Spin()
        {
            _targetYaw -= 360f;
        }

        void Save()
        {
            PlayerPrefs.SetFloat(KeyYaw, Mathf.Repeat(_targetYaw, 360f));
            PlayerPrefs.SetFloat(KeyPitch, _targetPitch);
            PlayerPrefs.SetFloat(KeyZoom, _targetZoom);
            PlayerPrefs.SetInt(KeyAuto, AutoRotate ? 1 : 0);
            PlayerPrefs.Save();
        }

        void OnApplicationQuit()
        {
            Save();
        }

        // ------------------------------------------------------------------
        // 输入
        // ------------------------------------------------------------------

        /// <summary>桌面坐标（像素，y 向下）。编辑器里用 Unity 鼠标坐标模拟。</summary>
        static Vector2 CursorDesktop()
        {
            if (TransparentWindow.TryGetCursorScreenPosition(out var p)) return new Vector2(p.x, p.y);
            var m = Input.mousePosition;
            return new Vector2(m.x, -m.y);
        }

        static bool OverCharacter()
        {
            var tw = TransparentWindow.Instance;
            if (TransparentWindow.IsNativeWindowActive && tw != null) return tw.IsPointerOverOpaque;
            return true;
        }

        void Update()
        {
            var outfit = OutfitManager.Instance;
            bool menuOpen = outfit != null && outfit.IsMenuOpen;
            var tw = TransparentWindow.Instance;
            bool shift = TransparentWindow.IsKeyPressed(TransparentWindow.VK_SHIFT, KeyCode.LeftShift, KeyCode.RightShift);
            bool ctrl = TransparentWindow.IsKeyPressed(TransparentWindow.VK_CONTROL, KeyCode.LeftControl, KeyCode.RightControl);

            // ---- 按下 ----
            if (_dragMode == DragMode.None)
            {
                if (TransparentWindow.GetMouseButtonDown(1) && (OverCharacter() || menuOpen || (outfit != null && !outfit.HasModel)))
                {
                    if (menuOpen) outfit.CloseMenuPublic();
                    BeginDrag(1, shift ? DragMode.Pitch : DragMode.Yaw);
                }
                else if (TransparentWindow.GetMouseButtonDown(2) && !menuOpen && OverCharacter())
                {
                    BeginDrag(2, DragMode.Pitch);
                }
            }

            // ---- 拖动中 ----
            if (_dragMode != DragMode.None)
            {
                Vector2 cur = CursorDesktop();
                if (!_dragMoved && (cur - _pressPos).magnitude >= clickThresholdPx * TransparentWindow.UiScale)
                {
                    _dragMoved = true;
                }
                if (_dragMoved)
                {
                    Vector2 d = cur - _lastPos;
                    if (_dragMode == DragMode.Yaw)
                    {
                        // 向右拖 => 角色正面转向屏幕右侧
                        _targetYaw -= d.x * yawDegPerPixel / TransparentWindow.UiScale;
                    }
                    else
                    {
                        // 向下拖 => 镜头升高（从上往下看）
                        _targetPitch = Mathf.Clamp(_targetPitch + d.y * pitchDegPerPixel / TransparentWindow.UiScale, minPitch, maxPitch);
                    }
                }
                _lastPos = cur;

                if (!TransparentWindow.IsMouseButtonPressed(_dragButton))
                {
                    bool wasClick = !_dragMoved;
                    int button = _dragButton;
                    EndDrag();
                    if (wasClick && button == 1 && outfit != null)
                    {
                        outfit.OpenMenuPublic();
                    }
                }
            }

            // ---- Ctrl + 滚轮：镜头远近 ----
            float wheel = Input.mouseScrollDelta.y;
            if (ctrl && !menuOpen && Mathf.Abs(wheel) > 0.01f && OverCharacter())
            {
                _targetZoom = Mathf.Clamp(_targetZoom * Mathf.Pow(0.9f, wheel), minZoom, maxZoom);
                Save();
            }

            // ---- 左键双击：转一圈 ----
            if (!menuOpen && TransparentWindow.GetMouseButtonDown(0) && OverCharacter())
            {
                Vector2 p = CursorDesktop();
                if (Time.unscaledTime - _lastLeftClickTime < 0.35f && (p - _lastLeftClickPos).magnitude < 8f * TransparentWindow.UiScale)
                {
                    Spin();
                    _lastLeftClickTime = -10f;
                }
                else
                {
                    _lastLeftClickTime = Time.unscaledTime;
                    _lastLeftClickPos = p;
                }
            }

            // ---- 自动旋转 ----
            if (AutoRotate && !IsRotatingByUser)
            {
                _targetYaw -= autoRotateSpeed * Time.deltaTime;
            }

            // ---- 平滑 ----
            float k = 1f - Mathf.Exp(-10f * Time.deltaTime);
            _yaw = Mathf.Lerp(_yaw, _targetYaw, k);
            _pitch = Mathf.Lerp(_pitch, _targetPitch, k);
            _zoom = Mathf.Lerp(_zoom, _targetZoom, k);
            // 防止数值无限增长（同时平移当前值和目标值，不影响动画）
            if (Mathf.Abs(_targetYaw) > 3600f && Mathf.Abs(_yaw) > 3600f)
            {
                float shift360 = Mathf.Floor(_targetYaw / 360f) * 360f;
                _targetYaw -= shift360;
                _yaw -= shift360;
            }

            if (tw != null) tw.HoldClickable |= _dragMode != DragMode.None;
        }

        void BeginDrag(int button, DragMode mode)
        {
            _dragMode = mode;
            _dragButton = button;
            _dragMoved = false;
            _pressPos = _lastPos = CursorDesktop();
            var tw = TransparentWindow.Instance;
            if (tw != null) tw.HoldClickable = true;
        }

        void EndDrag()
        {
            bool moved = _dragMoved;
            _dragMode = DragMode.None;
            _dragButton = -1;
            _dragMoved = false;
            var tw = TransparentWindow.Instance;
            var drag = GetComponent<WindowDragScale>();
            if (tw != null && (drag == null || !drag.IsDragging)) tw.HoldClickable = false;
            if (moved) Save();
        }

        // ------------------------------------------------------------------
        // 应用到角色和相机
        // ------------------------------------------------------------------

        void LateUpdate()
        {
            if (characterRoot != null)
            {
                characterRoot.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            }

            var cam = Camera.main;
            if (cam == null) return;

            float scale = characterRoot != null ? characterRoot.lossyScale.y : 1f;
            Vector3 pivot = characterRoot != null && characterRoot.parent != null ? characterRoot.parent.position : Vector3.zero;

            float h = (_maxY - _minY) * scale;
            float r = _radius * scale;
            float centerY = (_minY + _maxY) * 0.5f * scale;
            float aspect = Mathf.Max(0.1f, cam.aspect);
            float pr = Mathf.Abs(_pitch) * Mathf.Deg2Rad;

            // 圆柱在当前俯仰角下的投影尺寸
            float vert = h * Mathf.Cos(pr) + 2f * r * Mathf.Sin(pr);
            float halfV = Mathf.Max(vert * 0.5f, r / aspect) * 1.06f;
            float dist = halfV / Mathf.Tan(cameraFov * 0.5f * Mathf.Deg2Rad) + r;
            dist *= _zoom;

            float camYaw = _facing > 0f ? 0f : 180f;
            Vector3 dir = Quaternion.Euler(-_pitch, camYaw, 0f) * Vector3.forward;
            Vector3 center = pivot + Vector3.up * centerY;

            cam.orthographic = false;
            cam.fieldOfView = cameraFov;
            cam.nearClipPlane = Mathf.Max(0.02f, (dist - r) * 0.3f);
            cam.farClipPlane = dist + r * 4f + 10f;
            cam.transform.position = center + dir * dist;
            cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);

            // 主光跟随镜头（从镜头左上方照向角色），任何角度看到的一面都有光
            if (_lights == null || Time.frameCount % 120 == 0) _lights = FindObjectsOfType<Light>();
            foreach (var light in _lights)
            {
                if (light != null && light.type == LightType.Directional)
                {
                    light.transform.rotation = cam.transform.rotation * Quaternion.Euler(25f, -25f, 0f);
                }
            }
        }
    }
}
