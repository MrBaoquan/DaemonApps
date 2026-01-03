using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Linq;
using DaemonKit.Core;
using Microsoft.Win32;

namespace DaemonKit
{
    public partial class PickerOverlay : Window
    {
        public enum PickerMode
        {
            Color,
            Position,
            Screenshot
        }

        public enum DrawingTool
        {
            Move,
            Pencil,
            Line,
            Rectangle,
            Arrow,
            Text
        }

        public PickerMode Mode { get; set; }
        public string Result { get; private set; } = string.Empty;

        private DispatcherTimer _updateTimer;
        private Bitmap? _screenBitmap;
        private int _screenLeft;
        private int _screenTop;

        // 截图选区相关
        private bool _isSelecting = false;
        private bool _selectionCompleted = false;
        private bool _isDrawing = false;
        private System.Windows.Point _selectionStart;
        private System.Windows.Point _selectionEnd;
        private System.Windows.Point _lastDrawPoint;
        private System.Windows.Point _drawStartPoint;
        private int _lastScreenshotX = 0;
        private int _lastScreenshotY = 0;
        private int _lastScreenshotWidth = 0;
        private int _lastScreenshotHeight = 0;
        private Bitmap? _editingBitmap;
        private Graphics? _editingGraphics;
        private Bitmap? _previewBitmap; // 用于实时预览的临时位图
        private const int MaxUndoSteps = 20; // 最大撤销步数
        private Stack<Bitmap> _undoHistory = new Stack<Bitmap>(); // 撤销历史
        private System.Drawing.Color _currentBrushColor = System.Drawing.Color.Red;
        private int _currentBrushSize = 4;
        private DrawingTool _currentTool = DrawingTool.Move;
        private TextBox? _activeTextBox = null; // 当前活动的文本框

        // 选区拖动相关
        private bool _isDraggingSelection = false;
        private System.Windows.Vector _selectionDragOffset;

        // 选区调整相关
        private bool _isResizing = false;
        private ResizeHandle _resizeHandle = ResizeHandle.None;
        private const double ResizeHitMargin = 6.0;

        // 工具栏拖动相关
        private bool _isToolBarDragging = false;
        private System.Windows.Point _toolBarDragStart;

        // 放大镜相关
        private Border? _magnifierBorder;
        private System.Windows.Controls.Image? _magnifierImage;
        private const int MagnifierSize = 150;
        private const int MagnifierZoom = 5;

        private enum ResizeHandle
        {
            None,
            Left,
            Right,
            Top,
            Bottom,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        /// <summary>
        /// 获取选区矩形的坐标和尺寸
        /// </summary>
        private (double X, double Y, double Width, double Height) GetSelectionRect()
        {
            double x = Math.Min(_selectionStart.X, _selectionEnd.X);
            double y = Math.Min(_selectionStart.Y, _selectionEnd.Y);
            double width = Math.Abs(_selectionEnd.X - _selectionStart.X);
            double height = Math.Abs(_selectionEnd.Y - _selectionStart.Y);
            return (x, y, width, height);
        }

        public PickerOverlay()
        {
            InitializeComponent();
            Mode = PickerMode.Color;

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _updateTimer.Tick += UpdateTimer_Tick;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 获取扩展桌面信息
            var desktopInfo = DNHper.WinAPI.GetExtendedDesktopResolution();
            _screenLeft = desktopInfo.LeftBound;
            _screenTop = desktopInfo.TopBound;

            this.Left = _screenLeft;
            this.Top = _screenTop;
            this.Width = desktopInfo.TotalWidth;
            this.Height = desktopInfo.TotalHeight;

            // 预先截取整个桌面
            _screenBitmap = ColorPicker.CaptureScreen(
                _screenLeft,
                _screenTop,
                desktopInfo.TotalWidth,
                desktopInfo.TotalHeight
            );

            // 颜色拾取模式显示背景截图
            if (Mode == PickerMode.Color)
            {
                BackgroundImage.Source = ColorPicker.BitmapToBitmapSource(_screenBitmap);
                BackgroundImage.Width = desktopInfo.TotalWidth;
                BackgroundImage.Height = desktopInfo.TotalHeight;
                Canvas.SetLeft(BackgroundImage, 0);
                Canvas.SetTop(BackgroundImage, 0);
            }

            UpdateModeUI();
            CreateMagnifier();
            _updateTimer.Start();
        }

        private void CreateMagnifier()
        {
            _magnifierImage = new System.Windows.Controls.Image
            {
                Width = MagnifierSize,
                Height = MagnifierSize,
                Stretch = System.Windows.Media.Stretch.None
            };

            _magnifierBorder = new Border
            {
                Width = MagnifierSize,
                Height = MagnifierSize,
                Background = System.Windows.Media.Brushes.Black,
                BorderBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(0),
                Child = _magnifierImage,
                Visibility = Visibility.Visible
            };

            Panel.SetZIndex(_magnifierBorder, 200);
            MainCanvas.Children.Add(_magnifierBorder);
        }

        private void UpdateModeUI()
        {
            switch (Mode)
            {
                case PickerMode.Color:
                    ModeText.Text = "🎨 颜色拾取";
                    HintText.Text = "点击拾取颜色 | ESC 取消";
                    ColorText.Visibility = Visibility.Visible;
                    HexText.Visibility = Visibility.Visible;
                    ColorPreview.Visibility = Visibility.Visible;
                    SizeText.Visibility = Visibility.Collapsed;
                    CrosshairH.Visibility = Visibility.Visible;
                    CrosshairV.Visibility = Visibility.Visible;
                    InfoBox.Visibility = Visibility.Visible;
                    ToolBar.Visibility = Visibility.Collapsed;
                    break;
                case PickerMode.Position:
                    ModeText.Text = "📍 位置拾取";
                    HintText.Text = "点击拾取位置 | ESC 取消";
                    ColorText.Visibility = Visibility.Collapsed;
                    HexText.Visibility = Visibility.Collapsed;
                    ColorPreview.Visibility = Visibility.Collapsed;
                    SizeText.Visibility = Visibility.Collapsed;
                    CrosshairH.Visibility = Visibility.Visible;
                    CrosshairV.Visibility = Visibility.Visible;
                    InfoBox.Visibility = Visibility.Visible;
                    ToolBar.Visibility = Visibility.Collapsed;
                    break;
                case PickerMode.Screenshot:
                    ModeText.Text = "📷 截图";
                    HintText.Text = "拖动选择区域 | ESC 取消";
                    ColorText.Visibility = Visibility.Collapsed;
                    HexText.Visibility = Visibility.Collapsed;
                    ColorPreview.Visibility = Visibility.Collapsed;
                    SizeText.Visibility = Visibility.Visible;
                    SizeText.Text = "尺寸: 0 x 0";
                    CrosshairH.Visibility = Visibility.Visible;
                    CrosshairV.Visibility = Visibility.Visible;
                    InfoBox.Visibility = Visibility.Collapsed; // 截图模式不显示InfoBox
                    ToolBar.Visibility = Visibility.Collapsed;
                    // 截图模式显示半透明遮罩
                    this.Background = new SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(80, 0, 0, 0)
                    );
                    break;
            }
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            var screenPos = ColorPicker.GetMousePosition();
            var windowPos = this.PointFromScreen(
                new System.Windows.Point(screenPos.X, screenPos.Y)
            );
            UpdateCrosshairAndInfo(windowPos, screenPos);
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            var windowPos = e.GetPosition(this);
            var screenPos = this.PointToScreen(windowPos);

            UpdateCrosshairAndInfo(windowPos, screenPos);

            // 截图模式下更新选区/调整
            if (Mode == PickerMode.Screenshot)
            {
                if (_isDraggingSelection && _selectionCompleted)
                {
                    UpdateSelectionDrag(windowPos);
                }
                else if (_isResizing && _selectionCompleted)
                {
                    ApplyResize(windowPos);
                }
                else if (_isSelecting)
                {
                    _selectionEnd = windowPos;
                    UpdateSelectionRect();
                }
                else if (_selectionCompleted)
                {
                    // 调整/拖动时的命中测试，更新鼠标指针
                    var handle = HitTestResizeHandle(windowPos);
                    if (handle != ResizeHandle.None)
                    {
                        UpdateCursorForHandle(handle);
                    }
                    else if (_currentTool == DrawingTool.Move && IsPointInsideSelection(windowPos))
                    {
                        Mouse.OverrideCursor = Cursors.SizeAll;
                    }
                    else
                    {
                        Mouse.OverrideCursor = null;
                    }
                }
            }
        }

        private void UpdateCrosshairAndInfo(
            System.Windows.Point windowPos,
            System.Windows.Point screenPos
        )
        {
            // 更新十字准星位置
            if (!_isSelecting)
            {
                CrosshairH.X1 = 0;
                CrosshairH.X2 = ActualWidth;
                CrosshairH.Y1 = CrosshairH.Y2 = windowPos.Y;

                CrosshairV.Y1 = 0;
                CrosshairV.Y2 = ActualHeight;
                CrosshairV.X1 = CrosshairV.X2 = windowPos.X;
            }

            // 更新信息框位置
            double offsetX = 20;
            double offsetY = 20;
            double infoX = windowPos.X + offsetX;
            double infoY = windowPos.Y + offsetY;

            if (infoX + InfoBox.ActualWidth > ActualWidth)
                infoX = windowPos.X - InfoBox.ActualWidth - offsetX;
            if (infoY + InfoBox.ActualHeight > ActualHeight)
                infoY = windowPos.Y - InfoBox.ActualHeight - offsetY;

            Canvas.SetLeft(InfoBox, infoX);
            Canvas.SetTop(InfoBox, infoY);

            PositionText.Text = $"位置: ({(int)screenPos.X}, {(int)screenPos.Y})";

            if (Mode == PickerMode.Color && _screenBitmap != null)
            {
                // 从预先截取的位图获取颜色，避免十字准星干扰
                int bitmapX = (int)windowPos.X;
                int bitmapY = (int)windowPos.Y;

                var color = ColorPicker.GetColorFromBitmap(_screenBitmap, bitmapX, bitmapY);
                var hex = ColorPicker.ColorToHex(color);

                ColorText.Text = $"RGB: ({color.R}, {color.G}, {color.B})";
                HexText.Text = $"十六进制: {hex}";
                ColorPreview.Fill = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(color.R, color.G, color.B)
                );
            }

            UpdateMagnifier(windowPos);
        }

        private void UpdateMagnifier(System.Windows.Point windowPos)
        {
            if (_magnifierBorder == null || _magnifierImage == null || _screenBitmap == null)
                return;

            int centerX = (int)windowPos.X;
            int centerY = (int)windowPos.Y;
            int captureSize = MagnifierSize / MagnifierZoom;

            int sourceX = Math.Max(0, centerX - captureSize / 2);
            int sourceY = Math.Max(0, centerY - captureSize / 2);
            int sourceWidth = Math.Min(captureSize, _screenBitmap.Width - sourceX);
            int sourceHeight = Math.Min(captureSize, _screenBitmap.Height - sourceY);

            if (sourceWidth > 0 && sourceHeight > 0)
            {
                var magnifiedBitmap = new Bitmap(MagnifierSize, MagnifierSize);
                using (var g = Graphics.FromImage(magnifiedBitmap))
                {
                    g.InterpolationMode = System
                        .Drawing
                        .Drawing2D
                        .InterpolationMode
                        .NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(
                        _screenBitmap,
                        new System.Drawing.Rectangle(0, 0, MagnifierSize, MagnifierSize),
                        new System.Drawing.Rectangle(sourceX, sourceY, sourceWidth, sourceHeight),
                        GraphicsUnit.Pixel
                    );

                    // 绘制中心十字线
                    using (var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 1))
                    {
                        int center = MagnifierSize / 2;
                        g.DrawLine(pen, center, 0, center, MagnifierSize);
                        g.DrawLine(pen, 0, center, MagnifierSize, center);
                    }
                }

                _magnifierImage.Source = ColorPicker.BitmapToBitmapSource(magnifiedBitmap);
                magnifiedBitmap.Dispose();
            }

            // 定位放大镜，避免遮挡鼠标
            double magnifierX = windowPos.X + 30;
            double magnifierY = windowPos.Y + 30;

            if (magnifierX + MagnifierSize > ActualWidth)
                magnifierX = windowPos.X - MagnifierSize - 30;
            if (magnifierY + MagnifierSize > ActualHeight)
                magnifierY = windowPos.Y - MagnifierSize - 30;

            Canvas.SetLeft(_magnifierBorder, magnifierX);
            Canvas.SetTop(_magnifierBorder, magnifierY);
        }

        private void UpdateSelectionRect()
        {
            double x = Math.Min(_selectionStart.X, _selectionEnd.X);
            double y = Math.Min(_selectionStart.Y, _selectionEnd.Y);
            double width = Math.Abs(_selectionEnd.X - _selectionStart.X);
            double height = Math.Abs(_selectionEnd.Y - _selectionStart.Y);

            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = width;
            SelectionRect.Height = height;
            SelectionRect.Visibility = Visibility.Visible;

            // 更新选区遮罩 (选区外部半透明)
            var geometry = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)),
                new RectangleGeometry(new Rect(x, y, width, height))
            );
            SelectionMask.Data = geometry;
            SelectionMask.Visibility = Visibility.Visible;

            // 隐藏十字准星
            CrosshairH.Visibility = Visibility.Hidden;
            CrosshairV.Visibility = Visibility.Hidden;

            SizeText.Text = $"尺寸: {(int)width} x {(int)height}";
        }

        private ResizeHandle HitTestResizeHandle(System.Windows.Point pos)
        {
            double left = Canvas.GetLeft(SelectionRect);
            double top = Canvas.GetTop(SelectionRect);
            double right = left + SelectionRect.Width;
            double bottom = top + SelectionRect.Height;

            bool nearLeft = Math.Abs(pos.X - left) <= ResizeHitMargin;
            bool nearRight = Math.Abs(pos.X - right) <= ResizeHitMargin;
            bool nearTop = Math.Abs(pos.Y - top) <= ResizeHitMargin;
            bool nearBottom = Math.Abs(pos.Y - bottom) <= ResizeHitMargin;

            if (nearLeft && nearTop)
                return ResizeHandle.TopLeft;
            if (nearRight && nearTop)
                return ResizeHandle.TopRight;
            if (nearLeft && nearBottom)
                return ResizeHandle.BottomLeft;
            if (nearRight && nearBottom)
                return ResizeHandle.BottomRight;
            if (nearLeft)
                return ResizeHandle.Left;
            if (nearRight)
                return ResizeHandle.Right;
            if (nearTop)
                return ResizeHandle.Top;
            if (nearBottom)
                return ResizeHandle.Bottom;

            return ResizeHandle.None;
        }

        private void PositionToolBar(double selX, double selY, double selWidth, double selHeight)
        {
            ToolBar.Measure(
                new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity)
            );
            double toolBarWidth =
                ToolBar.ActualWidth > 0 ? ToolBar.ActualWidth : ToolBar.DesiredSize.Width;
            double toolBarHeight =
                ToolBar.ActualHeight > 0 ? ToolBar.ActualHeight : ToolBar.DesiredSize.Height;

            double toolBarX = selX + selWidth / 2 - toolBarWidth / 2;
            double toolBarY = selY + selHeight + 10;

            double margin = 10;

            // 智能定位:如果放在下方会超出屏幕,则放在上方
            if (toolBarY + toolBarHeight + margin > ActualHeight)
            {
                toolBarY = selY - toolBarHeight - 10;
                // 如果上方也放不下,则强制放在窗口内可见区域
                if (toolBarY < margin)
                {
                    toolBarY = Math.Min(
                        selY + selHeight / 2 - toolBarHeight / 2,
                        ActualHeight - toolBarHeight - margin
                    );
                }
            }

            toolBarX = Math.Max(margin, Math.Min(toolBarX, ActualWidth - toolBarWidth - margin));
            toolBarY = Math.Max(margin, Math.Min(toolBarY, ActualHeight - toolBarHeight - margin));

            Canvas.SetLeft(ToolBar, toolBarX);
            Canvas.SetTop(ToolBar, toolBarY);
        }

        private void UpdateCursorForHandle(ResizeHandle handle)
        {
            switch (handle)
            {
                case ResizeHandle.Left:
                case ResizeHandle.Right:
                    Mouse.OverrideCursor = Cursors.SizeWE;
                    break;
                case ResizeHandle.Top:
                case ResizeHandle.Bottom:
                    Mouse.OverrideCursor = Cursors.SizeNS;
                    break;
                case ResizeHandle.TopLeft:
                case ResizeHandle.BottomRight:
                    Mouse.OverrideCursor = Cursors.SizeNWSE;
                    break;
                case ResizeHandle.TopRight:
                case ResizeHandle.BottomLeft:
                    Mouse.OverrideCursor = Cursors.SizeNESW;
                    break;
                default:
                    Mouse.OverrideCursor = null;
                    break;
            }
        }

        private bool IsPointInsideSelection(System.Windows.Point pos)
        {
            double left = Canvas.GetLeft(SelectionRect);
            double top = Canvas.GetTop(SelectionRect);
            double right = left + SelectionRect.Width;
            double bottom = top + SelectionRect.Height;

            return pos.X >= left && pos.X <= right && pos.Y >= top && pos.Y <= bottom;
        }

        private void UpdateSelectionDrag(System.Windows.Point windowPos)
        {
            double width = SelectionRect.Width;
            double height = SelectionRect.Height;

            double newLeft = windowPos.X - _selectionDragOffset.X;
            double newTop = windowPos.Y - _selectionDragOffset.Y;

            // 边界约束
            newLeft = Math.Max(0, Math.Min(newLeft, ActualWidth - width));
            newTop = Math.Max(0, Math.Min(newTop, ActualHeight - height));

            _selectionStart = new System.Windows.Point(newLeft, newTop);
            _selectionEnd = new System.Windows.Point(newLeft + width, newTop + height);

            UpdateSelectionRect();

            // 同步绘制画布与截图显示位置
            Canvas.SetLeft(SelectionScreenshotImage, newLeft);
            Canvas.SetTop(SelectionScreenshotImage, newTop);
            Canvas.SetLeft(DrawingCanvas, newLeft);
            Canvas.SetTop(DrawingCanvas, newTop);

            PositionToolBar(newLeft, newTop, width, height);

            // 实时更新截图内容
            _lastScreenshotX = (int)newLeft + _screenLeft;
            _lastScreenshotY = (int)newTop + _screenTop;
            _lastScreenshotWidth = (int)width;
            _lastScreenshotHeight = (int)height;
            RefreshSelectionContentDuringDrag();
        }

        private void RefreshSelectionContentDuringDrag()
        {
            if (_screenBitmap == null || _lastScreenshotWidth <= 0 || _lastScreenshotHeight <= 0)
                return;

            try
            {
                int cropX = _lastScreenshotX - _screenLeft;
                int cropY = _lastScreenshotY - _screenTop;

                // 重新裁剪当前区域
                var newBitmap = new Bitmap(_lastScreenshotWidth, _lastScreenshotHeight);
                using (Graphics g = Graphics.FromImage(newBitmap))
                {
                    g.DrawImage(
                        _screenBitmap,
                        new System.Drawing.Rectangle(
                            0,
                            0,
                            _lastScreenshotWidth,
                            _lastScreenshotHeight
                        ),
                        new System.Drawing.Rectangle(
                            cropX,
                            cropY,
                            _lastScreenshotWidth,
                            _lastScreenshotHeight
                        ),
                        GraphicsUnit.Pixel
                    );
                }

                _editingGraphics?.Dispose();
                _editingBitmap?.Dispose();
                _editingBitmap = newBitmap;
                _editingGraphics = Graphics.FromImage(_editingBitmap);
                _editingGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                SelectionScreenshotImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);
                _previewBitmap?.Dispose();
                _previewBitmap = null;
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Warn($"拖动实时刷新失败: {ex.Message}");
            }
        }

        private void ApplyResize(System.Windows.Point currentPos)
        {
            var startX = _selectionStart.X;
            var startY = _selectionStart.Y;
            var endX = _selectionEnd.X;
            var endY = _selectionEnd.Y;

            switch (_resizeHandle)
            {
                case ResizeHandle.Left:
                case ResizeHandle.TopLeft:
                case ResizeHandle.BottomLeft:
                    startX = currentPos.X;
                    break;
                case ResizeHandle.Right:
                case ResizeHandle.TopRight:
                case ResizeHandle.BottomRight:
                    endX = currentPos.X;
                    break;
            }

            switch (_resizeHandle)
            {
                case ResizeHandle.Top:
                case ResizeHandle.TopLeft:
                case ResizeHandle.TopRight:
                    startY = currentPos.Y;
                    break;
                case ResizeHandle.Bottom:
                case ResizeHandle.BottomLeft:
                case ResizeHandle.BottomRight:
                    endY = currentPos.Y;
                    break;
            }

            // 保持最小尺寸
            if (Math.Abs(endX - startX) < 5)
                endX = startX + (endX >= startX ? 5 : -5);
            if (Math.Abs(endY - startY) < 5)
                endY = startY + (endY >= startY ? 5 : -5);

            _selectionStart = new System.Windows.Point(startX, startY);
            _selectionEnd = new System.Windows.Point(endX, endY);
            UpdateSelectionRect();

            // 调整时实时更新工具栏位置
            if (ToolBar.Visibility == Visibility.Visible)
            {
                var (selX, selY, selWidth, selHeight) = GetSelectionRect();
                PositionToolBar(selX, selY, selWidth, selHeight);
            }
        }

        private void RecalculateSelectionAndRefresh()
        {
            _isDraggingSelection = false;
            _isResizing = false;

            var (selX, selY, selWidth, selHeight) = GetSelectionRect();

            _lastScreenshotX = (int)selX + _screenLeft;
            _lastScreenshotY = (int)selY + _screenTop;
            _lastScreenshotWidth = (int)selWidth;
            _lastScreenshotHeight = (int)selHeight;

            // 清理绘制状态，重新捕获
            DrawingCanvas.Children.Clear();
            _undoHistory.Clear();
            _editingGraphics?.Dispose();
            _editingBitmap?.Dispose();
            _previewBitmap?.Dispose();
            _editingBitmap = null;
            _editingGraphics = null;
            _previewBitmap = null;

            CaptureAndShowScreenshot();
        }

        private void ShowToolBar()
        {
            try
            {
                // 先隐藏选区边框和遮罩，避免被截进图片
                SelectionMask.Visibility = Visibility.Collapsed;
                SelectionRect.Visibility = Visibility.Collapsed;
                SelectionRect.Stroke = System.Windows.Media.Brushes.Transparent;
                SelectionRect.StrokeThickness = 0;
                CrosshairH.Visibility = Visibility.Collapsed;
                CrosshairV.Visibility = Visibility.Collapsed;

                // 强制WPF立即更新布局和渲染，确保边框隐藏生效
                this.UpdateLayout();
                this.InvalidateVisual();

                // 使用Dispatcher延迟捕获，确保WPF渲染完成
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        CaptureAndShowScreenshot();

                        // 捕获后恢复dashed border的显示（绘制阶段使用）
                        SelectionRect.Stroke = System.Windows.Media.Brushes.CornflowerBlue;
                        SelectionRect.StrokeThickness = 2;
                        SelectionRect.Visibility = Visibility.Visible;
                    }),
                    System.Windows.Threading.DispatcherPriority.Render
                );
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"ShowToolBar 失败: {ex.Message}");
            }
        }

        private void CaptureAndShowScreenshot()
        {
            try
            {
                // 从预先捕获的干净屏幕截图中裁剪选定区域，避免捕获到 WPF 边框
                // _screenBitmap 是窗口显示前就截取的，绝对不含虚线边框
                int cropX = _lastScreenshotX - _screenLeft;
                int cropY = _lastScreenshotY - _screenTop;

                _editingBitmap = new Bitmap(_lastScreenshotWidth, _lastScreenshotHeight);
                using (Graphics g = Graphics.FromImage(_editingBitmap))
                {
                    g.DrawImage(
                        _screenBitmap,
                        new System.Drawing.Rectangle(
                            0,
                            0,
                            _lastScreenshotWidth,
                            _lastScreenshotHeight
                        ),
                        new System.Drawing.Rectangle(
                            cropX,
                            cropY,
                            _lastScreenshotWidth,
                            _lastScreenshotHeight
                        ),
                        GraphicsUnit.Pixel
                    );
                }

                _editingGraphics = Graphics.FromImage(_editingBitmap);
                _editingGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                var (selX, selY, selWidth, selHeight) = GetSelectionRect();

                // 在框选区域显示截图
                SelectionScreenshotImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);
                Canvas.SetLeft(SelectionScreenshotImage, selX);
                Canvas.SetTop(SelectionScreenshotImage, selY);
                SelectionScreenshotImage.Width = _lastScreenshotWidth;
                SelectionScreenshotImage.Height = _lastScreenshotHeight;
                SelectionScreenshotImage.Visibility = Visibility.Visible;

                // 启用绘制画布（与截图区域对齐）
                DrawingCanvas.Width = _lastScreenshotWidth;
                DrawingCanvas.Height = _lastScreenshotHeight;
                Canvas.SetLeft(DrawingCanvas, selX);
                Canvas.SetTop(DrawingCanvas, selY);
                DrawingCanvas.Visibility = Visibility.Visible;

                // 初始化撤销历史
                _undoHistory.Clear();
                _undoHistory.Push((Bitmap)_editingBitmap.Clone());

                // 显示工具栏在框选区域附近，确保不出屏幕
                ToolBar.Visibility = Visibility.Visible;
                PositionToolBar(selX, selY, selWidth, selHeight);

                // 隐藏放大镜
                if (_magnifierBorder != null)
                    _magnifierBorder.Visibility = Visibility.Collapsed;

                // 默认启用拖动模式
                _currentTool = DrawingTool.Move;
                UpdateToolButtonStyles();

                DNHper.NLogger.Info("进入截图编辑模式");
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"CaptureAndShowScreenshot 失败: {ex.Message}");
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 确保窗口获得焦点并捕获鼠标（解决偶尔鼠标点击无效的问题）
            if (!this.IsFocused)
                this.Focus();

            if (Mode == PickerMode.Screenshot)
            {
                // 如果已完成选择，检查是否点击调整区域
                if (_selectionCompleted)
                {
                    // 拖动模式下，允许在选区内拖动整体
                    if (
                        _currentTool == DrawingTool.Move
                        && IsPointInsideSelection(e.GetPosition(this))
                        && HitTestResizeHandle(e.GetPosition(this)) == ResizeHandle.None
                    )
                    {
                        _isDraggingSelection = true;
                        var selLeft = Canvas.GetLeft(SelectionRect);
                        var selTop = Canvas.GetTop(SelectionRect);
                        var mousePos = e.GetPosition(this);
                        _selectionDragOffset = mousePos - new System.Windows.Point(selLeft, selTop);
                        if (!this.IsMouseCaptured)
                            this.CaptureMouse();
                        e.Handled = true;
                        return;
                    }

                    var handle = HitTestResizeHandle(e.GetPosition(this));
                    if (handle != ResizeHandle.None)
                    {
                        _isResizing = true;
                        _resizeHandle = handle;
                        _isSelecting = false;
                        UpdateCursorForHandle(handle);
                        if (!this.IsMouseCaptured)
                            this.CaptureMouse();
                        e.Handled = true;
                        return;
                    }

                    // 工具按钮处理由对应的Button_Click事件处理
                    return;
                }

                // 开始新的选择区域
                _isSelecting = true;
                _selectionCompleted = false;
                _selectionStart = e.GetPosition(this);
                _selectionEnd = _selectionStart;
                ToolBar.Visibility = Visibility.Collapsed; // 隐藏工具按钮
                this.Background = System.Windows.Media.Brushes.Transparent;
                if (!this.IsMouseCaptured)
                    this.CaptureMouse();
            }
            else
            {
                // 颜色/位置拾取模式 - 直接完成
                FinishPicking(e.GetPosition(this));
            }
        }

        private void ToolBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 仅在拖动手柄区域启动拖动
            if (!IsClickOnDragHandle(e.OriginalSource))
                return;

            _isToolBarDragging = true;
            _toolBarDragStart = e.GetPosition(this);
            ToolBar.CaptureMouse();
            e.Handled = true;
        }

        private void ToolBar_MouseMove(object sender, MouseEventArgs e)
        {
            bool isOverHandle = IsClickOnDragHandle(e.OriginalSource);

            if (_isToolBarDragging)
            {
                var currentPos = e.GetPosition(this);
                double offsetX = currentPos.X - _toolBarDragStart.X;
                double offsetY = currentPos.Y - _toolBarDragStart.Y;

                double currentLeft = Canvas.GetLeft(ToolBar);
                double currentTop = Canvas.GetTop(ToolBar);

                double newLeft = currentLeft + offsetX;
                double newTop = currentTop + offsetY;

                // 限制在窗口范围内
                double margin = 10;
                newLeft = Math.Max(
                    margin,
                    Math.Min(newLeft, ActualWidth - ToolBar.ActualWidth - margin)
                );
                newTop = Math.Max(
                    margin,
                    Math.Min(newTop, ActualHeight - ToolBar.ActualHeight - margin)
                );

                Canvas.SetLeft(ToolBar, newLeft);
                Canvas.SetTop(ToolBar, newTop);

                _toolBarDragStart = currentPos;
                e.Handled = true;
            }

            // 悬停光标反馈
            ToolBar.Cursor = isOverHandle ? Cursors.SizeAll : Cursors.Arrow;
        }

        private void ToolBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isToolBarDragging)
            {
                _isToolBarDragging = false;
                ToolBar.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private bool IsClickOnButton(object originalSource)
        {
            if (originalSource is DependencyObject dep)
            {
                var current = dep;
                while (current != null)
                {
                    if (current is Button)
                        return true;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            return false;
        }

        private bool IsClickOnDragHandle(object originalSource)
        {
            if (originalSource is DependencyObject dep)
            {
                var current = dep;
                while (current != null)
                {
                    if (current is FrameworkElement fe && fe.Name == "ToolBarDragHandle")
                        return true;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            return false;
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Mode == PickerMode.Screenshot)
            {
                if (_isDraggingSelection)
                {
                    _isDraggingSelection = false;
                    if (this.IsMouseCaptured)
                        this.ReleaseMouseCapture();
                    RecalculateSelectionAndRefresh();
                    e.Handled = true;
                    return;
                }

                if (_isResizing && _selectionCompleted)
                {
                    _isResizing = false;
                    _resizeHandle = ResizeHandle.None;
                    Mouse.OverrideCursor = null;

                    RecalculateSelectionAndRefresh();
                    if (this.IsMouseCaptured)
                        this.ReleaseMouseCapture();
                    e.Handled = true;
                    return;
                }

                if (_isSelecting)
                {
                    _isSelecting = false;
                    _selectionEnd = e.GetPosition(this);

                    // 计算选区
                    int x = (int)Math.Min(_selectionStart.X, _selectionEnd.X) + _screenLeft;
                    int y = (int)Math.Min(_selectionStart.Y, _selectionEnd.Y) + _screenTop;
                    int width = (int)Math.Abs(_selectionEnd.X - _selectionStart.X);
                    int height = (int)Math.Abs(_selectionEnd.Y - _selectionStart.Y);

                    if (width > 5 && height > 5)
                    {
                        // 保存选区信息，直接启用编辑模式
                        _lastScreenshotX = x;
                        _lastScreenshotY = y;
                        _lastScreenshotWidth = width;
                        _lastScreenshotHeight = height;
                        _selectionCompleted = true;
                        ShowToolBar(); // 直接显示工具栏启用绘制
                    }

                    if (this.IsMouseCaptured)
                        this.ReleaseMouseCapture();
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 此方法已删除，现在框选完成后直接通过 ShowToolBar 启用编辑
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectionCompleted && _editingBitmap != null)
            {
                try
                {
                    // 弹出保存文件对话框
                    var saveDialog = new SaveFileDialog
                    {
                        Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp|所有文件|*.*",
                        FilterIndex = 1,
                        DefaultExt = ".png",
                        FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                        InitialDirectory = GetDefaultScreenshotFolder()
                    };

                    if (saveDialog.ShowDialog() == true)
                    {
                        // 根据文件扩展名确定保存格式
                        ImageFormat format = ImageFormat.Png;
                        string ext = System.IO.Path.GetExtension(saveDialog.FileName).ToLower();
                        switch (ext)
                        {
                            case ".jpg":
                            case ".jpeg":
                                format = ImageFormat.Jpeg;
                                break;
                            case ".bmp":
                                format = ImageFormat.Bmp;
                                break;
                        }

                        // 保存文件
                        _editingBitmap.Save(saveDialog.FileName, format);

                        // 同时复制到剪贴板
                        TrySetClipboardImage(_editingBitmap);

                        DNHper.NLogger.Info($"截图已保存: {saveDialog.FileName}");
                        Result = $"Saved:{saveDialog.FileName}";
                        DialogResult = true;
                        Close();
                    }
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Error($"截图保存失败: {ex.Message}");
                    MessageBox.Show(
                        $"截图保存失败: {ex.Message}",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
        }

        private string GetDefaultScreenshotFolder()
        {
            // 默认保存到 进程所在目录下的 "Screenshots" 文件夹
            string processDir = AppDomain.CurrentDomain.BaseDirectory;
            string folder = System.IO.Path.Combine(processDir, "Screenshots");

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            return folder;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 取消操作，直接退出截图窗口
            DialogResult = false;
            Close();
        }

        private void FinishPicking(System.Windows.Point windowPos)
        {
            _updateTimer.Stop();

            if (Mode == PickerMode.Color)
            {
                int bitmapX = (int)windowPos.X;
                int bitmapY = (int)windowPos.Y;
                var color = ColorPicker.GetColorFromBitmap(_screenBitmap, bitmapX, bitmapY);
                var hex = ColorPicker.ColorToHex(color);
                Result = hex;
                TrySetClipboard(hex);
            }
            else if (Mode == PickerMode.Position)
            {
                var screenPos = this.PointToScreen(windowPos);
                Result = $"{(int)screenPos.X},{(int)screenPos.Y}";
                TrySetClipboard(Result);
            }

            DialogResult = true;
            Close();
        }

        private void TrySetClipboard(string text, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    DNHper.NLogger.Info($"已复制到剪贴板: {text}");
                    return;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    DNHper.NLogger.Warn($"剪贴板操作失败 (尝试 {i + 1}/{maxRetries}): {ex.Message}");
                    if (i < maxRetries - 1)
                        System.Threading.Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Error($"剪贴板操作异常: {ex.Message}");
                    break;
                }
            }
            DNHper.NLogger.Warn($"无法复制到剪贴板，但结果已保存: {text}");
        }

        private void TrySetClipboardImage(Bitmap bitmap, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var bitmapSource = ColorPicker.BitmapToBitmapSource(bitmap);
                    Clipboard.SetImage(bitmapSource);
                    DNHper.NLogger.Info($"编辑后的截图已复制到剪贴板 ({bitmap.Width}x{bitmap.Height})");
                    return;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    DNHper.NLogger.Warn($"剪贴板操作失败 (尝试 {i + 1}/{maxRetries}): {ex.Message}");
                    if (i < maxRetries - 1)
                        System.Threading.Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Error($"剪贴板操作异常: {ex.Message}");
                    break;
                }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                // ESC 按键：直接退出截屏模式（关闭窗口）
                _updateTimer.Stop();
                _isSelecting = false;
                DialogResult = false;
                Close();
                e.Handled = true;
            }
            else if (
                e.Key == Key.Z
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            )
            {
                // Ctrl+Z
                if (_selectionCompleted && DrawingCanvas.Visibility == Visibility.Visible)
                {
                    UndoLastAction();
                    e.Handled = true;
                }
            }
        }

        private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (
                e.LeftButton == MouseButtonState.Pressed
                && DrawingCanvas.Visibility == Visibility.Visible
            )
            {
                if (_currentTool == DrawingTool.Move)
                {
                    // 由窗口级事件处理拖动，避免进入绘制流程
                    return;
                }

                if (_currentTool == DrawingTool.Text)
                {
                    // 文本工具：创建可移动的文本框
                    CreateTextBox(e.GetPosition(DrawingCanvas));
                }
                else
                {
                    // 其他工具：开始绘制
                    _isDrawing = true;
                    _drawStartPoint = e.GetPosition(DrawingCanvas);
                    _lastDrawPoint = _drawStartPoint;
                }
            }
        }

        private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (
                _isDrawing
                && e.LeftButton == MouseButtonState.Pressed
                && _editingBitmap != null
                && _currentTool != DrawingTool.Move
            )
            {
                System.Windows.Point currentPoint = e.GetPosition(DrawingCanvas);

                if (_currentTool == DrawingTool.Pencil)
                {
                    // 铅笔工具：直接绘制到编辑位图
                    _editingGraphics.DrawLine(
                        new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                        (float)_lastDrawPoint.X,
                        (float)_lastDrawPoint.Y,
                        (float)currentPoint.X,
                        (float)currentPoint.Y
                    );
                    _lastDrawPoint = currentPoint;

                    // 更新显示
                    SelectionScreenshotImage.Source = ColorPicker.BitmapToBitmapSource(
                        _editingBitmap
                    );
                }
                else
                {
                    // 其他工具：显示实时预览
                    if (_previewBitmap == null)
                    {
                        _previewBitmap = (Bitmap)_editingBitmap.Clone();
                    }
                    else
                    {
                        // 重置预览位图为当前编辑位图
                        _previewBitmap.Dispose();
                        _previewBitmap = (Bitmap)_editingBitmap.Clone();
                    }

                    using (Graphics g = Graphics.FromImage(_previewBitmap))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                        if (_currentTool == DrawingTool.Line)
                        {
                            // 直线预览
                            g.DrawLine(
                                new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                                (float)_drawStartPoint.X,
                                (float)_drawStartPoint.Y,
                                (float)currentPoint.X,
                                (float)currentPoint.Y
                            );
                        }
                        else if (_currentTool == DrawingTool.Rectangle)
                        {
                            // 矩形预览
                            int x = (int)Math.Min(_drawStartPoint.X, currentPoint.X);
                            int y = (int)Math.Min(_drawStartPoint.Y, currentPoint.Y);
                            int width = (int)Math.Abs(currentPoint.X - _drawStartPoint.X);
                            int height = (int)Math.Abs(currentPoint.Y - _drawStartPoint.Y);
                            g.DrawRectangle(
                                new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                                x,
                                y,
                                width,
                                height
                            );
                        }
                        else if (_currentTool == DrawingTool.Arrow)
                        {
                            // 箭头预览
                            DrawArrowOnGraphics(
                                g,
                                (float)_drawStartPoint.X,
                                (float)_drawStartPoint.Y,
                                (float)currentPoint.X,
                                (float)currentPoint.Y
                            );
                        }
                    }

                    // 显示预览
                    SelectionScreenshotImage.Source = ColorPicker.BitmapToBitmapSource(
                        _previewBitmap
                    );
                }
            }
        }

        private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawing && _editingBitmap != null && _currentTool != DrawingTool.Move)
            {
                System.Windows.Point endPoint = e.GetPosition(DrawingCanvas);

                // 对于非铅笔工具，将最终图形绘制到编辑位图
                if (_currentTool == DrawingTool.Line)
                {
                    _editingGraphics.DrawLine(
                        new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                        (float)_drawStartPoint.X,
                        (float)_drawStartPoint.Y,
                        (float)endPoint.X,
                        (float)endPoint.Y
                    );
                }
                else if (_currentTool == DrawingTool.Rectangle)
                {
                    int x = (int)Math.Min(_drawStartPoint.X, endPoint.X);
                    int y = (int)Math.Min(_drawStartPoint.Y, endPoint.Y);
                    int width = (int)Math.Abs(endPoint.X - _drawStartPoint.X);
                    int height = (int)Math.Abs(endPoint.Y - _drawStartPoint.Y);

                    _editingGraphics.DrawRectangle(
                        new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                        x,
                        y,
                        width,
                        height
                    );
                }
                else if (_currentTool == DrawingTool.Arrow)
                {
                    DrawArrowOnGraphics(
                        _editingGraphics,
                        (float)_drawStartPoint.X,
                        (float)_drawStartPoint.Y,
                        (float)endPoint.X,
                        (float)endPoint.Y
                    );
                }

                // 清理预览位图
                if (_previewBitmap != null)
                {
                    _previewBitmap.Dispose();
                    _previewBitmap = null;
                }

                // 保存到撤销历史
                if (_undoHistory.Count > MaxUndoSteps) // 限制历史记录数量
                {
                    var oldest = _undoHistory.Last();
                    oldest?.Dispose();
                    _undoHistory = new Stack<Bitmap>(_undoHistory.Take(MaxUndoSteps).Reverse());
                }
                _undoHistory.Push((Bitmap)_editingBitmap.Clone());

                // 更新显示为最终结果
                SelectionScreenshotImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);
                _isDrawing = false;
            }
        }

        private void DrawArrowOnGraphics(Graphics g, float x1, float y1, float x2, float y2)
        {
            const double headlen = 15;
            const double angle = Math.PI / 6;

            // 绘制箭头线
            g.DrawLine(
                new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                x1,
                y1,
                x2,
                y2
            );

            // 计算箭头方向
            double dx = x2 - x1;
            double dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double rad = Math.Atan2(dy, dx);

            // 箭头左边
            float endX1 = (float)(x2 - headlen * Math.Cos(rad - angle));
            float endY1 = (float)(y2 - headlen * Math.Sin(rad - angle));
            g.DrawLine(
                new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                x2,
                y2,
                endX1,
                endY1
            );

            // 箭头右边
            float endX2 = (float)(x2 - headlen * Math.Cos(rad + angle));
            float endY2 = (float)(y2 - headlen * Math.Sin(rad + angle));
            g.DrawLine(
                new System.Drawing.Pen(_currentBrushColor, _currentBrushSize),
                x2,
                y2,
                endX2,
                endY2
            );
        }

        private void ColorPickerBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.ColorDialog
            {
                Color = _currentBrushColor,
                AllowFullOpen = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _currentBrushColor = dialog.Color;
                ColorPickerBtn.Background = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(
                        _currentBrushColor.R,
                        _currentBrushColor.G,
                        _currentBrushColor.B
                    )
                );
            }
        }

        private void BrushSizeSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e
        )
        {
            _currentBrushSize = (int)BrushSizeSlider.Value;
            if (BrushSizeText != null)
            {
                BrushSizeText.Text = $"{_currentBrushSize}px";
            }
        }

        private void PencilToolBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentTool = DrawingTool.Pencil;
            UpdateToolButtonStyles();
        }

        private void MoveToolBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentTool = DrawingTool.Move;
            UpdateToolButtonStyles();
        }

        private void LineToolBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentTool = DrawingTool.Line;
            UpdateToolButtonStyles();
        }

        private void RectToolBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentTool = DrawingTool.Rectangle;
            UpdateToolButtonStyles();
        }

        private void ArrowToolBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentTool = DrawingTool.Arrow;
            UpdateToolButtonStyles();
        }

        private void TextToolBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentTool = DrawingTool.Text;
            UpdateToolButtonStyles();
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            UndoLastAction();
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            // 全屏截取：选区覆盖整个屏幕
            _selectionStart = new System.Windows.Point(0, 0);
            _selectionEnd = new System.Windows.Point(this.ActualWidth, this.ActualHeight);
            _selectionCompleted = true;

            // 立即更新虚线边框到全屏
            UpdateSelectionRect();

            RecalculateSelectionAndRefresh();
        }

        private void UpdateToolButtonStyles()
        {
            var activeColor = System.Windows.Media.Color.FromRgb(0, 102, 255);
            var inactiveColor = System.Windows.Media.Color.FromRgb(102, 102, 102);

            MoveToolBtn.Background = new SolidColorBrush(
                _currentTool == DrawingTool.Move ? activeColor : inactiveColor
            );
            PencilToolBtn.Background = new SolidColorBrush(
                _currentTool == DrawingTool.Pencil ? activeColor : inactiveColor
            );
            LineToolBtn.Background = new SolidColorBrush(
                _currentTool == DrawingTool.Line ? activeColor : inactiveColor
            );
            RectToolBtn.Background = new SolidColorBrush(
                _currentTool == DrawingTool.Rectangle ? activeColor : inactiveColor
            );
            ArrowToolBtn.Background = new SolidColorBrush(
                _currentTool == DrawingTool.Arrow ? activeColor : inactiveColor
            );
            if (TextToolBtn != null)
            {
                TextToolBtn.Background = new SolidColorBrush(
                    _currentTool == DrawingTool.Text ? activeColor : inactiveColor
                );
            }
        }

        private void CreateTextBox(System.Windows.Point position)
        {
            CommitActiveTextBox();

            _activeTextBox = new TextBox
            {
                MinWidth = 100,
                MinHeight = 30,
                Background = System.Windows.Media.Brushes.White,
                Foreground = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(
                        _currentBrushColor.R,
                        _currentBrushColor.G,
                        _currentBrushColor.B
                    )
                ),
                FontSize = 14,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 160, 255)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(4),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Cursor = Cursors.IBeam
            };

            Canvas.SetLeft(_activeTextBox, position.X);
            Canvas.SetTop(_activeTextBox, position.Y);
            DrawingCanvas.Children.Add(_activeTextBox);
            _activeTextBox.Focus();

            _activeTextBox.KeyDown += TextBox_KeyDown;
            _activeTextBox.LostFocus += TextBox_LostFocus;

            _activeTextBox.MouseLeftButtonDown += TextBox_MouseLeftButtonDown;
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_activeTextBox != null && !_activeTextBox.IsKeyboardFocusWithin)
                    {
                        CommitActiveTextBox();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background
            );
        }

        private System.Windows.Point _textBoxDragStart;
        private bool _isTextBoxDragging = false;

        private void TextBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                _isTextBoxDragging = true;
                _textBoxDragStart = e.GetPosition(DrawingCanvas);
                textBox.CaptureMouse();
                textBox.MouseMove += TextBox_MouseMove;
                textBox.MouseLeftButtonUp += TextBox_MouseLeftButtonUp;
                e.Handled = true;
            }
        }

        private void TextBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isTextBoxDragging && sender is TextBox textBox)
            {
                var currentPos = e.GetPosition(DrawingCanvas);
                var offset = currentPos - _textBoxDragStart;

                var left = Canvas.GetLeft(textBox) + offset.X;
                var top = Canvas.GetTop(textBox) + offset.Y;

                Canvas.SetLeft(textBox, left);
                Canvas.SetTop(textBox, top);

                _textBoxDragStart = currentPos;
            }
        }

        private void TextBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                _isTextBoxDragging = false;
                textBox.ReleaseMouseCapture();
                textBox.MouseMove -= TextBox_MouseMove;
                textBox.MouseLeftButtonUp -= TextBox_MouseLeftButtonUp;
            }
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox != null && !string.IsNullOrWhiteSpace(_activeTextBox.Text))
            {
                var left = Canvas.GetLeft(_activeTextBox);
                var top = Canvas.GetTop(_activeTextBox);

                using (var font = new System.Drawing.Font("Microsoft YaHei", 12))
                using (var brush = new System.Drawing.SolidBrush(_currentBrushColor))
                {
                    _editingGraphics.DrawString(
                        _activeTextBox.Text,
                        font,
                        brush,
                        (float)left,
                        (float)top
                    );
                }

                SelectionScreenshotImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);

                if (_undoHistory.Count > MaxUndoSteps)
                {
                    var oldest = _undoHistory.Last();
                    oldest?.Dispose();
                    _undoHistory = new Stack<Bitmap>(_undoHistory.Take(MaxUndoSteps).Reverse());
                }
                _undoHistory.Push((Bitmap)_editingBitmap.Clone());
            }

            if (_activeTextBox != null)
            {
                DrawingCanvas.Children.Remove(_activeTextBox);
                _activeTextBox = null;
            }
        }

        private void UndoLastAction()
        {
            if (_undoHistory.Count > 1)
            {
                var current = _undoHistory.Pop();
                current?.Dispose();

                var previous = _undoHistory.Peek();
                _editingBitmap?.Dispose();
                _editingBitmap = (Bitmap)previous.Clone();
                _editingGraphics?.Dispose();
                _editingGraphics = Graphics.FromImage(_editingBitmap);
                _editingGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                SelectionScreenshotImage.Source = ColorPicker.BitmapToBitmapSource(_editingBitmap);
                DNHper.NLogger.Info("已撤销上一步操作");
            }
        }

        private void ExitDrawingMode()
        {
            CommitActiveTextBox();

            DrawingCanvas.Visibility = Visibility.Collapsed;
            DrawingCanvas.Children.Clear();
            SelectionScreenshotImage.Visibility = Visibility.Collapsed;
            ToolBar.Visibility = Visibility.Collapsed;
            _editingGraphics?.Dispose();
            _editingBitmap?.Dispose();
            _previewBitmap?.Dispose();
            _editingBitmap = null;
            _editingGraphics = null;
            _previewBitmap = null;

            while (_undoHistory.Count > 0)
            {
                var bmp = _undoHistory.Pop();
                bmp?.Dispose();
            }

            SelectionRect.Visibility = Visibility.Visible;
            SelectionRect.Stroke = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0, 160, 255)
            );
            SelectionRect.StrokeThickness = 2;
            SelectionMask.Visibility = Visibility.Visible;
            _selectionCompleted = false;
            this.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 0, 0));
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _updateTimer?.Stop();
            _screenBitmap?.Dispose();
        }
    }
}
