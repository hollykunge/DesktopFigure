#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
#define DF_WIN32
#endif

using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace DesktopFigure
{
    /// <summary>
    /// 系统托盘（通知区域）图标：程序不在任务栏显示，改为在托盘里放一个图标。
    ///   左键单击 / 双击：显示桌宠并带到前台
    ///   右键：原生菜单「显示/隐藏 宠物名称」「退出」
    /// 实现：在主线程创建一个隐藏的顶层窗口（不用 message-only 窗口，因为它收不到广播的 "TaskbarCreated"），
    /// Unity 主线程的消息循环会把消息分发给它的 WndProc。资源管理器重启后自动重新添加图标；退出时 NIM_DELETE。
    /// 编辑器里不做任何事。macOS 上的对应功能（菜单栏图标）见 MacStatusItem。
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class TrayIcon : MonoBehaviour
    {
        public static TrayIcon Instance { get; private set; }

        const string FallbackName = "裹裹";

        /// <summary>托盘提示 / 菜单里用的名字：右键菜单里设置的宠物名称，取不到时用「裹裹」。</summary>
        public static string PetDisplayName
        {
            get
            {
                string n = null;
                try
                {
                    var om = OutfitManager.Instance;
                    if (om != null) n = om.PetName;
                }
                catch (Exception)
                {
                    n = null;
                }
                // 模型还没加载时 OutfitManager 返回占位名「宠物」，这时也用「裹裹」
                if (string.IsNullOrWhiteSpace(n) || n.Trim() == "宠物") return FallbackName;
                return n.Trim();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
#if DF_WIN32
            if (Instance != null) return;
            var go = new GameObject("TrayIcon");
            DontDestroyOnLoad(go);
            go.AddComponent<TrayIcon>();
#endif
        }

        /// <summary>立即删除托盘图标（退出前调用，避免残留“幽灵图标”）。macOS 上删除菜单栏图标（MacStatusItem）。</summary>
        public static void RemoveIcon()
        {
#if DF_WIN32
            if (Instance != null) Instance.DeleteIcon();
#endif
            MacStatusItem.RemoveItem();
        }

#if DF_WIN32
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            s_active = this;
        }

        void Start()
        {
            try
            {
                CreateHiddenWindow();
                LoadTrayIcon();
                AddIcon();
            }
            catch (Exception e)
            {
                Debug.LogError("[DesktopFigure] Tray icon init failed: " + e);
            }
        }

        void Update()
        {
            if (_hwnd == IntPtr.Zero) return;
            float t = Time.unscaledTime;
            if (!_added && !_quitting && t >= _nextRetry)
            {
                // 开机自启时资源管理器可能还没准备好：每 3 秒重试一次
                _nextRetry = t + 3f;
                AddIcon();
            }
            if (_added && t >= _nextTipCheck)
            {
                _nextTipCheck = t + 1f;
                string tip = PetDisplayName;
                if (tip != _tip) ModifyTip(tip);
            }
        }

        void OnApplicationQuit()
        {
            _quitting = true;
            DeleteIcon();
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            _quitting = true;
            DeleteIcon();
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_ownsIcon && _hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
            Instance = null;
            if (s_active == this) s_active = null;
        }

        // ------------------------------------------------------------------
        // 隐藏窗口 + WndProc
        // ------------------------------------------------------------------

        const string WindowClassName = "DesktopFigureTrayWindow";
        const uint WM_TRAYICON = 0x8000 + 0x51; // WM_APP + n
        const uint TRAY_ID = 1;

        const uint CMD_TOGGLE = 1001;
        const uint CMD_SHOW = 1002;
        const uint CMD_EXIT = 1003;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // 必须一直被静态字段引用，防止委托被 GC 回收导致原生回调崩溃
        static WndProcDelegate s_wndProc;
        static TrayIcon s_active;

        IntPtr _hwnd;
        IntPtr _hIcon;
        bool _ownsIcon;
        bool _added;
        bool _quitting;
        uint _taskbarCreatedMsg;
        string _tip = "";
        float _nextRetry;
        float _nextTipCheck;
        bool _menuOpen;

        void CreateHiddenWindow()
        {
            IntPtr hInstance = GetModuleHandle(null);
            if (s_wndProc == null) s_wndProc = WndProc;

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = hInstance,
                lpszClassName = WindowClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_CLASS_ALREADY_EXISTS) throw new Exception("RegisterClassEx failed, error " + err);
            }

            // 隐藏的顶层窗口（从不 ShowWindow），WS_EX_TOOLWINDOW 保证它也不会出现在任务栏
            _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, WindowClassName, "DesktopFigure Tray", WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new Exception("CreateWindowEx failed, error " + Marshal.GetLastWin32Error());

            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            try
            {
                // 以管理员权限运行时，UIPI 会拦截资源管理器发来的 TaskbarCreated；放行它
                if (_taskbarCreatedMsg != 0) ChangeWindowMessageFilterEx(_hwnd, _taskbarCreatedMsg, MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch (Exception)
            {
                // 老系统没有这个 API，忽略
            }
        }

        [AOT.MonoPInvokeCallback(typeof(WndProcDelegate))]
        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                var self = s_active;
                if (self != null && hWnd == self._hwnd && hWnd != IntPtr.Zero)
                {
                    if (msg == WM_TRAYICON)
                    {
                        self.OnTrayMessage((uint)(lParam.ToInt64() & 0xFFFF));
                        return IntPtr.Zero;
                    }
                    if (self._taskbarCreatedMsg != 0 && msg == self._taskbarCreatedMsg)
                    {
                        // 资源管理器重启了：之前的图标已经没了，重新添加
                        Debug.Log("[DesktopFigure] TaskbarCreated received, re-adding tray icon");
                        self._added = false;
                        if (!self._quitting) self.AddIcon();
                        return IntPtr.Zero;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        void OnTrayMessage(uint mouseMsg)
        {
            switch (mouseMsg)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ShowPet();
                    break;
                case WM_RBUTTONUP:
                    ShowContextMenu();
                    break;
            }
        }

        void ShowPet()
        {
            var tw = TransparentWindow.Instance;
            if (tw != null) tw.SetPetVisible(true, true);
        }

        void ShowContextMenu()
        {
            if (_menuOpen) return;
            _menuOpen = true;
            IntPtr menu = IntPtr.Zero;
            try
            {
                var tw = TransparentWindow.Instance;
                bool visible = tw == null || tw.IsPetVisible;
                string name = PetDisplayName;

                menu = CreatePopupMenu();
                if (menu == IntPtr.Zero) return;
                AppendMenu(menu, MF_STRING, (UIntPtr)CMD_TOGGLE, (visible ? "隐藏" : "显示") + name);
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, (UIntPtr)CMD_EXIT, "退出");

                GetCursorPos(out POINT pt);
                // 官方文档的做法：先 SetForegroundWindow，菜单关闭后 PostMessage(WM_NULL)，
                // 否则点击菜单外部时菜单不会消失
                SetForegroundWindow(_hwnd);
                uint flags = TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON |
                             (GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN) | TPM_BOTTOMALIGN;
                uint cmd = TrackPopupMenuEx(menu, flags, pt.X, pt.Y, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                switch (cmd)
                {
                    case CMD_TOGGLE:
                        if (tw != null) tw.SetPetVisible(!visible, !visible);
                        break;
                    case CMD_SHOW:
                        ShowPet();
                        break;
                    case CMD_EXIT:
                        Debug.Log("[DesktopFigure] Tray menu: Quit");
                        _quitting = true;
                        DeleteIcon();
                        Application.Quit();
                        break;
                }
            }
            finally
            {
                if (menu != IntPtr.Zero) DestroyMenu(menu);
                _menuOpen = false;
            }
        }

        // ------------------------------------------------------------------
        // Shell_NotifyIcon
        // ------------------------------------------------------------------

        NOTIFYICONDATA NewData()
        {
            return new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = _hwnd,
                uID = TRAY_ID,
                szTip = "",
                szInfo = "",
                szInfoTitle = "",
            };
        }

        static string Clip(string s, int max) => s == null ? "" : (s.Length > max ? s.Substring(0, max) : s);

        void AddIcon()
        {
            if (_hwnd == IntPtr.Zero) return;
            _tip = PetDisplayName;
            var nid = NewData();
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            nid.uCallbackMessage = WM_TRAYICON;
            nid.hIcon = _hIcon;
            nid.szTip = Clip(_tip, 127);

            bool ok = Shell_NotifyIcon(NIM_ADD, ref nid);
            if (!ok)
            {
                // 可能已经存在（例如 TaskbarCreated 与启动同时发生）：改用 MODIFY
                ok = Shell_NotifyIcon(NIM_MODIFY, ref nid);
            }
            _added = ok;
            if (ok) Debug.Log("[DesktopFigure] Tray icon added (tip: " + _tip + ")");
            else Debug.LogWarning("[DesktopFigure] Tray icon add failed (error " + Marshal.GetLastWin32Error() + "), will retry");
        }

        void ModifyTip(string tip)
        {
            _tip = tip;
            var nid = NewData();
            nid.uFlags = NIF_TIP;
            nid.szTip = Clip(tip, 127);
            if (Shell_NotifyIcon(NIM_MODIFY, ref nid)) Debug.Log("[DesktopFigure] Tray tooltip: " + tip);
            else _added = false; // 稍后重新添加
        }

        void DeleteIcon()
        {
            if (_hwnd == IntPtr.Zero || !_added) return;
            var nid = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
            Debug.Log("[DesktopFigure] Tray icon removed");
        }

        /// <summary>托盘图标 = exe 自己的图标（按当前 DPI 取合适尺寸），失败时用系统默认程序图标。</summary>
        void LoadTrayIcon()
        {
            string exe = GetExePath();
            int size = 16;
            try
            {
                uint dpi = GetDpiForWindow(_hwnd);
                if (dpi == 0) dpi = 96;
                size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
            }
            catch (Exception)
            {
                size = GetSystemMetrics(SM_CXSMICON);
            }
            if (size <= 0) size = 16;

            if (!string.IsNullOrEmpty(exe))
            {
                try
                {
                    uint sizes = (uint)(size & 0xFFFF) | ((uint)(size & 0xFFFF) << 16);
                    if (SHDefExtractIcon(exe, 0, 0, out IntPtr large, IntPtr.Zero, sizes) == 0 && large != IntPtr.Zero)
                    {
                        _hIcon = large;
                        _ownsIcon = true;
                    }
                }
                catch (Exception)
                {
                    _hIcon = IntPtr.Zero;
                }
                if (_hIcon == IntPtr.Zero)
                {
                    var small = new IntPtr[1];
                    if (ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                    {
                        _hIcon = small[0];
                        _ownsIcon = true;
                    }
                }
            }
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
                _ownsIcon = false; // 系统共享图标，不能 DestroyIcon
            }
            Debug.Log($"[DesktopFigure] Tray icon image: {(_ownsIcon ? exe : "IDI_APPLICATION")} ({size}px)");
        }

        static string GetExePath()
        {
            var sb = new StringBuilder(1024);
            uint n = GetModuleFileName(IntPtr.Zero, sb, (uint)sb.Capacity);
            return n > 0 ? sb.ToString() : null;
        }

        // ------------------------------------------------------------------
        // Win32 声明（全部 Unicode）
        // ------------------------------------------------------------------

        const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOOLWINDOW = 0x00000080;
        const uint MSGFLT_ALLOW = 1;

        const uint WM_NULL = 0x0000;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_LBUTTONDBLCLK = 0x0203;
        const uint WM_RBUTTONUP = 0x0205;

        const uint NIM_ADD = 0x0;
        const uint NIM_MODIFY = 0x1;
        const uint NIM_DELETE = 0x2;
        const uint NIF_MESSAGE = 0x1;
        const uint NIF_ICON = 0x2;
        const uint NIF_TIP = 0x4;

        const uint MF_STRING = 0x0;
        const uint MF_SEPARATOR = 0x800;
        const uint TPM_LEFTALIGN = 0x0;
        const uint TPM_RIGHTALIGN = 0x8;
        const uint TPM_BOTTOMALIGN = 0x20;
        const uint TPM_RIGHTBUTTON = 0x2;
        const uint TPM_NONOTIFY = 0x80;
        const uint TPM_RETURNCMD = 0x100;

        const int SM_CXSMICON = 49;
        const int SM_MENUDROPALIGNMENT = 40;
        const int IDI_APPLICATION = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHDefExtractIconW")]
        static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, IntPtr phiconSmall, uint nIconSize);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
        static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadIconW")]
        static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
        static extern uint GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, uint nSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
        static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
        static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);

        [DllImport("user32.dll")]
        static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);
#endif
    }
}
