// DesktopFigure NPR（绝区零 / 米哈游风格的卡通渲染，Built-in 管线）
// - 2~3 阶色阶 + 软边，暗部颜色来自贴图（乘暗部色调并提高饱和度，而不是灰色）
// - 脸：用头部朝向 + 球面化法线计算的“类 SDF”脸部阴影（干净、对称），眼睛/嘴/眉毛不受光
// - 皮肤：明暗交界处带一点红色（次表面散射感）
// - 头发：天使环（Kajiya-Kay 各向异性高光，发丝方向 ≈ 沿表面向下）
// - 边缘光（菲涅尔，只在受光侧）
// - 描边：背面外扩（inverted hull），宽度按视口高度比例 => 和镜头远近无关；可使用写在 tangent 里的平滑法线
// 透明窗口：所有 Pass 都使用 BlendOp Add, Max（alpha 取最大值），和 MToon 一样，保证窗口 alpha 正确。
Shader "DesktopFigure/NPR"
{
    Properties
    {
        _Color ("Base Color", Color) = (1, 1, 1, 1)
        _MainTex ("Base Texture", 2D) = "white" {}
        _ShadeColor ("Shade Color (MToon)", Color) = (1, 1, 1, 1)
        _ShadeTex ("Shade Texture (MToon)", 2D) = "white" {}
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1.0
        [HDR] _EmissionColor ("Emission Color", Color) = (0, 0, 0, 1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        _Category ("Category (0 cloth, 1 skin, 2 face, 3 hair, 4 unlit)", Float) = 0

        // 色阶
        _ShadowThreshold ("Shadow Threshold (half lambert)", Range(0, 1)) = 0.5
        _ShadowSoftness ("Shadow Softness", Range(0.001, 0.2)) = 0.025
        _DeepShadowThreshold ("Deep Shadow Threshold", Range(0, 1)) = 0.2
        _DeepShadowStrength ("Deep Shadow Brightness", Range(0, 1)) = 0.85
        _ShadowTint ("Shadow Tint", Color) = (0.8, 0.72, 0.88, 1)
        _ShadeTexWeight ("Use MToon Shade Texture", Range(0, 1)) = 0.5
        _ShadowSaturation ("Shadow Saturation", Range(0, 3)) = 1.35
        _LitBrightness ("Lit Brightness", Range(0, 2)) = 1.0
        _NormalStrength ("Normal Map Strength", Range(0, 1)) = 0.35

        // 脸
        _FaceShadowOffset ("Face Shadow Offset", Range(-1, 1)) = 0.0
        _FaceVertical ("Face Shadow Vertical Curvature", Range(0, 1)) = 0.3
        _FaceLightBias ("Face Light Bias (toward face front)", Range(0, 2)) = 0.4

        // 皮肤明暗交界
        _TerminatorColor ("Terminator Color", Color) = (1, 0.66, 0.58, 1)
        _TerminatorStrength ("Terminator Strength", Range(0, 1)) = 0.0
        _TerminatorWidth ("Terminator Width", Range(0.01, 0.3)) = 0.08

        // 边缘光
        _RimColor ("Rim Color", Color) = (1, 0.96, 0.92, 1)
        _RimStrength ("Rim Strength", Range(0, 2)) = 0.3
        _RimThreshold ("Rim Threshold", Range(0, 1)) = 0.72
        _RimSoftness ("Rim Softness", Range(0.001, 0.3)) = 0.04

        // 头发天使环
        _HairSpecColor ("Hair Highlight Color", Color) = (1, 0.97, 0.97, 1)
        _HairSpecStrength ("Hair Highlight Strength", Range(0, 2)) = 0.0
        _HairSpecExponent ("Hair Highlight Exponent", Range(1, 400)) = 120
        _HairSpecThreshold ("Hair Highlight Threshold", Range(0, 1)) = 0.5
        _HairSpecShift ("Hair Highlight Jitter", Range(0, 0.5)) = 0.08
        _HairSphereBlend ("Hair Normal -> Head Sphere", Range(0, 1)) = 0.0
        _HairSpecLift ("Hair Highlight Lift (moves ring up)", Range(-1, 2)) = 0.5

        _EmissionStrength ("Emission Strength", Range(0, 2)) = 1.0
        _UnlitBrightness ("Unlit Brightness", Range(0, 2)) = 1.0

        // 描边
        _OutlineWidth ("Outline Width (fraction of viewport height)", Range(0, 0.02)) = 0.0025
        _OutlineMinPixels ("Outline Min Width (px)", Range(0, 4)) = 1.0
        _OutlineZPush ("Outline Push Back (m, view space)", Range(0, 0.05)) = 0.004
        _OutlineDarken ("Outline Brightness", Range(0, 1)) = 0.45
        _OutlineSaturation ("Outline Saturation", Range(0, 3)) = 1.5
        _UseSmoothNormal ("Smooth Normal In Tangent", Float) = 0

        // 渲染状态（和 MToon10 相同的含义）
        _AlphaMode ("alphaMode", Int) = 0
        _M_CullMode ("_CullMode", Float) = 2.0
        _M_SrcBlend ("_SrcBlend", Float) = 1.0
        _M_DstBlend ("_DstBlend", Float) = 0.0
        _M_ZWrite ("_ZWrite", Float) = 1.0
        _M_AlphaToMask ("_AlphaToMask", Float) = 0.0
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex; float4 _MainTex_ST;
    sampler2D _ShadeTex;
    sampler2D _BumpMap;
    sampler2D _EmissionMap;
    float4 _Color, _ShadeColor, _EmissionColor, _ShadowTint, _TerminatorColor, _RimColor, _HairSpecColor;
    float4 _LightColor0;
    float _BumpScale, _Cutoff, _Category;
    float _ShadowThreshold, _ShadowSoftness, _DeepShadowThreshold, _DeepShadowStrength, _ShadeTexWeight, _ShadowSaturation, _LitBrightness, _NormalStrength;
    float _FaceShadowOffset, _FaceVertical, _FaceLightBias;
    float _TerminatorStrength, _TerminatorWidth;
    float _RimStrength, _RimThreshold, _RimSoftness;
    float _HairSpecStrength, _HairSpecExponent, _HairSpecThreshold, _HairSpecShift, _HairSphereBlend, _HairSpecLift;
    float _EmissionStrength, _UnlitBrightness;
    float _OutlineWidth, _OutlineMinPixels, _OutlineZPush, _OutlineDarken, _OutlineSaturation, _UseSmoothNormal;

    // 由 NprStyle.cs 每帧设置
    float4 _DF_HeadCenter;    // xyz = 头部（脸）中心，世界坐标
    float4 _DF_HeadForward;   // xyz = 脸朝向
    float4 _DF_HeadRight;
    float4 _DF_HeadUp;
    float4 _DF_ViewportPx;    // x = 视口宽(px)，y = 视口高(px)
    float4 _DF_KeyLightDir;   // xyz = 指向主光的方向（相对镜头固定：右上前方），w = 1 有效

    float3 UnpackNormalScaled(float4 packed, float scale)
    {
        float3 n = UnpackNormal(packed);
        n.xy *= scale;
        return normalize(n);
    }

    float3 Saturation(float3 c, float s)
    {
        float l = dot(c, float3(0.2126, 0.7152, 0.0722));
        return max(0, lerp(l.xxx, c, s));
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        // ---------------- 主体 ----------------
        Pass
        {
            Name "NPR_FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            Cull [_M_CullMode]
            Blend [_M_SrcBlend] [_M_DstBlend]
            ZWrite [_M_ZWrite]
            BlendOp Add, Max
            AlphaToMask [_M_AlphaToMask]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local __ _ALPHATEST_ON _ALPHABLEND_ON

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag(v2f i, float facing : VFACE) : SV_Target
            {
                float4 tex = tex2D(_MainTex, i.uv) * _Color;
                float alpha = tex.a;
            #if defined(_ALPHATEST_ON)
                clip(alpha - _Cutoff);
                alpha = 1;
            #elif !defined(_ALPHABLEND_ON)
                alpha = 1;
            #endif
                float3 baseCol = tex.rgb;
                float3 emission = tex2D(_EmissionMap, i.uv).rgb * _EmissionColor.rgb * _EmissionStrength;

                // 不受光：眼睛、眉毛、睫毛、高光等
                if (_Category > 3.5)
                {
                    return float4(baseCol * _UnlitBrightness + emission, alpha);
                }

                float3 N = normalize(i.worldNormal) * (facing > 0 ? 1.0 : -1.0);

                // 法线贴图（没有切线：用屏幕空间导数重建切线空间）
                if (_NormalStrength > 0.001 && (_Category < 1.5 || _Category > 2.5))
                {
                    float3 dp1 = ddx(i.worldPos), dp2 = ddy(i.worldPos);
                    float2 duv1 = ddx(i.uv), duv2 = ddy(i.uv);
                    float3 dp2perp = cross(dp2, N);
                    float3 dp1perp = cross(N, dp1);
                    float3 T = dp2perp * duv1.x + dp1perp * duv2.x;
                    float3 B = dp2perp * duv1.y + dp1perp * duv2.y;
                    float m = max(dot(T, T), dot(B, B));
                    if (m > 1e-12 && _NormalStrength > 0.001)
                    {
                        float invmax = rsqrt(m);
                        float3 nTS = UnpackNormalScaled(tex2D(_BumpMap, i.uv), _BumpScale * _NormalStrength);
                        N = normalize(nTS.x * T * invmax + nTS.y * B * invmax + nTS.z * N);
                    }
                }

                // 头发：法线向“头部球面”靠拢 => 大块、干净的明暗（避免发丝卡片法线造成的碎阴影）
                bool headValid = dot(_DF_HeadForward.xyz, _DF_HeadForward.xyz) > 0.5;
                if (_HairSphereBlend > 0.001 && headValid)
                {
                    float3 sph = normalize(i.worldPos - _DF_HeadCenter.xyz + float3(0, 1e-4, 0));
                    N = normalize(lerp(N, sph, _HairSphereBlend));
                }

                float3 L = _DF_KeyLightDir.w > 0.5 ? normalize(_DF_KeyLightDir.xyz) : normalize(_WorldSpaceLightPos0.xyz);
                float3 lightCol = _LightColor0.rgb;
                if (dot(lightCol, lightCol) < 1e-6) lightCol = float3(1, 1, 1);
                float3 V = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
                float NdotL = dot(N, L);
                float h = NdotL * 0.5 + 0.5;

                // 脸：头部空间里的球面化法线（只看水平方向） => 干净的、随光照角度移动的竖直阴影边界，类似 SDF 脸部阴影
                bool isFace = _Category > 1.5 && _Category < 2.5;
                if (isFace && dot(_DF_HeadForward.xyz, _DF_HeadForward.xyz) > 0.5)
                {
                    float3 fwd = _DF_HeadForward.xyz, right = _DF_HeadRight.xyz, up = _DF_HeadUp.xyz;
                    float3 d = i.worldPos - _DF_HeadCenter.xyz;
                    float3 lp = float3(dot(d, right), dot(d, up) * _FaceVertical, dot(d, fwd));
                    float3 sn = normalize(lp + float3(0, 0, 1e-4));
                    float3 lh = float3(dot(L, right), dot(L, up) * _FaceVertical, dot(L, fwd));
                    lh = normalize(lh + float3(0, 0, 1e-4));
                    lh = normalize(lh + float3(0, 0, _FaceLightBias));
                    float fd = dot(sn, lh);
                    h = fd * 0.5 + 0.5 + _FaceShadowOffset;
                }

                float s = _ShadowSoftness;
                float lit = smoothstep(_ShadowThreshold - s, _ShadowThreshold + s, h);
                float deepMask = smoothstep(_DeepShadowThreshold - s, _DeepShadowThreshold + s, h);

                float3 shadeTex = tex2D(_ShadeTex, i.uv).rgb * _ShadeColor.rgb;
                float3 shadowCol = lerp(baseCol, baseCol * shadeTex, _ShadeTexWeight) * _ShadowTint.rgb;
                shadowCol = Saturation(shadowCol, _ShadowSaturation);
                float3 deepCol = shadowCol * _DeepShadowStrength;
                float3 litCol = baseCol * lightCol * _LitBrightness;

                float3 col = lerp(lerp(deepCol, shadowCol, deepMask), litCol, lit);

                // 皮肤：明暗交界处偏红（次表面散射感）
                if (_TerminatorStrength > 0.001)
                {
                    float band = saturate(1 - abs(h - _ShadowThreshold) / _TerminatorWidth);
                    band *= band;
                    col = lerp(col, col * _TerminatorColor.rgb * 1.25, band * _TerminatorStrength);
                }

                // 头发天使环：发丝方向 ≈ 头部“向上”方向投影到表面
                if (_HairSpecStrength > 0.001)
                {
                    float3 up = dot(_DF_HeadUp.xyz, _DF_HeadUp.xyz) > 0.5 ? _DF_HeadUp.xyz : float3(0, 1, 0);
                    float3 T = up - N * dot(N, up);
                    T = normalize(T + float3(0, 1e-4, 0));
                    T = normalize(T + N * _HairSpecLift);
                    float3 H = normalize(L + V);
                    float jitter = (dot(tex.rgb, float3(0.3, 0.59, 0.11)) - 0.5) * _HairSpecShift;
                    float th = dot(T, H) + jitter;
                    float kk = sqrt(saturate(1 - th * th));
                    float spec = pow(kk, _HairSpecExponent);
                    float ring = smoothstep(_HairSpecThreshold - 0.08, _HairSpecThreshold + 0.08, spec) * saturate(lit + 0.25);
                    float3 hc = lerp(baseCol * 1.8 + 0.08, _HairSpecColor.rgb, 0.45);
                    col = lerp(col, hc, ring * _HairSpecStrength);
                }

                // 边缘光（只在受光一侧）
                if (_RimStrength > 0.001)
                {
                    float fres = 1 - saturate(dot(N, V));
                    float rim = smoothstep(_RimThreshold - _RimSoftness, _RimThreshold + _RimSoftness, fres);
                    float3 Lv = normalize(L - V * dot(L, V) + 1e-4);    // 光照方向在屏幕平面上的投影
                    rim *= smoothstep(-0.1, 0.35, dot(N, Lv));
                    col += rim * _RimStrength * lerp(_RimColor.rgb, baseCol * _RimColor.rgb * 1.6, 0.5);
                }

                col += emission;
                return float4(col, alpha);
            }
            ENDCG
        }

        // ---------------- 描边（背面外扩）----------------
        Pass
        {
            Name "NPR_OUTLINE"
            Tags { "LightMode" = "Always" }
            Cull Front
            Blend [_M_SrcBlend] [_M_DstBlend]
            ZWrite [_M_ZWrite]
            BlendOp Add, Max
            AlphaToMask [_M_AlphaToMask]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local __ _ALPHATEST_ON _ALPHABLEND_ON

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 n = _UseSmoothNormal > 0.5 ? v.tangent.xyz : v.normal;
                // 视空间里往后推一点：避免嘴角/鼻子等凹处的外扩面穿出来（轮廓处背后是空的，不受影响）
                float3 viewPos = UnityObjectToViewPos(v.vertex.xyz);
                viewPos.z -= _OutlineZPush;
                float4 pos = mul(UNITY_MATRIX_P, float4(viewPos, 1));
                float3 nView = mul((float3x3)UNITY_MATRIX_IT_MV, n);
                float2 nClip = float2(nView.x * UNITY_MATRIX_P[0][0], nView.y * UNITY_MATRIX_P[1][1]);
                float aspect = abs(UNITY_MATRIX_P[1][1] / UNITY_MATRIX_P[0][0]);
                // 像素空间里的方向
                float2 dirPx = float2(nClip.x * aspect, nClip.y);
                float len = length(dirPx);
                dirPx = len > 1e-6 ? dirPx / len : float2(0, 0);
                float vpH = max(_DF_ViewportPx.y, 1);
                float widthPx = max(_OutlineWidth * vpH, _OutlineMinPixels);
                if (_OutlineWidth <= 0) widthPx = 0;
                float2 ndcOff = float2(dirPx.x / aspect, dirPx.y) * (widthPx * 2.0 / vpH);
                pos.xy += ndcOff * pos.w;
                o.pos = pos;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_OutlineWidth <= 0) discard;
                float4 tex = tex2D(_MainTex, i.uv) * _Color;
                float alpha = tex.a;
            #if defined(_ALPHATEST_ON)
                clip(alpha - _Cutoff);
                alpha = 1;
            #elif !defined(_ALPHABLEND_ON)
                alpha = 1;
            #endif
                float3 c = tex.rgb * _ShadowTint.rgb;
                c = Saturation(c, _OutlineSaturation) * _OutlineDarken;
                return float4(c, alpha);
            }
            ENDCG
        }
    }
    FallBack Off
}
