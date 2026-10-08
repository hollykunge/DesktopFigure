#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
#define DF_WIN32
#endif
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
#define DF_MAC
#endif

using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace DesktopFigure
{
    /// <summary>
    /// Windows 独立运行时：无边框 + 透明背景 + 置顶 + 透明像素点击穿透 + 不在任务栏显示（图标在系统托盘，见 TrayIcon）。
    /// macOS 独立运行时：同样的效果，通过 Objective-C 运行时调用 AppKit 实现（见 MacNative.cs / MacStatusItem.cs，未经实机测试）。
    /// 在 Unity 编辑器里只设置相机背景，不做任何原生窗口操作。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class TransparentWindow : MonoBehaviour
    {
        public static TransparentWindow Instance { get; private set; }

        [Tooltip("光标下像素 alpha 大于该值时视为点到了角色（窗口可点击）")]
        [Range(0.01f, 1f)] public float alphaThreshold = 0.1f;

        [Tooltip("启动时默认置顶")]
        public bool topmostOnStart = true;

        /// <summary>为 true 时整个窗口都可点击（如右键菜单打开时）。</summary>
        public bool ForceClickable { get; set; }

        /// <summary>为 true 时保持当前可点击状态（如拖动中）。</summary>
        public bool HoldClickable { get; set; }

        /// <summary>光标下是否是角色（或 GUI）等不透明像素。</summary>
        public bool IsPointerOverOpaque { get; private set; }

        public bool IsTopmost { get; private set; }

        /// <summary>窗口当前是否处于点击穿透状态。</summary>
        public bool IsClickThrough { get; private set; }

        /// <summary>桌宠窗口当前是否可见（托盘菜单「隐藏」后为 false）。</summary>
        public bool IsPetVisible { get; private set; } = true;

        /// <summary>界面缩放（= 显示器 DPI / 96，例如 4K 250% 缩放时为 2.5）。</summary>
        public static float UiScale { get; private set; } = 1f;

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;

        /// <summary>原生窗口控制是否生效（Windows：找到了 Unity 窗口句柄；macOS：找到了 NSWindow）。编辑器里为 false。</summary>
        public static bool IsNativeWindowActive
        {
            get
            {
#if DF_WIN32
                return Instance != null && Instance._hwnd != IntPtr.Zero;
#elif DF_MAC
                return Instance != null && MacWindow.IsActive;
#else
                return false;
#endif
            }
        }

#if DF_MAC
        // ---- 鼠标按下（逐帧缓存）：macOS 上窗口不在前台 / 忽略鼠标事件时 Unity 可能收不到第一次点击，改为轮询全局按键状态 ----
        static readonly bool[] s_btnDown = new bool[3];
        static readonly bool[] s_btnPrev = new bool[3];
        static int s_btnFrame = -1;
#endif

        /// <summary>
        /// 本帧是否刚按下鼠标键（0 左 / 1 右 / 2 中）。Windows / 编辑器：等同 Input.GetMouseButtonDown；
        /// macOS：轮询 [NSEvent pressedMouseButtons] 的按下沿（与窗口是否收到事件无关），调用方自己判断光标是否在角色上。
        /// </summary>
        public static bool GetMouseButtonDown(int button)
        {
#if DF_MAC
            if (IsNativeWindowActive)
            {
                if (s_btnFrame != Time.frameCount)
                {
                    s_btnFrame = Time.frameCount;
                    for (int i = 0; i < 3; i++)
                    {
                        bool now = MacWindow.IsMouseButtonPressed(i);
                        s_btnDown[i] = now && !s_btnPrev[i];
                        s_btnPrev[i] = now;
                    }
                }
                return s_btnDown[Mathf.Clamp(button, 0, 2)];
            }
#endif
            return Input.GetMouseButtonDown(button);
        }

        Texture2D _sampleTex;
        Coroutine _sampleLoop;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            _sampleTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            SetupCamera(Camera.main);
#if DF_WIN32
            _hwnd = FindUnityWindow();
#elif DF_MAC
            MacWindow.Init(topmostOnStart);
#endif
            UpdateUiScale();
        }

        void UpdateUiScale()
        {
            float dpi = 0f;
#if DF_WIN32
            try
            {
                if (_hwnd != IntPtr.Zero) dpi = GetDpiForWindow(_hwnd);
            }
            catch (Exception)
            {
                dpi = 0f;
            }
#elif DF_MAC
            if (MacWindow.IsActive)
            {
                // macOS：1 point ≈ Windows 100% 缩放下的 1 像素，界面缩放 = backingScaleFactor（Retina 为 2）
                UiScale = Mathf.Clamp(MacWindow.Scale, 1f, 4f);
                return;
            }
#endif
            if (dpi <= 0f) dpi = Screen.dpi;
            UiScale = dpi > 0f ? Mathf.Clamp(dpi / 96f, 1f, 4f) : 1f;
        }

        void Start()
        {
            Application.runInBackground = true;
#if DF_WIN32
            if (_hwnd == IntPtr.Zero) _hwnd = FindUnityWindow();
            if (_hwnd != IntPtr.Zero)
            {
                ApplyWindowStyle();
                SetTopmost(topmostOnStart);
                SetClickThrough(false);
            }
            else
            {
                Debug.LogWarning("[TransparentWindow] Unity window handle not found.");
            }
#elif DF_MAC
            IsTopmost = topmostOnStart;
            if (MacWindow.Init(topmostOnStart))
            {
                MacWindow.SetTopmost(topmostOnStart);
                SetClickThrough(false);
                UpdateUiScale();
            }
            else
            {
                Debug.LogWarning("[TransparentWindow] macOS NSWindow not found yet, will retry for a few seconds.");
            }
#else
            IsTopmost = topmostOnStart;
#endif
            _sampleLoop = StartCoroutine(SampleLoop());
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_sampleTex != null) Destroy(_sampleTex);
        }

        /// <summary>相机背景必须是 RGBA(0,0,0,0)。</summary>
        public static void SetupCamera(Camera cam)
        {
            if (cam == null) return;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.allowHDR = false;
        }

        float _nextDpiCheck;

        void Update()
        {
            if (Time.unscaledTime > _nextDpiCheck)
            {
                _nextDpiCheck = Time.unscaledTime + 1f; // 窗口拖到别的显示器时 DPI 可能变化
                UpdateUiScale();
#if DF_WIN32
                EnsureHiddenFromTaskbar(); // 万一别的代码 / Unity 改回了扩展样式，重新从任务栏隐藏
#endif
            }
            if (Camera.main != null && Camera.main.backgroundColor.a != 0f)
            {
                SetupCamera(Camera.main);
            }
#if DF_WIN32
            if (_hwnd == IntPtr.Zero) return;
            bool clickable = ForceClickable || IsPointerOverOpaque;
            if (HoldClickable) clickable = !IsClickThrough || clickable;
            if (clickable == IsClickThrough)
            {
                SetClickThrough(!clickable);
            }
#elif DF_MAC
            if (!MacWindow.IsActive)
            {
                // 启动初期 NSApp 可能还没有 key/main window：前 10 秒内每秒重试一次
                if (Time.unscaledTime < 10f && Time.unscaledTime >= _nextMacRetry)
                {
                    _nextMacRetry = Time.unscaledTime + 1f;
                    if (MacWindow.Init(IsTopmost))
                    {
                        SetClickThrough(false);
                        UpdateUiScale();
                    }
                }
                return;
            }
            MacWindow.Tick();
            MacWindow.CheckResolution();
            // 菜单打开时激活本程序，否则 AppKit 可能把第一次点击只用来激活窗口
            if (ForceClickable && !_macWasForced) MacWindow.Activate();
            _macWasForced = ForceClickable;
            bool clickable = ForceClickable || IsPointerOverOpaque;
            if (HoldClickable) clickable = !IsClickThrough || clickable;
            if (clickable == IsClickThrough)
            {
                SetClickThrough(!clickable);
            }
#endif
        }

#if DF_MAC
        float _nextMacRetry;
        bool _macWasForced;

        void SetClickThrough(bool through)
        {
            try
            {
                MacWindow.SetClickThrough(through);
                IsClickThrough = through;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DesktopFigure] macOS setIgnoresMouseEvents failed: " + e.Message);
            }
        }
#endif

        IEnumerator SampleLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                SamplePixelUnderCursor();
            }
        }

        void SamplePixelUnderCursor()
        {
            Vector2 p = GetCursorUnityPosition();
            int x = Mathf.FloorToInt(p.x);
            int y = Mathf.FloorToInt(p.y);
            if (x < 0 || y < 0 || x >= Screen.width || y >= Screen.height)
            {
                IsPointerOverOpaque = false;
                return;
            }
            try
            {
                _sampleTex.ReadPixels(new Rect(x, y, 1, 1), 0, 0, false);
                IsPointerOverOpaque = _sampleTex.GetPixel(0, 0).a > alphaThreshold;
            }
            catch (Exception)
            {
                IsPointerOverOpaque = false;
            }
        }

        // ------------------------------------------------------------------
        // 光标 / 窗口工具方法（编辑器下退化为 Unity 自带实现）
        // ------------------------------------------------------------------

        /// <summary>光标在本窗口客户区内的位置（Unity 屏幕坐标，左下角为原点；可能超出窗口范围）。</summary>
        public static Vector2 GetCursorUnityPosition()
        {
#if DF_WIN32
            if (Instance != null && Instance._hwnd != IntPtr.Zero && GetCursorPos(out POINT pt))
            {
                ScreenToClient(Instance._hwnd, ref pt);
                float x = pt.X, y = pt.Y;
                // 客户区尺寸与 Unity 渲染分辨率不一致时（例如窗口刚被缩放）按比例换算
                if (GetClientRect(Instance._hwnd, out RECT cr))
                {
                    int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                    if (cw > 0 && ch > 0 && (cw != Screen.width || ch != Screen.height))
                    {
                        x = x * Screen.width / cw;
                        y = y * Screen.height / ch;
                    }
                }
                return new Vector2(x, Screen.height - 1 - y);
            }
#elif DF_MAC
            if (MacWindow.IsActive && MacWindow.TryGetCursorUnity(out var mp)) return mp;
#endif
            return Input.mousePosition;
        }

        /// <summary>光标的桌面坐标（像素，左上角为原点）。</summary>
        public static bool TryGetCursorScreenPosition(out Vector2Int pos)
        {
#if DF_WIN32
            if (GetCursorPos(out POINT pt))
            {
                pos = new Vector2Int(pt.X, pt.Y);
                return true;
            }
#elif DF_MAC
            if (MacWindow.IsActive && MacWindow.TryGetCursorDesktop(out pos)) return true;
#endif
            pos = Vector2Int.zero;
            return false;
        }

        /// <summary>左键当前是否按下（不依赖窗口是否收到鼠标消息）。</summary>
        public static bool IsLeftButtonPressed() => IsMouseButtonPressed(0);

        /// <summary>鼠标键当前是否按下（0 左 / 1 右 / 2 中），不依赖窗口是否收到鼠标消息。</summary>
        public static bool IsMouseButtonPressed(int button)
        {
#if DF_WIN32
            int vk = button == 0 ? VK_LBUTTON : (button == 1 ? VK_RBUTTON : VK_MBUTTON);
            // 系统设置了左右键互换时，物理键与逻辑键相反
            if (button < 2 && GetSystemMetrics(SM_SWAPBUTTON) != 0) vk = button == 0 ? VK_RBUTTON : VK_LBUTTON;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
#elif DF_MAC
            if (MacWindow.IsActive) return MacWindow.IsMouseButtonPressed(button);
            return Input.GetMouseButton(button);
#else
            return Input.GetMouseButton(button);
#endif
        }

        /// <summary>键盘修饰键当前是否按下（窗口没有焦点时也有效）。</summary>
        public static bool IsKeyPressed(int vk, KeyCode a, KeyCode b)
        {
#if DF_WIN32
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
#elif DF_MAC
            if (MacWindow.IsActive)
            {
                bool down = MacWindow.IsModifierPressed(vk, out bool known);
                if (known) return down;
            }
            return Input.GetKey(a) || Input.GetKey(b);
#else
            return Input.GetKey(a) || Input.GetKey(b);
#endif
        }

        public bool TryGetWindowRect(out RectInt rect)
        {
#if DF_WIN32
            if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out RECT r))
            {
                rect = new RectInt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                return true;
            }
#elif DF_MAC
            if (MacWindow.IsActive && MacWindow.TryGetWindowRect(out rect)) return true;
#endif
            rect = default;
            return false;
        }

        public void MoveWindow(int x, int y)
        {
#if DF_WIN32
            if (_hwnd == IntPtr.Zero) return;
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
#elif DF_MAC
            MacWindow.MoveWindow(x, y);
#endif
        }

        public void SetWindowRect(int x, int y, int width, int height)
        {
#if DF_WIN32
            if (_hwnd == IntPtr.Zero) return;
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, Mathf.Max(64, width), Mathf.Max(64, height), SWP_NOZORDER | SWP_NOACTIVATE);
#elif DF_MAC
            MacWindow.SetWindowRect(x, y, Mathf.Max(64, width), Mathf.Max(64, height));
#endif
        }

        /// <summary>主显示器工作区（不含任务栏），桌面像素坐标。</summary>
        public static RectInt GetPrimaryWorkArea()
        {
#if DF_WIN32
            var r = new RECT();
            if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref r, 0))
            {
                return new RectInt(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
#elif DF_MAC
            if (MacWindow.IsActive) return MacWindow.PrimaryWorkArea();
#endif
            return new RectInt(0, 0, Display.main.systemWidth, Display.main.systemHeight);
        }

        /// <summary>所有显示器合并后的虚拟桌面范围。</summary>
        public static RectInt GetVirtualScreen()
        {
#if DF_WIN32
            return new RectInt(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
#elif DF_MAC
            if (MacWindow.IsActive) return MacWindow.VirtualScreen();
            return new RectInt(0, 0, Display.main.systemWidth, Display.main.systemHeight);
#else
            return new RectInt(0, 0, Display.main.systemWidth, Display.main.systemHeight);
#endif
        }

        /// <summary>指定桌面坐标所在显示器的工作区（不含任务栏），桌面像素坐标。</summary>
        public static RectInt GetWorkAreaAt(Vector2Int point)
        {
#if DF_WIN32
            var mon = MonitorFromPoint(new POINT { X = point.x, Y = point.y }, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(mon, ref mi))
                {
                    return new RectInt(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
                }
            }
#elif DF_MAC
            if (MacWindow.IsActive) return MacWindow.WorkAreaAt(point);
#endif
            return GetPrimaryWorkArea();
        }

        /// <summary>把本窗口设为前台窗口（输入框需要键盘焦点 / 输入法）。</summary>
        public void Activate()
        {
#if DF_WIN32
            if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd);
#elif DF_MAC
            MacWindow.Activate();
#endif
        }

        /// <summary>显示 / 隐藏桌宠窗口（托盘菜单用）。activate=true 时同时把窗口带到前台。</summary>
        public void SetPetVisible(bool visible, bool activate = false)
        {
            if (!visible)
            {
                var om = OutfitManager.Instance;
                if (om != null) om.CloseMenuPublic();
            }
            IsPetVisible = visible;
#if DF_WIN32
            if (_hwnd == IntPtr.Zero) return;
            if (visible)
            {
                ShowWindow(_hwnd, activate ? SW_SHOW : SW_SHOWNA);
                SetTopmost(IsTopmost);
                if (activate)
                {
                    BringWindowToTop(_hwnd);
                    SetForegroundWindow(_hwnd);
                }
            }
            else
            {
                ShowWindow(_hwnd, SW_HIDE);
            }
#elif DF_MAC
            if (MacWindow.IsActive)
            {
                MacWindow.SetVisible(visible, activate);
                if (visible) MacWindow.SetTopmost(IsTopmost);
            }
#endif
            Debug.Log("[DesktopFigure] Pet window " + (visible ? "shown" : "hidden"));
        }

        public void SetTopmost(bool topmost)
        {
            IsTopmost = topmost;
#if DF_WIN32
            if (_hwnd == IntPtr.Zero) return;
            SetWindowPos(_hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
#elif DF_MAC
            MacWindow.SetTopmost(topmost);
#endif
        }

#if DF_WIN32
        IntPtr _hwnd = IntPtr.Zero;

        void ApplyWindowStyle()
        {
            // 1) 去掉标题栏和边框
            long style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            style |= WS_POPUP | WS_VISIBLE;
            SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style));

            // 2) 让 DWM 把“玻璃”扩展到整个客户区 => 按像素 alpha 透明
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);

            // 3) 通知窗口框架已变化
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            // 4) 不在任务栏显示（程序图标放到系统托盘，见 TrayIcon）
            HideFromTaskbar();
        }

        /// <summary>
        /// 加 WS_EX_TOOLWINDOW、去 WS_EX_APPWINDOW => 任务栏不显示按钮（也不出现在 Alt+Tab）。
        /// 任务栏只在窗口“显示”时读取样式，所以要先隐藏、改样式、再（不激活地）显示。
        /// </summary>
        void HideFromTaskbar()
        {
            bool wasVisible = IsWindowVisible(_hwnd);
            if (wasVisible) ShowWindow(_hwnd, SW_HIDE);

            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            if (wasVisible && IsPetVisible) ShowWindow(_hwnd, SW_SHOWNA);
            if (IsPetVisible) SetTopmost(IsTopmost);

            long now = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            Debug.Log($"[DesktopFigure] Hidden from taskbar (exstyle=0x{now:X8}, toolwindow={(now & WS_EX_TOOLWINDOW) != 0}, appwindow={(now & WS_EX_APPWINDOW) != 0})");
        }

        void EnsureHiddenFromTaskbar()
        {
            if (_hwnd == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) == 0 || (ex & WS_EX_APPWINDOW) != 0)
            {
                Debug.Log("[DesktopFigure] Taskbar ex-style lost, re-applying");
                HideFromTaskbar();
            }
        }

        void SetClickThrough(bool through)
        {
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            if (through) ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            else ex &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
            // 始终保持“不在任务栏显示”的样式，避免切换点击穿透时把任务栏按钮带回来
            ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
            IsClickThrough = through;
        }

        // ---------------- 查找本进程的 Unity 窗口 ----------------
        static IntPtr s_foundHwnd;
        static uint s_pid;

        static IntPtr FindUnityWindow()
        {
            s_foundHwnd = IntPtr.Zero;
            s_pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows(EnumWindowsCallback, IntPtr.Zero);
            if (s_foundHwnd == IntPtr.Zero)
            {
                s_foundHwnd = GetActiveWindow();
            }
            return s_foundHwnd;
        }

        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != s_pid) return true;
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            if (sb.ToString() == "UnityWndClass")
            {
                s_foundHwnd = hWnd;
                return false; // 停止枚举
            }
            return true;
        }

        // ---------------- Win32 声明 ----------------
        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;

        const long WS_POPUP = 0x80000000L;
        const long WS_VISIBLE = 0x10000000L;
        const long WS_CAPTION = 0x00C00000L;
        const long WS_THICKFRAME = 0x00040000L;
        const long WS_SYSMENU = 0x00080000L;
        const long WS_MINIMIZEBOX = 0x00020000L;
        const long WS_MAXIMIZEBOX = 0x00010000L;

        const long WS_EX_LAYERED = 0x00080000L;
        const long WS_EX_TRANSPARENT = 0x00000020L;
        const long WS_EX_TOOLWINDOW = 0x00000080L;
        const long WS_EX_APPWINDOW = 0x00040000L;

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;
        const int SW_SHOWNA = 8;

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_SHOWWINDOW = 0x0040;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        const int SM_SWAPBUTTON = 23;
        const uint SPI_GETWORKAREA = 0x0030;
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindowVisible(IntPtr hWnd);
#endif
    }
}
