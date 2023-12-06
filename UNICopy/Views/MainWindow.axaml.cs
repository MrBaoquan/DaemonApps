using System.Threading;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Storage.FileIO;
using System;
using System.IO;
using System.Threading.Tasks;
using ReactiveUI;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using Avalonia.ReactiveUI;
using System.Reactive.Disposables;
using System.Diagnostics;

namespace UNICopy.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindow>
    {
        class CopyProgress
        {
            public int TotalCount { get; set; } = 0;
            public int TotalCopiedCount { get; set; } = 0;
            public int CurrentFolderTotalCount { get; set; } = 0;
            public int CurrentFolderCopiedCount { get; set; } = 0;
            public int CurrentFolderProgress { get; set; } = 0;
            public string CurrentSourceFile { get; set; } = string.Empty;
            public string CurrentDestinationFile { get; set; } = string.Empty;
            public string CurrentSourceFolder { get; set; } = string.Empty;
            public string CurrentDestinationFolder { get; set; } = string.Empty;
        }

        private IObservable<System.Uri> onSelectedFileAsObservable(string title = "Select a file")
        {
            var _filePickerOptions = new FilePickerOpenOptions();
            _filePickerOptions.AllowMultiple = false;
            _filePickerOptions.Title = title;
            return this.StorageProvider
                .OpenFilePickerAsync(_filePickerOptions)
                .ToObservable()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Select(_files => _files.First().Path);
        }

        private IObservable<System.Collections.Generic.IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder>> onSelectedFolderAsObservable(
            string title = "Select a folder"
        )
        {
            var _folderPickerOptions = new FolderPickerOpenOptions();
            _folderPickerOptions.AllowMultiple = false;
            _folderPickerOptions.Title = title;
            return this.StorageProvider
                .OpenFolderPickerAsync(_folderPickerOptions)
                .ToObservable()
                .ObserveOn(RxApp.MainThreadScheduler);
        }

        public MainWindow()
        {
            InitializeComponent();
            // set window size to 300x200
            this.Width = 300;
            this.Height = 200;
            ReactiveCommand<CopyProgress, CopyProgress> copyProgressCommand =
                ReactiveCommand.Create<CopyProgress, CopyProgress>(_copyProgress => _copyProgress);

            this.WhenActivated(_disposables =>
            {
                //Observable
                //    .Concat(
                //        onSelectedFileAsObservable("选择复制选项")
                //            .Select(_ => _.AbsolutePath)
                //            .OnErrorResumeNext(Observable.Return("")),
                //        onSelectedFolderAsObservable("选择源目录").Select(_ => _.First().Path.LocalPath),
                //        onSelectedFolderAsObservable("选择目标目录").Select(_ => _.First().Path.LocalPath)
                //    )
                //    .Buffer(3)
                //    .Subscribe(
                //        _paths =>
                //        {
                //            //urldecode
                //            for (int i = 0; i < _paths.Count; i++)
                //            {
                //                _paths[i] = Uri.UnescapeDataString(_paths[i]);
                //            }

                //            var _configPath = _paths[0];
                //            var _sourcePath = _paths[1];
                //            var _destinationPath = _paths[2];

                //            Task.Run(
                //                () =>
                //                    CopyFolder(
                //                        _sourcePath,
                //                        _destinationPath,
                //                        true,
                //                        _progress =>
                //                            copyProgressCommand.Execute(_progress).Subscribe()
                //                    )
                //            );

                //            this.logText.Text =
                //                $"configPath: {_configPath}\nsourcePath: {_sourcePath}\ndestinationPath: {_destinationPath}";
                //        },
                //        _error =>
                //        {
                //            Debug.WriteLine(_error);
                //        }
                //    )
                //    .DisposeWith(_disposables);

                //copyProgressCommand
                //    .ObserveOn(RxApp.MainThreadScheduler)
                //    .Subscribe(_copyProgress =>
                //    {
                //        this.copyProgress.Value =
                //            _copyProgress.TotalCopiedCount * 100.0f / _copyProgress.TotalCount;
                //        this.logText.Text =
                //            @$"CurrentSourceFile: {_copyProgress.CurrentSourceFile}
                //        CurrentDestinationFile: {_copyProgress.CurrentDestinationFile}
                //        TotalCopiedCount: {_copyProgress.TotalCopiedCount}
                //        TotalCount: {_copyProgress.TotalCount}
                //        TotalProgress: {_copyProgress.TotalCopiedCount * 100.0f / _copyProgress.TotalCount}%
                //        ";
                //    })
                //    .DisposeWith(_disposables);
            });
        }

        // 统计文件夹所有文件数量
        private int CountFiles(string sourceFolder)
        {
            int count = 0;
            string[] files = Directory.GetFiles(sourceFolder);
            count += files.Length;

            string[] subFolders = Directory.GetDirectories(sourceFolder);
            foreach (string subFolder in subFolders)
            {
                count += CountFiles(subFolder);
            }

            return count;
        }

        private void CopyFolder(
            string sourceFolder,
            string destFolder,
            bool recursive = true,
            Action<CopyProgress> progressCallback = null,
            Action<string> errorCallback = null
        )
        {
            CopyProgress copyProgress = new CopyProgress();
            var _sourceFiles = GetFiles(sourceFolder);
            copyProgress.TotalCount = _sourceFiles.Count;
            _sourceFiles.ForEach(_sourceFile =>
            {
                try
                {
                    var _destFile = _sourceFile.Replace(sourceFolder, destFolder);
                    var _destDir = Path.GetDirectoryName(_destFile);
                    if (!Directory.Exists(_destDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(_destDir);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                    }

                    copyProgress.TotalCopiedCount++;
                    copyProgress.CurrentSourceFile = _sourceFile;
                    copyProgress.CurrentDestinationFile = _destFile;
                    copyProgress.CurrentSourceFolder = Path.GetDirectoryName(_sourceFile);
                    copyProgress.CurrentDestinationFolder = _destDir;
                    progressCallback?.Invoke(copyProgress);
                    File.Copy(_sourceFile, _destFile, true);
                }
                catch (System.Exception e)
                {
                    errorCallback?.Invoke(_sourceFile);
                }
            });
        }

        private List<string> GetFiles(string sourceFolder, bool recursive = true)
        {
            List<string> files = new List<string>();
            string[] _files = Directory.GetFiles(sourceFolder);
            files.AddRange(_files);
            if (recursive)
            {
                string[] subFolders = Directory.GetDirectories(sourceFolder);
                foreach (string subFolder in subFolders)
                {
                    files.AddRange(GetFiles(subFolder));
                }
            }
            return files;
        }
    }
}
