using System;
using System.Windows;
using LemonLite.LiquidGlass;

namespace LemonLite.LiquidGlass
{
    /// <summary>
    /// toggle 渲染状态快照。渲染器（WPF ShaderEffect + 桌面截图）每帧读取此结构。
    /// 字段语义与原 LiquidGlassDemo.LiquidToggleState 一致，仅改用 WPF Vector。
    /// </summary>
    public struct LiquidToggleState
    {
        public Vector TrackScreenPos;   // 控件内坐标 px（左上角）
        public Vector TrackSize;        // px
        public Vector ThumbScreenPos;   // 缩放前 thumb 左上角 px
        public Vector ThumbSize;        // px（缩放前）
        public Vector ThumbScale;       // scaleX, scaleY（速度+按压+弹簧动画）
        public float Fraction;          // 0..1
        public float PressProgress;     // 0..1
        public float Velocity;          // fraction 速度（px/sec 缩放）
        public Vector4 TrackColorOff;   // rgba 0..1
        public Vector4 TrackColorOn;
        public float BlurRadius;        // 8dp * (1 - pressProgress) px
        public float RefractionHeight;  // 5dp * pressProgress px
        public float RefractionAmount;  // 10dp * pressProgress px
        public float WhiteAlpha;        // 1 - pressProgress
        public float HighlightAlpha;    // pressProgress
        public float InnerShadowRadius; // 4dp * pressProgress px
        public float InnerShadowAlpha;  // pressProgress
        public float ShadowRadius;      // 4dp px
        public float ShadowAlpha;       // 0.05
        public Vector ShadowOffset;     // (0, 2dp) px
    }

    /// <summary>
    /// WPF 4 阶段向量，与 System.Numerics.Vector4 布局一致，用于颜色 (r,g,b,a)。
    /// 单独定义避免引入 System.Numerics 依赖。
    /// </summary>
    public struct Vector4
    {
        public float X, Y, Z, W;
        public Vector4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public float this[int i]
        {
            get => i switch { 0 => X, 1 => Y, 2 => Z, _ => W };
            set { switch (i) { case 0: X = value; break; case 1: Y = value; break; case 2: Z = value; break; default: W = value; break; } }
        }
    }

    /// <summary>
    /// 移植自 LiquidGlassDemo.LiquidGlass.LiquidToggle。
    /// 关键修改：去掉 ImGui 输入读取，改为通过 UpdateInput/UpdatePhysics 两段式 API。
    /// WPF 控件在 OnMouse* 回调里推送输入，在 CompositionTarget.Rendering 里调用 UpdatePhysics 推进弹簧。
    /// </summary>
    public sealed class LiquidToggle
    {
        // 布局（dp），构造时按 dpiScale 缩放。
        // track 56x28 + shadow padding 8 = DesiredSize 64x36，适合 48px 高 Border
        private const float TrackWidthDp  = 56f;
        private const float TrackHeightDp = 28f;
        private const float ThumbWidthDp  = 36f;
        private const float ThumbHeightDp = 22f;
        private const float PaddingDp     = 2f;
        private const float DragWidthDp   = 16f;  // TrackWidth - ThumbWidth - 2*Padding = 56-36-4 = 16

        // 弹簧参数 —— 对应 Android DampedDragAnimation / Compose spring specs。
        private const float FractionStiffness = 1000f;
        private const float FractionDamping   = 63.25f;
        private const float PressStiffness    = 1000f;
        private const float PressDamping      = 63.25f;
        private const float ScaleXStiffness   = 250f;
        private const float ScaleXDamping     = 18.97f;
        private const float ScaleYStiffness   = 250f;
        private const float ScaleYDamping     = 22.14f;

        private const float InitialScale = 1.0f;
        private const float PressedScale = 1.5f;

        private const float DragThresholdPx = 4f;

        private const float ShadowRadiusDp = 4f;
        private const float ShadowAlphaConst = 0.05f;
        private const float ShadowOffsetYDp = 2f;

        private const float RefractionHeightDp = 3f;
        private const float RefractionAmountDp = 5f;
        private const float BlurRadiusDp = 6f;
        private const float InnerShadowRadiusDp = 4f;

        public bool IsOn;
        public LiquidToggleState State;

        private float _dpiScale;
        private Vector _trackSizePx;
        private Vector _thumbSizePx;
        private float  _paddingPx;
        private float  _dragWidthPx;

        /// <summary>当前 DPI 缩放系数（96 DPI = 1.0）。</summary>
        public float DpiScale => _dpiScale;

        /// <summary>阴影外扩量（单边 px=DIP），用于控件布局留白。</summary>
        public float ShadowPadding => ShadowRadiusDp * _dpiScale;

        /// <summary>thumb 按压缩放外扩量（单边 px=DIP），用于控件布局留白。</summary>
        public float PressExpand => (float)(_thumbSizePx.X * 0.5 * (PressedScale - 1f));

        /// <summary>thumb 按压缩放 Y 方向外扩量（单边 px=DIP），用于 track 垂直定位。</summary>
        public float PressExpandY => (float)(_thumbSizePx.Y * 0.5 * (PressedScale - 1f));

        /// <summary>当前是否处于按下状态（外部查询用）。</summary>
        public bool IsPressed => _pressed;

        /// <summary>所有弹簧是否都已稳定（外部决定是否停止动画 hook）。</summary>
        public bool IsSettledState()
        {
            return _fractionSpring.IsSettled(0.001f)
                && _pressSpring.IsSettled(0.001f)
                && _scaleXSpring.IsSettled(0.001f)
                && _scaleYSpring.IsSettled(0.001f)
                && !_pressed;
        }

        /// <summary>当外部通过 DP 设置 IsOn 时，同步弹簧 target（不改变当前位置，让弹簧平滑过渡）。</summary>
        public void SyncTargetToIsOn()
        {
            _fractionSpring.Target = IsOn ? 1f : 0f;
        }

        /// <summary>在 DPI 变化或初始化后重新计算缩放尺寸（保留弹簧状态）。</summary>
        public void ReinitAtDpi(float dpiScale)
        {
            _dpiScale = dpiScale;
            _trackSizePx = new Vector(TrackWidthDp, TrackHeightDp) * dpiScale;
            _thumbSizePx = new Vector(ThumbWidthDp, ThumbHeightDp) * dpiScale;
            _paddingPx   = PaddingDp * dpiScale;
            _dragWidthPx = DragWidthDp * dpiScale;
            State.ShadowRadius = ShadowRadiusDp * dpiScale;
            State.ShadowOffset = new Vector(0, ShadowOffsetYDp * dpiScale);
        }

        private Spring _fractionSpring;
        private Spring _pressSpring;
        private Spring _scaleXSpring;
        private Spring _scaleYSpring;

        private bool  _pressed;
        private bool  _dragging;
        private float _dragStartMouseX;
        private float _dragStartFraction;
        private float _lastFraction;
        private float _smoothedVelocity;

        // 当前指针位置（控件内 px），由 WPF 控件 OnMouseMove 推送
        private Vector _mousePos;

        public LiquidToggle(float dpiScale = 1f)
        {
            _dpiScale = dpiScale;
            _trackSizePx = new Vector(TrackWidthDp, TrackHeightDp) * dpiScale;
            _thumbSizePx = new Vector(ThumbWidthDp, ThumbHeightDp) * dpiScale;
            _paddingPx   = PaddingDp * dpiScale;
            _dragWidthPx = DragWidthDp * dpiScale;

            IsOn = false;
            _fractionSpring = new Spring(0f, FractionStiffness, FractionDamping, 1f);
            _pressSpring    = new Spring(0f, PressStiffness, PressDamping, 1f);
            _scaleXSpring   = new Spring(InitialScale, ScaleXStiffness, ScaleXDamping, 1f);
            _scaleYSpring   = new Spring(InitialScale, ScaleYStiffness, ScaleYDamping, 1f);
            _lastFraction = 0f;

            State = default;
            State.TrackColorOff = new Vector4(0x78 / 255f, 0x78 / 255f, 0x78 / 255f, 0.20f);
            State.TrackColorOn  = new Vector4(0x34 / 255f, 0xC7 / 255f, 0x59 / 255f, 1.0f);
            State.ThumbScale = new Vector(1, 1);
            State.ShadowRadius = ShadowRadiusDp * dpiScale;
            State.ShadowAlpha  = ShadowAlphaConst;
            State.ShadowOffset = new Vector(0, ShadowOffsetYDp * dpiScale);
        }

        /// <summary>
        /// 布局尺寸（含阴影外扩 + thumb 按压缩放余量），用于 WPF 控件 MeasureOverride。
        /// thumb 按压时缩放 1.5x，会向四周外扩 (thumbSize*0.5*(1.5-1))，需预留空间避免 ClipToBounds 截断。
        /// </summary>
        public Vector DesiredSize
        {
            get
            {
                // thumb 按压缩放外扩量（单边）
                double pressExpandX = _thumbSizePx.X * 0.5 * (PressedScale - 1f);
                double pressExpandY = _thumbSizePx.Y * 0.5 * (PressedScale - 1f);
                double shadowPad = ShadowRadiusDp * _dpiScale;
                // 控件尺寸 = track + 2*(shadow + pressExpand) + 一点额外余量
                double w = _trackSizePx.X + 2.0 * (shadowPad + pressExpandX) + 2.0 * _dpiScale;
                double h = _trackSizePx.Y + 2.0 * (shadowPad + pressExpandY) + 2.0 * _dpiScale;
                return new Vector(w, h);
            }
        }

        /// <summary>由 WPF 控件推送当前鼠标位置（控件本地坐标 px）。</summary>
        public void SetMousePos(Vector pos) => _mousePos = pos;

        /// <summary>左键按下。trackPos 为 track 左上角控件内 px。</summary>
        public void Press(Vector trackPos)
        {
            Vector thumbPos = ComputeThumbPos(trackPos, _fractionSpring.Position);
            bool hitTrack = PointInRect(_mousePos, trackPos, _trackSizePx);
            bool hitThumb = PointInRect(_mousePos, thumbPos, _thumbSizePx);
            if (hitThumb || hitTrack)
            {
                _pressed = true;
                _dragging = false;
                _dragStartMouseX = (float)_mousePos.X;
                _dragStartFraction = _fractionSpring.Position;
                _pressSpring.Target = 1f;
                _scaleXSpring.Target = PressedScale;
                _scaleYSpring.Target = PressedScale;
            }
        }

        /// <summary>鼠标移动期间按下状态更新。返回是否处于拖拽中。</summary>
        public bool MoveDuringPress()
        {
            if (!_pressed) return false;
            float dx = (float)_mousePos.X - _dragStartMouseX;
            if (!_dragging && Math.Abs(dx) > DragThresholdPx)
            {
                _dragging = true;
            }
            if (_dragging)
            {
                float newFraction = Math.Clamp(_dragStartFraction + dx / _dragWidthPx, 0f, 1f);
                _fractionSpring.Position = newFraction;
                _fractionSpring.Target = newFraction;
            }
            return _dragging;
        }

        /// <summary>左键释放。返回该帧是否提交（点击切换或拖拽吸附）。trackPos 同上。</summary>
        public bool Release(Vector trackPos)
        {
            if (!_pressed) return false;
            _pressed = false;
            _pressSpring.Target = 0f;
            _scaleXSpring.Target = InitialScale;
            _scaleYSpring.Target = InitialScale;

            bool committed;
            if (_dragging)
            {
                bool wasOn = _fractionSpring.Position >= 0.5f;
                IsOn = wasOn;
                _fractionSpring.Target = wasOn ? 1f : 0f;
                _fractionSpring.Velocity = _smoothedVelocity;
                IsDraggingLastRelease = true;
                committed = true;
            }
            else
            {
                IsOn = !IsOn;
                _fractionSpring.Target = IsOn ? 1f : 0f;
                IsDraggingLastRelease = false;
                committed = true;
            }
            _dragging = false;
            return committed;
        }

        /// <summary>上次 Release 是否为拖拽切换（用于 LiquidGlassToggle 决定同步方式）。</summary>
        public bool IsDraggingLastRelease { get; private set; }

        /// <summary>
        /// 显示模式下由父 ToggleButton.IsPressed 驱动的按压触发。
        /// 只触发按压/缩放弹簧（thumb 放大），不进入拖拽逻辑，不改变 IsOn。
        /// </summary>
        public void TriggerPress()
        {
            _pressSpring.Target = 1f;
            _scaleXSpring.Target = PressedScale;
            _scaleYSpring.Target = PressedScale;
        }

        /// <summary>显示模式下释放：恢复按压/缩放弹簧。IsOn 由外部双向绑定驱动。</summary>
        public void TriggerRelease()
        {
            _pressSpring.Target = 0f;
            _scaleXSpring.Target = InitialScale;
            _scaleYSpring.Target = InitialScale;
        }

        /// <summary>
        /// 推进弹簧物理，并填充 <see cref="State"/>。dt 单位秒。
        /// trackPos 为 track 左上角控件内 px。
        /// </summary>
        public void UpdatePhysics(Vector trackPos, float dt)
        {
            if (!_dragging)
            {
                _fractionSpring.Update(dt);
            }
            _pressSpring.Update(dt);
            _scaleXSpring.Update(dt);
            _scaleYSpring.Update(dt);

            float fraction = _fractionSpring.Position;
            float pressProgress = _pressSpring.Position;

            // EMA 平滑速度（fraction 单位/秒），消除慢拖抖动。
            float rawVel = (fraction - _lastFraction) / Math.Max(dt, 1e-4f);
            float alpha = 1f - MathF.Exp(-dt / 0.05f);  // 50ms 时间常数
            _smoothedVelocity += (rawVel - _smoothedVelocity) * alpha;
            float velocity = _smoothedVelocity / 50f;

            // 速度拉伸（Android layerBlock）: scaleX /= 1 - clamp(v*0.75, -0.2, 0.2); scaleY *= 1 - clamp(v*0.25, -0.2, 0.2)
            float vx = Math.Clamp(velocity * 0.75f, -0.2f, 0.2f);
            float vy = Math.Clamp(velocity * 0.25f, -0.2f, 0.2f);
            float animatedScaleX = _scaleXSpring.Position;
            float animatedScaleY = _scaleYSpring.Position;
            float scaleX = animatedScaleX / (1f - vx);
            float scaleY = animatedScaleY * (1f - vy);
            scaleX = Math.Max(scaleX, 0.1f);
            scaleY = Math.Max(scaleY, 0.1f);

            Vector finalThumbPos = ComputeThumbPos(trackPos, fraction);

            // 填充渲染状态。
            State.TrackScreenPos = trackPos;
            State.TrackSize = _trackSizePx;
            State.ThumbScreenPos = finalThumbPos;
            State.ThumbSize = _thumbSizePx;
            State.ThumbScale = new Vector(scaleX, scaleY);
            State.Fraction = fraction;
            State.PressProgress = pressProgress;
            State.Velocity = velocity;

            // 按压驱动玻璃：静止时白色（pressProgress=0），按压时折射镜头（pressProgress=1）。
            State.BlurRadius        = BlurRadiusDp * (1f - pressProgress) * _dpiScale;
            State.RefractionHeight  = RefractionHeightDp * pressProgress * _dpiScale;
            State.RefractionAmount  = RefractionAmountDp * pressProgress * _dpiScale;
            State.WhiteAlpha        = 0.10f + 0.90f * (1f - pressProgress);
            State.HighlightAlpha    = pressProgress;
            State.InnerShadowRadius = InnerShadowRadiusDp * pressProgress * _dpiScale;
            State.InnerShadowAlpha  = pressProgress;

            _lastFraction = fraction;
        }

        /// <summary>trackPos + fraction → thumb 左上角 px。</summary>
        public Vector ComputeThumbPos(Vector trackPos, float fraction)
        {
            float x = (float)(trackPos.X + _paddingPx + _dragWidthPx * fraction);
            float y = (float)(trackPos.Y + (_trackSizePx.Y - _thumbSizePx.Y) * 0.5);
            return new Vector(x, y);
        }

        private static bool PointInRect(Vector p, Vector rectPos, Vector rectSize)
        {
            return p.X >= rectPos.X && p.X <= rectPos.X + rectSize.X
                && p.Y >= rectPos.Y && p.Y <= rectPos.Y + rectSize.Y;
        }
    }
}
