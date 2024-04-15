using Avalonia.Controls;
using Avalonia.ReactiveUI;
using LicenseMaker.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System;
using Avalonia;
using DNHper;
using System.Runtime.InteropServices;

namespace LicenseMaker.Views
{

    public static class License
    {
        const string DllName = "LicHper";

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern bool Validate([MarshalAs(UnmanagedType.BStr)] string AppID);
    }

    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {

        public MainWindow()
        {
            InitializeComponent();
            this.WhenActivated(disposables =>
            {

                var _ret = License.Validate("appid");

                this.BindCommand(this.ViewModel, vm => vm.OnNewLicense, v => v.btnGen)
                    .DisposeWith(disposables);

                this.btnCopy.IsEnabled = false;
                this.ViewModel.OnNewLicense
                    .Subscribe(_ =>
                    {
                        this.textSecret.Text = _;
                        this.btnCopy.IsEnabled = !this.textSecret.Text.Contains("����ȷ");
                    })
                    .DisposeWith(disposables);

                // ע��btnCopy����¼�
                this.btnCopy.Click += (sender, e) =>
                {
                    Clipboard.SetTextAsync(this.textSecret.Text);
                };

                // inputID changeEvent
                this.inputID
                    .GetObservable(TextBox.TextProperty)
                    .Subscribe(_ =>
                    {
                        // ����inputID��󳤶�Ϊ6
                        if (_.Length > 6)
                        {
                            this.ViewModel.MachineCode = _.Substring(0, 6);
                        }
                        this.btnGen.IsEnabled = this.ViewModel.MachineCode.Length == 6;
                    })
                    .DisposeWith(disposables);

                // ����inputDaysֻ����������
                this.inputDays
                    .GetObservable(TextBox.TextProperty)
                    .Subscribe(_ =>
                    {
                        var _val = _.Parse2Int();
                        this.ViewModel.LicenseDuration = _val == 0 ? "" : _val.ToString();
                    })
                    .DisposeWith(disposables);
            });
        }
    }
}
