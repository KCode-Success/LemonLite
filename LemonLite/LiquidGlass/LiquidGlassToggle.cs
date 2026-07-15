using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace LemonLite.LiquidGlass
{
    /// <summary>
    /// WPF "Liquid Glass" Toggle 控件 —— 移植自 LiquidGlassDemo 的 WinForms+D3D11+ImGui 版本。
    ///
    /// 架构（用户方案）：
    ///   1. 自定义 FrameworkElement，挂 <see cref="LiquidGlassEffect"/> (ps_3_0)。
    ///   2. Effect 的 s0 输入 = ImageBrush 包裹 <see cref="DesktopCapture"/> 的 WriteableBitmap
    ///      （每帧 BitBlt 控件屏幕区域对应桌面像素），绕开 WPF ShaderEffect 采样窗口背景得黑色的问题。
    ///   3. track + thumb + shadow 全部在 shader 内用 SDF 绘制（无 ImGui）；
    ///      thumb 折射采样 backdrop 时，shader 内在采样位置 alpha-over track 颜色，
    ///      近似复现原版 "CopyResource(backbuffer+track) → thumb 折射" 的效果。
    ///   4. 弹簧物理 + 输入由 <see cref="LiquidToggle"/> 承载，CompositionTarget.Rendering 驱动。
    ///
    /// 用法：
    ///   &lt;ll:LiquidGlassToggle Width="80" Height="40" /&gt;
    /// 通过 <see cref="IsOn"/> 或 <see cref="Toggled"/> 事件读取状态。
    /// </summary>
    public class LiquidGlassToggle : FrameworkElement
    {
        #region IsOn 依赖属性

        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(
                nameof(IsOn),
                typeof(bool),
                typeof(LiquidGlassToggle),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnIsOnChanged));

        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassToggle t && t._toggle != null)
            {
                t._toggle.IsOn = (bool)e.NewValue;
                t._toggle.SyncTargetToIsOn();
                // 外部设置 IsOn（例如 ControlTemplate 内双向绑定的 ToggleButton.IsChecked）
                // 也要启动渲染 hook，否则弹簧动画不会推进。
                t.EnsureRenderingHooked();
            }
        }

        #endregion

        #region IsDisplayOnly

        /// <summary>
        /// 显示模式：true=只渲染不响应鼠标（用于嵌入 ToggleButton.ControlTemplate，
        /// 由父级 ToggleButton 处理点击，IsOn 通过双向绑定驱动）。
        /// 默认 false（独立控件，自身处理鼠标）。
        /// </summary>
        public static readonly DependencyProperty IsDisplayOnlyProperty =
            DependencyProperty.Register(
                nameof(IsDisplayOnly),
                typeof(bool),
                typeof(LiquidGlassToggle),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnIsDisplayOnlyChanged));

        public bool IsDisplayOnly
        {
            get => (bool)GetValue(IsDisplayOnlyProperty);
            set => SetValue(IsDisplayOnlyProperty, value);
        }

        private static void OnIsDisplayOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassToggle t)
            {
                bool displayOnly = (bool)e.NewValue;
                t.Focusable = !displayOnly;
                // display-only 模式下仍保持 IsHitTestVisible=True，
                // 让 LiquidGlassToggle 自己处理 thumb 区域的鼠标点击和拖拽，
                // 阻止事件冒泡到父 ToggleButton 避免重复切换。
                t.IsHitTestVisible = true;
            }
        }

        #endregion

        #region TrackOnColor

        /// <summary>
        /// track 开启态填充画刷。ControlTemplate 中通过 {DynamicResource HighlightThemeColor} 绑定，
        /// 让 track 颜色随主题色变化（与其他 ToggleButton 一致）。
        /// 默认 Apple 绿 #34C759，实际值由绑定覆盖。
        /// </summary>
        public static readonly DependencyProperty TrackOnColorProperty =
            DependencyProperty.Register(
                nameof(TrackOnColor),
                typeof(Brush),
                typeof(LiquidGlassToggle),
                new FrameworkPropertyMetadata(
                    new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnTrackOnColorChanged));

        public Brush TrackOnColor
        {
            get => (Brush)GetValue(TrackOnColorProperty);
            set => SetValue(TrackOnColorProperty, value);
        }

        private static void OnTrackOnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassToggle t && t._effect != null)
            {
                // 颜色变化时立即更新 Effect 常量并重绘，即使 timer 已停止
                t.UpdateEffectConstants();
                t.InvalidateVisual();
            }
        }

        #endregion

        #region IsPressed

        /// <summary>
        /// 父 ToggleButton 的按压状态（仅 IsDisplayOnly 模式使用）。
        /// ControlTemplate 内通过 TemplateBinding 绑定到父 ToggleButton.IsPressed，
        /// 使 LiquidGlassToggle 能在显示模式下获得按压反馈，触发 thumb 缩放弹簧动画。
        /// </summary>
        public static readonly DependencyProperty IsPressedProperty =
            DependencyProperty.Register(
                nameof(IsPressed),
                typeof(bool),
                typeof(LiquidGlassToggle),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnIsPressedChanged));

        public bool IsPressed
        {
            get => (bool)GetValue(IsPressedProperty);
            set => SetValue(IsPressedProperty, value);
        }

        private static void OnIsPressedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LiquidGlassToggle t && t._toggle != null)
            {
                // display-only 模式下鼠标事件由 LiquidGlassToggle 自己处理（OnMouseLeftButtonDown/Up），
                // IsPressed 绑定不再驱动按压弹簧，避免重复触发。
                if (t.IsDisplayOnly) return;

                bool pressed = (bool)e.NewValue;
                if (pressed)
                {
                    t._toggle.TriggerPress();
                }
                else
                {
                    t._toggle.TriggerRelease();
                }
                t.EnsureRenderingHooked();
            }
        }

        #endregion

        /// <summary>切换事件。点击释放或拖拽吸附时触发。</summary>
        public event EventHandler<bool> Toggled;

        private readonly LiquidToggle _toggle;
        private readonly DesktopCapture _capture;
        private readonly LiquidGlassEffect _effect;
        private readonly ImageBrush _desktopBrush;
        private readonly DispatcherTimer _timer;

        private DateTime _lastTime;
        private bool _disposed;

        public LiquidGlassToggle()
        {
            // 构造时控件尚未加入视觉树，VisualTreeHelper.GetDpi 可能抛异常，用默认值 1.0
            float dpiScale = 1f;
            try
            {
                dpiScale = (float)VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (dpiScale <= 0) dpiScale = 1f;
            }
            catch { /* 设计期或未加入视觉树，用默认值 */ }
            _toggle = new LiquidToggle(dpiScale);
            _capture = new DesktopCapture();
            _effect = new LiquidGlassEffect();
            _desktopBrush = new ImageBrush
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            _effect.Input = _desktopBrush;
            Effect = _effect;
            Focusable = true;
            ClipToBounds = true;
            _lastTime = DateTime.UtcNow;

            // DispatcherTimer 在 UI 线程跑，生命周期与控件绑定，比 CompositionTarget.Rendering 更安全
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps
            };
            _timer.Tick += OnTimerTick;

            Loaded += LiquidGlassToggle_Loaded;
            Unloaded += LiquidGlassToggle_Unloaded;
        }

        private void LiquidGlassToggle_Unloaded(object sender, RoutedEventArgs e)
        {
            // 控件从视觉树移除时停止 timer，防止 PointToScreen/BitBlt 在控件无效时崩溃
            _timer.Stop();
            // 释放鼠标捕获（若按住时切换样式）
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private void LiquidGlassToggle_Loaded(object sender, RoutedEventArgs e)
        {
            // Loaded 后 DPI 才稳定，重新初始化 toggle 几何
            try
            {
                float dpiScale = (float)VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (dpiScale > 0 && Math.Abs(dpiScale - _toggle.DpiScale) > 0.01f)
                {
                    _toggle.ReinitAtDpi(dpiScale);
                }
            }
            catch { /* 忽略 */ }
            StartTimer();
        }

        private void StartTimer()
        {
            if (_disposed) return;
            if (!_timer.IsEnabled)
            {
                _lastTime = DateTime.UtcNow;
                _timer.Start();
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            StartTimer();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var desired = _toggle.DesiredSize;
            double w = desired.X;
            double h = desired.Y;
            return new Size(
                double.IsInfinity(availableSize.Width) ? w : Math.Min(w, availableSize.Width),
                double.IsInfinity(availableSize.Height) ? h : Math.Min(h, availableSize.Height));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            // 透明背景：让 shader 完全决定输出，避免灰色 fallback 透过半透明 track/thumb 显示。
            // shader 中 track/thumb 外的像素 alpha=0，完全透明。
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        #region 输入

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            CaptureMouse();
            var pos = e.GetPosition(this);
            _toggle.SetMousePos(new Vector(pos.X, pos.Y));
            _toggle.Press(TrackTopLeft);
            EnsureRenderingHooked();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pos = e.GetPosition(this);
            _toggle.SetMousePos(new Vector(pos.X, pos.Y));
            if (_toggle.IsPressed)
            {
                _toggle.MoveDuringPress();
                EnsureRenderingHooked();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            var pos = e.GetPosition(this);
            _toggle.SetMousePos(new Vector(pos.X, pos.Y));
            bool committed = _toggle.Release(TrackTopLeft);
            if (committed)
            {
                // IsOn 是 OneWay 绑定（从 ToggleButton.IsChecked），不能 SetValue 回写。
                // 直接通过 SetCurrentValue 同步父 ToggleButton.IsChecked：
                //   - 点击切换：翻转 IsChecked
                //   - 拖拽切换：同步到 _toggle.IsOn（吸附状态）
                var parent = FindAncestorToggleButton();
                if (parent != null)
                {
                    bool targetChecked = _toggle.IsDraggingLastRelease
                        ? _toggle.IsOn
                        : !(parent.IsChecked ?? false);
                    if (parent.IsChecked != targetChecked)
                    {
                        parent.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, targetChecked);
                    }
                }
                Toggled?.Invoke(this, _toggle.IsOn);
            }
            ReleaseMouseCapture();
            EnsureRenderingHooked();
            e.Handled = true;
        }

        private System.Windows.Controls.Primitives.ToggleButton FindAncestorToggleButton()
        {
            DependencyObject d = this;
            while (d != null)
            {
                if (d is System.Windows.Controls.Primitives.ToggleButton tb) return tb;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            // 鼠标离开但未释放时，保持拖拽（已 CaptureMouse），不强制释放
        }

        #endregion

        #region 渲染驱动

        private void EnsureRenderingHooked()
        {
            StartTimer();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (_disposed) { _timer.Stop(); return; }

            // 控件不可见或未加入视觉树时跳过
            if (!IsVisible || PresentationSource.FromVisual(this) == null)
            {
                return;
            }
            // 布局未完成时跳过，避免 BackdropSize=0 导致 shader 除零
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            try
            {
                DateTime now = DateTime.UtcNow;
                float dt = (float)(now - _lastTime).TotalSeconds;
                _lastTime = now;
                if (dt > 1f / 30f) dt = 1f / 30f;
                if (dt <= 0f) dt = 1f / 60f;

                // 推进物理
                _toggle.UpdatePhysics(TrackTopLeft, dt);

                // 更新桌面截图
                bool captured = false;
                try { captured = _capture.Update(this); }
                catch { /* 忽略截图失败 */ }

                // 更新 Effect 的 s0 输入为最新桌面截图
                if (captured && _capture.Bitmap != null)
                {
                    _desktopBrush.ImageSource = _capture.Bitmap;
                }

                // 推送 Effect 常量
                UpdateEffectConstants();

                // 触发重绘
                InvalidateVisual();

                // 弹簧稳定且无按压时停止 timer，省 CPU
                if (_toggle.IsSettledState())
                {
                    _timer.Stop();
                }
            }
            catch
            {
                /* 任何异常都吞掉，防止崩溃。timer 继续跑以便下一帧重试 */
            }
        }

        private void UpdateEffectConstants()
        {
            var s = _toggle.State;

            // c0: TrackColor = lerp(TrackColorOff, TrackColorOn, fraction)
            // TrackColorOn 从 TrackOnColor DP 读取（ControlTemplate 中绑定到 {DynamicResource HighlightThemeColor}），
            // 让 track 颜色随主题色变化（与其他 ToggleButton 一致）。
            var off = s.TrackColorOff;
            Color onColor = (TrackOnColor is SolidColorBrush scb) ? scb.Color : Color.FromRgb(0x34, 0xC7, 0x59);
            var on = new Vector4(
                onColor.R / 255f,
                onColor.G / 255f,
                onColor.B / 255f,
                1.0f);
            float r = Lerp(off.X, on.X, s.Fraction);
            float g = Lerp(off.Y, on.Y, s.Fraction);
            float b = Lerp(off.Z, on.Z, s.Fraction);
            float a = Lerp(off.W, on.W, s.Fraction);
            _effect.TrackColor = new Point4D(r, g, b, a);

            // c1: TrackRect (px=DIP) —— Point4D.X=TopLeftX, Y=TopLeftY, Z=Width, W=Height
            _effect.TrackRect = new Point4D(s.TrackScreenPos.X, s.TrackScreenPos.Y, s.TrackSize.X, s.TrackSize.Y);

            // c2: ThumbRect (缩放前)
            _effect.ThumbRect = new Point4D(s.ThumbScreenPos.X, s.ThumbScreenPos.Y, s.ThumbSize.X, s.ThumbSize.Y);

            // c3: ScaleFracPress
            _effect.ScaleFracPress = new Point4D(s.ThumbScale.X, s.ThumbScale.Y, s.Fraction, s.PressProgress);

            // c4: ShadowParams
            _effect.ShadowParams = new Point4D(s.ShadowOffset.X, s.ShadowOffset.Y, s.ShadowRadius, s.ShadowAlpha);

            // c5: GlassParams
            _effect.GlassParams = new Point4D(s.BlurRadius, s.RefractionAmount, s.InnerShadowRadius, s.HighlightAlpha);

            // c6: BackdropSize (XY=3x 控件 DIP 尺寸=backdrop 尺寸, ZW=控件 DIP 尺寸=控件在 backdrop 中的偏移)
            // backdrop 是 3x 控件区域（控件位于中心 1/3），折射采样能拿到 thumb 外侧的连续背景
            _effect.BackdropSize = new Point4D(ActualWidth * 3.0, ActualHeight * 3.0, ActualWidth, ActualHeight);
        }

        #endregion

        #region 辅助

        /// <summary>track 左上角在控件内的 DIP 坐标 —— 居中放置，留出阴影外扩 + thumb 按压缩放空间。</summary>
        private Vector TrackTopLeft
        {
            get
            {
                var desired = _toggle.DesiredSize;
                float ox = (float)((ActualWidth - desired.X) * 0.5);
                float oy = (float)((ActualHeight - desired.Y) * 0.5);
                // desired 已含阴影外扩 + thumb 按压外扩，track 左上角 = offset + (shadowPad + pressExpand)
                // X/Y 方向的 pressExpand 不同（thumb 36x22，按压 1.5x → pressExpandX=9, pressExpandY=5.5）
                float padX = _toggle.ShadowPadding + _toggle.PressExpand;
                float padY = _toggle.ShadowPadding + _toggle.PressExpandY;
                return new Vector(ox + padX, oy + padY);
            }
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        #endregion

        protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
        {
            base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        }
    }
}
