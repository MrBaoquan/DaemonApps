using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ReactiveUI;

namespace DaemonKit
{
    public partial class CountdownConfirmDialog : ReactiveWindow<CountdownConfirmViewModel>
    {
        private CancellationTokenSource? _cancellationTokenSource;
        private DispatcherTimer? _countdownTimer;
        private System.Windows.Controls.TextBlock? _countdownTextBlock;
        private readonly object _timerLock = new();

        public CountdownConfirmDialog()
        {
            InitializeComponent();
            Loaded += CountdownConfirmDialog_Loaded;
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            // 获取倒计时数字 TextBlock
            _countdownTextBlock =
                FindName("CountdownTextBlock") as System.Windows.Controls.TextBlock;
            // 确保 DataContext 已经设置
            if (DataContext == null && ViewModel != null)
            {
                DataContext = ViewModel;
            }
        }

        private void CountdownConfirmDialog_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: ViewModel is null in Loaded event");
                return;
            }

            // 获取 TextBlock 引用
            _countdownTextBlock =
                FindName("CountdownTextBlock") as System.Windows.Controls.TextBlock;

            System.Diagnostics.Debug.WriteLine(
                $"Countdown started with value: {ViewModel.Countdown}"
            );

            StartCountdownTimer();
        }

        private void StartCountdownTimer()
        {
            lock (_timerLock)
            {
                StopCountdownTimerInternal();

                _cancellationTokenSource = new CancellationTokenSource();
                _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _countdownTimer.Tick += OnCountdownTick;

                UpdateCountdownText();
                _countdownTimer.Start();
            }
        }

        public void ResetCountdown(int seconds)
        {
            if (ViewModel == null)
                return;

            lock (_timerLock)
            {
                ViewModel.Countdown = seconds;
                UpdateCountdownText();
                StopCountdownTimerInternal();

                _cancellationTokenSource = new CancellationTokenSource();
                _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _countdownTimer.Tick += OnCountdownTick;
                _countdownTimer.Start();
            }
        }

        private void UpdateCountdownText()
        {
            if (_countdownTextBlock != null && ViewModel != null)
            {
                _countdownTextBlock.Text = ViewModel.Countdown.ToString();
            }
        }

        private void OnCountdownTick(object? sender, EventArgs e)
        {
            if (_cancellationTokenSource?.IsCancellationRequested == true || ViewModel == null)
            {
                StopCountdownTimerInternal();
                return;
            }

            try
            {
                if (ViewModel.Countdown > 0)
                {
                    ViewModel.Countdown--;
                    UpdateCountdownText();
                    System.Diagnostics.Debug.WriteLine($"Countdown: {ViewModel.Countdown}");
                }

                if (ViewModel.Countdown <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("Countdown reached 0, auto-closing dialog");
                    StopCountdownTimerInternal();
                    DialogResult = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Countdown timer error: {ex.Message}");
                StopCountdownTimerInternal();
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            StopCountdownTimer();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            StopCountdownTimer();
            DialogResult = false;
            Close();
        }

        private void TextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            var textBlock = sender as System.Windows.Controls.TextBlock;
            if (textBlock != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"TextBlock loaded, DataContext: {textBlock.DataContext}"
                );
            }
        }

        private void StopCountdownTimer()
        {
            lock (_timerLock)
            {
                StopCountdownTimerInternal();
                _cancellationTokenSource?.Cancel();
            }
        }

        private void StopCountdownTimerInternal()
        {
            if (_countdownTimer != null)
            {
                _countdownTimer.Tick -= OnCountdownTick;
                _countdownTimer.Stop();
                _countdownTimer = null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                StopCountdownTimer();
            }
            catch { }
            finally
            {
                _cancellationTokenSource?.Dispose();
                base.OnClosed(e);
            }
        }
    }
}
