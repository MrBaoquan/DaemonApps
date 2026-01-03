using System;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace DaemonKit
{
    public class CountdownConfirmViewModel : ReactiveObject
    {
        private int _countdown = 10;
        public int Countdown
        {
            get => _countdown;
            set => this.RaiseAndSetIfChanged(ref _countdown, value);
        }

        private string _message = "";
        public string Message
        {
            get => _message;
            set => this.RaiseAndSetIfChanged(ref _message, value);
        }

        private string _title = "确认操作";
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        public ReactiveCommand<Unit, bool> Confirm { get; protected set; }
        public ReactiveCommand<Unit, bool> Cancel { get; protected set; }

        public CountdownConfirmViewModel(string title, string message, int countdownSeconds = 10)
        {
            Title = title;
            Message = message;
            Countdown = countdownSeconds;

            Confirm = ReactiveCommand.Create(
                () => true,
                outputScheduler: RxApp.MainThreadScheduler
            );
            Cancel = ReactiveCommand.Create(
                () => false,
                outputScheduler: RxApp.MainThreadScheduler
            );
        }
    }
}
