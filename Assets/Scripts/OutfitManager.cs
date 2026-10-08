using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniGLTF;
using UnityEngine;
using UniVRM10;

namespace DesktopFigure
{
    /// <summary>
    /// 换装管理：Outfits 文件夹（Windows：exe 同级；macOS：见 OutfitsDirectory）里的每个 .vrm = 一套服装。
    /// - 数字键 1~9：切换服装；记住上次选择（PlayerPrefs）
    /// - 宠物名称：默认取 VRM 的 meta 名称（VRM1 meta.name / VRM0 meta.title），可在 设置 → 宠物名称 里自定义
    /// - 右键菜单 / 衣橱子菜单 / 设置子菜单 / 改名弹窗：见 OutfitManager.Menu.cs
    /// 使用 UniVRM 1.0 的 Vrm10.LoadPathAsync 运行时加载（VRM 0.x 文件会自动迁移）。
    /// </summary>
    public partial class OutfitManager : MonoBehaviour
    {
        public static OutfitManager Instance { get; private set; }

        [Tooltip("角色挂载的根节点（位置/缩放在换装时保持不变）")]
        public Transform characterRoot;

        const string PrefLastOutfit = "DF_LastOutfit";
        const string PrefHintsShown = "DF_HintsShown";
        const string PrefPetName = "DF_PetName";
        const string PrefRenderStyle = "DF_RenderStyle";   // 0 = 原版（MToon），1 = 绝区零风（NPR，默认）

        /// <summary>当前渲染风格是否为绝区零风（NPR）。切换时不重新加载模型。</summary>
        public bool RenderStyleNpr => PlayerPrefs.GetInt(PrefRenderStyle, 1) != 0;

        public void SetRenderStyle(bool npr)
        {
            PlayerPrefs.SetInt(PrefRenderStyle, npr ? 1 : 0);
            PlayerPrefs.Save();
            if (_npr != null) _npr.Apply(npr);
        }

        static readonly string[] HintLines =
        {
            "左键拖动 = 移动",
            "右键拖动 = 旋转",
            "中键拖动 = 俯仰（或 Shift+右键拖动）",
            "滚轮 = 缩放窗口",
#if UNITY_STANDALONE_OSX
            "Ctrl 或 Command + 滚轮 = 镜头远近",
#else
            "Ctrl+滚轮 = 镜头远近",
#endif
            "右键单击 = 菜单",
            "左键双击 = 转一圈",
        };

        /// <summary>
        /// Windows 构建版：&lt;exe 所在目录&gt;/Outfits；编辑器：&lt;项目根目录&gt;/Outfits
        /// （两种情况下都是 Application.dataPath/../Outfits）。
        /// macOS 构建版：见 MacOutfitsDirectory（DesktopFigure.app 同级的 Outfits，或 ~/Library/Application Support/...）。
        /// </summary>
        public static string OutfitsDirectory
        {
            get
            {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
                return MacOutfitsDirectory();
#else
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Outfits"));
#endif
            }
        }

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        const string PrefMacSeeded = "DF_MacOutfitsSeeded";
        static string s_macOutfitsDir;

        /// <summary>
        /// macOS（Application.dataPath = …/DesktopFigure.app/Contents）：
        /// 1) 如果 DesktopFigure.app 同级目录下有 Outfits 文件夹（且程序没有被 App Translocation 放到只读的随机路径里运行），就用它；
        /// 2) 否则用 ~/Library/Application Support/DesktopFigure/DesktopFigure/Outfits（= Application.persistentDataPath/Outfits），
        ///    第一次使用时把 .app 里内置的 Contents/Resources/Outfits（打包时复制进去的默认模型）复制过去。
        /// 菜单「打开 Outfits 文件夹」会在访达里打开实际使用的文件夹。
        /// </summary>
        static string MacOutfitsDirectory()
        {
            if (s_macOutfitsDir != null) return s_macOutfitsDir;
            string contents = Path.GetFullPath(Application.dataPath);
            string bundled = Path.Combine(contents, "Resources", "Outfits");
            bool translocated = contents.IndexOf("/AppTranslocation/", StringComparison.Ordinal) >= 0;
            string appDir = Path.GetDirectoryName(contents);
            string parent = string.IsNullOrEmpty(appDir) ? null : Path.GetDirectoryName(appDir);
            string beside = string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "Outfits");
            if (!translocated && beside != null && Directory.Exists(beside))
            {
                s_macOutfitsDir = beside;
            }
            else
            {
                string user = Path.Combine(Application.persistentDataPath, "Outfits");
                SeedMacOutfits(user, bundled);
                s_macOutfitsDir = user;
            }
            Debug.Log("[DesktopFigure] macOS Outfits folder: " + s_macOutfitsDir + (translocated ? " (app is translocated; run xattr -dr com.apple.quarantine on the .app to use a folder beside it)" : ""));
            return s_macOutfitsDir;
        }

        static void SeedMacOutfits(string dst, string src)
        {
            try
            {
                Directory.CreateDirectory(dst);
                if (PlayerPrefs.GetInt(PrefMacSeeded, 0) != 0 || !Directory.Exists(src)) return;
                foreach (var f in Directory.GetFiles(src))
                {
                    string t = Path.Combine(dst, Path.GetFileName(f));
                    if (!File.Exists(t)) File.Copy(f, t);
                }
                PlayerPrefs.SetInt(PrefMacSeeded, 1);
                PlayerPrefs.Save();
                Debug.Log("[DesktopFigure] Copied bundled outfits " + src + " -> " + dst);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
#endif

        public bool IsLoading { get; private set; }
        public bool HasModel => _current != null;
        public IReadOnlyList<string> OutfitFiles => _files;
        public int CurrentIndex { get; private set; } = -1;

        readonly List<string> _files = new List<string>();
        Vrm10Instance _current;
        RuntimeGltfInstance _currentGltf;
        NprStyle _npr;
        IdleMotion _idle;
        FigureViewController _view;
        string _status = "";
        string _currentFileName = "";
        string _modelPetName = "";

        /// <summary>模型自带的名称（VRM1 meta.name；VRM0 的 meta.title 在迁移时会变成 meta.name），为空则用文件名。</summary>
        public string DefaultPetName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_modelPetName)) return _modelPetName.Trim();
                if (!string.IsNullOrEmpty(_currentFileName)) return Path.GetFileNameWithoutExtension(_currentFileName);
                return "宠物";
            }
        }

        /// <summary>用户自定义名称（PlayerPrefs）优先，否则用 DefaultPetName。</summary>
        public string PetName
        {
            get
            {
                string custom = PlayerPrefs.GetString(PrefPetName, "");
                return string.IsNullOrWhiteSpace(custom) ? DefaultPetName : custom.Trim();
            }
        }

        public void SetCustomPetName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == DefaultPetName) PlayerPrefs.DeleteKey(PrefPetName);
            else PlayerPrefs.SetString(PrefPetName, name);
            PlayerPrefs.Save();
            Debug.Log("[DesktopFigure] Pet name: " + PetName);
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            if (characterRoot == null)
            {
                var existing = transform.Find("CharacterRoot");
                characterRoot = existing != null ? existing : new GameObject("CharacterRoot").transform;
                if (existing == null) characterRoot.SetParent(transform, false);
            }
            _idle = GetComponent<IdleMotion>();
            if (_idle == null) _idle = gameObject.AddComponent<IdleMotion>();
            _view = GetComponent<FigureViewController>();
            if (_view == null) _view = gameObject.AddComponent<FigureViewController>();
            if (_view.characterRoot == null) _view.characterRoot = characterRoot;
        }

        void Start()
        {
            RefreshList();
            if (_files.Count == 0)
            {
                _status = "Outfits 文件夹里没有 .vrm 文件\n" + OutfitsDirectory;
                return;
            }
            string last = PlayerPrefs.GetString(PrefLastOutfit, "");
            int idx = _files.FindIndex(f => string.Equals(Path.GetFileName(f), last, StringComparison.OrdinalIgnoreCase));
            LoadOutfit(idx >= 0 ? idx : 0);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _skin.DestroyTextures();
        }

        public void RefreshList()
        {
            _files.Clear();
            string dir = OutfitsDirectory;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _files.AddRange(Directory.GetFiles(dir, "*.vrm", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            if (_current != null)
            {
                CurrentIndex = _files.FindIndex(f => string.Equals(Path.GetFileName(f), _currentFileName, StringComparison.OrdinalIgnoreCase));
            }
            else if (_files.Count > 0 && !IsLoading && _status.StartsWith("Outfits", StringComparison.Ordinal))
            {
                // 之前是空文件夹，刷新后有文件了 => 直接加载第一个
                _status = "";
                LoadOutfit(0);
            }
        }

        public async void LoadOutfit(int index)
        {
            if (IsLoading || index < 0 || index >= _files.Count) return;
            if (index == CurrentIndex && _current != null) return;

            string path = _files[index];
            IsLoading = true;
            _status = "加载中… " + Path.GetFileNameWithoutExtension(path);
            Vrm10Instance instance = null;
            try
            {
                instance = await Vrm10.LoadPathAsync(
                    path,
                    canLoadVrm0X: true,
                    controlRigGenerationOption: ControlRigGenerationOption.Generate,
                    showMeshes: false);

                if (instance == null)
                {
                    _status = "加载失败: " + Path.GetFileName(path);
                    return;
                }
                if (this == null)
                {
                    Destroy(instance.gameObject);
                    return;
                }

                // 重要：在挂到（可能已经旋转的）角色根节点之前创建 Runtime/ControlRig。
                // ControlRig 的归一化骨骼以创建时的世界朝向为基准，若在旋转后才创建，
                // IdleMotion 写入的骨骼旋转会错位（例如转身后手臂举起）。
                var runtime = instance.Runtime;

                // 挂到角色根节点下（根节点的位置/缩放/旋转保持不变 => 换装不影响位置、大小和朝向）
                instance.transform.SetParent(characterRoot, false);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                var gltf = instance.GetComponent<RuntimeGltfInstance>();
                if (gltf != null) gltf.EnableUpdateWhenOffscreen();

                // 销毁旧服装
                if (_currentGltf != null) _currentGltf.Dispose();
                else if (_current != null) Destroy(_current.gameObject);

                _current = instance;
                _currentGltf = gltf;
                CurrentIndex = index;
                _currentFileName = Path.GetFileName(path);
                try
                {
                    _modelPetName = instance.Vrm != null && instance.Vrm.Meta != null ? (instance.Vrm.Meta.Name ?? "") : "";
                }
                catch (Exception)
                {
                    _modelPetName = "";
                }

                _idle.SetTarget(instance);
                _view.SetModel(instance, _idle.Facing);

                // 渲染风格：生成 NPR 材质（两套材质都保留，切换时不重新加载）
                _npr = null;
                try
                {
                    _npr = instance.gameObject.AddComponent<NprStyle>();
                    _npr.Init(instance, _idle.Facing);
                    _npr.Apply(RenderStyleNpr);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                if (gltf != null) gltf.ShowMeshes();

                PlayerPrefs.SetString(PrefLastOutfit, _currentFileName);
                PlayerPrefs.Save();
                _status = "";

                if (PlayerPrefs.GetInt(PrefHintsShown, 0) == 0)
                {
                    ShowHints(15f);
                    PlayerPrefs.SetInt(PrefHintsShown, 1);
                    PlayerPrefs.Save();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status = "加载失败: " + Path.GetFileName(path) + "\n" + e.Message;
                if (instance != null && instance != _current) Destroy(instance.gameObject);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void ShowHints(float seconds)
        {
            if (!_showHints) _hintsShownAt = Time.unscaledTime;
            _showHints = true;
            _hintsUntil = Time.unscaledTime + seconds;
        }
        void Update()
        {
            // 数字键 1~9 切换（改名输入时不响应）
            if (!_renameOpen)
            {
                for (int i = 0; i < 9; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
                    {
                        if (i < _files.Count) LoadOutfit(i);
                    }
                }
            }

            // 右键单击打开菜单由 FigureViewController 处理（区分单击和拖动旋转）
            UpdateMenuInput();

            // 操作说明：超时或点击后隐藏
            if (_showHints)
            {
                if (Time.unscaledTime > _hintsUntil) _showHints = false;
                else if (TransparentWindow.GetMouseButtonDown(0) && _hintRect.Contains(GuiMousePosition())) _showHints = false;
            }

            var t = TransparentWindow.Instance;
            if (t != null) t.ForceClickable = IsMenuOpen;
        }

        void OnApplicationFocus(bool hasFocus)
        {
            // 失去焦点时关闭菜单（改名弹窗保留，避免输入法候选窗等情况误关）
            if (!hasFocus && _menuOpen) CloseMenu();
        }

        static void OpenOutfitsFolder()
        {
            string dir = OutfitsDirectory;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/open", "\"" + dir.Replace("\"", "\\\"") + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
#else
                Application.OpenURL("file://" + dir);
#endif
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
