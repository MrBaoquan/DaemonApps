using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DaemonKit.Core
{
    public static class ColorPicker
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// 获取当前鼠标位置
        /// </summary>
        public static System.Windows.Point GetMousePosition()
        {
            GetCursorPos(out POINT point);
            return new System.Windows.Point(point.X, point.Y);
        }

        /// <summary>
        /// 获取指定屏幕坐标的颜色
        /// </summary>
        public static Color GetColorAt(int x, int y)
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                uint pixel = GetPixel(hdc, x, y);
                // GetPixel returns 0x00BBGGRR format (BGR in low to high bytes)
                return Color.FromArgb(
                    (int)(pixel & 0x000000FF), // R (低位字节)
                    (int)((pixel & 0x0000FF00) >> 8), // G (中间字节)
                    (int)((pixel & 0x00FF0000) >> 16) // B (高位字节)
                );
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        /// <summary>
        /// 从Bitmap获取指定坐标的颜色
        /// </summary>
        public static Color GetColorFromBitmap(Bitmap bitmap, int x, int y)
        {
            if (bitmap == null || x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
                return Color.Black;
            return bitmap.GetPixel(x, y);
        }

        /// <summary>
        /// 获取当前鼠标位置的颜色
        /// </summary>
        public static Color GetColorAtMouse()
        {
            var pos = GetMousePosition();
            return GetColorAt((int)pos.X, (int)pos.Y);
        }

        /// <summary>
        /// 将颜色转换为十六进制字符串 (格式: #RRGGBB)
        /// </summary>
        public static string ColorToHex(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>
        /// 截取整个扩展桌面的屏幕截图
        /// </summary>
        public static Bitmap CaptureScreen(int left, int top, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height));
            }
            return bitmap;
        }

        /// <summary>
        /// 将Bitmap转换为WPF可用的ImageSource
        /// </summary>
        public static BitmapSource BitmapToBitmapSource(Bitmap bitmap)
        {
            if (bitmap == null)
                return null;

            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                bitmap.PixelFormat
            );

            var bitmapSource = BitmapSource.Create(
                bitmapData.Width,
                bitmapData.Height,
                bitmap.HorizontalResolution,
                bitmap.VerticalResolution,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                bitmapData.Scan0,
                bitmapData.Stride * bitmapData.Height,
                bitmapData.Stride
            );

            bitmap.UnlockBits(bitmapData);
            bitmapSource.Freeze(); // 使其可跨线程使用
            return bitmapSource;
        }

        /// <summary>
        /// 截取指定区域并保存到文件
        /// </summary>
        public static string SaveScreenshot(
            int x,
            int y,
            int width,
            int height,
            string folder = null
        )
        {
            if (string.IsNullOrEmpty(folder))
            {
                folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "DaemonKit Screenshots"
                );
            }

            if (!System.IO.Directory.Exists(folder))
                System.IO.Directory.CreateDirectory(folder);

            string filename = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string filepath = System.IO.Path.Combine(folder, filename);

            using (var bitmap = CaptureScreen(x, y, width, height))
            {
                bitmap.Save(filepath, ImageFormat.Png);
            }

            return filepath;
        }
    }
}
