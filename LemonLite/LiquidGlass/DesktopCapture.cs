using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Graphics = System.Drawing.Graphics;
using Point = System.Windows.Point;
// 显式别名消除 System.Drawing.Imaging.PixelFormat 与 System.Windows.Media.PixelFormat 歧义
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace LemonLite.LiquidGlass
{
    /// <summary>
        /// 桌面背景抓取器：为 LiquidGlassEffect 的 s0 输入提供 backdrop 纹理。
        ///
        /// 策略：直接用桌面壁纸作为 backdrop（不用 CopyFromScreen）。
        /// 原因：Mica/Acrylic 透明窗口下 CopyFromScreen 抓到的是合成层（含应用窗口半透明渲染），
        /// 偏黑或偏暗，无法看到真实桌面背景。壁纸是静态图但能保证看到真实桌面内容。
        ///
        /// 折射采样需要 thumb 外侧的连续背景，因此截取 3x 控件区域（以控件为中心），
        /// 控件位于 backdrop 中心 1/3，折射偏移后仍能采样到 thumb 外的桌面内容。
        /// shader 中 BackdropSize = (3W, 3H, W, H)，ZW 为控件在 backdrop 中的偏移。
        /// </summary>
    public sealed class DesktopCapture : IDisposable
    {
        private System.Drawing.Bitmap _bitmap;
        private WriteableBitmap _writeableBitmap;
        private int _physW, _physH;
        private bool _disposed;

        // 壁纸缓存（全屏，静态加载一次）
        private static System.Drawing.Bitmap _wallpaperCache;
        private static bool _wallpaperTried;

        /// <summary>当前可用的 WriteableBitmap（物理像素尺寸）。在 Update 后获取。</summary>
        public WriteableBitmap Bitmap => _writeableBitmap;

        /// <summary>
        /// 按控件当前屏幕矩形从壁纸裁剪 3x 区域（以控件为中心），返回是否成功更新。
        /// backdrop 尺寸 = 3x 控件物理像素，控件位于 backdrop 中心 1/3。
        /// </summary>
        public bool Update(FrameworkElement element)
        {
            if (element == null) return false;

            // 控件屏幕矩形（WPF device-independent units，1/96 inch）
            Point tl = element.PointToScreen(new Point(0, 0));
            Point br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            double dipX = Math.Floor(tl.X);
            double dipY = Math.Floor(tl.Y);
            double dipW = Math.Max(1, Math.Ceiling(br.X) - Math.Floor(tl.X));
            double dipH = Math.Max(1, Math.Ceiling(br.Y) - Math.Floor(tl.Y));

            // 转 Physical pixel
            PresentationSource src = PresentationSource.FromVisual(element);
            double dpiX = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
            double dpiY = src != null ? src.CompositionTarget.TransformToDevice.M22 : 1.0;
            int px = (int)Math.Floor(dipX * dpiX);
            int py = (int)Math.Floor(dipY * dpiY);
            int pw = (int)Math.Ceiling(dipW * dpiX);
            int ph = (int)Math.Ceiling(dipH * dpiY);
            if (pw <= 0 || ph <= 0) return false;

            // 扩大到 3x 区域（以控件为中心），给折射采样留出 thumb 外侧的连续背景
            // 控件位于 backdrop 中心 1/3，shader 中 BackdropSize.zw = (pw, ph) 作为 offset
            int captureX = px - pw;
            int captureY = py - ph;
            int captureW = pw * 3;
            int captureH = ph * 3;

            EnsureBuffers(captureW, captureH);
            EnsureWallpaperCache();

            // 如果壁纸加载成功，从壁纸裁剪 3x 区域
            if (_wallpaperCache != null)
            {
                try
                {
                    using (var g = Graphics.FromImage(_bitmap))
                    {
                        // 先填充纯色背景（处理壁纸边界外的区域，如控件在屏幕边缘时）
                        g.Clear(System.Drawing.Color.FromArgb(200, 200, 210));
                        // 从壁纸裁剪 3x 区域到 backdrop
                        g.DrawImage(_wallpaperCache,
                            new Rectangle(0, 0, captureW, captureH),
                            captureX, captureY, captureW, captureH,
                            GraphicsUnit.Pixel);
                    }

                    // 拷贝到 WriteableBitmap
                    BitmapData data = _bitmap.LockBits(
                        new Rectangle(0, 0, captureW, captureH),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppPArgb);
                    try
                    {
                        _writeableBitmap.WritePixels(
                            new Int32Rect(0, 0, captureW, captureH),
                            data.Scan0,
                            data.Stride * captureH,
                            data.Stride);
                    }
                    finally
                    {
                        _bitmap.UnlockBits(data);
                    }
                    return true;
                }
                catch { /* 忽略，fallback 到纯色 */ }
            }

            // 壁纸加载失败，填充纯色 fallback（浅灰，避免黑色）
            try
            {
                using (var g = Graphics.FromImage(_bitmap))
                {
                    g.Clear(System.Drawing.Color.FromArgb(200, 200, 210));
                }
                BitmapData data = _bitmap.LockBits(
                    new Rectangle(0, 0, captureW, captureH),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppPArgb);
                try
                {
                    _writeableBitmap.WritePixels(
                        new Int32Rect(0, 0, captureW, captureH),
                        data.Scan0,
                        data.Stride * captureH,
                        data.Stride);
                }
                finally
                {
                    _bitmap.UnlockBits(data);
                }
                return true;
            }
            catch { return false; }
        }

        private static void EnsureWallpaperCache()
        {
            if (_wallpaperTried) return;
            _wallpaperTried = true;
            try
            {
                string wallpaperPath = GetWallpaperPath();
                if (!string.IsNullOrEmpty(wallpaperPath) && File.Exists(wallpaperPath))
                {
                    _wallpaperCache = new System.Drawing.Bitmap(wallpaperPath);
                }
            }
            catch { /* 忽略 */ }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SystemParametersInfo(uint uAction, uint uParam, StringBuilder lpvParam, uint fuWinIni);

        private const uint SPI_GETDESKWALLPAPER = 0x0073;

        private static string GetWallpaperPath()
        {
            var sb = new StringBuilder(260);
            SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
            return sb.ToString();
        }

        private void EnsureBuffers(int pw, int ph)
        {
            if (_bitmap != null && _physW == pw && _physH == ph) return;

            _bitmap?.Dispose();
            _writeableBitmap = null;

            _bitmap = new System.Drawing.Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
            _writeableBitmap = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32, null);
            _physW = pw;
            _physH = ph;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
