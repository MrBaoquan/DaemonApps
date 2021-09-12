using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ReactiveUI;
using DaemonKit.Core;

namespace DaemonKit {
    public class MainViewModel : ReactiveObject {
        public MainViewModel () {
            DisplayCommand = ReactiveCommand.Create (
                () => this.WhenAny (x => x.Text, x => !string.IsNullOrEmpty (x.Value))
            );
            DisplayCommand.Subscribe (
                _ => MessageBox.Show ("You clicked on DisplayCommand: Name is " + Text)
            );

        }

        private string _Text;
        public string Text {
            get { return _Text; }
            set { this.RaiseAndSetIfChanged (ref _Text, value); }
        }

        public ReactiveCommand<Unit, IObservable<bool>> DisplayCommand { get; protected set; }
    }
}