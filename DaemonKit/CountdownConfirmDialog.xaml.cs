using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ReactiveUI;

namespace DaemonKit
{
    public partial class CountdownConfirmDialog : ReactiveWindow<CountdownConfirmViewModel>
    {
        private CancellationTokenSource _cancellationTokenSource;

        public CountdownConfirmDialog()
        {
            InitializeComponent();

            this.WhenActivated(disposables =>
            {
                if (ViewModel != null)
                {
                    ViewModel.Confirm.Subscribe(_ =>
                    {
                        DialogResult = true;
                        Close();
                    });

                    ViewModel.Cancel.Subscribe(_ =>
                    {
                        DialogResult = false;
                        Close();
                    });
                }
            });

            Loaded += CountdownConfirmDialog_Loaded;
        }

        private async void CountdownConfirmDialog_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
                return;

            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await Task.Run(
                    async () =>
                    {
                        while (
                            ViewModel.Countdown > 0
                            && !_cancellationTokenSource.Token.IsCancellationRequested
                        )
                        {
                            await Task.Delay(1000, _cancellationTokenSource.Token);
                            if (!_cancellationTokenSource.Token.IsCancellationRequested)
                            {
                                await Dispatcher.InvokeAsync(() => ViewModel.Countdown--);
                            }
                        }

                        // 倒计时结束，自动确认
                        if (
                            ViewModel.Countdown == 0
                            && !_cancellationTokenSource.Token.IsCancellationRequested
                        )
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                DialogResult = true;
                                Close();
                            });
                        }
                    },
                    _cancellationTokenSource.Token
                );
            }
            catch (TaskCanceledException)
            {
                // 任务被取消，忽略
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            base.OnClosed(e);
        }
    }
}
