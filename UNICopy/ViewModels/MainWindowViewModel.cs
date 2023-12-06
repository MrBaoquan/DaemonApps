using System.Reactive.Disposables;
using ReactiveUI;

namespace UNICopy.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IActivatableViewModel
    {
        public string Greeting => "Hello World";
        public ViewModelActivator Activator { get; }

        public MainWindowViewModel()
        {
            Activator = new ViewModelActivator();
            this.WhenActivated((CompositeDisposable _disposables) => { });
        }
    }
}
