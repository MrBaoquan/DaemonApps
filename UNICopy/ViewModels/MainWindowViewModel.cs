using System.Collections.Generic;
using System.Collections.ObjectModel;
using ReactiveUI;

namespace UNICopy.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        public MainWindowViewModel() { }

        public string Greeting => "Welcome to Avalonia!";

        public string[] FileCopyList { get; } = { "Item 1", "Item 2", "Item 3" };
    }
}
