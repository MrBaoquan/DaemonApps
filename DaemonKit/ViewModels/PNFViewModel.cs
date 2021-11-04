using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Core;
using Microsoft.Win32;
using ReactiveUI;

namespace DaemonKit {

    public enum FormType {
        Create,
        Edit
    }
    public class PNFViewModel : ReactiveObject {
        private FormType formType = FormType.Create;
        private OpenFileDialog openFileDialog = new OpenFileDialog ();
        public PNFViewModel () {
            this.Confirm = ReactiveCommand.Create<ProcessMetaData> (() => {
                return new ProcessMetaData {
                Name = this.Name,
                Path = this.Path,
                Arguments = this.Arguments,
                RunAs = this.RunAs,
                KeepTop = this.KeepTop,
                Delay = this.Delay
                };
            });

            this.Cancel = ReactiveCommand.Create (() => { });

            this.SelectProcess = ReactiveCommand.Create (() => { });
            this.SelectProcess.Subscribe (_ => {
                openFileDialog.ShowDialog ();
            });

            openFileDialog.InitialDirectory = System.IO.Path.GetDirectoryName (Path);
            openFileDialog.Filter = "可执行文件(*.exe)|*.exe";
            openFileDialog.FileOk += (o, args) => {
                Path = openFileDialog.FileName;
                DNHper.NLogger.Info (Name);
                if (Name == "notepad" || Name == string.Empty)
                    Name = System.IO.Path.GetFileNameWithoutExtension (Path);
                openFileDialog.InitialDirectory = System.IO.Path.GetDirectoryName (Path);
            };
        }

        public bool IsCreateMode { get => formType == FormType.Create; }
        public bool IsEditMode { get => formType == FormType.Edit; }

        public void SyncCreateFormProperties () {
            formType = FormType.Create;
            this.Title = "新建进程结点";

            this.Name = "notepad";
            this.KeepTop = false;
            this.RunAs = true;
            this.Path = @"C:\Windows\System32\notepad.exe";

            openFileDialog.InitialDirectory = System.IO.Path.GetDirectoryName (Path);
        }

        public void SyncEditFormProperties (ProcessMetaData InMeta) {

            formType = FormType.Edit;

            this.Title = "进程结点编辑";
            this.Name = InMeta.Name;
            this.KeepTop = InMeta.KeepTop;
            this.RunAs = InMeta.RunAs;
            this.Path = InMeta.Path;
            this.Arguments = InMeta.Arguments;

            openFileDialog.InitialDirectory = System.IO.Path.GetDirectoryName (Path);
        }

        // 窗口标题
        private string title = "进程节点编辑";
        public string Title {
            get { return title; }
            set { this.RaiseAndSetIfChanged (ref title, value); }
        }

        private string name = "notepad";
        public string Name {
            get { return name; }
            set { this.RaiseAndSetIfChanged (ref name, value); }
        }

        // 进程路径
        private string path = @"C:\Windows\System32\notepad.exe";
        public string Path {
            get { return path; }
            set { this.RaiseAndSetIfChanged (ref path, value); }
        }

        public string arguments = string.Empty;
        public string Arguments { get => arguments; set => this.RaiseAndSetIfChanged (ref arguments, value); }

        private bool keepTop = true;
        public bool KeepTop {
            get { return keepTop; }
            set { this.RaiseAndSetIfChanged (ref keepTop, value); }
        }
        public bool runAs = false;
        public bool RunAs {
            get { return runAs; }
            set { this.RaiseAndSetIfChanged (ref runAs, value); }
        }

        private int delay = 500;
        public int Delay {
            get => delay;
            set => this.RaiseAndSetIfChanged (ref delay, Math.Max(value, 0));
        }

        public ReactiveCommand<Unit, Unit> SelectProcess { get; protected set; }
        public ReactiveCommand<Unit, ProcessMetaData> Confirm { get; protected set; }
        public ReactiveCommand<Unit, Unit> Cancel { get; protected set; }
    }
}