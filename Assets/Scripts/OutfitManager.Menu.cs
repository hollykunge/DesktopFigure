using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DesktopFigure
{
    /// <summary>
    /// 右键菜单（Windows 11 风格，IMGUI + MenuSkin）：
    ///   GorkBot / Dot / Muse（预留入口：OnGorkBot / OnDot / OnMuse）
    ///   ─────
    ///   「宠物名称」的衣橱 ›   服装列表（当前打勾、数字键提示）/ 刷新服装列表 / 打开 Outfits 文件夹
    ///   设置 ›                 自动旋转 / 重置视角 / 渲染风格：原版 / 渲染风格：绝区零风 / 始终置顶 / 宠物名称… / 操作说明 / 退出
    ///
    /// 布局在“桌面坐标”里计算（菜单像 Win11 一样贴着光标、放不下就翻到另一边）。
    /// 窗口本身是一块比角色大的透明画布（见 WindowDragScale），菜单都排在画布里 => 打开/关闭菜单时窗口大小不变，角色不会抖。
    /// 只有画布实在放不下时（例如角色大半拖出了屏幕）才临时放大窗口，关闭后恢复。
    /// </summary>
    public partial class OutfitManager
    {
        // ------------------------------------------------------------------
        // 菜单顶部的扩展功能入口（目前是占位：点击只关闭菜单并打日志）。
        // 以后实现功能时，只需填写下面的 OnGorkBot / OnDot / OnMuse。
        // ------------------------------------------------------------------
        void OnGorkBot()
        {
            Debug.Log("[DesktopFigure] Menu: GorkBot");
            // TODO: GorkBot 功能
        }

        void OnDot()
        {
            Debug.Log("[DesktopFigure] Menu: Dot");
            // TODO: Dot 功能
        }

        void OnMuse()
        {
            Debug.Log("[DesktopFigure] Menu: Muse");
            // TODO: Muse 功能
        }

        // ---- 图标（Segoe Fluent Icons 码位；没有该字体时（如 macOS）MenuSkin 会映射到内置的 Material Icons）----
        const string IcoBot = "\uE99A";       // Robot
        const string IcoDot = "\uE8BD";       // Message（聊天气泡）
        const string IcoMuse = "\uEA80";      // Lightbulb
        const string IcoSettings = "\uE713";  // Settings（齿轮）
        const string IcoRotate = "\uE7AD";    // Rotate
        const string IcoReset = "\uE72C";     // Refresh
        const string IcoPin = "\uE718";       // Pin
        const string IcoRename = "\uE8AC";    // Rename
        const string IcoHelp = "\uE897";      // Help
        const string IcoPower = "\uE7E8";     // PowerButton
        const string IcoSync = "\uE895";      // Sync
        const string IcoFolder = "\uE838";    // FolderOpen
        const string IcoPerson = "\uE77B";    // Contact（人像）
        const string IcoStyleOriginal = "\uE91B"; // Photo（原版渲染）
        const string IcoStyleNpr = "\uE790";      // Color（调色板，绝区零风）

        // ---- 尺寸（逻辑像素 × DPI 缩放）----
        static float S => TransparentWindow.UiScale;
        static float ItemH => Mathf.Round(32f * S);
        static float SeparatorH => Mathf.Round(9f * S);
        static float PanelPadY => Mathf.Round(4f * S);
        static float RowInsetX => Mathf.Round(4f * S);
        static float MainWidth => Mathf.Round(240f * S);
        static float SubWidth => Mathf.Round(256f * S);
        static float SubGap => Mathf.Round(2f * S);
        static float ShadowRoom => Mathf.Round((MenuSkin.ShadowMarginL + MenuSkin.ShadowOffsetL) * S);
        const float OpenAnimSeconds = 0.13f;
        const float SubAnimSeconds = 0.10f;
        const float HoverOpenDelay = 0.22f;    // 悬停多久打开子菜单（Win11 有一个小延迟）
        const float HoverCloseDelay = 0.30f;   // 子菜单打开时，悬停到别的项多久后关闭
        const int PetNameMaxLength = 24;

        enum RowKind { Item, Separator }
        enum SubId { None = -1, Wardrobe = 0, Settings = 1 }

        struct MenuRow
        {
            public RowKind Kind;
            public string Glyph;
            public Texture2D IconTex;
            public string Label;
            public string Shortcut;
            public bool Checked;
            public bool Enabled;
            public bool KeepOpen;
            public SubId Submenu;
            public Action OnClick;

            public static MenuRow Sep() => new MenuRow { Kind = RowKind.Separator, Submenu = SubId.None };

            public static MenuRow Item(string glyph, string label, Action onClick, bool isChecked = false,
                string shortcut = null, bool enabled = true, bool keepOpen = false)
                => new MenuRow
                {
                    Kind = RowKind.Item, Glyph = glyph, Label = label, OnClick = onClick, Checked = isChecked,
                    Shortcut = shortcut, Enabled = enabled, KeepOpen = keepOpen, Submenu = SubId.None,
                };

            public static MenuRow Sub(string glyph, Texture2D tex, string label, SubId sub)
                => new MenuRow { Kind = RowKind.Item, Glyph = glyph, IconTex = tex, Label = label, Enabled = true, Submenu = sub };
        }

        readonly MenuSkin _skin = new MenuSkin();
        readonly List<MenuRow> _mainRows = new List<MenuRow>();
        readonly List<MenuRow> _wardrobeRows = new List<MenuRow>();
        readonly List<MenuRow> _settingsRows = new List<MenuRow>();

        // 状态
        bool _menuOpen;
        bool _renameOpen;
        public bool IsMenuOpen => _menuOpen || _renameOpen;

        // 布局（桌面坐标，物理像素）
        Rect _mainD;
        readonly Rect[] _subD = new Rect[2];
        readonly bool[] _subOnLeft = new bool[2];
        Vector2 _menuPivotD;
        Vector2Int _overlayOrigin;     // 当前窗口客户区左上角的桌面坐标
        float _menuOpenedAt;

        SubId _openSub = SubId.None;
        float _subOpenedAt;
        int _hoverMainIndex = -1;
        float _hoverSince;
        Vector2 _subScroll;

        // 改名弹窗
        Rect _renameD;
        string _renameText = "";
        bool _renameFocusPending;
        float _renameOpenedAt;
        const string RenameControl = "DF_PetNameField";

        // 操作说明
        bool _showHints;
        float _hintsShownAt;
        float _hintsUntil;
        Rect _hintRect;

        // 画布放不下时的临时放大
        bool _windowExpanded;
        RectInt _expandedWindowRect;
        public bool IsWindowExpanded => _windowExpanded;

        // ==================================================================
        // 菜单内容
        // ==================================================================

        void BuildRows()
        {
            _skin.Ensure(S);

            _mainRows.Clear();
            _mainRows.Add(MenuRow.Item(IcoBot, "GorkBot", OnGorkBot));
            _mainRows.Add(MenuRow.Item(IcoDot, "Dot", OnDot));
            _mainRows.Add(MenuRow.Item(IcoMuse, "Muse", OnMuse));
            _mainRows.Add(MenuRow.Sep());
            _mainRows.Add(MenuRow.Sub(null, _skin.HangerTex, PetName + "的衣橱", SubId.Wardrobe));
            _mainRows.Add(MenuRow.Sub(IcoSettings, null, "设置", SubId.Settings));

            _wardrobeRows.Clear();
            if (_files.Count == 0)
            {
                _wardrobeRows.Add(MenuRow.Item(IcoPerson, "Outfits 文件夹为空", null, enabled: false));
            }
            for (int i = 0; i < _files.Count; i++)
            {
                int index = i;
                _wardrobeRows.Add(MenuRow.Item(IcoPerson, Path.GetFileNameWithoutExtension(_files[i]), () => LoadOutfit(index),
                    isChecked: i == CurrentIndex, shortcut: i < 9 ? (i + 1).ToString() : null, enabled: !IsLoading));
            }
            _wardrobeRows.Add(MenuRow.Sep());
            _wardrobeRows.Add(MenuRow.Item(IcoSync, "刷新服装列表", () => { RefreshList(); LayoutMenus(_menuPivotD, true); }, keepOpen: true));
            _wardrobeRows.Add(MenuRow.Item(IcoFolder, "打开 Outfits 文件夹", OpenOutfitsFolder));

            _settingsRows.Clear();
            bool auto = _view != null && _view.AutoRotate;
            _settingsRows.Add(MenuRow.Item(IcoRotate, "自动旋转", () => { if (_view != null) _view.SetAutoRotate(!auto); }, isChecked: auto, keepOpen: true));
            _settingsRows.Add(MenuRow.Item(IcoReset, "重置视角", () => { if (_view != null) _view.ResetView(); }));
            _settingsRows.Add(MenuRow.Sep());
            bool npr = RenderStyleNpr;
            _settingsRows.Add(MenuRow.Item(IcoStyleOriginal, "渲染风格：原版", () => SetRenderStyle(false), isChecked: !npr, keepOpen: true, enabled: HasModel));
            _settingsRows.Add(MenuRow.Item(IcoStyleNpr, "渲染风格：绝区零风", () => SetRenderStyle(true), isChecked: npr, keepOpen: true, enabled: HasModel));
            _settingsRows.Add(MenuRow.Sep());
            var tw = TransparentWindow.Instance;
            bool top = tw != null && tw.IsTopmost;
            _settingsRows.Add(MenuRow.Item(IcoPin, "始终置顶", () => { if (tw != null) tw.SetTopmost(!top); }, isChecked: top, keepOpen: true));
            _settingsRows.Add(MenuRow.Item(IcoRename, "宠物名称…", OpenRename));
            _settingsRows.Add(MenuRow.Item(IcoHelp, "操作说明", () => ShowHints(20f)));
            _settingsRows.Add(MenuRow.Sep());
            _settingsRows.Add(MenuRow.Item(IcoPower, "退出", () =>
            {
                TrayIcon.RemoveIcon(); // 先删托盘图标，避免退出后残留“幽灵图标”
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
            }));
        }

        List<MenuRow> RowsOf(SubId id) => id == SubId.Wardrobe ? _wardrobeRows : _settingsRows;

        static float RowsHeight(List<MenuRow> rows)
        {
            float h = PanelPadY * 2f;
            foreach (var r in rows) h += r.Kind == RowKind.Separator ? SeparatorH : ItemH;
            return h;
        }

        static float RowTop(List<MenuRow> rows, int index)
        {
            float y = PanelPadY;
            for (int i = 0; i < index && i < rows.Count; i++) y += rows[i].Kind == RowKind.Separator ? SeparatorH : ItemH;
            return y;
        }

        /// <summary>桌面坐标 p 落在面板的哪一行（只算可点的行），没有返回 -1。</summary>
        static int RowAt(List<MenuRow> rows, Rect panelD, Vector2 p, float scrollY = 0f)
        {
            if (!panelD.Contains(p)) return -1;
            float y = panelD.y + PanelPadY - scrollY;
            for (int i = 0; i < rows.Count; i++)
            {
                float h = rows[i].Kind == RowKind.Separator ? SeparatorH : ItemH;
                if (p.y >= y && p.y < y + h) return rows[i].Kind == RowKind.Item ? i : -1;
                y += h;
            }
            return -1;
        }

        // ==================================================================
        // 打开 / 关闭 / 布局
        // ==================================================================

        public void OpenMenuPublic() => OpenMenu();
        public void CloseMenuPublic() => CloseAllOverlays();

        void OpenMenu()
        {
            if (_renameOpen) CloseRename(false);
            RefreshList();
            _menuOpen = true;
            _openSub = SubId.None;
            _hoverMainIndex = -1;
            _subScroll = Vector2.zero;
            _menuOpenedAt = Time.unscaledTime;
            LayoutMenus(CursorDesktop(), false);
        }

        void OpenMenuAtDesktop(Vector2 anchor)
        {
            RefreshList();
            _menuOpen = true;
            _openSub = SubId.None;
            _hoverMainIndex = -1;
            _menuOpenedAt = Time.unscaledTime;
            LayoutMenus(anchor, false);
        }

        void CloseMenu()
        {
            _menuOpen = false;
            _openSub = SubId.None;
            if (!IsMenuOpen) RestoreWindowAfterMenu();
        }

        void CloseAllOverlays()
        {
            if (_renameOpen) CloseRename(false);
            CloseMenu();
        }

        void OpenSub(SubId id)
        {
            if (_openSub == id) return;
            _openSub = id;
            _subOpenedAt = Time.unscaledTime;
            _subScroll = Vector2.zero;
        }

        void CloseSub()
        {
            _openSub = SubId.None;
        }

        /// <summary>
        /// 在桌面坐标里排版：主菜单贴着 anchor（光标），放不下翻到左/上；两个子菜单预先算好位置（右侧放不下就放左侧），
        /// 然后保证窗口能装下全部（必要时临时放大窗口）。
        /// </summary>
        void LayoutMenus(Vector2 anchor, bool keepMainPosition)
        {
            BuildRows();
            float room = ShadowRoom;
            float mw = MainWidth;
            float mh = RowsHeight(_mainRows);
            float sw = SubWidth;
            float subMaxH = Mathf.Max(RowsHeight(_wardrobeRows), RowsHeight(_settingsRows));
            var b = LayoutBounds(anchor, mw + SubGap + sw, Mathf.Max(mh, subMaxH));

            if (!keepMainPosition)
            {
                float x = anchor.x + mw <= b.xMax ? anchor.x : anchor.x - mw;
                float y = anchor.y + mh <= b.yMax ? anchor.y : anchor.y - mh;
                x = Mathf.Clamp(x, b.xMin, Mathf.Max(b.xMin, b.xMax - mw));
                y = Mathf.Clamp(y, b.yMin, Mathf.Max(b.yMin, b.yMax - mh));
                // 子菜单左右都放不下时，把主菜单往空间大的一侧挪一点（Win11 也会这样挪）
                bool rightOk = x + mw + SubGap + sw <= b.xMax;
                bool leftOk = x - SubGap - sw >= b.xMin;
                if (!rightOk && !leftOk)
                {
                    x = (b.xMax - (x + mw)) >= (x - b.xMin) ? b.xMax - mw - SubGap - sw : b.xMin + SubGap + sw;
                    x = Mathf.Clamp(x, b.xMin, Mathf.Max(b.xMin, b.xMax - mw));
                }
                _mainD = new Rect(Mathf.Round(x), Mathf.Round(y), mw, mh);
                _menuPivotD = new Vector2(Mathf.Clamp(anchor.x, _mainD.xMin, _mainD.xMax), Mathf.Clamp(anchor.y, _mainD.yMin, _mainD.yMax));
            }
            else
            {
                _mainD = new Rect(_mainD.x, _mainD.y, mw, mh);
            }

            for (int s = 0; s < 2; s++)
            {
                var id = (SubId)s;
                int rowIndex = _mainRows.FindIndex(r => r.Submenu == id);
                float sh = Mathf.Min(RowsHeight(RowsOf(id)), b.height);
                float xr = _mainD.xMax + SubGap;
                float xl = _mainD.xMin - SubGap - sw;
                bool rightFits = xr + sw <= b.xMax;
                bool leftFits = xl >= b.xMin;
                bool onLeft = !rightFits && (leftFits || (_mainD.xMin - b.xMin) > (b.xMax - _mainD.xMax));
                float sx = Mathf.Clamp(onLeft ? xl : xr, b.xMin, Mathf.Max(b.xMin, b.xMax - sw));
                float sy = _mainD.y + RowTop(_mainRows, rowIndex) - PanelPadY;
                if (sy + sh > b.yMax) sy = b.yMax - sh;
                if (sy < b.yMin) sy = b.yMin;
                _subD[s] = new Rect(Mathf.Round(sx), Mathf.Round(sy), sw, Mathf.Round(sh));
                _subOnLeft[s] = onLeft;
            }

            var need = Union(Union(_mainD, _subD[0]), _subD[1]);
            EnsureWindowRegion(Inflate(need, room));
        }

        /// <summary>
        /// 菜单 / 弹窗可以摆放的范围（桌面坐标，已扣掉阴影）：画布 ∩ 显示器工作区。
        /// 画布里放不下 needW x needH 时退回整个工作区（这时会临时放大窗口）。
        /// </summary>
        Rect LayoutBounds(Vector2 p, float needW, float needH)
        {
            float room = ShadowRoom;
            var w = WorkAreaAt(p);
            var work = Rect.MinMaxRect(w.xMin + room, w.yMin + room, w.xMax - room, w.yMax - room);
            var c = CanvasD();
            var canvas = Rect.MinMaxRect(c.xMin + room, c.yMin + room, c.xMax - room, c.yMax - room);
            var both = Rect.MinMaxRect(Mathf.Max(work.xMin, canvas.xMin), Mathf.Max(work.yMin, canvas.yMin),
                                       Mathf.Min(work.xMax, canvas.xMax), Mathf.Min(work.yMax, canvas.yMax));
            if (both.width >= needW && both.height >= needH) return both;
            return work;
        }

        /// <summary>画布（正常状态下的窗口）桌面矩形。</summary>
        RectInt CanvasD()
        {
            var d = WindowDragScale.Instance;
            if (d != null && d.HasFigureRect) return d.CanvasRect;
            var tw = TransparentWindow.Instance;
            if (TransparentWindow.IsNativeWindowActive && tw != null && tw.TryGetWindowRect(out var r)) return r;
            return new RectInt(0, 0, Screen.width, Screen.height);
        }

        /// <summary>角色区域（相机视口）桌面矩形。</summary>
        RectInt FigureD()
        {
            var d = WindowDragScale.Instance;
            if (d != null && d.HasFigureRect) return d.FigureRect;
            return CanvasD();
        }

        static Rect ToRect(RectInt r) => new Rect(r.x, r.y, r.width, r.height);

        static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
        static Rect Inflate(Rect r, float m) => new Rect(r.x - m, r.y - m, r.width + m * 2f, r.height + m * 2f);

        Rect ToGui(Rect d) => new Rect(d.x - _overlayOrigin.x, d.y - _overlayOrigin.y, d.width, d.height);
        Vector2 ToGui(Vector2 d) => new Vector2(d.x - _overlayOrigin.x, d.y - _overlayOrigin.y);

        // ---- 桌面坐标工具（编辑器里退化为 GUI 坐标）----

        static Vector2 CursorDesktop()
        {
            if (TransparentWindow.IsNativeWindowActive && TransparentWindow.TryGetCursorScreenPosition(out var c)) return c;
            return GuiMousePosition();
        }

        static RectInt WorkAreaAt(Vector2 p)
        {
            if (TransparentWindow.IsNativeWindowActive) return TransparentWindow.GetWorkAreaAt(new Vector2Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)));
            return new RectInt(0, 0, Screen.width, Screen.height);
        }

        static Vector2 GuiMousePosition()
        {
            Vector2 p = TransparentWindow.GetCursorUnityPosition();
            return new Vector2(p.x, Screen.height - p.y);
        }

        // ==================================================================
        // 临时放大窗口
        // ==================================================================

        /// <summary>
        /// 正常情况下菜单都在画布里，什么都不用做（不改变窗口大小 => 角色不抖）。
        /// 只有画布放不下 needD 时才临时放大窗口；放得下了就恢复成画布。
        /// </summary>
        void EnsureWindowRegion(Rect needD)
        {
            var tw = TransparentWindow.Instance;
            if (!TransparentWindow.IsNativeWindowActive || tw == null) return;
            var c = CanvasD();
            var vs = TransparentWindow.GetVirtualScreen();
            int x0 = Mathf.Min(c.xMin, Mathf.Max(vs.xMin, Mathf.FloorToInt(needD.xMin)));
            int y0 = Mathf.Min(c.yMin, Mathf.Max(vs.yMin, Mathf.FloorToInt(needD.yMin)));
            int x1 = Mathf.Max(c.xMax, Mathf.Min(vs.xMax, Mathf.CeilToInt(needD.xMax)));
            int y1 = Mathf.Max(c.yMax, Mathf.Min(vs.yMax, Mathf.CeilToInt(needD.yMax)));
            var target = new RectInt(x0, y0, x1 - x0, y1 - y0);

            if (target.Equals(c))
            {
                if (_windowExpanded) RestoreWindowAfterMenu();
                return;
            }
            if (!_windowExpanded || !target.Equals(_expandedWindowRect))
            {
                _expandedWindowRect = target;
                _windowExpanded = true;
                Debug.Log($"[DesktopFigure] Overlay does not fit in the canvas; window temporarily enlarged to {target}");
                tw.SetWindowRect(target.x, target.y, target.width, target.height);
            }
        }

        void RestoreWindowAfterMenu()
        {
            if (!_windowExpanded) return;
            _windowExpanded = false;
            var d = WindowDragScale.Instance;
            if (d != null) d.RestoreCanvas();
        }

        /// <summary>GUI 坐标原点 = 窗口客户区左上角（WS_POPUP：窗口矩形 = 客户区）。</summary>
        void UpdateOverlayOrigin()
        {
            var tw = TransparentWindow.Instance;
            if (TransparentWindow.IsNativeWindowActive && tw != null && tw.TryGetWindowRect(out var r)) _overlayOrigin = new Vector2Int(r.x, r.y);
            else _overlayOrigin = Vector2Int.zero;
        }

        // ==================================================================
        // 输入：悬停打开子菜单、点外面关闭、Esc
        // ==================================================================

        void UpdateMenuInput()
        {
            if (!_menuOpen) return;
            float now = Time.unscaledTime;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_openSub != SubId.None) CloseSub();
                else CloseMenu();
                return;
            }

            Vector2 m = CursorDesktop();
            bool inSub = _openSub != SubId.None && _subD[(int)_openSub].Contains(m);
            bool inMain = _mainD.Contains(m);

            if (TransparentWindow.GetMouseButtonDown(0) && !inSub && !inMain)
            {
                CloseMenu();
                return;
            }

            if (inSub)
            {
                _hoverMainIndex = -1;
                return;
            }

            int hi = RowAt(_mainRows, _mainD, m);
            if (hi < 0)
            {
                _hoverMainIndex = -1;    // 鼠标在菜单外 / 分隔线上：保持子菜单（Win11 行为）
                return;
            }
            if (hi != _hoverMainIndex)
            {
                _hoverMainIndex = hi;
                _hoverSince = now;
            }
            var row = _mainRows[hi];
            if (row.Submenu != SubId.None)
            {
                if (row.Submenu != _openSub && now - _hoverSince >= HoverOpenDelay) OpenSub(row.Submenu);
            }
            else if (_openSub != SubId.None && now - _hoverSince >= HoverCloseDelay)
            {
                CloseSub();
            }
        }

        // ==================================================================
        // 改名弹窗
        // ==================================================================

        void OpenRename()
        {
            _renameOpen = true;
            _menuOpen = false;
            _openSub = SubId.None;
            string custom = PlayerPrefs.GetString(PrefPetName, "");
            _renameText = string.IsNullOrWhiteSpace(custom) ? PetName : custom.Trim();
            _renameFocusPending = true;
            _renameOpenedAt = Time.unscaledTime;
            Input.imeCompositionMode = IMECompositionMode.On;   // 中文输入法

            // 放在角色上部居中（画布里放不下才临时放大窗口）
            _skin.Ensure(S);
            float w = Mathf.Round(320f * S), h = RenameHeight();
            var tw = TransparentWindow.Instance;
            var fig = FigureD();
            var center = new Vector2(fig.x + fig.width * 0.5f, fig.y + fig.height * 0.3f);
            var b = LayoutBounds(center, w, h);
            float x = Mathf.Clamp(center.x - w * 0.5f, b.xMin, Mathf.Max(b.xMin, b.xMax - w));
            float y = Mathf.Clamp(center.y - h * 0.5f, b.yMin, Mathf.Max(b.yMin, b.yMax - h));
            _renameD = new Rect(Mathf.Round(x), Mathf.Round(y), w, h);
            EnsureWindowRegion(Inflate(_renameD, ShadowRoom));
            if (tw != null) tw.Activate();   // 输入框需要键盘焦点
        }

        static float RenameHeight()
        {
            float pad = Mathf.Round(20f * S);
            return pad + Mathf.Round(24f * S) + Mathf.Round(20f * S) + Mathf.Round(12f * S) + Mathf.Round(32f * S)
                   + Mathf.Round(20f * S) + Mathf.Round(32f * S) + pad;
        }

        void CloseRename(bool commit)
        {
            if (!_renameOpen) return;
            if (commit) SetCustomPetName(_renameText);
            _renameOpen = false;
            Input.imeCompositionMode = IMECompositionMode.Auto;
            GUIUtility.keyboardControl = 0;
            if (!IsMenuOpen) RestoreWindowAfterMenu();
        }

        // ==================================================================
        // 绘制
        // ==================================================================

        static float EaseOutCubic(float t)
        {
            t = 1f - Mathf.Clamp01(t);
            return 1f - t * t * t;
        }

        void OnGUI()
        {
            _skin.Ensure(S);
            UpdateOverlayOrigin();
            var oldColor = GUI.color;
            var oldMatrix = GUI.matrix;

            // 状态提示（没有服装 / 加载中 / 加载失败）。不透明背景 => 该区域可点击，可以右键打开菜单。
            if (!string.IsNullOrEmpty(_status) && (_current == null || IsLoading || _status.StartsWith("加载失败", StringComparison.Ordinal)))
            {
                float bh = Mathf.Round(110f * S);
                float m = Mathf.Round(12f * S);
                var f = ToGui(ToRect(FigureD()));
                var r = new Rect(f.x + m, Mathf.Round(f.center.y - bh * 0.5f), Mathf.Max(f.width - m * 2f, 120f * S), bh);
                _skin.DrawPanel(r);
                GUI.Label(new Rect(r.x + 10f * S, r.y + 6f * S, r.width - 20f * S, r.height - 12f * S),
                    _status + "\n（右键打开菜单）", _skin.Message);
            }

            if (_showHints && _current != null) DrawHints();
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;

            if (_menuOpen) DrawMenus();
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;

            if (_renameOpen) DrawRename();
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;
        }

        void DrawMenus()
        {
            BuildRows();
            var baseMatrix = GUI.matrix;

            // ---- 主菜单：0.13s 淡入 + 96%→100% 放大（以光标处为中心）----
            float t = EaseOutCubic((Time.unscaledTime - _menuOpenedAt) / OpenAnimSeconds);
            if (t < 1f)
            {
                float sc = Mathf.Lerp(0.96f, 1f, t);
                GUIUtility.ScaleAroundPivot(new Vector2(sc, sc), ToGui(_menuPivotD));
            }
            GUI.color = new Color(1f, 1f, 1f, t);
            int highlight = _openSub == SubId.None ? -1 : _mainRows.FindIndex(r => r.Submenu == _openSub);
            Vector2 noScroll = Vector2.zero;
            int clickedMain = DrawRows(_mainRows, ToGui(_mainD), highlight, ref noScroll, false);
            GUI.matrix = baseMatrix;

            // ---- 子菜单：0.10s 淡入 + 从主菜单一侧滑出 4px ----
            int clickedSub = -1;
            var openSub = _openSub;
            if (openSub != SubId.None)
            {
                float ts = EaseOutCubic((Time.unscaledTime - _subOpenedAt) / SubAnimSeconds);
                var rect = ToGui(_subD[(int)openSub]);
                float slide = (1f - ts) * 4f * S * (_subOnLeft[(int)openSub] ? 1f : -1f);
                rect.x += Mathf.Round(slide);
                GUI.color = new Color(1f, 1f, 1f, ts * t);
                clickedSub = DrawRows(RowsOf(openSub), rect, -1, ref _subScroll, true);
            }

            // ---- 点击处理（循环结束后再执行，避免边遍历边修改）----
            if (clickedMain >= 0)
            {
                var row = _mainRows[clickedMain];
                if (row.Submenu != SubId.None) OpenSub(row.Submenu);
                else Activate(row);
            }
            else if (clickedSub >= 0 && openSub != SubId.None)
            {
                var rows = RowsOf(openSub);
                if (clickedSub < rows.Count) Activate(rows[clickedSub]);
            }
        }

        void Activate(MenuRow row)
        {
            // 非开关类：先关菜单再执行（窗口恢复放到最后，这样“宠物名称…”打开弹窗时不会先缩回再放大）
            if (!row.KeepOpen)
            {
                _menuOpen = false;
                _openSub = SubId.None;
            }
            try
            {
                row.OnClick?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            if (!IsMenuOpen) RestoreWindowAfterMenu();
        }

        /// <summary>画一个菜单面板，返回被点击的行号（-1 = 没有）。</summary>
        int DrawRows(List<MenuRow> rows, Rect panel, int highlightIndex, ref Vector2 scroll, bool allowScroll)
        {
            _skin.DrawPanel(panel);
            int clicked = -1;
            var inner = new Rect(panel.x, panel.y + PanelPadY, panel.width, panel.height - PanelPadY * 2f);
            float contentH = RowsHeight(rows) - PanelPadY * 2f;
            bool scrolling = allowScroll && contentH > inner.height + 0.5f;
            var view = new Rect(0f, 0f, inner.width - (scrolling ? 14f * S : 0f), contentH);
            scroll = GUI.BeginScrollView(inner, scroll, view, false, false, GUIStyle.none, scrolling ? GUI.skin.verticalScrollbar : GUIStyle.none);

            float y = 0f;
            float x = RowInsetX;
            float w = view.width - RowInsetX * 2f;
            float iconW = Mathf.Round(16f * S);
            float iconX = x + Mathf.Round(12f * S);
            float rightW = Mathf.Round(16f * S);
            float rightX = x + w - Mathf.Round(12f * S) - rightW;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind == RowKind.Separator)
                {
                    _skin.DrawSeparator(0f, y + Mathf.Floor((SeparatorH - _skin.SeparatorThickness) * 0.5f), view.width);
                    y += SeparatorH;
                    continue;
                }
                var rr = new Rect(x, y, w, ItemH);
                if (i == highlightIndex) _skin.DrawHover(rr);
                GUI.enabled = row.Enabled;
                if (GUI.Button(rr, row.Label, _skin.Item) && row.Enabled) clicked = i;
                _skin.DrawIcon(new Rect(iconX, y, iconW, ItemH), row.Glyph, row.IconTex);
                var right = new Rect(rightX, y, rightW, ItemH);
                if (row.Submenu != SubId.None) _skin.DrawChevron(right);
                else if (row.Checked) _skin.DrawCheckGlyph(right);
                if (!string.IsNullOrEmpty(row.Shortcut))
                {
                    GUI.Label(new Rect(x, y, rightX - x - Mathf.Round(8f * S), ItemH), row.Shortcut, _skin.Shortcut);
                }
                GUI.enabled = true;
                y += ItemH;
            }
            GUI.EndScrollView();
            return clicked;
        }

        void DrawRename()
        {
            var r = ToGui(_renameD);
            float t = EaseOutCubic((Time.unscaledTime - _renameOpenedAt) / OpenAnimSeconds);
            if (t < 1f)
            {
                float sc = Mathf.Lerp(0.96f, 1f, t);
                GUIUtility.ScaleAroundPivot(new Vector2(sc, sc), r.center);
            }
            GUI.color = new Color(1f, 1f, 1f, t);

            // Enter / Esc（输入法正在组字时 Enter 交给输入法）
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    CloseRename(false);
                    return;
                }
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && string.IsNullOrEmpty(Input.compositionString))
                {
                    e.Use();
                    CloseRename(true);
                    return;
                }
            }

            _skin.DrawPanel(r);
            float pad = Mathf.Round(20f * S);
            float x = r.x + pad, y = r.y + pad, w = r.width - pad * 2f;
            GUI.Label(new Rect(x, y, w, Mathf.Round(24f * S)), "宠物名称", _skin.Title);
            y += Mathf.Round(24f * S);
            GUI.Label(new Rect(x, y, w, Mathf.Round(20f * S)), "留空则使用模型名称：" + DefaultPetName, _skin.PopupCaption);
            y += Mathf.Round(20f * S) + Mathf.Round(12f * S);

            var field = new Rect(x, y, w, Mathf.Round(32f * S));
            // Win11 风格的选中/光标颜色（默认 IMGUI 是橙色选区）
            var settings = GUI.skin.settings;
            Color oldSel = settings.selectionColor, oldCursor = settings.cursorColor;
            settings.selectionColor = new Color(0f, 0.47f, 0.84f, 1f);
            settings.cursorColor = Color.white;
            GUI.SetNextControlName(RenameControl);
            _renameText = GUI.TextField(field, _renameText ?? "", PetNameMaxLength, _skin.TextField);
            settings.selectionColor = oldSel;
            settings.cursorColor = oldCursor;
            if (_renameFocusPending)
            {
                GUI.FocusControl(RenameControl);
                if (e.type == EventType.Repaint) _renameFocusPending = false;
            }
            if (GUI.GetNameOfFocusedControl() == RenameControl && e.type == EventType.Repaint)
            {
                // Win11 文本框聚焦时底部的强调色线
                float lh = Mathf.Max(2f, Mathf.Round(2f * S));
                float inset = Mathf.Round(4f * S);
                GUI.DrawTexture(new Rect(field.x + inset, field.yMax - lh, field.width - inset * 2f, lh), _skin.AccentLineTex);
            }
            y += Mathf.Round(32f * S) + Mathf.Round(20f * S);

            float bw = Mathf.Round(96f * S), bh = Mathf.Round(32f * S), gap = Mathf.Round(8f * S);
            var cancel = new Rect(x + w - bw, y, bw, bh);
            var ok = new Rect(cancel.x - gap - bw, y, bw, bh);
            if (GUI.Button(ok, "确定", _skin.ButtonAccent))
            {
                CloseRename(true);
                return;
            }
            if (GUI.Button(cancel, "取消", _skin.Button))
            {
                CloseRename(false);
            }
        }

        void DrawHints()
        {
            // 淡入 0.15s / 到时间前 0.4s 淡出
            float now = Time.unscaledTime;
            float alpha = Mathf.Min(EaseOutCubic((now - _hintsShownAt) / 0.15f), Mathf.Clamp01((_hintsUntil - now) / 0.4f));
            if (alpha <= 0f) return;

            float pad = Mathf.Round(14f * S);
            float titleH = Mathf.Round(24f * S);
            float rowH = Mathf.Round(24f * S);
            float keyW = Mathf.Round(92f * S);
            float hw = Mathf.Round(300f * S);
            float hh = pad * 2f + titleH + Mathf.Round(6f * S) + rowH * HintLines.Length;
            float margin = ShadowRoom;

            // 按设计尺寸绘制，放在角色区域顶部居中；画布放不下时整体等比缩小（保证完整显示在窗口内）
            float k = Mathf.Min(1f, (Screen.width - margin * 2f) / hw, (Screen.height - margin * 2f) / hh);
            k = Mathf.Max(0.2f, k);
            var f = ToGui(ToRect(FigureD()));
            float rx = Mathf.Clamp(f.center.x - hw * k * 0.5f, margin, Mathf.Max(margin, Screen.width - margin - hw * k));
            float ry = Mathf.Clamp(f.y + 8f * S, margin, Mathf.Max(margin, Screen.height - margin - hh * k));
            var r = new Rect(Mathf.Round(rx), Mathf.Round(ry), hw, hh);
            var pivot = r.position;
            if (k < 1f) GUIUtility.ScaleAroundPivot(new Vector2(k, k), pivot);
            GUI.color = new Color(1f, 1f, 1f, alpha);

            _skin.DrawPanel(r);
            float x0 = r.x + pad;
            float y = r.y + pad;
            GUI.Label(new Rect(x0, y, hw - pad * 2f, titleH), "操作说明", _skin.Title);
            GUI.Label(new Rect(x0, y, hw - pad * 2f, titleH), "点击关闭", _skin.Caption);
            y += titleH + Mathf.Round(6f * S);
            _skin.DrawSeparator(x0, y - Mathf.Round(4f * S), hw - pad * 2f);
            foreach (var line in HintLines)
            {
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                string key = eq >= 0 ? line.Substring(0, eq) : line;
                string val = eq >= 0 ? line.Substring(eq + 3) : "";
                GUI.Label(new Rect(x0, y, keyW, rowH), key, _skin.HintKey);
                GUI.Label(new Rect(x0 + keyW, y, hw - pad * 2f - keyW, rowH), val, _skin.HintValue);
                y += rowH;
            }

            _hintRect = new Rect(pivot.x + (r.x - pivot.x) * k, pivot.y + (r.y - pivot.y) * k, hw * k, hh * k);
        }
    }
}
