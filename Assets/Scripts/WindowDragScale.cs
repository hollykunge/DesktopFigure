using UnityEngine;

namespace DesktopFigure
{
    /// <summary>
    /// 左键拖动角色 = 移动窗口；滚轮 = 缩放（脚底位置保持不变）。
    ///
    /// “角色区域”（FigureRect，桌面坐标）是角色实际显示的矩形，位置/缩放记到 PlayerPrefs。
    /// 真正的 Win32 窗口（画布）= 角色区域四周再加一圈透明边距，菜单 / 子菜单 / 改名弹窗 / 操作说明都画在这圈边距里，
    /// 所以打开菜单时**不需要改变窗口大小**（改变窗口大小时 DWM 会把旧尺寸的画面拉伸显示一两帧 => 角色抖一下）。
    /// 透明边距是点击穿透的，不影响桌面操作。相机视口（Camera.rect）始终限制在角色区域。
    /// 在编辑器里退化为移动/缩放角色根节点，方便调试。
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class WindowDragScale : MonoBehaviour
    {
        [Tooltip("缩放 1.0 时角色区域的大小（逻辑像素，会乘以显示器 DPI 缩放）")]
        public int baseWidth = 345;
        public int baseHeight = 460;
        [Tooltip("首次启动（没有保存过缩放）时角色高度最多占主屏工作区高度的比例")]
        public float defaultMaxHeightFraction = 0.5f;
        public float minScale = 0.25f;
        public float maxScale = 3.0f;
        public float wheelStep = 1.1f;

        // 画布边距（逻辑像素，常量：不走场景序列化，避免旧场景里存的值覆盖）
        // 左右：光标在角色左侧附近时，主菜单 + 子菜单也能向左展开（240 + 2 + 256 + 阴影 ≈ 515，光标通常在角色内部）
        const int CanvasMarginX = 420;
        // 上下：最小边距；画布高度至少要放得下最高的子菜单 / 改名弹窗 + 阴影
        const int CanvasMarginY = 48;
        const int CanvasMinHeight = 380;

        [Tooltip("编辑器调试用：被移动/缩放的角色根节点")]
        public Transform characterRoot;

        const string KeyX = "DF_WinX";
        const string KeyY = "DF_WinY";
        const string KeyScale = "DF_WinScale";

        public static WindowDragScale Instance { get; private set; }
        public bool IsDragging { get; private set; }

        /// <summary>角色区域（桌面坐标，物理像素）。</summary>
        public RectInt FigureRect => _fig;
        public bool HasFigureRect => _windowInitialized;

        float _scale = 1f;
        RectInt _fig;
        Vector2Int _dragStartCursor;
        RectInt _dragStartFig;
        Vector3 _editorDragStartWorld;
        Vector3 _editorDragStartRootPos;
        bool _windowInitialized;
        float _nextSizeCheck;
        Camera _clearCamera;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            _scale = Mathf.Clamp(PlayerPrefs.GetFloat(KeyScale, 1f), minScale, maxScale);
            InitWindowRect();
        }

        void InitWindowRect()
        {
            var tw = TransparentWindow.Instance;
            if (!TransparentWindow.IsNativeWindowActive || tw == null) return;

            var work = TransparentWindow.GetPrimaryWorkArea();
            if (!PlayerPrefs.HasKey(KeyScale) && work.height > 0)
            {
                // 首次启动：默认不超过屏幕高度的一半左右
                _scale = Mathf.Min(1f, work.height * defaultMaxHeightFraction / BaseH);
            }
            ClampScaleToHeight(work.height);
            int w = Mathf.RoundToInt(BaseW * _scale);
            int h = Mathf.RoundToInt(BaseH * _scale);

            int x, y;
            if (PlayerPrefs.HasKey(KeyX) && PlayerPrefs.HasKey(KeyY))
            {
                x = PlayerPrefs.GetInt(KeyX);
                y = PlayerPrefs.GetInt(KeyY);
                // 确保角色区域完整地在桌面范围内（显示器拔掉、DPI 变化等情况）
                var vs = TransparentWindow.GetVirtualScreen();
                x = Mathf.Clamp(x, vs.xMin, Mathf.Max(vs.xMin, vs.xMax - w));
                y = Mathf.Clamp(y, vs.yMin, Mathf.Max(vs.yMin, vs.yMax - h));
            }
            else
            {
                // 默认：主屏幕工作区右下角
                x = work.xMax - w - 40;
                y = work.yMax - h;
            }
            ApplyFigureRect(new RectInt(x, y, w, h));
            _windowInitialized = true;
        }

        float BaseW => baseWidth * TransparentWindow.UiScale;
        float BaseH => baseHeight * TransparentWindow.UiScale;

        void ClampScaleToHeight(int workHeight)
        {
            if (workHeight > 0)
            {
                float maxByScreen = workHeight / BaseH;
                _scale = Mathf.Clamp(_scale, minScale, Mathf.Max(minScale, Mathf.Min(maxScale, maxByScreen)));
            }
        }

        // ------------------------------------------------------------------
        // 画布（真实窗口）= 角色区域 + 透明边距
        // ------------------------------------------------------------------

        /// <summary>给定角色区域时，画布（真实窗口）应有的桌面矩形。</summary>
        public RectInt CanvasFor(RectInt fig)
        {
            float s = TransparentWindow.UiScale;
            int mx = Mathf.RoundToInt(CanvasMarginX * s);
            int my = Mathf.Max(Mathf.RoundToInt(CanvasMarginY * s), Mathf.CeilToInt((CanvasMinHeight * s - fig.height) * 0.5f));
            return new RectInt(fig.x - mx, fig.y - my, fig.width + mx * 2, fig.height + my * 2);
        }

        /// <summary>当前角色区域对应的画布矩形。</summary>
        public RectInt CanvasRect => CanvasFor(_fig);

        void ApplyFigureRect(RectInt fig)
        {
            _fig = fig;
            var tw = TransparentWindow.Instance;
            if (!TransparentWindow.IsNativeWindowActive || tw == null) return;
            var c = CanvasFor(fig);
            tw.SetWindowRect(c.x, c.y, c.width, c.height);   // 位置 + 大小一次设置
        }

        /// <summary>菜单临时放大窗口后恢复成正常画布。</summary>
        public void RestoreCanvas()
        {
            if (_windowInitialized) ApplyFigureRect(_fig);
        }

        // ------------------------------------------------------------------

        void Update()
        {
            var outfit = OutfitManager.Instance;
            bool menuOpen = outfit != null && outfit.IsMenuOpen;

            // ---------- 拖动 ----------
            if (!IsDragging && !menuOpen && TransparentWindow.GetMouseButtonDown(0) && IsOverCharacter())
            {
                BeginDrag();
            }
            if (IsDragging)
            {
                if (!TransparentWindow.IsLeftButtonPressed() || menuOpen)
                {
                    EndDrag();
                }
                else
                {
                    UpdateDrag();
                }
            }

            EnforceWindowRect();

            // ---------- 滚轮缩放窗口（Ctrl+滚轮 是镜头远近，由 FigureViewController 处理）----------
            float wheel = Input.mouseScrollDelta.y;
            bool ctrl = TransparentWindow.IsKeyPressed(TransparentWindow.VK_CONTROL, KeyCode.LeftControl, KeyCode.RightControl);
            if (!menuOpen && !ctrl && Mathf.Abs(wheel) > 0.01f && IsOverCharacter())
            {
                ApplyWheel(wheel);
            }
        }

        /// <summary>
        /// Unity 播放器在某些启动情况下会在我们设置完窗口之后按“带边框窗口”的尺寸再调整一次（例如 798x1105），
        /// 或者窗口被系统移动（显示器变化）。这里定期校正：尺寸不对就按角色区域重新设置；只是位置变了就让角色区域跟着窗口走。
        /// 菜单临时放大窗口时不处理。
        /// </summary>
        void EnforceWindowRect()
        {
            if (!_windowInitialized || IsDragging || Time.unscaledTime < _nextSizeCheck) return;
            _nextSizeCheck = Time.unscaledTime + 0.5f;
            var tw = TransparentWindow.Instance;
            if (!TransparentWindow.IsNativeWindowActive || tw == null) return;
            var outfit = OutfitManager.Instance;
            if (outfit != null && outfit.IsWindowExpanded) return;
            if (!tw.TryGetWindowRect(out var r)) return;

            // DPI 变化时角色区域的像素尺寸也跟着变
            int w = Mathf.RoundToInt(BaseW * _scale);
            int h = Mathf.RoundToInt(BaseH * _scale);
            var expectFig = new RectInt(_fig.x, _fig.y, w, h);
            var c = CanvasFor(expectFig);
            if (r.width != c.width || r.height != c.height)
            {
                Debug.Log($"[DesktopFigure] Window size corrected: {r.width}x{r.height} -> {c.width}x{c.height}");
                ApplyFigureRect(expectFig);
            }
            else if (r.x != c.x || r.y != c.y)
            {
                _fig = new RectInt(_fig.x + (r.x - c.x), _fig.y + (r.y - c.y), w, h);
            }
        }

        bool IsOverCharacter()
        {
            var tw = TransparentWindow.Instance;
            if (TransparentWindow.IsNativeWindowActive && tw != null)
            {
                return tw.IsPointerOverOpaque;
            }
            return true; // 编辑器里不做像素判定
        }

        void BeginDrag()
        {
            IsDragging = true;
            var tw = TransparentWindow.Instance;
            if (TransparentWindow.IsNativeWindowActive && tw != null)
            {
                tw.HoldClickable = true;
                TransparentWindow.TryGetCursorScreenPosition(out _dragStartCursor);
                _dragStartFig = _fig;
            }
            else if (characterRoot != null && Camera.main != null)
            {
                _editorDragStartWorld = ScreenToWorldOnCharacterPlane(Input.mousePosition);
                _editorDragStartRootPos = characterRoot.position;
            }
        }

        void UpdateDrag()
        {
            var tw = TransparentWindow.Instance;
            if (TransparentWindow.IsNativeWindowActive && tw != null)
            {
                if (TransparentWindow.TryGetCursorScreenPosition(out var cur))
                {
                    var d = cur - _dragStartCursor;
                    _fig = new RectInt(_dragStartFig.x + d.x, _dragStartFig.y + d.y, _dragStartFig.width, _dragStartFig.height);
                    var c = CanvasFor(_fig);
                    tw.MoveWindow(c.x, c.y);   // 只移动不改大小
                }
            }
            else if (characterRoot != null && Camera.main != null)
            {
                var now = ScreenToWorldOnCharacterPlane(Input.mousePosition);
                var delta = now - _editorDragStartWorld;
                characterRoot.position = _editorDragStartRootPos + new Vector3(delta.x, delta.y, 0f);
            }
        }

        void EndDrag()
        {
            IsDragging = false;
            var tw = TransparentWindow.Instance;
            if (tw != null) tw.HoldClickable = false;
            SaveWindowRect();
        }

        void ApplyWheel(float wheel)
        {
            float factor = Mathf.Pow(wheelStep, wheel);
            if (TransparentWindow.IsNativeWindowActive && TransparentWindow.Instance != null && _windowInitialized)
            {
                _scale *= factor;
                ClampScaleToHeight(TransparentWindow.GetVirtualScreen().height);
                int w = Mathf.RoundToInt(BaseW * _scale);
                int h = Mathf.RoundToInt(BaseH * _scale);
                // 保持底边中点（脚底）不动
                int centerX = _fig.x + _fig.width / 2;
                int bottom = _fig.y + _fig.height;
                ApplyFigureRect(new RectInt(centerX - w / 2, bottom - h, w, h));
                SaveWindowRect();
            }
            else if (characterRoot != null)
            {
                _scale = Mathf.Clamp(_scale * factor, minScale, maxScale);
                characterRoot.localScale = Vector3.one * _scale;
            }
        }

        void SaveWindowRect()
        {
            PlayerPrefs.SetFloat(KeyScale, _scale);
            if (_windowInitialized)
            {
                PlayerPrefs.SetInt(KeyX, _fig.x);
                PlayerPrefs.SetInt(KeyY, _fig.y);
            }
            PlayerPrefs.Save();
        }

        // ------------------------------------------------------------------
        // 相机视口：始终只渲染角色区域
        // ------------------------------------------------------------------

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var tw = TransparentWindow.Instance;
            if (!_windowInitialized || !TransparentWindow.IsNativeWindowActive || tw == null || !tw.TryGetWindowRect(out var win)
                || win.width <= 0 || win.height <= 0 || _fig.width <= 0 || _fig.height <= 0)
            {
                if (cam.rect != new Rect(0f, 0f, 1f, 1f)) cam.rect = new Rect(0f, 0f, 1f, 1f);
                cam.ResetAspect();
                return;
            }
            EnsureClearCamera(cam);
            // 用“归一化”视口 + 固定宽高比：即使窗口刚改变大小、渲染分辨率还没跟上（DWM 会拉伸旧画面），角色的位置和比例也是对的
            float left = _fig.x - win.x;
            float bottom = (win.y + win.height) - (_fig.y + _fig.height);
            var r = new Rect(left / win.width, bottom / win.height, (float)_fig.width / win.width, (float)_fig.height / win.height);
            if (cam.rect != r) cam.rect = r;
            cam.aspect = (float)_fig.width / _fig.height;
        }

        /// <summary>主相机视口小于窗口，视口外的区域需要另一个相机清成全透明。</summary>
        void EnsureClearCamera(Camera main)
        {
            if (_clearCamera != null) return;
            var go = new GameObject("DF_ClearCamera");
            go.transform.SetParent(transform, false);
            _clearCamera = go.AddComponent<Camera>();
            _clearCamera.cullingMask = 0;
            _clearCamera.clearFlags = CameraClearFlags.SolidColor;
            _clearCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _clearCamera.allowHDR = false;
            _clearCamera.allowMSAA = false;
            _clearCamera.depth = main.depth - 10f;
        }

        Vector3 ScreenToWorldOnCharacterPlane(Vector3 screenPos)
        {
            var cam = Camera.main;
            float z = characterRoot != null ? characterRoot.position.z : 0f;
            var ray = cam.ScreenPointToRay(screenPos);
            var plane = new Plane(Vector3.forward, new Vector3(0f, 0f, z));
            return plane.Raycast(ray, out float dist) ? ray.GetPoint(dist) : Vector3.zero;
        }

        void OnApplicationQuit()
        {
            if (_windowInitialized) SaveWindowRect();
        }
    }
}
