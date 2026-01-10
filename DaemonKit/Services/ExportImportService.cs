using DaemonKit.Models;
using DaemonKit.Utilities;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DaemonKit.Services
{
    /// <summary>
    /// 导出导入包的元数据
    /// </summary>
    public class PackageMetadata
    {
        public string Version { get; set; } = "1.0";
        public DateTime CreatedAt { get; set; }
        public string MachineName { get; set; }
        public string UserName { get; set; }
        public List<string> IncludedConfigs { get; set; } = new List<string>();
        public List<ProgramInfo> IncludedPrograms { get; set; } = new List<ProgramInfo>();
        public string Description { get; set; }
    }

    /// <summary>
    /// 程序信息
    /// </summary>
    public class ProgramInfo
    {
        public string Name { get; set; }
        public string ExecutablePath { get; set; }
        public long SizeBytes { get; set; }
        public string ProgramType { get; set; } // "Unity", "UnrealEngine", "Other"
    }

    /// <summary>
    /// 导出导入服务 - 类似Docker的配置打包系统
    /// </summary>
    public class ExportImportService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string MetadataFileName = "metadata.json";
        private const string ConfigsDirName = "Configs";
        private const string ProgramsDirName = "Programs";

        /// <summary>
        /// 导出配置包
        /// </summary>
        public static async Task<bool> ExportPackageAsync(
            string packagePath,
            IEnumerable<string> configFiles,
            IEnumerable<ProcessItem> processTree,
            bool includeAllPrograms,
            string description,
            IProgress<string> statusProgress = null,
            IProgress<CompressionProgress> compressionProgress = null,
            IProgress<FileCopyProgress> copyProgress = null,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                statusProgress?.Report("开始导出配置包...");
                Logger.Info($"Starting package export to: {packagePath}");

                // 创建临时目录
                var tempDir = Path.Combine(
                    Path.GetTempPath(),
                    $"DaemonKit_Export_{Guid.NewGuid()}"
                );
                Directory.CreateDirectory(tempDir);

                try
                {
                    // 1. 创建目录结构
                    var configsDir = Path.Combine(tempDir, ConfigsDirName);
                    var programsDir = Path.Combine(tempDir, ProgramsDirName);
                    Directory.CreateDirectory(configsDir);
                    Directory.CreateDirectory(programsDir);

                    // 2. 复制配置文件
                    statusProgress?.Report("复制配置文件...");
                    var configList = configFiles.ToList();
                    foreach (var configFile in configList)
                    {
                        if (File.Exists(configFile))
                        {
                            var fileName = Path.GetFileName(configFile);
                            var destPath = Path.Combine(configsDir, fileName);
                            File.Copy(configFile, destPath, true);
                            Logger.Debug($"Copied config: {fileName}");
                        }
                    }

                    // 3. 导出程序文件
                    var programInfos = new List<ProgramInfo>();
                    if (includeAllPrograms && processTree != null)
                    {
                        statusProgress?.Report("分析程序树...");
                        var allPrograms = GetAllProgramsFromTree(processTree);

                        statusProgress?.Report($"导出 {allPrograms.Count} 个程序...");
                        programInfos = await ExportProgramFilesAsync(
                            allPrograms,
                            programsDir,
                            copyProgress,
                            cancellationToken
                        );
                    }

                    // 4. 创建元数据
                    statusProgress?.Report("生成元数据...");
                    var metadata = new PackageMetadata
                    {
                        CreatedAt = DateTime.Now,
                        MachineName = Environment.MachineName,
                        UserName = Environment.UserName,
                        IncludedConfigs = configList.Select(Path.GetFileName).ToList(),
                        IncludedPrograms = programInfos,
                        Description = description
                    };

                    var metadataPath = Path.Combine(tempDir, MetadataFileName);
                    var metadataJson = JsonConvert.SerializeObject(metadata, Formatting.Indented);
                    File.WriteAllText(metadataPath, metadataJson);

                    // 5. 导出进程树配置（并转换为相对路径）
                    // 方案B: 直接导出List<ProcessItem>，支持多节点选择
                    statusProgress?.Report("导出进程树配置...");
                    if (processTree != null && processTree.Any())
                    {
                        var processTreeList = processTree.ToList();

                        // 创建树的深拷贝，进行路径转换
                        var treeForExport = processTreeList
                            .Select(CloneProcessItemWithRelativePaths)
                            .Where(item => item != null)
                            .ToList();

                        var treeFileName = Path.GetFileName(AppPathes.TreeViewDataPath);
                        var treeExportPath = Path.Combine(configsDir, treeFileName);

                        var treeSerializer = new System.Xml.Serialization.XmlSerializer(
                            typeof(List<ProcessItem>)
                        );
                        using (var stream = File.Create(treeExportPath))
                        {
                            treeSerializer.Serialize(stream, treeForExport);
                            Logger.Info(
                                $"Process tree exported with relative paths: {treeForExport.Count} root nodes to {treeExportPath}"
                            );
                        }
                    }

                    // 6. 压缩到ZIP
                    statusProgress?.Report("压缩打包中...");
                    await HighPerformanceCompressor.CompressDirectoryAsync(
                        tempDir,
                        packagePath,
                        compressionProgress,
                        cancellationToken
                    );

                    statusProgress?.Report("导出完成！");
                    Logger.Info($"Package exported successfully: {packagePath}");
                    return true;
                }
                finally
                {
                    // 清理临时目录
                    try
                    {
                        if (Directory.Exists(tempDir))
                            Directory.Delete(tempDir, true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to delete temp directory");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to export package");
                statusProgress?.Report($"导出失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检测并迁移进程树中的绝对路径到相对路径
        /// </summary>
        public static (
            List<ProcessItem> AbsolutePathNodes,
            int MigratedCount
        ) MigrateProcessTreeToRelativePaths(
            IEnumerable<ProcessItem> processTree,
            bool migrateAll = false,
            IEnumerable<ProcessItem> selectedNodesToMigrate = null
        )
        {
            try
            {
                Logger.Info(
                    $"Starting process tree migration. migrateAll={migrateAll}, selectedCount={selectedNodesToMigrate?.Count() ?? 0}"
                );

                // 检测所有绝对路径节点
                var absolutePathNodes = DetectAbsolutePathNodes(processTree);
                Logger.Info($"Detected {absolutePathNodes.Count} nodes with absolute paths");

                int migratedCount = 0;

                if (migrateAll)
                {
                    // 迁移所有绝对路径节点
                    migratedCount = MigrateNodesToRelativePaths(absolutePathNodes);
                    Logger.Info(
                        $"Migrated all {migratedCount} absolute path nodes to relative paths"
                    );
                }
                else if (selectedNodesToMigrate != null)
                {
                    // 只迁移选中的节点
                    migratedCount = MigrateNodesToRelativePaths(selectedNodesToMigrate);
                    Logger.Info($"Migrated {migratedCount} selected nodes to relative paths");
                }

                return (absolutePathNodes, migratedCount);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to migrate process tree to relative paths");
                return (new List<ProcessItem>(), 0);
            }
        }

        /// <summary>
        /// 导入配置包
        /// </summary>
        public static async Task<bool> ImportPackageAsync(
            string packagePath,
            bool importConfigs, // 是否导入配置文件
            bool importProcesses, // 是否导入进程树和程序文件
            IEnumerable<ProcessItem> selectedProcessNodes,
            bool clearExistingTree,
            bool overwriteConflicts = true, // 新增：冲突时是否覆盖
            IProgress<string> statusProgress = null,
            IProgress<CompressionProgress> decompressionProgress = null,
            IProgress<FileCopyProgress> copyProgress = null,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                statusProgress?.Report("开始导入配置包...");
                Logger.Info($"Starting package import from: {packagePath}");

                if (!File.Exists(packagePath))
                {
                    statusProgress?.Report("包文件不存在！");
                    return false;
                }

                // 创建临时目录
                var tempDir = Path.Combine(
                    Path.GetTempPath(),
                    $"DaemonKit_Import_{Guid.NewGuid()}"
                );
                Directory.CreateDirectory(tempDir);

                try
                {
                    // 1. 解压ZIP
                    statusProgress?.Report("解压包文件...");
                    await HighPerformanceCompressor.DecompressZipAsync(
                        packagePath,
                        tempDir,
                        decompressionProgress,
                        cancellationToken
                    );

                    // 2. 读取元数据
                    statusProgress?.Report("读取元数据...");
                    var metadataPath = Path.Combine(tempDir, MetadataFileName);
                    if (!File.Exists(metadataPath))
                    {
                        statusProgress?.Report("包格式错误：缺少元数据文件！");
                        return false;
                    }

                    var metadataJson = File.ReadAllText(metadataPath);
                    var metadata = JsonConvert.DeserializeObject<PackageMetadata>(metadataJson);
                    Logger.Info(
                        $"Package metadata: Version={metadata.Version}, CreatedAt={metadata.CreatedAt}"
                    );

                    // 3. 导入配置文件
                    var configsDir = Path.Combine(tempDir, ConfigsDirName);
                    if (importConfigs && Directory.Exists(configsDir))
                    {
                        statusProgress?.Report("导入配置文件...");
                        await ImportConfigFilesAsync(
                            configsDir,
                            true, // 默认覆盖现有配置
                            copyProgress,
                            cancellationToken
                        );
                    }
                    else if (importConfigs)
                    {
                        Logger.Warn("用户选择导入配置，但包内没有配置文件目录");
                    }

                    // 4. 导入程序文件并获取路径映射
                    var programsDir = Path.Combine(tempDir, ProgramsDirName);
                    Dictionary<string, string> pathMappings = null;
                    if (importProcesses && Directory.Exists(programsDir))
                    {
                        statusProgress?.Report("导入程序文件...");
                        Logger.Info(
                            $"Programs directory exists: {programsDir}, importing programs..."
                        );
                        pathMappings = await ImportProgramFilesAsync(
                            programsDir,
                            selectedProcessNodes,
                            copyProgress,
                            cancellationToken
                        );
                        Logger.Info(
                            $"Program import completed. Path mappings: {pathMappings?.Count ?? 0}"
                        );
                    }
                    else if (importProcesses)
                    {
                        Logger.Warn($"用户选择导入进程，但包内没有程序文件目录");
                    }
                    else
                    {
                        Logger.Info($"Skipping program import: importProcesses={importProcesses}");
                    }

                    // 5. 处理进程树配置
                    var treeConfigPath = Path.Combine(
                        configsDir,
                        Path.GetFileName(AppPathes.TreeViewDataPath)
                    );
                    if (importProcesses && File.Exists(treeConfigPath))
                    {
                        statusProgress?.Report("处理进程树配置...");
                        Logger.Info($"Tree config file found: {treeConfigPath}");
                        await MergeProcessTreeAsync(
                            treeConfigPath,
                            selectedProcessNodes,
                            clearExistingTree,
                            overwriteConflicts, // 传递冲突覆盖策略
                            pathMappings,
                            statusProgress
                        );
                    }
                    else if (importProcesses)
                    {
                        Logger.Warn($"用户选择导入进程树，但配置文件不存在: {treeConfigPath}");
                    }

                    statusProgress?.Report("导入完成！");
                    Logger.Info("Package imported successfully");
                    return true;
                }
                finally
                {
                    // 清理临时目录
                    try
                    {
                        if (Directory.Exists(tempDir))
                            Directory.Delete(tempDir, true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to delete temp directory");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to import package");
                statusProgress?.Report($"导入失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 读取包元数据（不解压整个包）
        /// </summary>
        public static async Task<PackageMetadata> ReadPackageMetadataAsync(string packagePath)
        {
            try
            {
                var tempFile = Path.Combine(Path.GetTempPath(), $"metadata_{Guid.NewGuid()}.json");
                try
                {
                    await HighPerformanceCompressor.ExtractFileAsync(
                        packagePath,
                        MetadataFileName,
                        tempFile
                    );
                    var json = File.ReadAllText(tempFile);
                    return JsonConvert.DeserializeObject<PackageMetadata>(json);
                }
                finally
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to read package metadata");
                return null;
            }
        }

        /// <summary>
        /// 从包中读取进程树数据（不解压整个包）
        /// 方案B: 返回List<ProcessItem>，支持多节点
        /// </summary>
        public static async Task<List<ProcessItem>> ReadProcessTreeFromPackageAsync(
            string packagePath
        )
        {
            try
            {
                var tempFile = Path.Combine(Path.GetTempPath(), $"treedata_{Guid.NewGuid()}.xml");
                try
                {
                    // 尝试从包中提取treeViewData.xml
                    var treeFileName = Path.GetFileName(AppPathes.TreeViewDataPath);
                    var extractPath = $"Configs/{treeFileName}";

                    await HighPerformanceCompressor.ExtractFileAsync(
                        packagePath,
                        extractPath,
                        tempFile
                    );

                    if (!File.Exists(tempFile))
                        return null;

                    // 反序列化进程树（List<ProcessItem>）
                    var serializer = new System.Xml.Serialization.XmlSerializer(
                        typeof(List<ProcessItem>)
                    );
                    using (var stream = File.OpenRead(tempFile))
                    {
                        var nodeList = (List<ProcessItem>)serializer.Deserialize(stream);

                        // 重建父子关系
                        void RebuildParentRelations(ProcessItem node, ProcessItem parent = null)
                        {
                            if (node == null)
                                return;

                            node.Parent = parent;

                            if (node.Children != null && node.Children.Count > 0)
                            {
                                foreach (var child in node.Children)
                                {
                                    RebuildParentRelations(child, node);
                                }
                            }
                        }

                        // 对每个根节点重建父子关系和验证数据
                        if (nodeList != null)
                        {
                            foreach (var node in nodeList)
                            {
                                RebuildParentRelations(node);

                                // 调试：验证MetaData是否正确加载
                                if (node.MetaData != null)
                                {
                                    Logger.Debug(
                                        $"Loaded node: Name={node.MetaData.Name}, Path={node.MetaData.Path}"
                                    );
                                }
                                else
                                {
                                    Logger.Error($"Node with null MetaData! NodeId={node.NodeId}");
                                }
                            }
                        }

                        Logger.Info(
                            $"Process tree loaded from package: {nodeList?.Count ?? 0} root nodes"
                        );
                        return nodeList ?? new List<ProcessItem>();
                    }
                }
                finally
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to read process tree from package");
                return null;
            }
        }

        #region 私有辅助方法

        /// <summary>
        /// 深拷贝ProcessItem并将路径转换为相对路径
        /// </summary>
        private static ProcessItem CloneProcessItemWithRelativePaths(ProcessItem item)
        {
            if (item == null)
                return null;

            var clone = new ProcessItem
            {
                NodeId = item.NodeId,
                Parent = null, // 不保留父引用
            };

            // 克隆MetaData
            if (item.MetaData != null)
            {
                var applicationsDir = AppPathes.AppDir;
                var originalPath = item.MetaData.Path ?? "";
                var convertedPath = originalPath;

                Logger.Debug($"Converting export path for '{item.MetaData.Name}': {originalPath}");

                // 转换为相对路径：无论原始路径在哪，都提取为相对于Applications的相对路径
                if (!string.IsNullOrEmpty(originalPath))
                {
                    if (Path.IsPathRooted(originalPath))
                    {
                        // 绝对路径：检查是否在Applications目录下
                        if (
                            originalPath.StartsWith(
                                applicationsDir,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            // 在Applications下：直接提取相对路径
                            convertedPath = originalPath
                                .Substring(applicationsDir.Length)
                                .TrimStart(
                                    Path.DirectorySeparatorChar,
                                    Path.AltDirectorySeparatorChar
                                );
                            Logger.Debug(
                                $"  Converted to relative (from AppDir): {originalPath} -> {convertedPath}"
                            );
                        }
                        else
                        {
                            // 不在Applications下：使用程序名作为基准
                            // 例: F:\xxx\UNIPlayer@名人馆六尺巷\UNIPlayer.exe -> UNIPlayer@名人馆六尺巷\UNIPlayer.exe
                            var programDir = Path.GetDirectoryName(originalPath);
                            var programName = Path.GetFileName(programDir); // UNIPlayer@名人馆六尺巷
                            var fileName = Path.GetFileName(originalPath); // UNIPlayer.exe
                            convertedPath = Path.Combine(programName, fileName);
                            Logger.Debug(
                                $"  Converted to relative (from program name): {originalPath} -> {convertedPath}"
                            );
                        }
                    }
                    // 如果已经是相对路径，保持不变
                }

                clone.MetaData = new ProcessMetaData
                {
                    Name = item.MetaData.Name,
                    Delay = item.MetaData.Delay,
                    Path = convertedPath,
                    Arguments = item.MetaData.Arguments,
                    RunAs = item.MetaData.RunAs,
                    KeepTop = item.MetaData.KeepTop,
                    NoDaemon = item.MetaData.NoDaemon,
                    IsScript = item.MetaData.IsScript,
                    MoveWindow = item.MetaData.MoveWindow,
                    ResizeWindow = item.MetaData.ResizeWindow,
                    MinimizedStartUp = item.MetaData.MinimizedStartUp,
                    Enable = item.MetaData.Enable,
                    PosX = item.MetaData.PosX,
                    PosY = item.MetaData.PosY,
                    Width = item.MetaData.Width,
                    Height = item.MetaData.Height,
                    Triggers =
                        item.MetaData.Triggers != null
                            ? new List<TaskTrigger>(item.MetaData.Triggers)
                            : new List<TaskTrigger>()
                };
            }

            // 递归克隆子节点
            if (item.Children != null && item.Children.Count > 0)
            {
                clone.Children =
                    new System.Collections.ObjectModel.ObservableCollection<ProcessItem>();
                foreach (var child in item.Children)
                {
                    var clonedChild = CloneProcessItemWithRelativePaths(child);
                    if (clonedChild != null)
                    {
                        clonedChild.Parent = clone;
                        clone.Children.Add(clonedChild);
                    }
                }
            }

            return clone;
        }

        /// <summary>
        /// 检测进程树中是否包含绝对路径
        /// </summary>
        public static List<ProcessItem> DetectAbsolutePathNodes(
            IEnumerable<ProcessItem> processTree
        )
        {
            var absolutePathNodes = new List<ProcessItem>();

            void TraverseTree(IEnumerable<ProcessItem> items)
            {
                if (items == null)
                    return;

                foreach (var item in items)
                {
                    if (
                        item.MetaData != null
                        && !string.IsNullOrEmpty(item.MetaData.Path)
                        && Path.IsPathRooted(item.MetaData.Path)
                    )
                    {
                        absolutePathNodes.Add(item);
                        Logger.Debug(
                            $"Found absolute path node: {item.MetaData.Name} -> {item.MetaData.Path}"
                        );
                    }

                    if (item.Children != null && item.Children.Count > 0)
                    {
                        TraverseTree(item.Children);
                    }
                }
            }

            TraverseTree(processTree);
            return absolutePathNodes;
        }

        /// <summary>
        /// 将指定节点的路径转换为相对路径（如果在Applications目录下）
        /// 返回实际迁移的节点数
        /// </summary>
        public static int MigrateNodesToRelativePaths(IEnumerable<ProcessItem> nodesToMigrate)
        {
            int migratedCount = 0;

            if (nodesToMigrate == null)
                return 0;

            var applicationsDir = AppPathes.AppDir;

            foreach (var node in nodesToMigrate)
            {
                if (node.MetaData != null && !string.IsNullOrEmpty(node.MetaData.Path))
                {
                    var originalPath = node.MetaData.Path;

                    // 如果是绝对路径且在Applications目录下，转换为相对路径
                    if (
                        Path.IsPathRooted(originalPath)
                        && originalPath.StartsWith(
                            applicationsDir,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        var relativePath = originalPath
                            .Substring(applicationsDir.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        node.MetaData.Path = relativePath;
                        migratedCount++;
                        Logger.Info(
                            $"Migrated node '{node.MetaData.Name}' to relative path: {originalPath} -> {relativePath}"
                        );
                    }
                }
            }

            return migratedCount;
        }

        /// <summary>
        /// 从进程树中获取所有程序路径
        /// </summary>
        private static List<string> GetAllProgramsFromTree(IEnumerable<ProcessItem> processTree)
        {
            var programs = new List<string>();

            void TraverseTree(IEnumerable<ProcessItem> items)
            {
                if (items == null)
                    return;

                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(item.NodePath) && File.Exists(item.NodePath))
                    {
                        programs.Add(item.NodePath);
                    }

                    if (item.Children != null && item.Children.Count > 0)
                    {
                        TraverseTree(item.Children);
                    }
                }
            }

            TraverseTree(processTree);
            return programs.Distinct().ToList();
        }

        /// <summary>
        /// 检测程序类型
        /// </summary>
        private static string DetectProgramType(string exePath)
        {
            try
            {
                var exeDir = Path.GetDirectoryName(exePath);
                var exeName = Path.GetFileNameWithoutExtension(exePath);

                // Unity检测：存在 ExeName_Data 文件夹
                var unityDataFolder = Path.Combine(exeDir, $"{exeName}_Data");
                if (Directory.Exists(unityDataFolder))
                {
                    return "Unity";
                }

                // UE检测：存在 Binaries/Win64 结构
                if (exeDir.Contains("Binaries") && exeDir.Contains("Win64"))
                {
                    return "UnrealEngine";
                }

                return "Other";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// 获取需要导出的程序文件夹
        /// </summary>
        private static string GetProgramRootDirectory(string exePath, string programType)
        {
            var exeDir = Path.GetDirectoryName(exePath);

            switch (programType)
            {
                case "Unity":
                    // Unity: 导出整个exe所在目录（包含 ExeName_Data）
                    return exeDir;

                case "UnrealEngine":
                    // UE: 导出项目根目录（Binaries上两级）
                    if (exeDir.Contains("Binaries"))
                    {
                        var binariesIndex = exeDir.IndexOf(
                            "Binaries",
                            StringComparison.OrdinalIgnoreCase
                        );
                        return exeDir
                            .Substring(0, binariesIndex)
                            .TrimEnd(Path.DirectorySeparatorChar);
                    }
                    return exeDir;

                default:
                    // 其他：只导出exe所在目录
                    return exeDir;
            }
        }

        /// <summary>
        /// 导出程序文件
        /// </summary>
        private static async Task<List<ProgramInfo>> ExportProgramFilesAsync(
            List<string> programPaths,
            string destinationDir,
            IProgress<FileCopyProgress> progress,
            CancellationToken cancellationToken
        )
        {
            var programInfos = new List<ProgramInfo>();

            foreach (var exePath in programPaths)
            {
                try
                {
                    var programType = DetectProgramType(exePath);
                    var rootDir = GetProgramRootDirectory(exePath, programType);
                    var programName = new DirectoryInfo(rootDir).Name;

                    // 防御性编程：如果程序名称是"Applications"，则使用其父目录名称
                    // 这可能表示rootDir的路径选择不当
                    if (programName.Equals("Applications", StringComparison.OrdinalIgnoreCase))
                    {
                        var parentDir = Directory.GetParent(rootDir);
                        if (parentDir != null && parentDir.Exists)
                        {
                            Logger.Warn(
                                $"Program root directory is named 'Applications', using parent: {parentDir.Name}"
                            );
                            programName = parentDir.Name;
                            rootDir = parentDir.FullName;
                        }
                    }

                    var destProgramDir = Path.Combine(destinationDir, programName);

                    Logger.Info($"Exporting {programType} program: {programName} from {rootDir}");
                    Logger.Debug($"  Source: {rootDir}");
                    Logger.Debug($"  Destination: {destProgramDir}");

                    // 复制整个程序目录
                    await HighPerformanceFileCopier.CopyDirectoryAsync(
                        rootDir,
                        destProgramDir,
                        progress,
                        cancellationToken
                    );

                    programInfos.Add(
                        new ProgramInfo
                        {
                            Name = programName,
                            ExecutablePath = Path.GetRelativePath(rootDir, exePath),
                            SizeBytes = HighPerformanceFileCopier.CalculateDirectorySize(rootDir),
                            ProgramType = programType
                        }
                    );
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to export program: {exePath}");
                }
            }

            return programInfos;
        }

        /// <summary>
        /// 导入配置文件
        /// </summary>
        private static async Task ImportConfigFilesAsync(
            string sourceDir,
            bool overwrite,
            IProgress<FileCopyProgress> progress,
            CancellationToken cancellationToken
        )
        {
            var configFiles = Directory.GetFiles(sourceDir);
            var filePairs = new List<(string Source, string Destination)>();
            var treeviewFileName = Path.GetFileName(AppPathes.TreeViewDataPath); // 获取 treeview.xml 文件名

            foreach (var configFile in configFiles)
            {
                var fileName = Path.GetFileName(configFile);

                // 跳过 treeview.xml - 这个文件由进程树导入单独处理
                if (fileName.Equals(treeviewFileName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Debug(
                        $"Skipping treeview.xml in config import (handled by process tree import)"
                    );
                    continue;
                }

                var destPath = Path.Combine(AppPathes.ConfigDir, fileName);

                if (!overwrite && File.Exists(destPath))
                {
                    Logger.Info($"Skipping existing config: {fileName}");
                    continue;
                }

                filePairs.Add((configFile, destPath));
            }

            if (filePairs.Count > 0)
            {
                await HighPerformanceFileCopier.CopyFilesAsync(
                    filePairs,
                    progress,
                    cancellationToken
                );
            }
        }

        /// <summary>
        /// 导入程序文件，返回路径映射 (原始绝对路径 -> 新的绝对目标路径 -> 相对路径)
        /// </summary>
        private static async Task<Dictionary<string, string>> ImportProgramFilesAsync(
            string sourceDir,
            IEnumerable<ProcessItem> selectedNodes,
            IProgress<FileCopyProgress> progress,
            CancellationToken cancellationToken
        )
        {
            var pathMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var programDirs = Directory.GetDirectories(sourceDir);

            Logger.Info(
                $"ImportProgramFilesAsync: sourceDir={sourceDir}, programDirs count={programDirs.Length}"
            );
            foreach (var dir in programDirs)
            {
                Logger.Debug($"  Found subdirectory: {Path.GetFileName(dir)}");
            }

            // 检查是否存在"Applications"文件夹（来自错误的ZIP结构）
            // 如果Programs文件夹下直接是Applications文件夹，则应该使用Applications下的内容
            var applicationsDir = programDirs.FirstOrDefault(
                d => Path.GetFileName(d).Equals("Applications", StringComparison.OrdinalIgnoreCase)
            );

            if (applicationsDir != null && programDirs.Length == 1)
            {
                Logger.Warn(
                    $"Detected misplaced 'Applications' folder structure. Using subdirectories of {applicationsDir}"
                );
                programDirs = Directory.GetDirectories(applicationsDir);
                Logger.Info($"After unwrapping: programDirs count={programDirs.Length}");
                foreach (var dir in programDirs)
                {
                    Logger.Debug($"  Found unwrapped subdirectory: {Path.GetFileName(dir)}");
                }
            }

            // 如果有选中的节点，只导入选中的程序
            HashSet<string> selectedProgramNames = null;
            if (selectedNodes != null && selectedNodes.Any())
            {
                selectedProgramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void CollectProgramNames(IEnumerable<ProcessItem> nodes)
                {
                    if (nodes == null)
                        return;

                    foreach (var node in nodes)
                    {
                        // 从MetaData.Path提取程序目录名（支持相对路径）
                        var nodePath = node.MetaData?.Path;
                        if (!string.IsNullOrEmpty(nodePath))
                        {
                            // 相对路径格式: "程序名\文件.exe" -> "程序名"
                            // 绝对路径格式: "C:\...\程序名\文件.exe" -> "程序名"
                            var pathParts = nodePath.Split(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar
                            );
                            if (pathParts.Length >= 2)
                            {
                                // 倒数第二段就是程序目录名
                                var programName = pathParts[pathParts.Length - 2];
                                if (!string.IsNullOrEmpty(programName))
                                {
                                    selectedProgramNames.Add(programName);
                                    Logger.Debug(
                                        $"Collected program name: {programName} from path: {nodePath}"
                                    );
                                }
                            }
                        }

                        if (node.Children != null && node.Children.Count > 0)
                        {
                            CollectProgramNames(node.Children);
                        }
                    }
                }

                CollectProgramNames(selectedNodes);
                Logger.Info(
                    $"ImportProgramFilesAsync: selected program names count={selectedProgramNames.Count}"
                );
            }

            foreach (var programDir in programDirs)
            {
                var programName = Path.GetFileName(programDir);

                // 如果指定了选中的节点，检查是否在选中列表中
                if (selectedProgramNames != null && !selectedProgramNames.Contains(programName))
                {
                    Logger.Info($"Skipping unselected program: {programName}");
                    continue;
                }

                var destDir = Path.Combine(AppPathes.AppDir, programName);

                Logger.Info($"Importing program: {programName} from {programDir} to {destDir}");

                await HighPerformanceFileCopier.CopyDirectoryAsync(
                    programDir,
                    destDir,
                    progress,
                    cancellationToken
                );

                // 构建路径映射：
                // 1. 原始源文件路径 -> 新的绝对目标路径
                // 2. 新的绝对目标路径 -> 相对于Applications的路径
                var files = Directory.GetFiles(programDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(programDir, file);
                    var newAbsolutePath = Path.Combine(destDir, relativePath);
                    var newRelativePath = Path.Combine(programName, relativePath);

                    // 添加两种映射方式
                    pathMappings[file] = newAbsolutePath; // 源文件 -> 新绝对路径
                    pathMappings[newAbsolutePath] = newRelativePath; // 绝对路径 -> 相对路径

                    Logger.Debug(
                        $"Path mapping: {file} -> {newAbsolutePath} (also -> {newRelativePath})"
                    );
                }
            }

            Logger.Info(
                $"ImportProgramFilesAsync completed: {pathMappings.Count} path mappings created"
            );
            return pathMappings;
        }

        /// <summary>
        /// 合并进程树配置
        /// </summary>
        /// <summary>
        /// 合并进程树 - 方案B: 支持List<ProcessItem>结构和灵活合并策略
        /// </summary>
        private static async Task MergeProcessTreeAsync(
            string importedTreePath,
            IEnumerable<ProcessItem> selectedNodes,
            bool clearExisting,
            bool overwriteConflicts,
            Dictionary<string, string> pathMappings,
            IProgress<string> statusProgress
        )
        {
            try
            {
                Logger.Info(
                    $"MergeProcessTreeAsync: clearExisting={clearExisting}, overwriteConflicts={overwriteConflicts}, pathMappings count={pathMappings?.Count ?? 0}"
                );

                // 读取当前进程树
                ProcessItem currentRootNode = null;
                if (File.Exists(AppPathes.TreeViewDataPath) && !clearExisting)
                {
                    statusProgress?.Report("读取现有进程树...");
                    var serializer = new System.Xml.Serialization.XmlSerializer(
                        typeof(ProcessItem)
                    );
                    using (var stream = File.OpenRead(AppPathes.TreeViewDataPath))
                    {
                        currentRootNode = (ProcessItem)serializer.Deserialize(stream);
                        Logger.Info(
                            $"Current tree loaded: root has {currentRootNode?.Children?.Count ?? 0} children"
                        );
                    }
                }

                // 读取导入的进程树（List<ProcessItem>）
                statusProgress?.Report("读取导入的进程树...");
                List<ProcessItem> importedNodeList = null;
                var importSerializer = new System.Xml.Serialization.XmlSerializer(
                    typeof(List<ProcessItem>)
                );
                using (var stream = File.OpenRead(importedTreePath))
                {
                    importedNodeList = (List<ProcessItem>)importSerializer.Deserialize(stream);
                    Logger.Info($"Imported tree loaded: {importedNodeList?.Count ?? 0} root nodes");
                }

                // 过滤选中的节点
                if (selectedNodes != null && selectedNodes.Any())
                {
                    statusProgress?.Report("过滤选中的节点...");
                    var selectedNodeIds = new HashSet<string>(selectedNodes.Select(n => n.NodeId));
                    Logger.Info($"Filtering by {selectedNodeIds.Count} selected node IDs");

                    importedNodeList = importedNodeList
                        ?.Where(node => IsNodeOrDescendantSelected(node, selectedNodeIds))
                        .ToList();

                    Logger.Info($"After filtering: {importedNodeList?.Count ?? 0} nodes");
                }

                // 路径转换
                if (importedNodeList != null && importedNodeList.Any())
                {
                    statusProgress?.Report("转换路径...");
                    foreach (var node in importedNodeList)
                    {
                        ConvertToRelativePaths(node, pathMappings);
                    }
                    Logger.Info("Path conversion completed");
                }

                ProcessItem resultRootNode;

                if (clearExisting || currentRootNode == null)
                {
                    // 完整导入模式：重建整个树
                    statusProgress?.Report("重建进程树...");
                    Logger.Info("Clear mode: rebuilding entire tree");

                    resultRootNode = new ProcessItem
                    {
                        NodeId = Guid.NewGuid().ToString(),
                        MetaData = new ProcessMetaData
                        {
                            Name = "[ 进程树 ]",
                            Delay = 0,
                            Path = string.Empty,
                            Enable = true
                        },
                        Children =
                            new System.Collections.ObjectModel.ObservableCollection<ProcessItem>()
                    };

                    if (importedNodeList != null)
                    {
                        foreach (var node in importedNodeList)
                        {
                            node.Parent = resultRootNode;
                            resultRootNode.Children.Add(node);
                        }
                    }
                }
                else
                {
                    // 部分导入合并模式
                    statusProgress?.Report("合并进程树...");
                    Logger.Info($"Merge mode: overwriteConflicts={overwriteConflicts}");
                    resultRootNode = currentRootNode;

                    if (resultRootNode.Children == null)
                        resultRootNode.Children =
                            new System.Collections.ObjectModel.ObservableCollection<ProcessItem>();

                    if (importedNodeList != null)
                    {
                        foreach (var importedNode in importedNodeList)
                        {
                            // 查找同名节点
                            var existing = resultRootNode.Children.FirstOrDefault(
                                c => c.MetaData?.Name == importedNode.MetaData?.Name
                            );

                            if (existing == null)
                            {
                                // 新节点：直接添加
                                importedNode.Parent = resultRootNode;
                                resultRootNode.Children.Add(importedNode);
                                Logger.Info($"Added new node: {importedNode.MetaData?.Name}");
                            }
                            else if (overwriteConflicts)
                            {
                                // 冲突节点 + 覆盖模式：替换
                                var index = resultRootNode.Children.IndexOf(existing);
                                resultRootNode.Children.RemoveAt(index);
                                importedNode.Parent = resultRootNode;
                                resultRootNode.Children.Insert(index, importedNode);
                                Logger.Info(
                                    $"Replaced existing node: {importedNode.MetaData?.Name}"
                                );
                            }
                            else
                            {
                                // 冲突节点 + 保留模式：跳过
                                Logger.Info(
                                    $"Skipped existing node (no overwrite): {importedNode.MetaData?.Name}"
                                );
                            }
                        }
                    }
                }

                // 保存结果（单根节点格式）
                statusProgress?.Report("保存进程树配置...");
                var saveSerializer = new System.Xml.Serialization.XmlSerializer(
                    typeof(ProcessItem)
                );
                using (var stream = File.Create(AppPathes.TreeViewDataPath))
                {
                    saveSerializer.Serialize(stream, resultRootNode);
                }

                Logger.Info(
                    $"Process tree saved: root with {resultRootNode.Children?.Count ?? 0} children"
                );
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to merge process tree");
                throw;
            }
        }

        /// <summary>
        /// 检查节点或其子孙节点是否在选中列表中
        /// </summary>
        private static bool IsNodeOrDescendantSelected(
            ProcessItem node,
            HashSet<string> selectedIds
        )
        {
            if (node == null)
                return false;

            // 检查当前节点
            if (selectedIds.Contains(node.NodeId))
                return true;

            // 递归检查子节点
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (IsNodeOrDescendantSelected(child, selectedIds))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 根据选中的节点ID过滤树
        /// </summary>
        private static ProcessItem FilterTreeBySelectedNodes(
            ProcessItem tree,
            HashSet<string> selectedNodeIds
        )
        {
            if (tree == null)
                return null;

            var filteredTree = new ProcessItem { MetaData = tree.MetaData, NodeId = tree.NodeId };

            if (tree.Children != null && tree.Children.Count > 0)
            {
                filteredTree.Children =
                    new System.Collections.ObjectModel.ObservableCollection<ProcessItem>();

                foreach (var child in tree.Children)
                {
                    // 如果当前节点被选中，或其子节点中有被选中的，则保留
                    if (
                        selectedNodeIds.Contains(child.NodeId)
                        || HasSelectedDescendant(child, selectedNodeIds)
                    )
                    {
                        var filteredChild = FilterTreeBySelectedNodes(child, selectedNodeIds);
                        if (filteredChild != null)
                        {
                            filteredTree.Children.Add(filteredChild);
                        }
                    }
                }
            }

            return filteredTree;
        }

        /// <summary>
        /// 检查节点是否有被选中的后代
        /// </summary>
        private static bool HasSelectedDescendant(ProcessItem node, HashSet<string> selectedNodeIds)
        {
            if (node.Children == null || node.Children.Count == 0)
                return false;

            foreach (var child in node.Children)
            {
                if (
                    selectedNodeIds.Contains(child.NodeId)
                    || HasSelectedDescendant(child, selectedNodeIds)
                )
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 将进程树中的路径转换为相对路径（相对于Applications目录）
        /// </summary>
        /// <param name="node">要处理的节点</param>
        /// <param name="pathMappings">路径映射字典，可为null</param>
        private static void ConvertToRelativePaths(
            ProcessItem node,
            Dictionary<string, string> pathMappings = null
        )
        {
            if (node == null)
                return;

            // 转换当前节点的路径
            if (node.MetaData != null && !string.IsNullOrEmpty(node.MetaData.Path))
            {
                var originalPath = node.MetaData.Path;
                var convertedPath = originalPath;
                var applicationsDir = AppPathes.AppDir;
                var isPathChanged = false;

                Logger.Debug($"Converting path for node '{node.MetaData.Name}': {originalPath}");

                // 第一步：检查是否有路径映射
                if (pathMappings != null && pathMappings.Count > 0)
                {
                    // 尝试1：直接匹配原始路径
                    if (pathMappings.TryGetValue(originalPath, out var mappedPath))
                    {
                        Logger.Debug($"  Found direct mapping: {originalPath} -> {mappedPath}");
                        convertedPath = mappedPath;
                        isPathChanged = true;
                    }
                    // 尝试2：规范化原始路径后匹配
                    else if (Path.IsPathRooted(originalPath))
                    {
                        var normalizedPath = Path.GetFullPath(originalPath);
                        if (pathMappings.TryGetValue(normalizedPath, out mappedPath))
                        {
                            Logger.Debug(
                                $"  Found normalized mapping: {normalizedPath} -> {mappedPath}"
                            );
                            convertedPath = mappedPath;
                            isPathChanged = true;
                        }
                        else
                        {
                            // 尝试3：在映射中查找前缀匹配（用于目录级别的映射）
                            foreach (var kvp in pathMappings)
                            {
                                var mappingKey = kvp.Key;
                                var mappingValue = kvp.Value;

                                // 检查是否是相同的文件或目录
                                if (
                                    normalizedPath.Equals(
                                        mappingKey,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    || normalizedPath.Equals(
                                        Path.GetFullPath(mappingKey),
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    Logger.Debug(
                                        $"  Found lookup mapping: {normalizedPath} -> {mappingValue}"
                                    );
                                    convertedPath = mappingValue;
                                    isPathChanged = true;
                                    break;
                                }

                                // 检查是否是某个映射源的子路径
                                var mappingKeyDir = Path.GetDirectoryName(mappingKey);
                                if (
                                    !string.IsNullOrEmpty(mappingKeyDir)
                                    && normalizedPath.StartsWith(
                                        mappingKeyDir,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    var mappingValueDir = Path.GetDirectoryName(mappingValue);
                                    var subPath = normalizedPath
                                        .Substring(mappingKeyDir.Length)
                                        .TrimStart(Path.DirectorySeparatorChar);
                                    if (
                                        !string.IsNullOrEmpty(mappingValueDir)
                                        && !string.IsNullOrEmpty(subPath)
                                    )
                                    {
                                        convertedPath = Path.Combine(mappingValueDir, subPath);
                                        Logger.Debug(
                                            $"  Found sub-path mapping: {normalizedPath} -> {convertedPath}"
                                        );
                                        isPathChanged = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                // 第二步：如果还是绝对路径，尝试转换为相对路径；如果是相对路径，转换为绝对路径
                if (!isPathChanged)
                {
                    if (Path.IsPathRooted(convertedPath))
                    {
                        // 绝对路径：检查是否在Applications目录下，转换为相对路径
                        if (
                            convertedPath.StartsWith(
                                applicationsDir,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            var relativePath = convertedPath
                                .Substring(applicationsDir.Length)
                                .TrimStart(
                                    Path.DirectorySeparatorChar,
                                    Path.AltDirectorySeparatorChar
                                );
                            Logger.Debug(
                                $"  Converting absolute to relative: {convertedPath} -> {relativePath}"
                            );
                            convertedPath = relativePath;
                            isPathChanged = true;
                        }
                    }
                    else
                    {
                        // 相对路径：转换为Applications目录下的绝对路径
                        var absolutePath = Path.Combine(applicationsDir, convertedPath);
                        Logger.Debug(
                            $"  Converting relative to absolute: {convertedPath} -> {absolutePath}"
                        );
                        convertedPath = absolutePath;
                        isPathChanged = true;
                    }
                }

                // 应用转换后的路径
                if (isPathChanged)
                {
                    node.MetaData.Path = convertedPath;
                    Logger.Info(
                        $"Path converted for node '{node.MetaData.Name}': {originalPath} -> {convertedPath}"
                    );
                }
                else
                {
                    Logger.Warn(
                        $"Could not convert path for node '{node.MetaData.Name}': {originalPath} (not in mapping and not in Applications dir)"
                    );
                }
            }

            // 递归处理子节点
            if (node.Children != null && node.Children.Count > 0)
            {
                foreach (var child in node.Children)
                {
                    ConvertToRelativePaths(child, pathMappings);
                }
            }
        }

        #endregion
    }
}
