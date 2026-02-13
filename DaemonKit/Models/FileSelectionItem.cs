using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace DaemonKit.Models
{
    /// <summary>
    /// 文件树节点 — 用于导出补丁包时以树形结构展示并勾选文件/文件夹
    /// </summary>
    public class FileTreeNode : ReactiveObject
    {
        /// <summary>节点名称（文件名或文件夹名）</summary>
        public string Name { get; }

        /// <summary>相对于程序根目录的完整路径（文件夹末尾无分隔符）</summary>
        public string RelativePath { get; }

        /// <summary>是否为文件夹</summary>
        public bool IsDirectory { get; }

        /// <summary>文件大小（仅文件节点有值）</summary>
        public long FileSize { get; }

        /// <summary>格式化后的文件大小（文件夹显示汇总大小）</summary>
        public string FormattedSize =>
            IsDirectory ? FormatFileSize(GetTotalSize()) : FormatFileSize(FileSize);

        /// <summary>子节点列表</summary>
        public ObservableCollection<FileTreeNode> Children { get; } =
            new ObservableCollection<FileTreeNode>();

        private bool _isExpanded;

        /// <summary>树节点是否展开</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
        }

        private bool _isVisible = true;

        /// <summary>过滤后是否可见</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        private bool? _isSelected = true;

        /// <summary>
        /// 选中状态：true=全选, false=全不选, null=部分选中（仅文件夹）
        /// </summary>
        public bool? IsSelected
        {
            get => _isSelected;
            set
            {
                // 禁止用户手动点击到不确定状态（"-"）
                // 不确定状态仅由 UpdateSelectionFromChildren 设置
                if (value == null && !_updatingFromChildren)
                {
                    value = false;
                }

                if (_isSelected == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isSelected, value);

                // 向下级联：文件夹选中/取消 → 所有子节点同步
                if (value.HasValue && IsDirectory && !_updatingFromChildren)
                {
                    _updatingChildren = true;
                    foreach (var child in Children)
                        child.IsSelected = value.Value;
                    _updatingChildren = false;
                }

                // 向上通知父节点重新计算
                if (!_updatingChildren)
                {
                    _parent?.UpdateSelectionFromChildren();
                }
            }
        }

        private FileTreeNode _parent;
        private bool _updatingFromChildren;
        private bool _updatingChildren;

        /// <summary>
        /// 由子节点状态变化触发，重新计算当前文件夹的选中状态
        /// </summary>
        private void UpdateSelectionFromChildren()
        {
            if (!IsDirectory || Children.Count == 0)
                return;

            _updatingFromChildren = true;

            var allSelected = Children.All(c => c.IsSelected == true);
            var noneSelected = Children.All(c => c.IsSelected == false);

            if (allSelected)
                IsSelected = true;
            else if (noneSelected)
                IsSelected = false;
            else
                IsSelected = null; // 部分选中

            _updatingFromChildren = false;
        }

        public FileTreeNode(
            string name,
            string relativePath,
            bool isDirectory,
            long fileSize = 0,
            FileTreeNode parent = null
        )
        {
            Name = name;
            RelativePath = relativePath;
            IsDirectory = isDirectory;
            FileSize = fileSize;
            _parent = parent;
            _isExpanded = isDirectory; // 文件夹默认展开
        }

        /// <summary>递归获取所有选中的文件相对路径</summary>
        public IEnumerable<string> GetSelectedFiles()
        {
            if (!IsDirectory)
            {
                if (IsSelected == true)
                    yield return RelativePath;
                yield break;
            }

            foreach (var child in Children)
            {
                foreach (var file in child.GetSelectedFiles())
                    yield return file;
            }
        }

        /// <summary>递归获取所有文件节点（用于统计）</summary>
        public IEnumerable<FileTreeNode> GetAllFileNodes()
        {
            if (!IsDirectory)
            {
                yield return this;
                yield break;
            }
            foreach (var child in Children)
            {
                foreach (var node in child.GetAllFileNodes())
                    yield return node;
            }
        }

        /// <summary>递归获取此节点下的文件总大小</summary>
        public long GetTotalSize()
        {
            if (!IsDirectory)
                return FileSize;
            return Children.Sum(c => c.GetTotalSize());
        }

        /// <summary>
        /// 递归排序子节点：文件夹在前、文件在后，各自按名称升序（Windows 资源管理器风格）
        /// </summary>
        public void SortChildren()
        {
            if (!IsDirectory || Children.Count == 0)
                return;

            var sorted = Children
                .OrderByDescending(c => c.IsDirectory)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Children.Clear();
            foreach (var child in sorted)
            {
                Children.Add(child);
                child.SortChildren();
            }
        }

        /// <summary>
        /// 应用过滤器，返回当前节点或后代是否匹配
        /// </summary>
        public bool ApplyFilter(string filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                // 无过滤：全部可见
                IsVisible = true;
                foreach (var child in Children)
                    child.ApplyFilter(filterText);
                return true;
            }

            if (IsDirectory)
            {
                bool anyChildVisible = false;
                foreach (var child in Children)
                {
                    if (child.ApplyFilter(filterText))
                        anyChildVisible = true;
                }
                IsVisible = anyChildVisible;
                if (anyChildVisible)
                    IsExpanded = true;
                return anyChildVisible;
            }
            else
            {
                bool match = Name.Contains(filterText, StringComparison.OrdinalIgnoreCase);
                IsVisible = match;
                return match;
            }
        }

        /// <summary>
        /// 从相对路径列表构建文件树
        /// </summary>
        /// <param name="rootDir">程序根目录绝对路径</param>
        /// <returns>顶层节点列表（根目录下的直接子文件/文件夹）</returns>
        public static ObservableCollection<FileTreeNode> BuildTree(string rootDir)
        {
            var root = new FileTreeNode(Path.GetFileName(rootDir), "", true);
            var allFiles = Directory.GetFiles(rootDir, "*", SearchOption.AllDirectories);

            foreach (var fullPath in allFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(rootDir, fullPath).Replace('\\', '/');
                var parts = relativePath.Split('/');
                var current = root;

                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    var isFile = (i == parts.Length - 1);

                    if (isFile)
                    {
                        var fileInfo = new FileInfo(fullPath);
                        var fileNode = new FileTreeNode(
                            part,
                            relativePath,
                            false,
                            fileInfo.Length,
                            current
                        );
                        current.Children.Add(fileNode);
                    }
                    else
                    {
                        // 查找或创建文件夹节点
                        var dirPath = string.Join("/", parts.Take(i + 1));
                        var existing = current.Children.FirstOrDefault(
                            c => c.IsDirectory && c.Name == part
                        );
                        if (existing != null)
                        {
                            current = existing;
                        }
                        else
                        {
                            var dirNode = new FileTreeNode(part, dirPath, true, parent: current);
                            current.Children.Add(dirNode);
                            current = dirNode;
                        }
                    }
                }
            }

            // 排序：文件夹在前，文件在后（Windows 资源管理器风格）
            root.SortChildren();

            // 返回根节点的 children（不含根自身）
            return root.Children;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
