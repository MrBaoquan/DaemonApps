using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using DNHper;

namespace DaemonKit.Services
{
    /// <summary>
    /// 截图相关的 GDI+ 图形操作服务
    /// 提供图层合成、裁剪、绘图、剪贴板、文件保存等纯图形操作
    /// 从 PickerOverlay 代码后置中提取，降低 UI 类复杂度
    /// </summary>
    public static class ScreenCaptureService
    {
        #region 图层裁剪与合成

        /// <summary>
        /// 从全屏截图中裁剪指定区域，生成背景层位图
        /// </summary>
        /// <param name="screenBitmap">全屏截图（原始数据）</param>
        /// <param name="cropX">裁剪起始 X（相对全屏）</param>
        /// <param name="cropY">裁剪起始 Y（相对全屏）</param>
        /// <param name="width">裁剪宽度</param>
        /// <param name="height">裁剪高度</param>
        /// <returns>裁剪后的背景层位图</returns>
        public static Bitmap CropFromScreen(
            Bitmap screenBitmap,
            int cropX,
            int cropY,
            int width,
            int height
        )
        {
            var result = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.DrawImage(
                    screenBitmap,
                    new Rectangle(0, 0, width, height),
                    new Rectangle(cropX, cropY, width, height),
                    GraphicsUnit.Pixel
                );
            }
            return result;
        }

        /// <summary>
        /// 初始化透明笔触层，设置高质量绘图参数
        /// </summary>
        /// <param name="width">笔触层宽度（全屏幕大小）</param>
        /// <param name="height">笔触层高度（全屏幕大小）</param>
        /// <returns>透明的笔触层位图和 Graphics 对象（调用方负责 Dispose）</returns>
        public static (Bitmap bitmap, Graphics graphics) CreateStrokeLayer(int width, int height)
        {
            var bitmap = new Bitmap(width, height);
            var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            return (bitmap, graphics);
        }

        /// <summary>
        /// 合成背景层 + 笔触层 → 编辑显示层
        /// </summary>
        /// <param name="editingBitmap">编辑显示层位图（输出目标）</param>
        /// <param name="captureBitmap">背景截图层</param>
        /// <param name="strokeBitmap">笔触层（全屏幕大小）</param>
        /// <param name="cropX">笔触层裁剪起始 X</param>
        /// <param name="cropY">笔触层裁剪起始 Y</param>
        /// <param name="width">输出宽度</param>
        /// <param name="height">输出高度</param>
        public static void CompositeLayersToEditing(
            Bitmap editingBitmap,
            Bitmap captureBitmap,
            Bitmap strokeBitmap,
            int cropX,
            int cropY,
            int width,
            int height
        )
        {
            using (Graphics g = Graphics.FromImage(editingBitmap))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(captureBitmap, 0, 0);
                g.DrawImage(
                    strokeBitmap,
                    new Rectangle(0, 0, width, height),
                    new Rectangle(cropX, cropY, width, height),
                    GraphicsUnit.Pixel
                );
            }
        }

        #endregion

        #region 绘图操作

        /// <summary>
        /// 在 Graphics 上绘制箭头（线段 + 箭头头部）
        /// </summary>
        public static void DrawArrow(
            Graphics g,
            float x1,
            float y1,
            float x2,
            float y2,
            Color color,
            int penSize
        )
        {
            const double headlen = 15;
            const double angle = Math.PI / 6;

            using (var pen = new Pen(color, penSize))
            {
                g.DrawLine(pen, x1, y1, x2, y2);
            }

            double dx = x2 - x1;
            double dy = y2 - y1;
            double rad = Math.Atan2(dy, dx);

            float endX1 = (float)(x2 - headlen * Math.Cos(rad - angle));
            float endY1 = (float)(y2 - headlen * Math.Sin(rad - angle));
            float endX2 = (float)(x2 - headlen * Math.Cos(rad + angle));
            float endY2 = (float)(y2 - headlen * Math.Sin(rad + angle));

            using (var pen = new Pen(color, penSize))
            {
                g.DrawLine(pen, x2, y2, endX1, endY1);
                g.DrawLine(pen, x2, y2, endX2, endY2);
            }
        }

        /// <summary>
        /// 在 Graphics 上绘制直线
        /// </summary>
        public static void DrawLine(
            Graphics g,
            float x1,
            float y1,
            float x2,
            float y2,
            Color color,
            int penSize
        )
        {
            using (var pen = new Pen(color, penSize))
            {
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        /// <summary>
        /// 在 Graphics 上绘制矩形
        /// </summary>
        public static void DrawRectangle(
            Graphics g,
            float x1,
            float y1,
            float x2,
            float y2,
            Color color,
            int penSize
        )
        {
            int x = (int)Math.Min(x1, x2);
            int y = (int)Math.Min(y1, y2);
            int width = (int)Math.Abs(x2 - x1);
            int height = (int)Math.Abs(y2 - y1);

            using (var pen = new Pen(color, penSize))
            {
                g.DrawRectangle(pen, x, y, width, height);
            }
        }

        /// <summary>
        /// 在笔触层上渲染文字
        /// </summary>
        /// <param name="g">笔触层 Graphics</param>
        /// <param name="text">文字内容</param>
        /// <param name="x">绘制 X 坐标</param>
        /// <param name="y">绘制 Y 坐标</param>
        /// <param name="fontSize">文字大小（像素）</param>
        /// <param name="color">文字颜色</param>
        /// <param name="fontFamily">字体名称（默认 Microsoft YaHei）</param>
        public static void RenderText(
            Graphics g,
            string text,
            float x,
            float y,
            float fontSize,
            Color color,
            string fontFamily = "Microsoft YaHei"
        )
        {
            using (var font = new Font(fontFamily, fontSize, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags = StringFormatFlags.MeasureTrailingSpaces;
                g.DrawString(text, font, brush, x, y, format);
            }
        }

        #endregion

        #region 剪贴板操作

        /// <summary>
        /// 将文字复制到剪贴板（带重试机制）
        /// </summary>
        public static bool TrySetClipboard(string text, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    NLogger.Debug("已复制到剪贴板: {Text}", text);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    NLogger.Warn(
                        "剪贴板操作失败 (尝试 {Attempt}/{MaxRetries}): {ErrorMessage}",
                        i + 1,
                        maxRetries,
                        ex.Message
                    );
                    if (i < maxRetries - 1)
                        System.Threading.Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    NLogger.Error("剪贴板操作异常: {ErrorMessage}", ex.Message);
                    break;
                }
            }
            NLogger.Warn("无法复制到剪贴板，但结果已保存: {Text}", text);
            return false;
        }

        /// <summary>
        /// 将位图复制到剪贴板（带重试机制）
        /// </summary>
        public static bool TrySetClipboardImage(Bitmap bitmap, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var bitmapSource = DaemonKit.Utilities.ColorPicker.BitmapToBitmapSource(bitmap);
                    Clipboard.SetImage(bitmapSource);
                    NLogger.Debug("编辑后的截图已复制到剪贴板 ({Width}x{Height})", bitmap.Width, bitmap.Height);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    NLogger.Warn(
                        "剪贴板操作失败 (尝试 {Attempt}/{MaxRetries}): {ErrorMessage}",
                        i + 1,
                        maxRetries,
                        ex.Message
                    );
                    if (i < maxRetries - 1)
                        System.Threading.Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    NLogger.Error("剪贴板操作异常: {ErrorMessage}", ex.Message);
                    break;
                }
            }
            return false;
        }

        #endregion

        #region 文件保存

        /// <summary>
        /// 根据扩展名获取图片保存格式
        /// </summary>
        public static ImageFormat GetImageFormatFromExtension(string extension)
        {
            switch (extension.ToLower())
            {
                case ".jpg":
                case ".jpeg":
                    return ImageFormat.Jpeg;
                case ".bmp":
                    return ImageFormat.Bmp;
                case ".png":
                default:
                    return ImageFormat.Png;
            }
        }

        /// <summary>
        /// 保存位图到文件
        /// </summary>
        /// <param name="bitmap">要保存的位图</param>
        /// <param name="filePath">文件路径</param>
        /// <returns>保存是否成功</returns>
        public static bool SaveBitmapToFile(Bitmap bitmap, string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath);
                ImageFormat format = GetImageFormatFromExtension(ext);
                bitmap.Save(filePath, format);
                NLogger.Info("截图已保存: {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                NLogger.Error("截图保存失败: {ErrorMessage}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 获取默认截图保存目录（不存在时自动创建）
        /// </summary>
        public static string GetDefaultScreenshotFolder()
        {
            string folder = Utilities.AppPathes.ScreenshotsDir;
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return folder;
        }

        #endregion

        #region 撤销操作

        /// <summary>
        /// 将当前笔触层状态压入撤销栈
        /// </summary>
        /// <param name="undoHistory">撤销历史栈</param>
        /// <param name="currentStroke">当前笔触层位图</param>
        /// <param name="maxSteps">最大撤销步数</param>
        public static void PushUndoState(
            ref Stack<Bitmap> undoHistory,
            Bitmap currentStroke,
            int maxSteps = 20
        )
        {
            if (undoHistory.Count >= maxSteps)
            {
                var oldest = System.Linq.Enumerable.Last(undoHistory);
                oldest?.Dispose();
                undoHistory = new Stack<Bitmap>(
                    System.Linq.Enumerable.Reverse(
                        System.Linq.Enumerable.Take(undoHistory, maxSteps - 1)
                    )
                );
            }
            undoHistory.Push((Bitmap)currentStroke.Clone());
        }

        /// <summary>
        /// 从撤销栈恢复上一步状态
        /// </summary>
        /// <param name="undoHistory">撤销历史栈</param>
        /// <returns>恢复后的笔触层位图（调用方需自行处理 Graphics 重建），或 null（无法撤销）</returns>
        public static Bitmap? PopUndoState(Stack<Bitmap> undoHistory)
        {
            if (undoHistory.Count <= 1)
                return null;

            undoHistory.Pop()?.Dispose();
            var previousStroke = undoHistory.Peek();
            if (previousStroke != null)
            {
                NLogger.Info("已撤销上一步操作");
                return (Bitmap)previousStroke.Clone();
            }
            return null;
        }

        #endregion
    }
}
