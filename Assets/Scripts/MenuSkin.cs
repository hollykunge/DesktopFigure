using System.Collections.Generic;
using UnityEngine;

namespace DesktopFigure
{
    /// <summary>
    /// Windows 11 风格（深色、半透明“亚克力”、圆角）的 IMGUI 皮肤（Windows 和 macOS 通用）。
    /// 所有纹理都在运行时按当前 DPI 缩放生成（抗锯齿 SDF），九宫格绘制时角落 1:1 像素对应 => 高分屏上依然清晰。
    ///
    /// 透明窗口说明：IMGUI 的混合模式是 SrcAlpha/OneMinusSrcAlpha（alpha 通道也同样混合），
    /// 写进背景缓冲的 alpha ≈ a²，DWM 按预乘 alpha 合成。所以阴影的 alpha 取 sqrt(目标覆盖率) 做补偿。
    /// 真正的桌面背景模糊在逐像素透明窗口里做不到，这里用“半透明深色 + 极淡噪点 + 顶部微光”来模拟磨砂玻璃。
    /// </summary>
    public sealed class MenuSkin
    {
        // ---- 设计尺寸（逻辑像素，乘以 DPI 缩放）----
        public const float RadiusL = 8f;
        public const float ItemRadiusL = 4f;
        public const float ShadowMarginL = 14f;
        public const float ShadowOffsetL = 3f;

        static readonly Color Fill = new Color(32f / 255f, 32f / 255f, 32f / 255f, 0.88f);
        const float BorderStrength = 0.10f;   // 1px 浅色描边（白色约 10%）
        const float ShadowMaxCoverage = 0.30f;

        public float Scale { get; private set; } = -1f;
        public int Radius { get; private set; }
        public int ShadowMargin { get; private set; }
        public int ShadowOffset { get; private set; }
        public int SeparatorThickness { get; private set; }

        public Font Font { get; private set; }
        public Texture2D NoiseTex { get; private set; }
        public Texture2D GlossTex { get; private set; }
        public Texture2D CheckTex { get; private set; }
        public Texture2D WhiteTex { get; private set; }

        public GUIStyle Panel, Shadow, Item, Shortcut, Title, Caption, HintKey, HintValue, Message;
        public GUIStyle Icon, IconCheck, IconChevron, ChevronText;
        public GUIStyle TextField, ButtonAccent, Button, PopupCaption, HoverStyle;

        // ---- 图标（矢量字体，按像素尺寸渲染 => 高分屏清晰）----
        // Windows：优先用系统自带的 Segoe Fluent Icons（Win11）/ Segoe MDL2 Assets（Win10），只从系统加载，项目里不包含任何微软字体文件。
        // macOS / 其它情况：用项目内置的 Material Icons（Google，Apache License 2.0，Assets/Resources/DesktopFigure/MaterialIcons-Regular.ttf）。
        // 菜单代码里的图标常量是 Segoe 码位，用 Material Icons 时通过 MaterialGlyphs 映射。
        // 都没有时不显示图标（勾/箭头用生成的矢量纹理/文字代替）。
        public static readonly string[] IconFontNames = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };
        public const string BundledIconFontResource = "DesktopFigure/MaterialIcons-Regular";
        public Font IconFont { get; private set; }
        public string IconFontName { get; private set; } = "";
        public bool HasIconFont => IconFont != null;
        /// <summary>当前图标字体是否为内置的 Material Icons（需要做码位映射）。</summary>
        public bool IconFontIsMaterial { get; private set; }

        /// <summary>Segoe Fluent Icons 码位 → Material Icons 码位。</summary>
        static readonly Dictionary<string, string> MaterialGlyphs = new Dictionary<string, string>
        {
            { "\uE99A", "\uF06C" }, // Robot → smart_toy
            { "\uE8BD", "\uE0CB" }, // Message → chat_bubble_outline
            { "\uEA80", "\uE90F" }, // Lightbulb → lightbulb_outline
            { "\uE713", "\uE8B8" }, // Settings → settings
            { "\uE7AD", "\uE84D" }, // Rotate → 3d_rotation
            { "\uE72C", "\uE5D5" }, // Refresh → refresh
            { "\uE718", "\uF10D" }, // Pin → push_pin
            { "\uE8AC", "\uE9A2" }, // Rename → drive_file_rename_outline
            { "\uE897", "\uE8FD" }, // Help → help_outline
            { "\uE7E8", "\uE8AC" }, // PowerButton → power_settings_new
            { "\uE895", "\uE627" }, // Sync → sync
            { "\uE838", "\uE2C8" }, // FolderOpen → folder_open
            { "\uE77B", "\uE7FF" }, // Contact → person_outline
            { "\uE91B", "\uE410" }, // Photo → photo
            { "\uE790", "\uE40A" }, // Color → palette
            { "\uE73E", "\uE5CA" }, // CheckMark → check
            { "\uE76C", "\uE5CC" }, // ChevronRight → chevron_right
        };

        string Glyph(string segoe)
        {
            if (!IconFontIsMaterial || string.IsNullOrEmpty(segoe)) return segoe;
            return MaterialGlyphs.TryGetValue(segoe, out var m) ? m : segoe;
        }

        // ---- 文字字体（需要中文）：只从系统加载 ----
        // Windows 列表保持原样；macOS 优先苹方，找不到（新系统里苹方可能不在常规字体目录）时依次退到冬青黑体 / 黑体 / 宋体。
        static readonly string[] TextFontNamesWindows = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "PingFang SC", "SimHei", "Arial" };
        static readonly string[] TextFontNamesMac = { "PingFang SC", "Hiragino Sans GB", "Heiti SC", "STHeiti", "Songti SC", "Arial Unicode MS", "Helvetica Neue", "Arial" };
        static readonly string[] TextFontNamesOther = { "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "WenQuanYi Micro Hei", "Droid Sans Fallback", "DejaVu Sans", "Arial" };

        static string[] TextFontCandidates()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return TextFontNamesWindows;
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return TextFontNamesMac;
                default:
                    return TextFontNamesOther;
            }
        }

        static Font CreateTextFont()
        {
            string[] names = TextFontCandidates();
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)
            {
                // 非 Windows：已安装的放前面（Unity 按列表顺序选字体，缺字时向后回退）
                try
                {
                    var installed = new HashSet<string>(Font.GetOSInstalledFontNames());
                    var ordered = new List<string>();
                    foreach (var n in names) if (installed.Contains(n)) ordered.Add(n);
                    foreach (var n in names) if (!ordered.Contains(n)) ordered.Add(n);
                    Debug.Log("[DesktopFigure] Text font candidates: " + string.Join(", ", ordered) +
                              " (installed: " + string.Join(", ", ordered.FindAll(installed.Contains)) + ")");
                    names = ordered.ToArray();
                }
                catch (System.Exception)
                {
                    // 用默认顺序
                }
            }
            return Font.CreateDynamicFontFromOSFont(names, 14);
        }
        /// <summary>“衣橱”用的衣架图标：图标字体里没有衣架，这里在运行时按 DPI 用 SDF 画一个矢量衣架。</summary>
        public Texture2D HangerTex { get; private set; }
        public Texture2D HoverTex => _hoverTex;
        public Texture2D AccentLineTex { get; private set; }

        public static readonly Color Accent = new Color(0.298f, 0.761f, 1.0f, 1f); // Win11 深色模式默认强调色 #4CC2FF

        Texture2D _panelTex, _shadowTex, _hoverTex, _pressTex;
        Texture2D _fieldTex, _fieldFocusTex, _btnTex, _btnHoverTex, _btnPressTex, _accentTex, _accentHoverTex, _accentPressTex;

        public void Ensure(float s)
        {
            if (Panel != null && Mathf.Approximately(s, Scale) && _panelTex != null) return;
            DestroyTextures();
            Scale = s;
            Radius = Mathf.Max(2, Mathf.RoundToInt(RadiusL * s));
            ShadowMargin = Mathf.Max(4, Mathf.RoundToInt(ShadowMarginL * s));
            ShadowOffset = Mathf.RoundToInt(ShadowOffsetL * s);
            SeparatorThickness = Mathf.Max(1, Mathf.RoundToInt(s));
            int itemRadius = Mathf.Max(2, Mathf.RoundToInt(ItemRadiusL * s));

            if (Font == null)
            {
                try
                {
                    Font = CreateTextFont();
                }
                catch (System.Exception)
                {
                    Font = null;
                }
            }

            if (IconFont == null)
            {
                try
                {
                    var installed = Font.GetOSInstalledFontNames();
                    foreach (var n in IconFontNames)
                    {
                        if (System.Array.IndexOf(installed, n) >= 0)
                        {
                            IconFont = Font.CreateDynamicFontFromOSFont(n, 16);
                            IconFontName = n;
                            break;
                        }
                    }
                }
                catch (System.Exception)
                {
                    IconFont = null;
                }
                if (IconFont == null)
                {
                    IconFont = Resources.Load<Font>(BundledIconFontResource);
                    if (IconFont != null)
                    {
                        IconFontName = "Material Icons (bundled)";
                        IconFontIsMaterial = true;
                    }
                }
                Debug.Log("[DesktopFigure] Menu icon font: " + (IconFont != null ? IconFontName : "(none)"));
            }

            _panelTex = RoundedRect(Radius, Fill, BorderStrength, Mathf.Max(1f, Mathf.Round(s * 0.75f)), out int pb);
            _shadowTex = ShadowRect(Radius, ShadowMargin, ShadowMaxCoverage, out int sb);
            _hoverTex = RoundedRect(itemRadius, new Color(1f, 1f, 1f, 0.085f), 0f, 0f, out int hb);
            _pressTex = RoundedRect(itemRadius, new Color(1f, 1f, 1f, 0.05f), 0f, 0f, out _);
            NoiseTex = Noise(64, 0.035f);
            GlossTex = Gloss(32, 0.045f);
            CheckTex = CheckMark(Mathf.CeilToInt(16f * s), s);
            WhiteTex = Solid(Color.white);
            HangerTex = Hanger(Mathf.RoundToInt(16f * s), s);
            AccentLineTex = Solid(Accent);
            float bw = Mathf.Max(1f, Mathf.Round(s * 0.75f));
            _fieldTex = RoundedRect(itemRadius, new Color(1f, 1f, 1f, 0.055f), 0.10f, bw, out int fb);
            _fieldFocusTex = RoundedRect(itemRadius, new Color(0.11f, 0.11f, 0.11f, 0.95f), 0.10f, bw, out _);
            _btnTex = RoundedRect(itemRadius, new Color(1f, 1f, 1f, 0.08f), 0.07f, bw, out int bb);
            _btnHoverTex = RoundedRect(itemRadius, new Color(1f, 1f, 1f, 0.12f), 0.07f, bw, out _);
            _btnPressTex = RoundedRect(itemRadius, new Color(1f, 1f, 1f, 0.05f), 0.05f, bw, out _);
            _accentTex = RoundedRect(itemRadius, Accent, 0f, 0f, out _);
            _accentHoverTex = RoundedRect(itemRadius, new Color(Accent.r * 0.92f, Accent.g * 0.95f, Accent.b, 0.92f), 0f, 0f, out _);
            _accentPressTex = RoundedRect(itemRadius, new Color(Accent.r * 0.85f, Accent.g * 0.9f, Accent.b, 0.8f), 0f, 0f, out _);

            Panel = new GUIStyle { border = new RectOffset(pb, pb, pb, pb) };
            Panel.normal.background = _panelTex;
            Shadow = new GUIStyle { border = new RectOffset(sb, sb, sb, sb) };
            Shadow.normal.background = _shadowTex;

            var text = new Color(1f, 1f, 1f, 0.94f);
            Item = new GUIStyle
            {
                border = new RectOffset(hb, hb, hb, hb),
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.RoundToInt(14f * s),
                padding = new RectOffset(Mathf.RoundToInt(36f * s), Mathf.RoundToInt(40f * s), 0, Mathf.RoundToInt(1f * s)),
                clipping = TextClipping.Clip,
                font = Font,
            };
            Item.normal.textColor = text;
            Item.hover.textColor = Color.white;
            Item.active.textColor = new Color(1f, 1f, 1f, 0.8f);
            Item.hover.background = _hoverTex;
            Item.active.background = _pressTex;

            HoverStyle = new GUIStyle { border = new RectOffset(hb, hb, hb, hb) };
            HoverStyle.normal.background = _hoverTex;
            Item.padding.left = Mathf.RoundToInt(40f * s);
            Item.padding.right = Mathf.RoundToInt(64f * s);

            Shortcut = Label(12f * s, new Color(1f, 1f, 1f, 0.5f), TextAnchor.MiddleRight);
            // Material Icons 的图形在 24 格里留了 2 格边距，字号放大 1.2 倍后视觉大小与 Segoe 图标接近
            float iconK = IconFontIsMaterial ? 1.2f : 1f;
            Icon = new GUIStyle { font = IconFont, fontSize = Mathf.RoundToInt(16f * s * iconK), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            Icon.normal.textColor = new Color(1f, 1f, 1f, 0.88f);
            IconCheck = new GUIStyle(Icon) { fontSize = Mathf.RoundToInt(14f * s * iconK) };
            IconCheck.normal.textColor = new Color(1f, 1f, 1f, 0.92f);
            IconChevron = new GUIStyle(Icon) { fontSize = Mathf.RoundToInt(12f * s * (IconFontIsMaterial ? 1.35f : 1f)) };
            IconChevron.normal.textColor = new Color(1f, 1f, 1f, 0.7f);
            ChevronText = Label(16f * s, new Color(1f, 1f, 1f, 0.7f), TextAnchor.MiddleCenter);

            TextField = new GUIStyle
            {
                font = Font,
                fontSize = Mathf.RoundToInt(14f * s),
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(Mathf.RoundToInt(10f * s), Mathf.RoundToInt(10f * s), 0, Mathf.RoundToInt(1f * s)),
                border = new RectOffset(fb, fb, fb, fb),
                clipping = TextClipping.Clip,
            };
            TextField.normal.background = _fieldTex;
            TextField.hover.background = _fieldTex;
            TextField.focused.background = _fieldFocusTex;
            TextField.active.background = _fieldFocusTex;
            TextField.normal.textColor = TextField.hover.textColor = TextField.focused.textColor = TextField.active.textColor = Color.white;

            Button = new GUIStyle
            {
                font = Font,
                fontSize = Mathf.RoundToInt(14f * s),
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(bb, bb, bb, bb),
                padding = new RectOffset(0, 0, 0, Mathf.RoundToInt(1f * s)),
            };
            Button.normal.background = _btnTex;
            Button.hover.background = _btnHoverTex;
            Button.active.background = _btnPressTex;
            Button.normal.textColor = Button.hover.textColor = Color.white;
            Button.active.textColor = new Color(1f, 1f, 1f, 0.8f);
            ButtonAccent = new GUIStyle(Button);
            ButtonAccent.normal.background = _accentTex;
            ButtonAccent.hover.background = _accentHoverTex;
            ButtonAccent.active.background = _accentPressTex;
            ButtonAccent.normal.textColor = ButtonAccent.hover.textColor = new Color(0f, 0f, 0f, 0.92f);
            ButtonAccent.active.textColor = new Color(0f, 0f, 0f, 0.7f);
            PopupCaption = Label(12f * s, new Color(1f, 1f, 1f, 0.6f), TextAnchor.MiddleLeft);
            Title = Label(14f * s, Color.white, TextAnchor.MiddleLeft);
            Title.fontStyle = FontStyle.Bold;
            Caption = Label(12f * s, new Color(1f, 1f, 1f, 0.5f), TextAnchor.MiddleRight);
            HintKey = Label(13f * s, new Color(1f, 1f, 1f, 0.95f), TextAnchor.MiddleLeft);
            HintValue = Label(13f * s, new Color(1f, 1f, 1f, 0.68f), TextAnchor.MiddleLeft);
            Message = Label(14f * s, new Color(1f, 1f, 1f, 0.94f), TextAnchor.MiddleCenter);
            Message.wordWrap = true;
        }

        GUIStyle Label(float size, Color color, TextAnchor anchor)
        {
            var st = new GUIStyle
            {
                fontSize = Mathf.RoundToInt(size),
                alignment = anchor,
                clipping = TextClipping.Clip,
                font = Font,
            };
            st.normal.textColor = color;
            return st;
        }

        // ------------------------------------------------------------------
        // 绘制工具
        // ------------------------------------------------------------------

        /// <summary>画一块“亚克力”面板：阴影 + 半透明圆角底 + 噪点 + 顶部微光 + 描边。</summary>
        public void DrawPanel(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            var sr = new Rect(r.x - ShadowMargin, r.y - ShadowMargin + ShadowOffset, r.width + ShadowMargin * 2, r.height + ShadowMargin * 2);
            Shadow.Draw(sr, false, false, false, false);
            Panel.Draw(r, false, false, false, false);
            // 噪点/微光只画在圆角内接区域，避免溢出圆角
            float inset = Radius * 0.32f + 1f;
            var inner = new Rect(r.x + inset, r.y + inset, r.width - inset * 2f, r.height - inset * 2f);
            if (inner.width > 0 && inner.height > 0)
            {
                GUI.DrawTextureWithTexCoords(inner, NoiseTex, new Rect(0f, 0f, inner.width / NoiseTex.width, inner.height / NoiseTex.height));
                var gloss = new Rect(inner.x, inner.y, inner.width, Mathf.Min(inner.height, 60f * Scale));
                GUI.DrawTexture(gloss, GlossTex, ScaleMode.StretchToFill);
            }
        }

        public void DrawSeparator(float x, float y, float w)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = new Color(old.r, old.g, old.b, old.a * 0.10f);
            GUI.DrawTexture(new Rect(Mathf.Round(x), Mathf.Round(y), Mathf.Round(w), SeparatorThickness), WhiteTex);
            GUI.color = old;
        }

        /// <summary>图标列：字体图标（glyph）或纹理图标，居中、像素对齐。</summary>
        public void DrawIcon(Rect area, string glyph, Texture2D tex)
        {
            if (tex != null)
            {
                if (Event.current.type != EventType.Repaint) return;
                float w = tex.width, h = tex.height;
                var r = new Rect(Mathf.Round(area.x + (area.width - w) * 0.5f), Mathf.Round(area.y + (area.height - h) * 0.5f), w, h);
                var old = GUI.color;
                GUI.color = new Color(old.r, old.g, old.b, old.a * 0.88f);
                GUI.DrawTexture(r, tex);
                GUI.color = old;
            }
            else if (HasIconFont && !string.IsNullOrEmpty(glyph))
            {
                GUI.Label(PixelRect(area), Glyph(glyph), Icon);
            }
        }

        public void DrawCheckGlyph(Rect area)
        {
            if (HasIconFont) GUI.Label(PixelRect(area), Glyph("\uE73E"), IconCheck);
            else DrawCheck(area);
        }

        public void DrawChevron(Rect area)
        {
            if (HasIconFont) GUI.Label(PixelRect(area), Glyph("\uE76C"), IconChevron);
            else GUI.Label(PixelRect(area), "\u203A", ChevronText);
        }

        public void DrawHover(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            HoverStyle.Draw(r, false, false, false, false);
        }

        static Rect PixelRect(Rect r) => new Rect(Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height));

        public void DrawCheck(Rect area)
        {
            if (Event.current.type != EventType.Repaint) return;
            float sz = CheckTex.width;
            var r = new Rect(Mathf.Round(area.x + (area.width - sz) * 0.5f), Mathf.Round(area.y + (area.height - sz) * 0.5f), sz, sz);
            GUI.DrawTexture(r, CheckTex);
        }

        // ------------------------------------------------------------------
        // 纹理生成
        // ------------------------------------------------------------------

        static float SdRoundBox(float px, float py, float hx, float hy, float r)
        {
            float qx = Mathf.Abs(px) - (hx - r);
            float qy = Mathf.Abs(py) - (hy - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        static Texture2D NewTex(int w, int h, TextureWrapMode wrap = TextureWrapMode.Clamp, FilterMode filter = FilterMode.Bilinear)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = wrap,
                filterMode = filter,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        /// <summary>九宫格圆角矩形（中间 1px 可拉伸），可带 1px 浅色描边。</summary>
        static Texture2D RoundedRect(int radius, Color fill, float borderStrength, float borderWidth, out int border)
        {
            border = radius + 2;
            int n = border * 2 + 1;
            var tex = NewTex(n, n);
            var px = new Color[n * n];
            float half = n * 0.5f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = SdRoundBox(x + 0.5f - half, y + 0.5f - half, half, half, radius);
                    float co = Mathf.Clamp01(0.5f - d);
                    float ci = borderWidth > 0f ? Mathf.Clamp01(0.5f - (d + borderWidth)) : co;
                    float ring = co > 1e-4f ? (co - ci) / co : 0f;
                    float mix = borderStrength * ring;
                    var c = new Color(
                        Mathf.Lerp(fill.r, 1f, mix),
                        Mathf.Lerp(fill.g, 1f, mix),
                        Mathf.Lerp(fill.b, 1f, mix),
                        (fill.a + (1f - fill.a) * mix) * co);
                    px[y * n + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>柔和投影（黑色，向外渐隐）。alpha 做了 sqrt 补偿，见类注释。</summary>
        static Texture2D ShadowRect(int radius, int margin, float maxCoverage, out int border)
        {
            border = margin + radius + 2;
            int n = border * 2 + 1;
            var tex = NewTex(n, n);
            var px = new Color[n * n];
            float half = n * 0.5f;
            float inner = half - margin;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = SdRoundBox(x + 0.5f - half, y + 0.5f - half, inner, inner, radius);
                    float t = Mathf.Clamp01(d / margin);
                    float cov = maxCoverage * (1f - t) * (1f - t) * (1f - t * 0.5f);
                    if (d < 0f) cov = maxCoverage;
                    px[y * n + x] = new Color(0f, 0f, 0f, Mathf.Sqrt(Mathf.Max(0f, cov)));
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        static Texture2D Noise(int size, float maxAlpha)
        {
            var tex = NewTex(size, size, TextureWrapMode.Repeat, FilterMode.Point);
            var px = new Color[size * size];
            var rng = new System.Random(20261008);
            for (int i = 0; i < px.Length; i++)
            {
                float v = (float)rng.NextDouble();
                px[i] = new Color(1f, 1f, 1f, v * v * maxAlpha);
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        static Texture2D Gloss(int h, float topAlpha)
        {
            var tex = NewTex(1, h);
            var px = new Color[h];
            for (int y = 0; y < h; y++)
            {
                // y=0 是纹理底部；IMGUI 绘制时纹理顶部在上
                float t = (float)y / (h - 1);   // 0 底 → 1 顶
                px[y] = new Color(1f, 1f, 1f, topAlpha * t * t);
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        static Texture2D CheckMark(int size, float s)
        {
            var tex = NewTex(size, size);
            var px = new Color[size * size];
            // 纹理坐标 y 向上；勾的三个点（归一化，y 向下的设计坐标）
            Vector2 a = new Vector2(0.20f, 0.52f), b = new Vector2(0.41f, 0.72f), c = new Vector2(0.80f, 0.30f);
            float halfW = Mathf.Max(0.75f, 0.8f * s);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + 0.5f) / size, 1f - (y + 0.5f) / size) * size;
                    float d = Mathf.Min(SegDist(p, a * size, b * size), SegDist(p, b * size, c * size));
                    float cov = Mathf.Clamp01(halfW + 0.5f - d);
                    px[y * size + x] = new Color(1f, 1f, 1f, cov);
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>矢量衣架（16 单位设计网格，y 向下）：挂钩圆弧 + 脖子 + 三角形肩线。</summary>
        static Texture2D Hanger(int size, float s)
        {
            var tex = NewTex(size, size);
            var px = new Color[size * size];
            float u = size / 16f;                 // 1 设计单位 = u 像素
            float halfW = Mathf.Max(0.6f, 0.62f * u);
            Vector2 hookC = new Vector2(8f, 4.1f) * u;
            float hookR = 1.9f * u;
            Vector2 neckA = new Vector2(8f, 6.0f) * u, neckB = new Vector2(8f, 7.4f) * u;
            Vector2 left = new Vector2(1.6f, 12.3f) * u, right = new Vector2(14.4f, 12.3f) * u;
            Vector2 hookStart = hookC + new Vector2(-hookR, 0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, size - (y + 0.5f));
                    // 挂钩：圆弧，去掉左下四分之一（y 向下坐标系里角度 90°~180°）
                    var d0 = p - hookC;
                    float ang = Mathf.Atan2(d0.y, d0.x) * Mathf.Rad2Deg;
                    float dHook = (ang > 90f && ang < 180f)
                        ? Mathf.Min(Vector2.Distance(p, hookStart), Vector2.Distance(p, neckA))
                        : Mathf.Abs(d0.magnitude - hookR);
                    float d = Mathf.Min(dHook, SegDist(p, neckA, neckB));
                    d = Mathf.Min(d, SegDist(p, neckB, left));
                    d = Mathf.Min(d, SegDist(p, neckB, right));
                    d = Mathf.Min(d, SegDist(p, left, right));
                    float cov = Mathf.Clamp01(halfW + 0.5f - d);
                    px[y * size + x] = new Color(1f, 1f, 1f, cov);
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        static Texture2D Solid(Color c)
        {
            var tex = NewTex(1, 1);
            tex.SetPixel(0, 0, c);
            tex.Apply(false, true);
            return tex;
        }

        public void DestroyTextures()
        {
            Kill(_panelTex); Kill(_shadowTex); Kill(_hoverTex); Kill(_pressTex);
            Kill(NoiseTex); Kill(GlossTex); Kill(CheckTex); Kill(WhiteTex);
            Kill(HangerTex); Kill(AccentLineTex);
            Kill(_fieldTex); Kill(_fieldFocusTex); Kill(_btnTex); Kill(_btnHoverTex); Kill(_btnPressTex);
            Kill(_accentTex); Kill(_accentHoverTex); Kill(_accentPressTex);
            _panelTex = _shadowTex = _hoverTex = _pressTex = null;
            NoiseTex = GlossTex = CheckTex = WhiteTex = null;
            HangerTex = AccentLineTex = null;
            _fieldTex = _fieldFocusTex = _btnTex = _btnHoverTex = _btnPressTex = _accentTex = _accentHoverTex = _accentPressTex = null;
        }

        static void Kill(Object o)
        {
            if (o != null) Object.Destroy(o);
        }
    }
}
