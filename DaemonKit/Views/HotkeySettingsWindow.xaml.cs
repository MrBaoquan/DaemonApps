using System.Windows;

namespace DaemonKit
{
    public partial class HotkeySettingsWindow : Window
    {
        public HotkeySettingsViewModel ViewModel { get; }

        public HotkeySettingsWindow()
        {
            InitializeComponent();
            ViewModel = new HotkeySettingsViewModel();
            DataContext = ViewModel;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
