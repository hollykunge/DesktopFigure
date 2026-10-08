#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
#define DF_MAC
#endif

#if DF_MAC
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace DesktopFigure
{
    // ======================================================================
    // macOS 原生窗口控制：不使用编译好的原生插件（没有 Xcode），
    // 直接 P/Invoke Objective-C 运行时（/usr/lib/libobjc.A.dylib）调用 AppKit。
    //
    // 坐标约定（与 Windows 版保持一致，供 WindowDragScale / 菜单使用）：
    //   “桌面坐标” = 像素（point × backingScaleFactor），原点在主显示器左上角，y 向下。
    // Cocoa 的全局坐标是 point、原点在主显示器左下角、y 向上，这里统一换算。
    //
    // 注意：此文件只在 macOS 播放器里编译（UNITY_STANDALONE_OSX && !UNITY_EDITOR）。
    // 作者没有 Mac 实机测试，所有调用都包在 try/catch 里，失败时写日志并退化为普通窗口。
    // ======================================================================

    [StructLayout(LayoutKind.Sequential)]
    internal struct NSPoint
    {
        public double X;
        public double Y;
        public NSPoint(double x, double y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NSSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NSRect
    {
        public NSPoint Origin;
        public NSSize Size;

        public NSRect(double x, double y, double w, double h)
        {
            Origin = new NSPoint(x, y);
            Size = new NSSize { Width = w, Height = h };
        }

        public double X => Origin.X;
        public double Y => Origin.Y;
        public double W => Size.Width;
        public double H => Size.Height;
        public override string ToString() => $"({X:0.#},{Y:0.#} {W:0.#}x{H:0.#})";
    }

    /// <summary>Objective-C 运行时的最小封装（objc_msgSend 按签名声明多个重载）。</summary>
    internal static class ObjC
    {
        const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib)] public static extern IntPtr objc_getClass(string name);
        [DllImport(Lib)] public static extern IntPtr sel_registerName(string name);
        [DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);
        [DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(Lib)] public static extern IntPtr object_getClass(IntPtr obj);
        [DllImport(Lib)] public static extern IntPtr object_setClass(IntPtr obj, IntPtr cls);
        [DllImport(Lib)] public static extern IntPtr class_getName(IntPtr cls);

        // ---- objc_msgSend：每种参数/返回值组合一个声明（arm64 上 objc_msgSend 必须按确切的非变参原型调用）----
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a, IntPtr b, IntPtr c);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr r, IntPtr s, byte[] utf8z);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendD(IntPtr r, IntPtr s, double a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendU(IntPtr r, IntPtr s, ulong a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendV(IntPtr r, IntPtr s, long a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendV(IntPtr r, IntPtr s, ulong a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendV(IntPtr r, IntPtr s, byte a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendV(IntPtr r, IntPtr s, NSPoint a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendV(IntPtr r, IntPtr s, NSRect a, byte b);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendB(IntPtr r, IntPtr s);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendB(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern ulong SendUL(IntPtr r, IntPtr s);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern double SendF(IntPtr r, IntPtr s);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern NSPoint SendPoint(IntPtr r, IntPtr s);
        // NSRect（32 字节）返回值：arm64 是 HFA，走普通 objc_msgSend；x86_64 走 objc_msgSend_stret（隐藏返回指针）
        [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern NSRect SendRectArm(IntPtr r, IntPtr s);
        [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] static extern NSRect SendRectX64(IntPtr r, IntPtr s);

        static readonly Dictionary<string, IntPtr> s_sel = new Dictionary<string, IntPtr>();
        static readonly Dictionary<string, IntPtr> s_cls = new Dictionary<string, IntPtr>();
        static int s_arm64 = -1;

        public static bool IsArm64
        {
            get
            {
                if (s_arm64 < 0)
                {
                    bool arm = false;
                    try
                    {
                        arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                    }
                    catch (Exception)
                    {
                        string cpu = SystemInfo.processorType ?? "";
                        arm = cpu.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    s_arm64 = arm ? 1 : 0;
                }
                return s_arm64 == 1;
            }
        }

        public static IntPtr Sel(string name)
        {
            if (!s_sel.TryGetValue(name, out var p))
            {
                p = sel_registerName(name);
                s_sel[name] = p;
            }
            return p;
        }

        public static IntPtr Cls(string name)
        {
            if (!s_cls.TryGetValue(name, out var p) || p == IntPtr.Zero)
            {
                p = objc_getClass(name);
                s_cls[name] = p;
            }
            return p;
        }

        public static IntPtr Send(IntPtr r, string sel) => r == IntPtr.Zero ? IntPtr.Zero : Send(r, Sel(sel));
        public static IntPtr Send(IntPtr r, string sel, IntPtr a) => r == IntPtr.Zero ? IntPtr.Zero : Send(r, Sel(sel), a);
        public static bool SendBool(IntPtr r, string sel) => r != IntPtr.Zero && SendB(r, Sel(sel)) != 0;
        public static void SendBool(IntPtr r, string sel, bool v) { if (r != IntPtr.Zero) SendV(r, Sel(sel), (byte)(v ? 1 : 0)); }

        public static NSRect SendRect(IntPtr r, string sel)
        {
            if (r == IntPtr.Zero) return default;
            return IsArm64 ? SendRectArm(r, Sel(sel)) : SendRectX64(r, Sel(sel));
        }

        public static bool RespondsTo(IntPtr obj, string sel) =>
            obj != IntPtr.Zero && SendB(obj, Sel("respondsToSelector:"), Sel(sel)) != 0;

        /// <summary>NSString（autorelease）。</summary>
        public static IntPtr NSString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes((s ?? "") + "\0");
            return Send(Cls("NSString"), Sel("stringWithUTF8String:"), bytes);
        }

        public static string ClassName(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return "(nil)";
            var p = class_getName(object_getClass(obj));
            return p == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(p);
        }

        public static IntPtr NSApp => Send(Cls("NSApplication"), Sel("sharedApplication"));

        /// <summary>BOOL 的类型编码：arm64 上是 B（bool），x86_64 上是 c（signed char）。</summary>
        public static string BoolType => IsArm64 ? "B" : "c";
    }

    /// <summary>macOS 上 TransparentWindow 的实现（主线程调用）。</summary>
    internal static class MacWindow
    {
        // AppKit 常量
        const ulong NSWindowStyleMaskBorderless = 0;
        const long NSNormalWindowLevel = 0;
        const long NSFloatingWindowLevel = 3;
        const ulong CanJoinAllSpaces = 1UL << 0;
        const ulong Stationary = 1UL << 4;
        const ulong IgnoresCycle = 1UL << 6;
        const ulong FullScreenAuxiliary = 1UL << 8;
        const long ActivationPolicyAccessory = 1;
        const ulong ModShift = 1UL << 17;
        const ulong ModControl = 1UL << 18;
        const ulong ModCommand = 1UL << 20;

        public static IntPtr Window { get; private set; }
        public static bool IsActive => Window != IntPtr.Zero;
        public static float Scale { get; private set; } = 1f;

        static bool s_failed;
        static bool s_ignoring;
        static float s_nextLayerCheck;

        // 必须一直被静态字段引用，防止委托被 GC 回收后原生回调崩溃
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate byte BoolImp(IntPtr self, IntPtr cmd);
        static BoolImp s_yesImp;

        [AOT.MonoPInvokeCallback(typeof(BoolImp))]
        static byte ReturnYes(IntPtr self, IntPtr cmd) => 1;

        /// <summary>查找 Unity 的 NSWindow 并设置为透明、无边框、浮动、不在 Dock / Cmd-Tab。</summary>
        public static bool Init(bool topmost)
        {
            if (Window != IntPtr.Zero) return true;
            if (s_failed) return false;
            try
            {
                var app = ObjC.NSApp;
                if (app == IntPtr.Zero) return false;

                // 不在 Dock、不在 Cmd-Tab（Info.plist 里也设置了 LSUIElement=true，这里再保险一次）
                ObjC.SendV(app, ObjC.Sel("setActivationPolicy:"), ActivationPolicyAccessory);

                IntPtr w = FindUnityWindow(app);
                if (w == IntPtr.Zero) return false;
                Window = w;
                Debug.Log($"[DesktopFigure] macOS window found: {ObjC.ClassName(w)}, frame={Frame()}, arm64={ObjC.IsArm64}");

                ApplyStyle(topmost);
                UpdateScale();
                return true;
            }
            catch (Exception e)
            {
                s_failed = true;
                Window = IntPtr.Zero;
                Debug.LogError("[DesktopFigure] macOS native window init failed, falling back to a normal window: " + e);
                return false;
            }
        }

        static IntPtr FindUnityWindow(IntPtr app)
        {
            IntPtr w = ObjC.Send(app, "keyWindow");
            if (w == IntPtr.Zero) w = ObjC.Send(app, "mainWindow");
            if (w != IntPtr.Zero) return w;
            IntPtr windows = ObjC.Send(app, "windows");
            ulong n = windows == IntPtr.Zero ? 0 : ObjC.SendUL(windows, ObjC.Sel("count"));
            IntPtr first = IntPtr.Zero;
            for (ulong i = 0; i < n; i++)
            {
                IntPtr c = ObjC.SendU(windows, ObjC.Sel("objectAtIndex:"), i);
                if (c == IntPtr.Zero) continue;
                if (first == IntPtr.Zero) first = c;
                if (ObjC.SendBool(c, "isVisible")) return c;
            }
            return first;
        }

        static void ApplyStyle(bool topmost)
        {
            var w = Window;
            Step("styleMask", () => ObjC.SendV(w, ObjC.Sel("setStyleMask:"), NSWindowStyleMaskBorderless));
            Step("opaque", () => ObjC.SendBool(w, "setOpaque:", false));
            Step("background", () => ObjC.Send(w, "setBackgroundColor:", ObjC.Send(ObjC.Cls("NSColor"), "clearColor")));
            Step("shadow", () => ObjC.SendBool(w, "setHasShadow:", false));
            Step("hidesOnDeactivate", () => ObjC.SendBool(w, "setHidesOnDeactivate:", false));
            Step("movable", () => ObjC.SendBool(w, "setMovableByWindowBackground:", false));
            Step("collectionBehavior", () => ObjC.SendV(w, ObjC.Sel("setCollectionBehavior:"),
                CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary));
            Step("canBecomeKey", MakeKeyable);
            Step("layers", ClearLayers);
            SetTopmost(topmost);
            Step("activate", () => ObjC.SendBool(ObjC.NSApp, "activateIgnoringOtherApps:", true));
        }

        static void Step(string name, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DesktopFigure] macOS window step '{name}' failed: {e.Message}");
            }
        }

        /// <summary>
        /// 无边框 NSWindow 默认不能成为 key window（收不到键盘输入，改名输入框无法打字）。
        /// 运行时为 Unity 窗口的类创建一个子类，覆盖 canBecomeKeyWindow / canBecomeMainWindow 返回 YES，然后 isa-swizzle 过去。
        /// </summary>
        static void MakeKeyable()
        {
            IntPtr orig = ObjC.object_getClass(Window);
            string origName = Marshal.PtrToStringAnsi(ObjC.class_getName(orig)) ?? "NSWindow";
            if (origName.StartsWith("DFKeyable_", StringComparison.Ordinal)) return;
            string subName = "DFKeyable_" + origName;
            IntPtr sub = ObjC.objc_getClass(subName);
            if (sub == IntPtr.Zero)
            {
                sub = ObjC.objc_allocateClassPair(orig, subName, IntPtr.Zero);
                if (sub == IntPtr.Zero) throw new Exception("objc_allocateClassPair failed");
                if (s_yesImp == null) s_yesImp = ReturnYes;
                IntPtr imp = Marshal.GetFunctionPointerForDelegate(s_yesImp);
                string types = ObjC.BoolType + "@:";
                ObjC.class_addMethod(sub, ObjC.Sel("canBecomeKeyWindow"), imp, types);
                ObjC.class_addMethod(sub, ObjC.Sel("canBecomeMainWindow"), imp, types);
                ObjC.objc_registerClassPair(sub);
            }
            ObjC.object_setClass(Window, sub);
        }

        /// <summary>contentView 及其子视图的 layer（包括 Unity 的 CAMetalLayer）都设为非不透明，否则透明像素会显示成黑色。</summary>
        static void ClearLayers()
        {
            IntPtr view = ObjC.Send(Window, "contentView");
            if (view == IntPtr.Zero) return;
            ObjC.SendBool(view, "setWantsLayer:", true);
            ClearViewLayers(view, 0);
        }

        static void ClearViewLayers(IntPtr view, int depth)
        {
            if (view == IntPtr.Zero || depth > 4) return;
            IntPtr layer = ObjC.Send(view, "layer");
            ClearLayer(layer, 0);
            IntPtr subviews = ObjC.Send(view, "subviews");
            ulong n = subviews == IntPtr.Zero ? 0 : ObjC.SendUL(subviews, ObjC.Sel("count"));
            for (ulong i = 0; i < n; i++) ClearViewLayers(ObjC.SendU(subviews, ObjC.Sel("objectAtIndex:"), i), depth + 1);
        }

        static void ClearLayer(IntPtr layer, int depth)
        {
            if (layer == IntPtr.Zero || depth > 4) return;
            if (ObjC.SendBool(layer, "isOpaque")) ObjC.SendBool(layer, "setOpaque:", false);
            IntPtr subs = ObjC.Send(layer, "sublayers");
            ulong n = subs == IntPtr.Zero ? 0 : ObjC.SendUL(subs, ObjC.Sel("count"));
            for (ulong i = 0; i < n; i++) ClearLayer(ObjC.SendU(subs, ObjC.Sel("objectAtIndex:"), i), depth + 1);
        }

        /// <summary>每帧调用：刷新缩放、确保 layer 仍然透明（Unity 可能重建 CAMetalLayer）。</summary>
        public static void Tick()
        {
            if (Window == IntPtr.Zero) return;
            if (Time.unscaledTime >= s_nextLayerCheck)
            {
                s_nextLayerCheck = Time.unscaledTime + 1f;
                try
                {
                    UpdateScale();
                    ClearLayers();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DesktopFigure] macOS layer check failed: " + e.Message);
                }
            }
        }

        static void UpdateScale()
        {
            double s = ObjC.SendF(Window, ObjC.Sel("backingScaleFactor"));
            Scale = s > 0.5 && s < 8 ? (float)s : 1f;
        }

        public static void SetTopmost(bool topmost)
        {
            if (Window == IntPtr.Zero) return;
            Step("level", () => ObjC.SendV(Window, ObjC.Sel("setLevel:"), topmost ? NSFloatingWindowLevel : NSNormalWindowLevel));
        }

        public static void SetClickThrough(bool through)
        {
            if (Window == IntPtr.Zero) return;
            ObjC.SendBool(Window, "setIgnoresMouseEvents:", through);
            s_ignoring = through;
        }

        public static bool IsClickThrough => s_ignoring;

        public static void SetVisible(bool visible, bool activate)
        {
            if (Window == IntPtr.Zero) return;
            if (visible)
            {
                if (activate)
                {
                    ObjC.SendBool(ObjC.NSApp, "activateIgnoringOtherApps:", true);
                    ObjC.Send(Window, "makeKeyAndOrderFront:", IntPtr.Zero);
                }
                else
                {
                    ObjC.Send(Window, "orderFrontRegardless");
                }
            }
            else
            {
                ObjC.Send(Window, "orderOut:", IntPtr.Zero);
            }
        }

        public static void Activate()
        {
            if (Window == IntPtr.Zero) return;
            ObjC.SendBool(ObjC.NSApp, "activateIgnoringOtherApps:", true);
            ObjC.Send(Window, "makeKeyAndOrderFront:", IntPtr.Zero);
        }

        // ------------------------------------------------------------------
        // 坐标换算
        // ------------------------------------------------------------------

        public static NSRect Frame() => ObjC.SendRect(Window, "frame");

        static IntPtr Screens() => ObjC.Send(ObjC.Cls("NSScreen"), "screens");

        static int ScreenCount(IntPtr screens) => screens == IntPtr.Zero ? 0 : (int)ObjC.SendUL(screens, ObjC.Sel("count"));

        static IntPtr ScreenAt(IntPtr screens, int i) => ObjC.SendU(screens, ObjC.Sel("objectAtIndex:"), (ulong)i);

        /// <summary>主显示器（菜单栏所在，screens[0]）高度（point），用于翻转 y。</summary>
        static double PrimaryHeight()
        {
            var screens = Screens();
            if (ScreenCount(screens) == 0) return Display.main.systemHeight / Math.Max(1f, Scale);
            return ObjC.SendRect(ScreenAt(screens, 0), "frame").H;
        }

        /// <summary>Cocoa 矩形（point，左下原点）→ 桌面坐标（像素，左上原点）。</summary>
        public static RectInt ToDesktop(NSRect r, double primaryH)
        {
            double s = Scale;
            double top = primaryH - (r.Y + r.H);
            return new RectInt((int)Math.Round(r.X * s), (int)Math.Round(top * s), (int)Math.Round(r.W * s), (int)Math.Round(r.H * s));
        }

        static NSRect FromDesktop(int x, int y, int w, int h, double primaryH)
        {
            double s = Scale;
            double pw = w / s, ph = h / s;
            double px = x / s;
            double py = primaryH - y / s - ph;
            return new NSRect(px, py, pw, ph);
        }

        public static bool TryGetWindowRect(out RectInt rect)
        {
            rect = default;
            if (Window == IntPtr.Zero) return false;
            rect = ToDesktop(Frame(), PrimaryHeight());
            return rect.width > 0 && rect.height > 0;
        }

        public static void SetWindowRect(int x, int y, int w, int h)
        {
            if (Window == IntPtr.Zero) return;
            var r = FromDesktop(x, y, w, h, PrimaryHeight());
            ObjC.SendV(Window, ObjC.Sel("setFrame:display:"), r, (byte)1);
            s_requestedPx = new Vector2Int(w, h);
            s_requestedAt = Time.unscaledTime;
        }

        public static void MoveWindow(int x, int y)
        {
            if (Window == IntPtr.Zero) return;
            var f = Frame();
            double s = Scale;
            double px = x / s;
            double py = PrimaryHeight() - y / s - f.H;
            ObjC.SendV(Window, ObjC.Sel("setFrameOrigin:"), new NSPoint(px, py));
        }

        static Vector2Int s_requestedPx;
        static float s_requestedAt = -1f;
        static Vector2Int s_setResolutionFor;

        /// <summary>
        /// 退化处理：如果 setFrame 之后 Unity 的渲染分辨率（Screen.width/height）没有跟上，1 秒后改用 Screen.SetResolution。
        /// </summary>
        public static void CheckResolution()
        {
            if (Window == IntPtr.Zero || s_requestedAt < 0f || Time.unscaledTime - s_requestedAt < 1f) return;
            var want = s_requestedPx;
            s_requestedAt = -1f;
            if (Mathf.Abs(Screen.width - want.x) <= 2 && Mathf.Abs(Screen.height - want.y) <= 2) return;
            if (s_setResolutionFor == want) return;
            s_setResolutionFor = want;
            Debug.LogWarning($"[DesktopFigure] macOS: render size {Screen.width}x{Screen.height} did not follow window {want.x}x{want.y}; calling Screen.SetResolution");
            TryGetWindowRect(out var before);
            Screen.SetResolution(want.x, want.y, FullScreenMode.Windowed);
            MoveWindow(before.x, before.y);
        }

        public static bool TryGetCursorDesktop(out Vector2Int pos)
        {
            pos = default;
            try
            {
                NSPoint m = ObjC.SendPoint(ObjC.Cls("NSEvent"), ObjC.Sel("mouseLocation"));
                double s = Scale;
                pos = new Vector2Int((int)Math.Round(m.X * s), (int)Math.Round((PrimaryHeight() - m.Y) * s));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>光标在窗口里的 Unity 屏幕坐标（左下原点，像素；窗口不接收鼠标事件时也有效）。</summary>
        public static bool TryGetCursorUnity(out Vector2 p)
        {
            p = default;
            if (Window == IntPtr.Zero) return false;
            NSPoint m = ObjC.SendPoint(ObjC.Cls("NSEvent"), ObjC.Sel("mouseLocation"));
            var f = Frame();
            if (f.W <= 0 || f.H <= 0) return false;
            p = new Vector2((float)((m.X - f.X) / f.W * Screen.width), (float)((m.Y - f.Y) / f.H * Screen.height));
            return true;
        }

        public static bool IsMouseButtonPressed(int button)
        {
            ulong mask = ObjC.SendUL(ObjC.Cls("NSEvent"), ObjC.Sel("pressedMouseButtons"));
            return (mask & (1UL << Mathf.Clamp(button, 0, 2))) != 0;
        }

        public static bool IsModifierPressed(int vk, out bool known)
        {
            known = true;
            ulong flags = ObjC.SendUL(ObjC.Cls("NSEvent"), ObjC.Sel("modifierFlags"));
            switch (vk)
            {
                case TransparentWindow.VK_SHIFT: return (flags & ModShift) != 0;
                // Ctrl 在 Mac 上同时接受 Control 和 Command（Ctrl+滚轮 / ⌘+滚轮 = 镜头远近）
                case TransparentWindow.VK_CONTROL: return (flags & (ModControl | ModCommand)) != 0;
                default:
                    known = false;
                    return false;
            }
        }

        static RectInt ScreenRect(IntPtr screen, string sel, double primaryH) => ToDesktop(ObjC.SendRect(screen, sel), primaryH);

        public static RectInt PrimaryWorkArea()
        {
            var screens = Screens();
            double ph = PrimaryHeight();
            if (ScreenCount(screens) == 0) return new RectInt(0, 0, Display.main.systemWidth, Display.main.systemHeight);
            return ScreenRect(ScreenAt(screens, 0), "visibleFrame", ph);
        }

        public static RectInt VirtualScreen()
        {
            var screens = Screens();
            int n = ScreenCount(screens);
            if (n == 0) return new RectInt(0, 0, Display.main.systemWidth, Display.main.systemHeight);
            double ph = PrimaryHeight();
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                var r = ScreenRect(ScreenAt(screens, i), "frame", ph);
                x0 = Mathf.Min(x0, r.xMin); y0 = Mathf.Min(y0, r.yMin);
                x1 = Mathf.Max(x1, r.xMax); y1 = Mathf.Max(y1, r.yMax);
            }
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        public static RectInt WorkAreaAt(Vector2Int p)
        {
            var screens = Screens();
            int n = ScreenCount(screens);
            if (n == 0) return PrimaryWorkArea();
            double ph = PrimaryHeight();
            int best = 0;
            long bestDist = long.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var r = ScreenRect(ScreenAt(screens, i), "frame", ph);
                long dx = p.x < r.xMin ? r.xMin - p.x : (p.x >= r.xMax ? p.x - r.xMax + 1 : 0);
                long dy = p.y < r.yMin ? r.yMin - p.y : (p.y >= r.yMax ? p.y - r.yMax + 1 : 0);
                long d = dx * dx + dy * dy;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return ScreenRect(ScreenAt(screens, best), "visibleFrame", ph);
        }
    }
}
#endif
