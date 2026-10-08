#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
#define DF_MAC
#endif

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DesktopFigure
{
    /// <summary>
    /// macOS 菜单栏图标（相当于 Windows 的系统托盘图标，见 TrayIcon）：
    ///   NSStatusBar 上的一个 NSStatusItem，菜单「显示/隐藏 宠物名称」「退出」。
    /// 菜单项的 target 是运行时用 objc_allocateClassPair 创建的 Objective-C 类，方法实现（IMP）是 C# 委托。
    /// 只在 macOS 播放器里生效；其它平台 / 编辑器里什么都不做。
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class MacStatusItem : MonoBehaviour
    {
        public static MacStatusItem Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
#if DF_MAC
            if (Instance != null) return;
            var go = new GameObject("MacStatusItem");
            DontDestroyOnLoad(go);
            go.AddComponent<MacStatusItem>();
#endif
        }

        /// <summary>立即移除菜单栏图标（退出前调用）。</summary>
        public static void RemoveItem()
        {
#if DF_MAC
            if (Instance != null) Instance.Remove();
#endif
        }

#if DF_MAC
        const string TargetClassName = "DFStatusItemTarget";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void ActionImp(IntPtr self, IntPtr cmd, IntPtr sender);

        // 必须一直被静态字段引用，防止委托被 GC 回收导致原生回调崩溃
        static ActionImp s_toggleImp;
        static ActionImp s_quitImp;
        static IntPtr s_targetClass;

        IntPtr _statusItem;
        IntPtr _toggleItem;
        IntPtr _target;
        string _title = "";
        string _tip = "";
        float _nextUpdate;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        void Start()
        {
            try
            {
                Create();
            }
            catch (Exception e)
            {
                Debug.LogError("[DesktopFigure] macOS status item init failed (no menu bar icon): " + e);
            }
        }

        void Update()
        {
            if (_statusItem == IntPtr.Zero || Time.unscaledTime < _nextUpdate) return;
            _nextUpdate = Time.unscaledTime + 0.5f;
            try
            {
                RefreshTitles();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DesktopFigure] macOS status item update failed: " + e.Message);
                _nextUpdate = Time.unscaledTime + 10f;
            }
        }

        void OnApplicationQuit() => Remove();

        void OnDestroy()
        {
            if (Instance != this) return;
            Remove();
            Instance = null;
        }

        static IntPtr EnsureTargetClass()
        {
            if (s_targetClass != IntPtr.Zero) return s_targetClass;
            IntPtr cls = ObjC.objc_getClass(TargetClassName);
            if (cls == IntPtr.Zero)
            {
                cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSObject"), TargetClassName, IntPtr.Zero);
                if (cls == IntPtr.Zero) throw new Exception("objc_allocateClassPair failed");
                if (s_toggleImp == null) s_toggleImp = OnToggle;
                if (s_quitImp == null) s_quitImp = OnQuit;
                ObjC.class_addMethod(cls, ObjC.Sel("dfToggle:"), Marshal.GetFunctionPointerForDelegate(s_toggleImp), "v@:@");
                ObjC.class_addMethod(cls, ObjC.Sel("dfQuit:"), Marshal.GetFunctionPointerForDelegate(s_quitImp), "v@:@");
                ObjC.objc_registerClassPair(cls);
            }
            s_targetClass = cls;
            return cls;
        }

        void Create()
        {
            IntPtr cls = EnsureTargetClass();
            _target = ObjC.Send(ObjC.Send(cls, "alloc"), "init");

            IntPtr bar = ObjC.Send(ObjC.Cls("NSStatusBar"), "systemStatusBar");
            _statusItem = ObjC.SendD(bar, ObjC.Sel("statusItemWithLength:"), -1.0);   // NSVariableStatusItemLength
            if (_statusItem == IntPtr.Zero) throw new Exception("statusItemWithLength returned nil");
            ObjC.Send(_statusItem, "retain");   // NSStatusBar 不持有 status item

            IntPtr button = ObjC.Send(_statusItem, "button");
            IntPtr image = IntPtr.Zero;
            IntPtr imageClass = ObjC.Cls("NSImage");
            if (ObjC.RespondsTo(imageClass, "imageWithSystemSymbolName:accessibilityDescription:"))   // macOS 11+
            {
                foreach (var name in new[] { "figure.wave", "person.fill" })
                {
                    image = ObjC.Send(imageClass, ObjC.Sel("imageWithSystemSymbolName:accessibilityDescription:"), ObjC.NSString(name), IntPtr.Zero);
                    if (image != IntPtr.Zero) break;
                }
            }
            if (button != IntPtr.Zero)
            {
                if (image != IntPtr.Zero)
                {
                    ObjC.SendBool(image, "setTemplate:", true);
                    ObjC.Send(button, "setImage:", image);
                }
                else
                {
                    ObjC.Send(button, "setTitle:", ObjC.NSString("裹"));
                }
            }

            IntPtr menu = ObjC.Send(ObjC.Send(ObjC.Cls("NSMenu"), "alloc"), "initWithTitle:", ObjC.NSString("DesktopFigure"));
            ObjC.SendBool(menu, "setAutoenablesItems:", false);

            _toggleItem = NewItem("隐藏" + TrayIcon.PetDisplayName, "dfToggle:");
            ObjC.Send(menu, "addItem:", _toggleItem);
            ObjC.Send(menu, "addItem:", ObjC.Send(ObjC.Cls("NSMenuItem"), "separatorItem"));
            ObjC.Send(menu, "addItem:", NewItem("退出", "dfQuit:"));
            ObjC.Send(_statusItem, "setMenu:", menu);

            RefreshTitles();
            Debug.Log("[DesktopFigure] macOS status item added (" + (image != IntPtr.Zero ? "SF Symbol" : "text") + " icon)");
        }

        IntPtr NewItem(string title, string action)
        {
            IntPtr item = ObjC.Send(ObjC.Send(ObjC.Cls("NSMenuItem"), "alloc"), ObjC.Sel("initWithTitle:action:keyEquivalent:"),
                ObjC.NSString(title), ObjC.Sel(action), ObjC.NSString(""));
            ObjC.Send(item, "setTarget:", _target);
            ObjC.SendBool(item, "setEnabled:", true);
            return item;
        }

        void RefreshTitles()
        {
            var tw = TransparentWindow.Instance;
            bool visible = tw == null || tw.IsPetVisible;
            string name = TrayIcon.PetDisplayName;
            string title = (visible ? "隐藏" : "显示") + name;
            if (title != _title && _toggleItem != IntPtr.Zero)
            {
                _title = title;
                ObjC.Send(_toggleItem, "setTitle:", ObjC.NSString(title));
            }
            if (name != _tip)
            {
                _tip = name;
                IntPtr button = ObjC.Send(_statusItem, "button");
                if (button != IntPtr.Zero) ObjC.Send(button, "setToolTip:", ObjC.NSString(name));
            }
        }

        void Remove()
        {
            if (_statusItem == IntPtr.Zero) return;
            try
            {
                IntPtr bar = ObjC.Send(ObjC.Cls("NSStatusBar"), "systemStatusBar");
                ObjC.Send(bar, "removeStatusItem:", _statusItem);
                ObjC.Send(_statusItem, "release");
                Debug.Log("[DesktopFigure] macOS status item removed");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DesktopFigure] macOS status item remove failed: " + e.Message);
            }
            _statusItem = IntPtr.Zero;
            _toggleItem = IntPtr.Zero;
        }

        // ---- 菜单回调（AppKit 在主线程调用，可以直接用 Unity API）----

        [AOT.MonoPInvokeCallback(typeof(ActionImp))]
        static void OnToggle(IntPtr self, IntPtr cmd, IntPtr sender)
        {
            try
            {
                var tw = TransparentWindow.Instance;
                if (tw == null) return;
                bool visible = tw.IsPetVisible;
                tw.SetPetVisible(!visible, !visible);
                if (Instance != null) Instance.RefreshTitles();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        [AOT.MonoPInvokeCallback(typeof(ActionImp))]
        static void OnQuit(IntPtr self, IntPtr cmd, IntPtr sender)
        {
            try
            {
                Debug.Log("[DesktopFigure] Status item menu: Quit");
                if (Instance != null) Instance.Remove();
                Application.Quit();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
#endif
    }
}
