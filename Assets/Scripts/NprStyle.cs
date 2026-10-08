using System.Collections.Generic;
using UnityEngine;
using UniVRM10;

namespace DesktopFigure
{
    /// <summary>
    /// 渲染风格：原版（MToon） / 绝区零风（DesktopFigure/NPR 卡通渲染）。
    /// 每次加载 VRM 后由 OutfitManager 挂到模型上：
    /// - 为每个 MToon 材质生成一份 NPR 材质（复制基础色/贴图、暗部色/贴图、alpha 模式/裁剪、剔除、自发光、法线贴图、渲染队列）
    /// - 为每个网格复制一份并把“平滑法线”写进 tangent（用于描边，GPU 蒙皮会同时变换 tangent）
    /// - 两套材质 / 网格都保留 => 切换风格不需要重新加载模型
    /// - 每帧（Camera.onPreCull）把头部朝向写进全局 shader 变量，供脸部阴影和头发高光使用
    /// </summary>
    public class NprStyle : MonoBehaviour
    {
        public const string ShaderName = "DesktopFigure/NPR";

        enum Category { Cloth = 0, Skin = 1, Face = 2, Hair = 3, Unlit = 4 }

        class RendererSet
        {
            public Renderer Renderer;
            public Material[] Original;
            public Material[] Npr;
            public Mesh OriginalMesh;
            public Mesh NprMesh;
        }

        static readonly int IdHeadCenter = Shader.PropertyToID("_DF_HeadCenter");
        static readonly int IdHeadForward = Shader.PropertyToID("_DF_HeadForward");
        static readonly int IdHeadRight = Shader.PropertyToID("_DF_HeadRight");
        static readonly int IdHeadUp = Shader.PropertyToID("_DF_HeadUp");
        static readonly int IdViewportPx = Shader.PropertyToID("_DF_ViewportPx");
        static readonly int IdKeyLightDir = Shader.PropertyToID("_DF_KeyLightDir");

        // NPR 主光方向（相对镜头）：从右上前方照过来，比 MToon 用的灯更偏侧面 => 明暗分界更清楚。
        // 跟随镜头 => 模型怎么转，看到的一面光照都一致。
        static readonly Quaternion KeyLightFromCamera = Quaternion.Euler(35f, -48f, 0f);

        // 头部中心相对头骨骼的偏移（单位：米，按模型缩放）。VRoid：头骨骼在下巴附近，脸部中心约在眼睛高度。
        const float HeadCenterUp = 0.06f;
        const float HeadCenterForward = -0.005f;

        readonly List<RendererSet> _sets = new List<RendererSet>();
        readonly List<Object> _created = new List<Object>();
        Transform _head;
        Vector3 _headLocalForward = Vector3.forward, _headLocalRight = Vector3.right, _headLocalUp = Vector3.up;
        bool _npr;
        bool _initialized;

        public bool IsNpr => _npr;
        public bool Available { get; private set; }

        public void Init(Vrm10Instance instance, float facing)
        {
            if (_initialized) return;
            _initialized = true;

            var shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[DesktopFigure] NPR shader not available: " + ShaderName);
                Available = false;
                return;
            }

            var root = instance.transform;
            if (instance.TryGetBoneTransform(HumanBodyBones.Head, out var head) && head != null)
            {
                _head = head;
                var inv = Quaternion.Inverse(head.rotation);
                _headLocalForward = inv * (root.forward * facing);
                _headLocalRight = inv * (root.right * facing);
                _headLocalUp = inv * root.up;
            }

            var converted = new Dictionary<Material, Material>();
            var smoothed = new Dictionary<Mesh, Mesh>();
            int matCount = 0, meshCount = 0;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                var set = new RendererSet { Renderer = r, Original = r.sharedMaterials };
                set.Npr = new Material[set.Original.Length];
                bool anyOutline = false;
                for (int i = 0; i < set.Original.Length; i++)
                {
                    var src = set.Original[i];
                    if (src == null) { set.Npr[i] = null; continue; }
                    if (!converted.TryGetValue(src, out var dst))
                    {
                        dst = Convert(src, shader);
                        converted[src] = dst;
                        if (dst != src) { _created.Add(dst); matCount++; }
                    }
                    set.Npr[i] = dst;
                    if (dst != src && dst.GetShaderPassEnabled("Always")) anyOutline = true;
                }

                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else if (r.TryGetComponent<MeshFilter>(out var mf)) mesh = mf.sharedMesh;
                set.OriginalMesh = mesh;
                if (mesh != null && anyOutline)
                {
                    if (!smoothed.TryGetValue(mesh, out var sm))
                    {
                        sm = BuildSmoothNormalMesh(mesh);
                        smoothed[mesh] = sm;
                        if (sm != null) { _created.Add(sm); meshCount++; }
                    }
                    set.NprMesh = sm;
                }
                foreach (var m in set.Npr)
                    if (m != null && m.shader == shader) m.SetFloat("_UseSmoothNormal", set.NprMesh != null ? 1f : 0f);
                _sets.Add(set);
            }
            Available = true;
            Debug.Log($"[DesktopFigure] NPR ready: {matCount} materials, {meshCount} smoothed meshes, head={(_head != null ? _head.name : "none")}");
        }

        public void Apply(bool npr)
        {
            if (!Available) npr = false;
            _npr = npr;
            foreach (var s in _sets)
            {
                if (s.Renderer == null) continue;
                s.Renderer.sharedMaterials = npr ? s.Npr : s.Original;
                var mesh = npr && s.NprMesh != null ? s.NprMesh : s.OriginalMesh;
                if (mesh == null) continue;
                if (s.Renderer is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh != mesh) smr.sharedMesh = mesh;
                }
                else if (s.Renderer.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != mesh)
                {
                    mf.sharedMesh = mesh;
                }
            }
        }

        void OnEnable() { Camera.onPreCull += OnCameraPreCull; }
        void OnDisable() { Camera.onPreCull -= OnCameraPreCull; }

        void OnDestroy()
        {
            // 先换回原始网格/材质（RuntimeGltfInstance 负责销毁它们），再销毁我们创建的副本
            Apply(false);
            foreach (var o in _created)
                if (o != null) Destroy(o);
            _created.Clear();
        }

        void OnCameraPreCull(Camera cam)
        {
            if (!_npr || cam == null || (cam.cullingMask & (1 << gameObject.layer)) == 0) return;
            Shader.SetGlobalVector(IdViewportPx, new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0));
            Vector3 toLight = -(cam.transform.rotation * KeyLightFromCamera * Vector3.forward);
            Shader.SetGlobalVector(IdKeyLightDir, new Vector4(toLight.x, toLight.y, toLight.z, 1f));
            if (_head == null)
            {
                Shader.SetGlobalVector(IdHeadForward, Vector4.zero);
                return;
            }
            var rot = _head.rotation;
            Vector3 fwd = (rot * _headLocalForward).normalized;
            Vector3 right = (rot * _headLocalRight).normalized;
            Vector3 up = (rot * _headLocalUp).normalized;
            float scale = Mathf.Abs(transform.lossyScale.y);
            Vector3 center = _head.position + (up * HeadCenterUp + fwd * HeadCenterForward) * scale;
            Shader.SetGlobalVector(IdHeadCenter, center);
            Shader.SetGlobalVector(IdHeadForward, fwd);
            Shader.SetGlobalVector(IdHeadRight, right);
            Shader.SetGlobalVector(IdHeadUp, up);
        }

        // ---------------------------------------------------------------- 材质转换

        static Category Classify(string materialName)
        {
            var n = (materialName ?? "").ToLowerInvariant();
            if (n.Contains("eyeiris") || n.Contains("eyehighlight") || n.Contains("eyewhite") || n.Contains("_eye") ||
                n.Contains("facebrow") || n.Contains("faceeyelash") || n.Contains("faceeyeline") || n.Contains("eyelash") ||
                n.Contains("eyebrow") || n.Contains("eyeline"))
                return Category.Unlit;
            if (n.Contains("facemouth") || (n.Contains("face") && n.Contains("skin"))) return Category.Face;
            if (n.Contains("skin") || n.Contains("body")) return Category.Skin;
            if (n.Contains("hair")) return Category.Hair;
            return Category.Cloth;
        }

        static Material Convert(Material src, Shader npr)
        {
            var sn = src.shader != null ? src.shader.name : "";
            bool isMToon10 = sn == "VRM10/MToon10";
            bool isMToon0 = sn == "VRM/MToon";
            if (!isMToon10 && !isMToon0) return src;   // 非 MToon（PBR / Unlit 等）保持原样

            var cat = Classify(src.name);
            var m = new Material(npr) { name = src.name + " (NPR)" };

            CopyColor(src, m, "_Color");
            CopyTex(src, m, "_MainTex");
            CopyColor(src, m, "_ShadeColor");
            CopyTex(src, m, "_ShadeTex");
            CopyTex(src, m, "_BumpMap");
            CopyFloat(src, m, "_BumpScale");
            CopyColor(src, m, "_EmissionColor");
            CopyTex(src, m, "_EmissionMap");
            CopyFloat(src, m, "_Cutoff");
            if (src.HasProperty("_MainTex"))
            {
                m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            }

            // 渲染状态
            bool blend = src.IsKeywordEnabled("_ALPHABLEND_ON");
            bool test = src.IsKeywordEnabled("_ALPHATEST_ON");
            if (isMToon10)
            {
                CopyFloat(src, m, "_AlphaMode");
                CopyFloat(src, m, "_M_CullMode");
                CopyFloat(src, m, "_M_SrcBlend");
                CopyFloat(src, m, "_M_DstBlend");
                CopyFloat(src, m, "_M_ZWrite");
                CopyFloat(src, m, "_M_AlphaToMask");
            }
            else
            {
                // VRM0 MToon：_CullMode / _SrcBlend / _DstBlend / _ZWrite
                if (src.HasProperty("_CullMode")) m.SetFloat("_M_CullMode", src.GetFloat("_CullMode"));
                if (src.HasProperty("_SrcBlend")) m.SetFloat("_M_SrcBlend", src.GetFloat("_SrcBlend"));
                if (src.HasProperty("_DstBlend")) m.SetFloat("_M_DstBlend", src.GetFloat("_DstBlend"));
                if (src.HasProperty("_ZWrite")) m.SetFloat("_M_ZWrite", src.GetFloat("_ZWrite"));
                if (src.HasProperty("_AlphaToMask")) m.SetFloat("_M_AlphaToMask", src.GetFloat("_AlphaToMask"));
            }
            if (blend) m.EnableKeyword("_ALPHABLEND_ON");
            else if (test) m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = src.renderQueue;
            m.SetOverrideTag("RenderType", blend ? "Transparent" : test ? "TransparentCutout" : "Opaque");

            ApplyPreset(m, cat, blend);
            return m;
        }

        /// <summary>按材质类型设置绝区零风格的参数（线性空间颜色）。</summary>
        static void ApplyPreset(Material m, Category cat, bool transparent)
        {
            m.SetFloat("_Category", (float)cat);
            bool outline = !transparent;
            switch (cat)
            {
                case Category.Unlit:
                    outline = false;
                    m.SetFloat("_UnlitBrightness", 1.0f);
                    m.SetFloat("_EmissionStrength", 0.5f);
                    break;
                case Category.Face:
                    outline = outline && !m.name.ToLowerInvariant().Contains("mouth");
                    m.SetColor("_ShadowTint", new Color(0.97f, 0.82f, 0.83f));
                    m.SetFloat("_ShadeTexWeight", 0.6f);
                    m.SetFloat("_ShadowSaturation", 1.1f);
                    m.SetFloat("_ShadowThreshold", 0.47f);
                    m.SetFloat("_ShadowSoftness", 0.02f);
                    m.SetFloat("_DeepShadowThreshold", -1f);
                    m.SetFloat("_NormalStrength", 0f);
                    m.SetFloat("_TerminatorStrength", 0.22f);
                    m.SetFloat("_TerminatorWidth", 0.05f);
                    m.SetFloat("_RimStrength", 0.12f);
                    m.SetFloat("_OutlineWidth", 0.0016f);
                    m.SetFloat("_OutlineZPush", 0.012f);
                    m.SetFloat("_OutlineDarken", 0.36f);
                    m.SetFloat("_OutlineSaturation", 1.2f);
                    m.SetFloat("_LitBrightness", 1.05f);
                    break;
                case Category.Skin:
                    m.SetColor("_ShadowTint", new Color(0.97f, 0.83f, 0.85f));
                    m.SetFloat("_ShadeTexWeight", 0.6f);
                    m.SetFloat("_ShadowSaturation", 1.1f);
                    m.SetFloat("_ShadowThreshold", 0.5f);
                    m.SetFloat("_ShadowSoftness", 0.025f);
                    m.SetFloat("_DeepShadowThreshold", 0.18f);
                    m.SetFloat("_DeepShadowStrength", 0.88f);
                    m.SetFloat("_NormalStrength", 0.3f);
                    m.SetFloat("_TerminatorStrength", 0.22f);
                    m.SetFloat("_TerminatorWidth", 0.06f);
                    m.SetFloat("_RimStrength", 0.2f);
                    m.SetFloat("_RimThreshold", 0.8f);
                    m.SetFloat("_OutlineWidth", 0.0022f);
                    m.SetFloat("_OutlineDarken", 0.34f);
                    m.SetFloat("_OutlineSaturation", 1.2f);
                    m.SetFloat("_LitBrightness", 1.05f);
                    break;
                case Category.Hair:
                    m.SetColor("_ShadowTint", new Color(0.82f, 0.74f, 0.92f));
                    m.SetFloat("_LitBrightness", 1.25f);
                    m.SetFloat("_ShadeTexWeight", 0.5f);
                    m.SetFloat("_ShadowSaturation", 1.2f);
                    m.SetFloat("_ShadowThreshold", 0.52f);
                    m.SetFloat("_ShadowSoftness", 0.025f);
                    m.SetFloat("_DeepShadowThreshold", 0.25f);
                    m.SetFloat("_DeepShadowStrength", 0.8f);
                    m.SetFloat("_NormalStrength", 0f);
                    m.SetFloat("_HairSphereBlend", 0.65f);
                    m.SetFloat("_HairSpecStrength", 0.5f);
                    m.SetFloat("_HairSpecExponent", 140f);
                    m.SetFloat("_HairSpecThreshold", 0.55f);
                    m.SetFloat("_RimStrength", 0.3f);
                    m.SetFloat("_EmissionStrength", 1.0f);
                    m.SetFloat("_OutlineWidth", 0.0026f);
                    m.SetFloat("_OutlineDarken", 0.38f);
                    break;
                default:
                    m.SetColor("_ShadowTint", new Color(0.8f, 0.74f, 0.9f));
                    m.SetFloat("_LitBrightness", 1.1f);
                    m.SetFloat("_ShadeTexWeight", 0.5f);
                    m.SetFloat("_ShadowSaturation", 1.35f);
                    m.SetFloat("_ShadowThreshold", 0.5f);
                    m.SetFloat("_ShadowSoftness", 0.025f);
                    m.SetFloat("_DeepShadowThreshold", 0.2f);
                    m.SetFloat("_DeepShadowStrength", 0.8f);
                    m.SetFloat("_NormalStrength", 0.45f);
                    m.SetFloat("_RimStrength", 0.5f);
                    m.SetFloat("_RimThreshold", 0.66f);
                    m.SetColor("_RimColor", new Color(0.8f, 0.82f, 0.95f));
                    m.SetFloat("_OutlineWidth", 0.0026f);
                    m.SetFloat("_OutlineDarken", 0.4f);
                    break;
            }
            if (!outline) m.SetFloat("_OutlineWidth", 0f);
            m.SetShaderPassEnabled("Always", outline);
        }

        static void CopyColor(Material s, Material d, string n) { if (s.HasProperty(n)) d.SetColor(n, s.GetColor(n)); }
        static void CopyFloat(Material s, Material d, string n) { if (s.HasProperty(n)) d.SetFloat(n, s.GetFloat(n)); }
        static void CopyTex(Material s, Material d, string n) { if (s.HasProperty(n) && s.GetTexture(n) != null) d.SetTexture(n, s.GetTexture(n)); }

        // ---------------------------------------------------------------- 平滑法线（描边用）

        static Mesh BuildSmoothNormalMesh(Mesh src)
        {
            if (src == null || !src.isReadable)
            {
                Debug.LogWarning("[DesktopFigure] NPR: mesh not readable, outline uses raw normals: " + (src != null ? src.name : "null"));
                return null;
            }
            var verts = src.vertices;
            var normals = src.normals;
            if (normals == null || normals.Length != verts.Length) return null;

            var sum = new Dictionary<Vector3Int, Vector3>(verts.Length);
            var keys = new Vector3Int[verts.Length];
            const float q = 1f / 0.00005f; // 0.05 mm 量化
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                var k = new Vector3Int(Mathf.RoundToInt(v.x * q), Mathf.RoundToInt(v.y * q), Mathf.RoundToInt(v.z * q));
                keys[i] = k;
                sum.TryGetValue(k, out var acc);
                sum[k] = acc + normals[i];
            }
            var tangents = new Vector4[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var n = sum[keys[i]];
                n = n.sqrMagnitude > 1e-12f ? n.normalized : normals[i];
                tangents[i] = new Vector4(n.x, n.y, n.z, 1f);
            }
            var clone = Object.Instantiate(src);
            clone.name = src.name + " (NPR outline)";
            clone.tangents = tangents;
            return clone;
        }
    }
}
