using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace auth_ghost
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        public static extern int Validate(
            [MarshalAs(UnmanagedType.BStr)] string appid,
            int uiFlag = 0
        );

        public MainWindow()
        {
            InitializeComponent();
            Validate("App");
        }
    }
}