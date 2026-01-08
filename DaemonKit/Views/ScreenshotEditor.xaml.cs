using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using DaemonKit.Models;
using DaemonKit.Utilities;

namespace DaemonKit
{
    public partial class ScreenshotEditor : Window
    {
        private Bitmap _originalBitmap;
        private Bitmap _editingBitmap;
        private Graphics _graphics;
        private bool _isDrawing = false;
        private System.Windows.Point _lastPoint;
        private System.Drawing.Color _currentColor = System.Drawing.Color.Red;
        private int _currentBrushSize = 6;

        public string? SavedFilePath { get; private set; }

        public ScreenshotEditor(Bitmap screenshotBitmap)
        {
            InitializeComponent();

            _originalBitmap = screenshotBitmap;
            _editingBitmap = new Bitmap(screenshotBitmap);
            _graphics = Graphics.FromImage(_editingBitmap);
            _graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 显示截图
            BackgroundImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);

            // 设置画布大小
            DrawingCanvas.Width = _editingBitmap.Width;
            DrawingCanvas.Height = _editingBitmap.Height;

            // 初始化颜色按钮
            ColorPickerButton.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(
                    _currentColor.R,
                    _currentColor.G,
                    _currentColor.B
                )
            );

            StatusText.Text = $"图像尺寸: {_editingBitmap.Width} x {_editingBitmap.Height} 像素";

            // 订阅笔刷大小变化事件
            BrushSizeCombo.SelectionChanged += BrushSizeCombo_SelectionChanged;
        }

        private void BrushSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BrushSizeCombo.SelectedIndex >= 0)
            {
                _currentBrushSize = (BrushSizeCombo.SelectedIndex + 1) * 2;
            }
        }

        private void ColorPickerButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.ColorDialog
            {
                Color = _currentColor,
                AllowFullOpen = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _currentColor = dialog.Color;
                ColorPickerButton.Background = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(
                        _currentColor.R,
                        _currentColor.G,
                        _currentColor.B
                    )
                );
                StatusText.Text =
                    $"颜色已更改: #{_currentColor.R:X2}{_currentColor.G:X2}{_currentColor.B:X2}";
            }
        }

        private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDrawing = true;
                _lastPoint = e.GetPosition(DrawingCanvas);
            }
        }

        private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDrawing && e.LeftButton == MouseButtonState.Pressed)
            {
                System.Windows.Point currentPoint = e.GetPosition(DrawingCanvas);

                // 在Graphics上绘制
                _graphics.DrawLine(
                    new System.Drawing.Pen(_currentColor, _currentBrushSize),
                    (float)_lastPoint.X,
                    (float)_lastPoint.Y,
                    (float)currentPoint.X,
                    (float)currentPoint.Y
                );

                // 在Canvas上显示
                var line = new Line
                {
                    X1 = _lastPoint.X,
                    Y1 = _lastPoint.Y,
                    X2 = currentPoint.X,
                    Y2 = currentPoint.Y,
                    Stroke = new SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            _currentColor.R,
                            _currentColor.G,
                            _currentColor.B
                        )
                    ),
                    StrokeThickness = _currentBrushSize
                };

                Canvas.SetLeft(line, 0);
                Canvas.SetTop(line, 0);
                DrawingCanvas.Children.Add(line);

                // 更新背景图像
                BackgroundImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);

                _lastPoint = currentPoint;
                StatusText.Text = $"绘制中... 坐标: ({(int)currentPoint.X}, {(int)currentPoint.Y})";
            }
        }

        private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawing)
            {
                _isDrawing = false;
                StatusText.Text = "绘制完成";
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            // 恢复到原始图像
            _editingBitmap = new Bitmap(_originalBitmap);
            _graphics?.Dispose();
            _graphics = Graphics.FromImage(_editingBitmap);
            _graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 清空Canvas
            DrawingCanvas.Children.Clear();

            // 更新背景图像
            BackgroundImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);

            StatusText.Text = "已清空所有绘制";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 打开保存对话框
            var dialog = new SaveFileDialog
            {
                Title = "保存截图",
                Filter =
                    "PNG 文件 (*.png)|*.png|JPG 文件 (*.jpg)|*.jpg|BMP 文件 (*.bmp)|*.bmp|GIF 文件 (*.gif)|*.gif",
                FilterIndex = 0,
                FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                InitialDirectory = GetDefaultScreenshotsFolder()
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // 确保目录存在
                    string directory = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 确定保存格式
                    System.Drawing.Imaging.ImageFormat format = System
                        .Drawing
                        .Imaging
                        .ImageFormat
                        .Png;
                    string extension = System.IO.Path.GetExtension(dialog.FileName).ToLower();
                    switch (extension)
                    {
                        case ".jpg":
                        case ".jpeg":
                            format = System.Drawing.Imaging.ImageFormat.Jpeg;
                            break;
                        case ".bmp":
                            format = System.Drawing.Imaging.ImageFormat.Bmp;
                            break;
                        case ".gif":
                            format = System.Drawing.Imaging.ImageFormat.Gif;
                            break;
                    }

                    // 保存图像
                    _editingBitmap.Save(dialog.FileName, format);
                    SavedFilePath = dialog.FileName;

                    // 复制到剪贴板
                    try
                    {
                        Clipboard.SetImage(ColorPicker.BitmapToBitmapSource(_editingBitmap));
                        DNHper.NLogger.Info($"截图已保存: {dialog.FileName}，并已复制到剪贴板");
                    }
                    catch (Exception ex)
                    {
                        DNHper.NLogger.Warn($"无法复制到剪贴板: {ex.Message}");
                    }

                    StatusText.Text = $"已保存: {System.IO.Path.GetFileName(dialog.FileName)}";
                    DialogResult = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"保存失败: {ex.Message}",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    DNHper.NLogger.Error($"截图保存失败: {ex.Message}");
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (
                MessageBox.Show("确定要放弃编辑吗?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes
            )
            {
                DialogResult = false;
                Close();
            }
        }

        private string GetDefaultScreenshotsFolder()
        {
            string processDir = AppDomain.CurrentDomain.BaseDirectory;
            string screenshotsFolder = System.IO.Path.Combine(processDir, "Screenshots");

            if (!Directory.Exists(screenshotsFolder))
            {
                try
                {
                    Directory.CreateDirectory(screenshotsFolder);
                }
                catch { }
            }

            return screenshotsFolder;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _graphics?.Dispose();
            _editingBitmap?.Dispose();
        }
    }
}
