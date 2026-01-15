using ReactiveUI;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using DaemonKit.Models;
using MaterialDesignThemes.Wpf;

namespace DaemonKit.ViewModels
{
    public class ProgressWindowViewModel : ReactiveObject
    {
        private string _operationTitle;
        private PackIconKind _operationIcon;
        private string _packagePath;
        private string _operationSummary;
        private string _statusMessage;
        private double _progressPercentage;
        private string _currentFile;
        private bool _isCompleted;
        private CancellationTokenSource _cancellationTokenSource;
        private PackageOperationType _operationType;

        public event EventHandler RequestClose;
        public event EventHandler RequestMinimize;

        public ProgressWindowViewModel(PackageOperationType operationType)
        {
            _operationType = operationType;

            // 根据操作类型设置标题和图标
            switch (operationType)
            {
                case PackageOperationType.Export:
                    OperationTitle = "正在打包软件包";
                    OperationIcon = PackIconKind.PackageUp;
                    break;
                case PackageOperationType.Import:
                    OperationTitle = "正在安装软件包";
                    OperationIcon = PackIconKind.PackageDown;
                    break;
            }

            PackagePath = "";
            OperationSummary = "";
            StatusMessage = "准备中...";
            CurrentFile = "";
            _cancellationTokenSource = new CancellationTokenSource();

            // 订阅进度消息 - 只处理对应操作类型的消息
            MessageBus.Current
                .Listen<PackageProgressInfo>()
                .Where(p => p.OperationType == _operationType)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnProgressUpdate);

            // 命令
            CancelCommand = ReactiveCommand.Create(OnCancel);
            CloseCommand = ReactiveCommand.Create(OnClose);
            MinimizeCommand = ReactiveCommand.Create(OnMinimize);
        }

        public string OperationTitle
        {
            get => _operationTitle;
            set => this.RaiseAndSetIfChanged(ref _operationTitle, value);
        }

        public PackIconKind OperationIcon
        {
            get => _operationIcon;
            set => this.RaiseAndSetIfChanged(ref _operationIcon, value);
        }

        public string PackagePath
        {
            get => _packagePath;
            set => this.RaiseAndSetIfChanged(ref _packagePath, value);
        }

        public string OperationSummary
        {
            get => _operationSummary;
            set => this.RaiseAndSetIfChanged(ref _operationSummary, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => this.RaiseAndSetIfChanged(ref _progressPercentage, value);
        }

        public string CurrentFile
        {
            get => _currentFile;
            set => this.RaiseAndSetIfChanged(ref _currentFile, value);
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set => this.RaiseAndSetIfChanged(ref _isCompleted, value);
        }

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }
        public ReactiveCommand<Unit, Unit> MinimizeCommand { get; }

        private void OnProgressUpdate(PackageProgressInfo progress)
        {
            if (progress.StatusMessage != null)
                StatusMessage = progress.StatusMessage;

            // 仅在ProgressPercentage > 0时更新，避免被重置为0
            if (progress.ProgressPercentage > 0)
                ProgressPercentage = progress.ProgressPercentage;

            if (progress.CurrentFile != null)
                CurrentFile = progress.CurrentFile;

            // 根据IsActive字段判断操作是否完成（而不是根据进度百分比）
            // IsActive=false表示操作结束（成功、失败或取消）
            if (!progress.IsActive)
            {
                IsCompleted = true;

                // 如果窗口被隐藏（最小化），操作完成时自动恢复显示
                if (progress.DialogInstance is Window window && !window.IsVisible)
                {
                    // 必须在UI线程执行窗口操作
                    window.Dispatcher.Invoke(() =>
                    {
                        window.Show();
                        window.WindowState = System.Windows.WindowState.Normal;
                        window.Activate();
                    });
                }
            }
        }

        private void OnCancel()
        {
            _cancellationTokenSource?.Cancel();
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void OnClose()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void OnMinimize()
        {
            RequestMinimize?.Invoke(this, EventArgs.Empty);
        }
    }
}
