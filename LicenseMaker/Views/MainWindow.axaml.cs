using Avalonia.Controls;
using Avalonia.ReactiveUI;
using LicenseMaker.ViewModels;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System;
using Avalonia;
using DNHper;

namespace LicenseMaker.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();
            this.WhenActivated(disposables => {
                
                this.BindCommand(this.ViewModel, vm => vm.OnNewLicense, v => v.btnGen).DisposeWith(disposables);

                this.btnCopy.IsEnabled = false;
                this.ViewModel.OnNewLicense.Subscribe(_ =>
                {
                    this.textSecret.Text = _;
                    this.btnCopy.IsEnabled = !this.textSecret.Text.Contains("不正确");
                }).DisposeWith(disposables);

                // 注册btnCopy点击事件
                this.btnCopy.Click += (sender, e) =>
                {
                     Clipboard.SetTextAsync(this.textSecret.Text);
                };


                // inputID changeEvent
                this.inputID.GetObservable(TextBox.TextProperty).Subscribe(_ =>
                {
                    // 限制inputID最大长度为6
                    if (_.Length > 6)
                    {
                        this.ViewModel.MachineCode = _.Substring(0, 6);
                    }
                    this.btnGen.IsEnabled = this.ViewModel.MachineCode.Length == 6;
                }).DisposeWith(disposables);

                // 限制inputDays只能输入数字
                this.inputDays.GetObservable(TextBox.TextProperty).Subscribe(_ =>
                {
                    var _val = _.Parse2Int();
                    this.ViewModel.LicenseDuration = _val==0?"":_val.ToString();
                }).DisposeWith(disposables);
            });
          
        }
    }
}
