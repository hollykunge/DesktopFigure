using UnityEngine;
using UniVRM10;

namespace DesktopFigure
{
    /// <summary>
    /// 程序化待机动作：放下手臂、呼吸、轻微摇摆、眨眼，头部/眼睛看向鼠标。
    /// 通过 UniVRM 1.0 的 ControlRig（归一化骨骼，T-Pose 时 localRotation = identity）驱动，
    /// 在 Vrm10Instance（ExecutionOrder 11000, LateUpdate）之前写入，因此对所有 VRoid 模型通用。
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class IdleMotion : MonoBehaviour
    {
        [Header("手臂")]
        [Tooltip("上臂从 T-Pose 向下放的角度")]
        public float armDownAngle = 70f;
        public float elbowBend = 12f;

        [Header("呼吸 / 摇摆")]
        public float breathPeriod = 4.0f;
        public float breathAngle = 1.5f;
        public float swayPeriod = 7.0f;
        public float swayAngle = 1.5f;

        [Header("看向鼠标")]
        public bool lookAtCursor = true;
        public float maxYaw = 35f;
        public float maxPitch = 20f;
        public float lookSmooth = 6f;

        [Header("眨眼")]
        public bool autoBlink = true;

        Vrm10Instance _target;
        Transform _lookAtTarget;
        float _yaw, _pitch;
        float _nextBlink = 2f;
        float _blinkT = -1f;
        float _time;

        public Transform LookAtTarget => _lookAtTarget;

        /// <summary>+1 = 模型面向根节点 +Z（UniVRM 标准），-1 = 面向 -Z。加载时根据左右上臂位置自动判断。</summary>
        public float Facing { get; private set; } = 1f;

        void Awake()
        {
            var go = new GameObject("LookAtTarget");
            go.transform.SetParent(transform, false);
            _lookAtTarget = go.transform;
        }

        public void SetTarget(Vrm10Instance instance)
        {
            _target = instance;
            Facing = 1f;
            if (_target != null)
            {
                // T-Pose 下右上臂应在左上臂的“右侧”。面向 +Z 时右侧是 +X。
                if (_target.TryGetBoneTransform(HumanBodyBones.LeftUpperArm, out var l) &&
                    _target.TryGetBoneTransform(HumanBodyBones.RightUpperArm, out var r) && l != null && r != null)
                {
                    var root = _target.transform;
                    float dx = root.InverseTransformPoint(r.position).x - root.InverseTransformPoint(l.position).x;
                    Facing = dx >= 0f ? 1f : -1f;
                }
                _target.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
                _target.LookAtTarget = lookAtCursor ? _lookAtTarget : null;
            }
        }

        void Update()
        {
            if (_target == null) return;

            Vrm10Runtime runtime;
            try
            {
                runtime = _target.Runtime;
            }
            catch (System.Exception)
            {
                return;
            }
            var rig = runtime?.ControlRig;
            if (rig == null) return;

            _time += Time.deltaTime;
            float breath = Mathf.Sin(_time * Mathf.PI * 2f / Mathf.Max(0.1f, breathPeriod));
            float sway = Mathf.Sin(_time * Mathf.PI * 2f / Mathf.Max(0.1f, swayPeriod));

            // ---- 躯干：呼吸 + 摇摆 ----
            float f = Facing;
            SetLocal(rig, HumanBodyBones.Spine, Quaternion.Euler(f * breath * breathAngle * 0.5f, 0f, sway * swayAngle * 0.6f));
            SetLocal(rig, HumanBodyBones.Chest, Quaternion.Euler(-f * breath * breathAngle, 0f, sway * swayAngle * 0.4f));
            SetLocal(rig, HumanBodyBones.UpperChest, Quaternion.Euler(-f * breath * breathAngle * 0.5f, 0f, 0f));
            SetLocal(rig, HumanBodyBones.Hips, Quaternion.Euler(0f, 0f, -sway * swayAngle * 0.5f));

            // ---- 手臂：从 T-Pose 放下，随呼吸轻微摆动 ----
            // 模型面向 +Z 时左臂沿 -X、右臂沿 +X；绕 Z 轴旋转使手臂朝下（面向 -Z 时符号相反）。
            float arm = armDownAngle + breath * 1.5f;
            SetLocal(rig, HumanBodyBones.LeftShoulder, Quaternion.identity);
            SetLocal(rig, HumanBodyBones.RightShoulder, Quaternion.identity);
            SetLocal(rig, HumanBodyBones.LeftUpperArm, Quaternion.Euler(0f, 0f, f * arm));
            SetLocal(rig, HumanBodyBones.RightUpperArm, Quaternion.Euler(0f, 0f, -f * arm));
            SetLocal(rig, HumanBodyBones.LeftLowerArm, Quaternion.Euler(0f, elbowBend, 0f));
            SetLocal(rig, HumanBodyBones.RightLowerArm, Quaternion.Euler(0f, -elbowBend, 0f));

            // ---- 看向鼠标（头/脖子 + VRM LookAt 眼睛）----
            UpdateLookAt(rig);

            // ---- 眨眼 ----
            if (autoBlink) UpdateBlink(runtime);
        }

        void UpdateLookAt(Vrm10RuntimeControlRig rig)
        {
            var cam = Camera.main;
            float targetYaw = 0f, targetPitch = 0f;
            var head = rig.GetBoneTransform(HumanBodyBones.Head);

            if (lookAtCursor && cam != null && head != null)
            {
                Vector2 cursor = TransparentWindow.GetCursorUnityPosition();
                var ray = cam.ScreenPointToRay(new Vector3(cursor.x, cursor.y, 0f));
                // 在角色脸前方 0.6m 处放一个“视线目标”平面
                var root = _target.transform;
                Vector3 faceForward = root.forward * Facing;
                Vector3 faceRight = root.right * Facing;
                var planePoint = head.position + faceForward * 0.6f;
                var plane = new Plane(-faceForward, planePoint);
                Vector3 p = plane.Raycast(ray, out float dist) ? ray.GetPoint(dist) : head.position + faceForward;
                _lookAtTarget.position = p;

                Vector3 v = p - head.position;
                float lx = Vector3.Dot(v, faceRight);
                float lz = Vector3.Dot(v, faceForward);
                float ly = Vector3.Dot(v, root.up);
                targetYaw = Mathf.Atan2(lx, Mathf.Max(0.01f, lz)) * Mathf.Rad2Deg;
                float horiz = new Vector2(lx, lz).magnitude;
                // Euler X 为正 = 低头（面向 +Z 时）；面向 -Z 时符号相反
                targetPitch = -Mathf.Atan2(ly, Mathf.Max(0.01f, horiz)) * Mathf.Rad2Deg * Facing;
                targetYaw = Mathf.Clamp(targetYaw, -maxYaw, maxYaw);
                targetPitch = Mathf.Clamp(targetPitch, -maxPitch, maxPitch);

                // 角色转身（侧面/背面朝向镜头）时逐渐停止看鼠标，避免扭头和旋转打架
                Vector3 toCam = cam.transform.position - head.position;
                toCam.y = 0f;
                Vector3 ff = faceForward;
                ff.y = 0f;
                float facingCam = (toCam.sqrMagnitude > 1e-6f && ff.sqrMagnitude > 1e-6f)
                    ? Vector3.Dot(ff.normalized, toCam.normalized) : 1f;
                float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.75f, facingCam));
                var view = FigureViewController.Instance;
                if (view != null && view.IsRotatingByUser) w = 0f;
                targetYaw *= w;
                targetPitch *= w;
                _lookAtTarget.position = Vector3.Lerp(head.position + faceForward * 2f, p, w);
            }

            float k = 1f - Mathf.Exp(-lookSmooth * Time.deltaTime);
            _yaw = Mathf.Lerp(_yaw, targetYaw, k);
            _pitch = Mathf.Lerp(_pitch, targetPitch, k);

            SetLocal(rig, HumanBodyBones.Neck, Quaternion.Euler(_pitch * 0.4f, _yaw * 0.4f, 0f));
            SetLocal(rig, HumanBodyBones.Head, Quaternion.Euler(_pitch * 0.6f, _yaw * 0.6f, 0f));

            if (_target.LookAtTarget != (lookAtCursor ? _lookAtTarget : null))
            {
                _target.LookAtTarget = lookAtCursor ? _lookAtTarget : null;
            }
        }

        void UpdateBlink(Vrm10Runtime runtime)
        {
            var expression = runtime.Expression;
            if (expression == null) return;

            if (_blinkT < 0f && _time >= _nextBlink)
            {
                _blinkT = 0f;
            }

            float w = 0f;
            if (_blinkT >= 0f)
            {
                _blinkT += Time.deltaTime;
                const float close = 0.06f, hold = 0.04f, open = 0.10f;
                if (_blinkT < close) w = _blinkT / close;
                else if (_blinkT < close + hold) w = 1f;
                else if (_blinkT < close + hold + open) w = 1f - (_blinkT - close - hold) / open;
                else
                {
                    w = 0f;
                    _blinkT = -1f;
                    _nextBlink = _time + Random.Range(2.0f, 6.0f);
                }
            }
            try
            {
                expression.SetWeight(ExpressionKey.Blink, w);
            }
            catch (System.Exception)
            {
                autoBlink = false; // 模型没有 blink 表情
            }
        }

        static void SetLocal(Vrm10RuntimeControlRig rig, HumanBodyBones bone, Quaternion rot)
        {
            var t = rig.GetBoneTransform(bone);
            if (t != null) t.localRotation = rot;
        }
    }
}
