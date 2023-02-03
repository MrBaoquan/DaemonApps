using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        const string DEFAULT_APP_NAME = "示例程序";
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
                NoDaemon = this.NoDaemon,
                MinimizedStartUp = this.MinimizedStartUp,
                Delay = this.Delay,
                PosX = this.PosX,
                PosY = this.PosY,
                Width = this.Width,
                Height = this.Height,
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
                var _path = openFileDialog.FileName;
                Path = _path.Replace (AppPathes.AppDir + "\\", "");
                DNHper.NLogger.Info (Path);
                if (Name == DEFAULT_APP_NAME || Name == string.Empty)
                    Name = System.IO.Path.GetFileNameWithoutExtension (Path);
                openFileDialog.InitialDirectory = System.IO.Path.GetDirectoryName (Path);
            };
        }

        public bool IsCreateMode { get => formType == FormType.Create; }
        public bool IsEditMode { get => formType == FormType.Edit; }

        public void SyncCreateFormProperties () {
            formType = FormType.Create;
            this.Title = "新建进程结点";

            this.Name = DEFAULT_APP_NAME;
            this.KeepTop = false;
            this.NoDaemon = false;
            this.MinimizedStartUp = false;
            this.RunAs = true;
            this.Path = "demo.exe";

            openFileDialog.InitialDirectory = AppPathes.AppDir;
            if (!File.Exists (System.IO.Path.Combine (AppPathes.AppDir, "demo.exe"))) {
                openFileDialog.ShowDialog ();
            }
        }

        public void SyncEditFormProperties (ProcessMetaData InMeta) {

            formType = FormType.Edit;

            this.Title = "进程结点编辑";
            this.Name = InMeta.Name;
            this.KeepTop = InMeta.KeepTop;
            this.NoDaemon = InMeta.NoDaemon;
            this.MinimizedStartUp = InMeta.MinimizedStartUp;
            this.RunAs = InMeta.RunAs;
            this.Path = InMeta.Path;
            this.Arguments = InMeta.Arguments;
            this.Delay = InMeta.Delay;
            this.PosX = InMeta.PosX;
            this.PosY = InMeta.PosY;
            this.Width = InMeta.Width;
            this.Height = InMeta.Height;

            openFileDialog.InitialDirectory = System.IO.Path.GetDirectoryName (Path);
        }

        // 窗口标题
        private string title = "进程节点编辑";
        public string Title {
            get { return title; }
            set { this.RaiseAndSetIfChanged (ref title, value); }
        }

        private string name = string.Empty;
        public string Name {
            get { return name; }
            set { this.RaiseAndSetIfChanged (ref name, value); }
        }

        // 进程路径
        private string path = string.Empty;
        public string Path {
            get { return path; }
            set { this.RaiseAndSetIfChanged (ref path, value); }
        }

        public string arguments = string.Empty;
        public string Arguments { get => arguments; set => this.RaiseAndSetIfChanged (ref arguments, value); }

        private bool keepTop = true;
        public bool KeepTop {
            get { return keepTop; }
            set {
                this.RaiseAndSetIfChanged (ref keepTop, value);
                if (value) NoDaemon = false;
            }
        }
        public bool runAs = false;
        public bool RunAs {
            get { return runAs; }
            set { this.RaiseAndSetIfChanged (ref runAs, value); }
        }

        private bool noDaemon = false;
        public bool NoDaemon {
            get { return noDaemon; }
            set {
                this.RaiseAndSetIfChanged (ref noDaemon, value);
                if (value) KeepTop = false;
            }
        }

        private bool minimizedStartUp = false;
        public bool MinimizedStartUp {
            get { return minimizedStartUp; }
            set { this.RaiseAndSetIfChanged (ref minimizedStartUp, value); }
        }

        private int delay = 500;
        public int Delay {
            get => delay;
            set => this.RaiseAndSetIfChanged (ref delay, Math.Max (value, 0));
        }

        private int posX = 0;
        public int PosX {
            get => posX;
            set => this.RaiseAndSetIfChanged (ref posX, value);
        }
        private int posY = 0;
        public int PosY {
            get => posY;
            set => this.RaiseAndSetIfChanged (ref posY, value);
        }

        private int width = 0;
        public int Width {
            get => width;
            set => this.RaiseAndSetIfChanged (ref width, value);
        }

        private int height = 0;
        public int Height {
            get => height;
            set => this.RaiseAndSetIfChanged (ref height, value);
        }

        public ReactiveCommand<Unit, Unit> SelectProcess { get; protected set; }
        public ReactiveCommand<Unit, ProcessMetaData> Confirm { get; protected set; }
        public ReactiveCommand<Unit, Unit> Cancel { get; protected set; }
    }
}