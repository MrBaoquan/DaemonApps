using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DNHper;
using Microsoft.Win32;
using ReactiveUI;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 节点包对话框 ViewModel — 处理 NodeFull / NodePatch 的应用流程
    /// 匹配逻辑: targetExe → targetNodeName → 手动选择
    /// </summary>
    public class NodePackageDialogViewModel : ReactiveObject
    {
        private readonly Action<bool> _closeDialog;
        private CancellationTokenSource _cts;

        /// <summary>进程树根节点（用于新增节点时添加到树上）</summary>
        private readonly ProcessItem _rootNode;

        #region 属性

        private string _packagePath;

        /// <summary>包文件路径</summary>
        public string PackagePath
        {
            get => _packagePath;
            set => this.RaiseAndSetIfChanged(ref _packagePath, value);
        }

        private PackageManifest _manifest;

        /// <summary>解析后的清单</summary>
        public PackageManifest Manifest
        {
            get => _manifest;
            set => this.RaiseAndSetIfChanged(ref _manifest, value);
        }

        private string _packageTypeText;

        /// <summary>包类型显示文本</summary>
        public string PackageTypeText
        {
            get => _packageTypeText;
            set => this.RaiseAndSetIfChanged(ref _packageTypeText, value);
        }

        private string _patchModeText;

        /// <summary>应用模式显示文本</summary>
        public string PatchModeText
        {
            get => _patchModeText;
            set => this.RaiseAndSetIfChanged(ref _patchModeText, value);
        }

        private string _targetInfo;

        /// <summary>目标程序信息（exeName + version）</summary>
        public string TargetInfo
        {
            get => _targetInfo;
            set => this.RaiseAndSetIfChanged(ref _targetInfo, value);
        }

        private string _description;

        /// <summary>描述</summary>
        public string Description
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        private string _matchStatus;

        /// <summary>匹配状态文本</summary>
        public string MatchStatus
        {
            get => _matchStatus;
            set => this.RaiseAndSetIfChanged(ref _matchStatus, value);
        }

        private string _statusMessage;

        /// <summary>状态消息（进度显示）</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private double _progressPercentage;

        /// <summary>进度百分比 0-100</summary>
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => this.RaiseAndSetIfChanged(ref _progressPercentage, value);
        }

        private bool _isApplying;

        /// <summary>是否正在应用中</summary>
        public bool IsApplying
        {
            get => _isApplying;
            set => this.RaiseAndSetIfChanged(ref _isApplying, value);
        }

        private bool _isLoaded;

        /// <summary>清单是否已加载</summary>
        public bool IsLoaded
        {
            get => _isLoaded;
            set => this.RaiseAndSetIfChanged(ref _isLoaded, value);
        }

        private bool _showNodeSelection;

        /// <summary>是否显示节点选择列表（多匹配或无匹配时）</summary>
        public bool ShowNodeSelection
        {
            get => _showNodeSelection;
            set => this.RaiseAndSetIfChanged(ref _showNodeSelection, value);
        }

        private bool _showPatchModeSelection;

        /// <summary>是否显示补丁模式选择（仅 NodePatch/NodeFull 时显示）</summary>
        public bool ShowPatchModeSelection
        {
            get => _showPatchModeSelection;
            set => this.RaiseAndSetIfChanged(ref _showPatchModeSelection, value);
        }

        private bool _isOverlayMode = true;

        /// <summary>是否为覆盖模式</summary>
        public bool IsOverlayMode
        {
            get => _isOverlayMode;
            set => this.RaiseAndSetIfChanged(ref _isOverlayMode, value);
        }

        private bool _isReplaceMode;

        /// <summary>是否为替换模式</summary>
        public bool IsReplaceMode
        {
            get => _isReplaceMode;
            set => this.RaiseAndSetIfChanged(ref _isReplaceMode, value);
        }

        private ProcessItem _selectedNode;

        /// <summary>用户选中的目标节点</summary>
        public ProcessItem SelectedNode
        {
            get => _selectedNode;
            set => this.RaiseAndSetIfChanged(ref _selectedNode, value);
        }

        /// <summary>候选节点列表（供用户手动选择）</summary>
        public ObservableCollection<ProcessItem> CandidateNodes { get; } =
            new ObservableCollection<ProcessItem>();

        private bool _isAddNewMode;

        /// <summary>是否为新增节点模式（全量包未找到匹配时）</summary>
        public bool IsAddNewMode
        {
            get => _isAddNewMode;
            set
            {
                this.RaiseAndSetIfChanged(ref _isAddNewMode, value);
                this.RaisePropertyChanged(nameof(ShowInstallDirectory));
            }
        }

        private string _installDirectory;

        /// <summary>新增节点时的安装目录</summary>
        public string InstallDirectory
        {
            get => _installDirectory;
            set => this.RaiseAndSetIfChanged(ref _installDirectory, value);
        }

        /// <summary>是否显示安装目录区域</summary>
        public bool ShowInstallDirectory => IsAddNewMode;

        #endregion

        #region 命令

        /// <summary>确认应用</summary>
        public ReactiveCommand<Unit, Unit> ApplyCommand { get; }

        /// <summary>取消</summary>
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        /// <summary>浏览安装目录</summary>
        public ReactiveCommand<Unit, Unit> BrowseInstallDirCommand { get; }

        #endregion

        /// <summary>
        /// 所有进程节点（扁平列表，由外部传入）
        /// </summary>
        private readonly List<ProcessItem> _allNodes;

        public NodePackageDialogViewModel(
            Action<bool> closeDialog,
            List<ProcessItem> allNodes,
            ProcessItem rootNode = null
        )
        {
            _closeDialog = closeDialog;
            _allNodes = allNodes ?? new List<ProcessItem>();
            _rootNode = rootNode;

            // 全量包新增模式：SelectedNode 可以为 null，只要 IsAddNewMode + 有安装目录即可
            var canApply = this.WhenAnyValue(
                x => x.IsLoaded,
                x => x.IsApplying,
                x => x.SelectedNode,
                x => x.IsAddNewMode,
                x => x.InstallDirectory,
                (loaded, applying, node, addNew, installDir) =>
                    loaded
                    && !applying
                    && (node != null || (addNew && !string.IsNullOrWhiteSpace(installDir)))
            );

            ApplyCommand = ReactiveCommand.CreateFromTask(ApplyAsync, canApply);
            CancelCommand = ReactiveCommand.Create(() =>
            {
                _cts?.Cancel();
                _closeDialog(false);
            });

            BrowseInstallDirCommand = ReactiveCommand.Create(() =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "选择程序安装目录",
                    ShowNewFolderButton = true,
                    UseDescriptionForTitle = true
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    InstallDirectory = dialog.SelectedPath;
                }
            });
        }

        /// <summary>
        /// 加载补丁包信息并自动匹配节点
        /// </summary>
        public async Task LoadPackageAsync(string packagePath)
        {
            try
            {
                PackagePath = packagePath;
                StatusMessage = "读取包清单...";

                var manifest = await ExportImportService.ReadManifestAsync(packagePath);
                if (manifest == null)
                {
                    StatusMessage = "无法读取包清单（manifest.json）";
                    return;
                }

                Manifest = manifest;

                // 填充显示信息
                PackageTypeText = manifest.PackageType switch
                {
                    PackageType.NodeFull => "全量更新包",
                    PackageType.NodePatch => "增量补丁包",
                    PackageType.TreeBundle => "进程树包",
                    _ => "未知"
                };

                if (
                    manifest.PackageType == PackageType.NodePatch
                    || manifest.PackageType == PackageType.NodeFull
                )
                {
                    ShowPatchModeSelection = true;
                    // 全量包默认替换模式，补丁包默认覆盖模式
                    if (manifest.PackageType == PackageType.NodeFull)
                    {
                        IsReplaceMode = true;
                        IsOverlayMode = false;
                        PatchModeText = "替换模式（清空后全量替换）";
                    }
                    else
                    {
                        IsOverlayMode = true;
                        IsReplaceMode = false;
                        PatchModeText = "覆盖模式（保留已有文件）";
                    }
                }
                else
                {
                    ShowPatchModeSelection = false;
                    PatchModeText = "不适用";
                }

                var target = manifest.Target;
                if (target != null)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(target.ExeName))
                        parts.Add(target.ExeName);
                    if (!string.IsNullOrEmpty(target.Version))
                        parts.Add($"v{target.Version}");
                    if (!string.IsNullOrEmpty(target.ProgramType))
                        parts.Add(target.ProgramType);
                    TargetInfo = string.Join("  |  ", parts);
                }

                Description = manifest.Description;

                // 执行节点匹配
                MatchNodes(manifest);

                IsLoaded = true;
                StatusMessage = string.Empty;
            }
            catch (Exception ex)
            {
                StatusMessage = $"加载失败: {ex.Message}";
                NLogger.Error($"[节点包] 加载清单失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 节点匹配逻辑:
        /// 1. targetExe 主匹配 — 按 exe 文件名匹配（不区分大小写）
        /// 2. targetNodeName 回退匹配 — 按节点显示名匹配
        /// 3. 零匹配 → 显示完整列表供手动选择
        /// 4. 多匹配 → 显示候选列表供用户选择
        /// </summary>
        private void MatchNodes(PackageManifest manifest)
        {
            var target = manifest.Target;
            if (target == null)
            {
                // 没有 target 信息，只能手动选择
                ShowFullNodeList("包中未指定目标程序");
                return;
            }

            // 只匹配非 SuperRoot 的节点
            var candidates = _allNodes.Where(n => !n.IsSuperRoot).ToList();

            // 第一步: targetExe 匹配
            if (!string.IsNullOrEmpty(target.ExeName))
            {
                var exeMatches = candidates
                    .Where(
                        n =>
                            !string.IsNullOrEmpty(n.MetaData?.Path)
                            && string.Equals(
                                System.IO.Path.GetFileName(n.MetaData.Path),
                                target.ExeName,
                                StringComparison.OrdinalIgnoreCase
                            )
                    )
                    .ToList();

                if (exeMatches.Count == 1)
                {
                    // 精确匹配
                    SelectedNode = exeMatches[0];
                    MatchStatus = $"✓ 已匹配: {SelectedNode.MetaData.Name}（{target.ExeName}）";
                    ShowNodeSelection = false;
                    return;
                }
                else if (exeMatches.Count > 1)
                {
                    // 多匹配，展示候选列表
                    ShowCandidateList(
                        exeMatches,
                        $"找到 {exeMatches.Count} 个匹配 {target.ExeName} 的节点，请选择："
                    );
                    return;
                }
            }

            // 第二步: targetNodeName 回退匹配
            if (!string.IsNullOrEmpty(target.NodeName))
            {
                var nameMatches = candidates
                    .Where(
                        n =>
                            !string.IsNullOrEmpty(n.MetaData?.Name)
                            && string.Equals(
                                n.MetaData.Name,
                                target.NodeName,
                                StringComparison.OrdinalIgnoreCase
                            )
                    )
                    .ToList();

                if (nameMatches.Count == 1)
                {
                    SelectedNode = nameMatches[0];
                    MatchStatus = $"✓ 已匹配（按名称）: {SelectedNode.MetaData.Name}";
                    ShowNodeSelection = false;
                    return;
                }
                else if (nameMatches.Count > 1)
                {
                    ShowCandidateList(
                        nameMatches,
                        $"找到 {nameMatches.Count} 个名为「{target.NodeName}」的节点，请选择："
                    );
                    return;
                }
            }

            // 第三步: 零匹配
            // 全量包：允许新增节点（不必手动选择已有节点）
            if (manifest.PackageType == PackageType.NodeFull)
            {
                IsAddNewMode = true;
                MatchStatus = $"未找到匹配「{target.ExeName}」的节点，将新增为新节点。\n也可从下方列表选择已有节点进行覆盖：";
                ShowFullNodeList(null); // 仍显示候选列表供可选覆盖
                return;
            }

            // 补丁包：必须选择已有节点
            ShowFullNodeList($"未找到匹配「{target.ExeName}」的节点，请手动选择：");
        }

        private void ShowCandidateList(List<ProcessItem> matches, string message)
        {
            CandidateNodes.Clear();
            foreach (var node in matches)
                CandidateNodes.Add(node);

            MatchStatus = message;
            ShowNodeSelection = true;

            // 默认选中第一个
            if (matches.Count > 0)
                SelectedNode = matches[0];
        }

        private void ShowFullNodeList(string message)
        {
            var candidates = _allNodes
                .Where(n => !n.IsSuperRoot && !string.IsNullOrEmpty(n.MetaData?.Path))
                .ToList();

            CandidateNodes.Clear();
            foreach (var node in candidates)
                CandidateNodes.Add(node);

            if (!string.IsNullOrEmpty(message))
                MatchStatus = message;
            ShowNodeSelection = true;

            // 非 AddNewMode 时默认选中第一个
            if (!IsAddNewMode && candidates.Count > 0)
                SelectedNode = candidates[0];
        }

        /// <summary>
        /// 执行应用操作:
        /// - 已有节点: 停止进程 → 应用包 → 重启进程
        /// - 新增节点: 解压到指定目录 → 创建节点 → 添加到进程树
        /// </summary>
        private async Task ApplyAsync()
        {
            if (Manifest == null)
                return;

            // 新增模式：SelectedNode 可以为 null
            var isAddNew = IsAddNewMode && SelectedNode == null;

            if (!isAddNew && SelectedNode == null)
                return;

            IsApplying = true;
            _cts = new CancellationTokenSource();

            try
            {
                if (isAddNew)
                {
                    await ApplyAsNewNodeAsync();
                }
                else
                {
                    await ApplyToExistingNodeAsync();
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "操作已取消";
            }
            catch (Exception ex)
            {
                StatusMessage = $"更新失败: {ex.Message}";
                NLogger.Error($"[节点包] 更新失败: {ex.Message}");
            }
            finally
            {
                IsApplying = false;
            }
        }

        /// <summary>
        /// 新增节点模式：解压到用户指定目录，在进程树中创建新节点
        /// </summary>
        private async Task ApplyAsNewNodeAsync()
        {
            var target = Manifest.Target;
            var exeName = target?.ExeName ?? "unknown.exe";

            // 确定安装目录
            string targetDir;
            if (!string.IsNullOrWhiteSpace(InstallDirectory))
            {
                targetDir = InstallDirectory;
            }
            else
            {
                StatusMessage = "请先选择安装目录";
                IsApplying = false;
                return;
            }

            StatusMessage = "解压安装文件...";
            NLogger.Info($"[节点包] 新增节点安装到: {targetDir}");

            var statusProgress = new Progress<string>(msg =>
            {
                Application.Current.Dispatcher.Invoke(() => StatusMessage = msg);
            });
            var decompressionProgress = new Progress<CompressionProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProgressPercentage = p.Percentage;
                });
            });

            var patchMode = IsReplaceMode ? PatchMode.Replace : PatchMode.Overlay;

            var success = await ExportImportService.ApplyNodePackageAsync(
                PackagePath,
                targetDir,
                Manifest,
                patchMode,
                statusProgress,
                decompressionProgress,
                _cts.Token
            );

            if (!success)
            {
                StatusMessage = "安装失败";
                IsApplying = false;
                return;
            }

            // 在进程树中创建新节点
            StatusMessage = "添加到进程树...";
            var exePath = Path.Combine(targetDir, exeName);
            var nodeName = target?.NodeName ?? Path.GetFileNameWithoutExtension(exeName);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var newNode = new ProcessItem
                {
                    MetaData = new ProcessMetaData
                    {
                        Name = nodeName,
                        Path = exePath,
                        Enable = true,
                        RunAs = true
                    }
                };

                if (_rootNode != null)
                {
                    _rootNode.AddChild(newNode);
                    NLogger.Info($"[节点包] 新节点已添加: {nodeName} -> {exePath}");
                }
                else
                {
                    NLogger.Warn("[节点包] 无法访问进程树根节点，新节点未添加到树中");
                }
            });

            StatusMessage = "安装完成！节点已添加到进程树。";
            NLogger.Info($"[节点包] 新增节点完成: {nodeName}");

            await Task.Delay(800);
            _closeDialog(true);
        }

        /// <summary>
        /// 更新已有节点模式：停止进程 → 应用包 → 重启进程
        /// </summary>
        private async Task ApplyToExistingNodeAsync()
        {
            var targetNode = SelectedNode;
            var targetDir = System.IO.Path.GetDirectoryName(targetNode.NodePath);

            if (string.IsNullOrEmpty(targetDir))
            {
                StatusMessage = "无法确定目标程序目录";
                return;
            }

            // 1. 停止目标进程节点
            StatusMessage = "停止目标进程...";
            NLogger.Info($"[节点包] 停止进程: {targetNode.MetaData.Name}");

            // 找到第二级根节点（RootNode），停止整棵子树
            var rootNode = targetNode.RootNode;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                rootNode.KillNode();
            });

            // 等待进程完全退出
            await Task.Delay(1500, _cts.Token);

            // 2. 应用包文件
            StatusMessage = "应用更新...";
            var statusProgress = new Progress<string>(msg =>
            {
                Application.Current.Dispatcher.Invoke(() => StatusMessage = msg);
            });
            var decompressionProgress = new Progress<CompressionProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProgressPercentage = p.Percentage;
                });
            });

            var patchMode = IsReplaceMode ? PatchMode.Replace : PatchMode.Overlay;

            var success = await ExportImportService.ApplyNodePackageAsync(
                PackagePath,
                targetDir,
                Manifest,
                patchMode,
                statusProgress,
                decompressionProgress,
                _cts.Token
            );

            if (!success)
            {
                StatusMessage = "应用失败";
                IsApplying = false;
                return;
            }

            // 3. 重启进程
            StatusMessage = "重启进程...";
            NLogger.Info($"[节点包] 重启进程: {targetNode.MetaData.Name}");

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                rootNode.RunNode();
            });

            StatusMessage = "更新完成！";
            NLogger.Info($"[节点包] 更新完成: {targetNode.MetaData.Name}");

            await Task.Delay(800);
            _closeDialog(true);
        }
    }
}
