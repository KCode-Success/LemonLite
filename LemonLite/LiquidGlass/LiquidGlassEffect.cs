using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace LemonLite.LiquidGlass
{
    /// <summary>
    /// WPF ShaderEffect 包装 LiquidGlass.ps（ps_3_0）。
    ///
    /// 寄存器布局（与 LiquidGlass.hlsl 一一对应）：
    ///   s0 = Input (Brush) —— 桌面截图 ImageBrush，由 DesktopCapture 每帧更新
    ///   c0 = TrackColor      (Point4D: X=r, Y=g, Z=b, W=a, 0..1)
    ///   c1 = TrackRect       (Point4D: X=topLeftX, Y=topLeftY, Z=width, W=height, px)
    ///   c2 = ThumbRect       (Point4D: X=topLeftX, Y=topLeftY, Z=width, W=height, px)
    ///   c3 = ScaleFracPress  (Point4D: X=scaleX, Y=scaleY, Z=fraction, W=pressProgress)
    ///   c4 = ShadowParams    (Point4D: X=offsetX, Y=offsetY, Z=radius, W=alpha, px/0..1)
    ///   c5 = GlassParams     (Point4D: X=blurRadius, Y=refractionAmount, Z=innerShadowRadius, W=highlightAlpha)
    ///   c6 = BackdropSize    (Point4D: X=width, Y=height, ZW=pad)
    ///
    /// 说明：WPF PixelShaderConstantCallback 仅允许 double/float/Point/Size/Vector/Color/Point4D 等类型，
    /// 不允许 Rect 和 Thickness。Point4D（System.Windows.Media.Media3D）是 4 doubles，序列化为 float4 寄存器，
    /// 与 shader 的 float4 寄存器布局完全一致，因此用 Point4D 打包所有 4-float 常量。
    /// </summary>
    public sealed class LiquidGlassEffect : ShaderEffect
    {
        public LiquidGlassEffect()
        {
            PixelShader = new PixelShader
            {
                UriSource = new Uri(
                    "pack://application:,,,/LemonLite;component/LiquidGlass/LiquidGlass.ps",
                    UriKind.Absolute)
            };
            PixelShader.Freeze();

            UpdateShaderValue(InputProperty);
            UpdateShaderValue(TrackColorProperty);
            UpdateShaderValue(TrackRectProperty);
            UpdateShaderValue(ThumbRectProperty);
            UpdateShaderValue(ScaleFracPressProperty);
            UpdateShaderValue(ShadowParamsProperty);
            UpdateShaderValue(GlassParamsProperty);
            UpdateShaderValue(BackdropSizeProperty);
        }

        // ---- s0: Input ----
        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty(
                "Input",
                typeof(LiquidGlassEffect),
                0,
                SamplingMode.NearestNeighbor);

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        // ---- c0: TrackColor (rgba 0..1) ----
        public static readonly DependencyProperty TrackColorProperty =
            DependencyProperty.Register(
                nameof(TrackColor),
                typeof(Point4D),
                typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(0)));

        public Point4D TrackColor
        {
            get => (Point4D)GetValue(TrackColorProperty);
            set => SetValue(TrackColorProperty, value);
        }

        // ---- c1: TrackRect (px) ----
        public static readonly DependencyProperty TrackRectProperty =
            DependencyProperty.Register(
                nameof(TrackRect),
                typeof(Point4D),
                typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(1)));

        public Point4D TrackRect
        {
            get => (Point4D)GetValue(TrackRectProperty);
            set => SetValue(TrackRectProperty, value);
        }

        // ---- c2: ThumbRect (px, 缩放前) ----
        public static readonly DependencyProperty ThumbRectProperty =
            DependencyProperty.Register(
                nameof(ThumbRect),
                typeof(Point4D),
                typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(2)));

        public Point4D ThumbRect
        {
            get => (Point4D)GetValue(ThumbRectProperty);
            set => SetValue(ThumbRectProperty, value);
        }

        // ---- c3: ScaleFracPress ----
        public static readonly DependencyProperty ScaleFracPressProperty =
            DependencyProperty.Register(
                nameof(ScaleFracPress),
                typeof(Point4D),
                typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(1, 1, 0, 0), PixelShaderConstantCallback(3)));

        public Point4D ScaleFracPress
        {
            get => (Point4D)GetValue(ScaleFracPressProperty);
            set => SetValue(ScaleFracPressProperty, value);
        }

        // ---- c4: ShadowParams ----
        public static readonly DependencyProperty ShadowParamsProperty =
            DependencyProperty.Register(
                nameof(ShadowParams),
                typeof(Point4D),
                typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(0, 2, 4, 0.05), PixelShaderConstantCallback(4)));

        public Point4D ShadowParams
        {
            get => (Point4D)GetValue(ShadowParamsProperty);
            set => SetValue(ShadowParamsProperty, value);
        }

        // ---- c5: GlassParams ----
        public static readonly DependencyProperty GlassParamsProperty =
            DependencyProperty.Register(
                nameof(GlassParams),
                typeof(Point4D),
                typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(6, 5, 4, 0), PixelShaderConstantCallback(5)));

        public Point4D GlassParams
        {
            get => (Point4D)GetValue(GlassParamsProperty);
            set => SetValue(GlassParamsProperty, value);
        }

        // ---- c6: BackdropSize ----
        public static readonly DependencyProperty BackdropSizeProperty =
            DependencyProperty.Register(
                nameof(BackdropSize),
                typeof(Point4D),
                typeof(LiquidGlassEffect),
                new UIPropertyMetadata(new Point4D(1, 1, 0, 0), PixelShaderConstantCallback(6)));

        public Point4D BackdropSize
        {
            get => (Point4D)GetValue(BackdropSizeProperty);
            set => SetValue(BackdropSizeProperty, value);
        }
    }
}
