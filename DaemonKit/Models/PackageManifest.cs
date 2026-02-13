using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;

namespace DaemonKit.Models
{
    /// <summary>
    /// 包类型枚举
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PackageType
    {
        /// <summary>进程树包 — 多节点 + 配置（.dkp.zip）</summary>
        TreeBundle,

        /// <summary>单节点全量包 — 完整程序目录（.dkp.zip）</summary>
        NodeFull,

        /// <summary>单节点补丁包 — 增量更新（.dkp-patch.zip）</summary>
        NodePatch
    }

    /// <summary>
    /// 补丁应用模式
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PatchMode
    {
        /// <summary>覆盖模式 — 仅覆盖 files/ 中的文件，保留目标目录其他文件</summary>
        Overlay,

        /// <summary>替换模式 — 清空目标目录后全量拷贝 files/ 内容</summary>
        Replace
    }

    /// <summary>
    /// 统一包清单 v2 — 兼容 TreeBundle / NodeFull / NodePatch 三种包类型
    /// </summary>
    public class PackageManifest
    {
        /// <summary>清单格式版本，当前为 "1.0"</summary>
        [JsonProperty("schemaVersion")]
        public string SchemaVersion { get; set; } = "1.0";

        /// <summary>包类型</summary>
        [JsonProperty("packageType")]
        public PackageType PackageType { get; set; }

        /// <summary>创建时间 (ISO 8601)</summary>
        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        /// <summary>用户描述</summary>
        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        /// <summary>制作来源信息</summary>
        [JsonProperty("source")]
        public ManifestSource Source { get; set; } = new ManifestSource();

        /// <summary>目标程序信息 — NodeFull / NodePatch 必填，TreeBundle 时为 null</summary>
        [JsonProperty("target", NullValueHandling = NullValueHandling.Ignore)]
        public ManifestTarget Target { get; set; }

        /// <summary>进程树信息 — 仅 TreeBundle 时存在</summary>
        [JsonProperty("tree", NullValueHandling = NullValueHandling.Ignore)]
        public ManifestTree Tree { get; set; }

        /// <summary>补丁信息 — 仅 NodePatch 时存在</summary>
        [JsonProperty("patch", NullValueHandling = NullValueHandling.Ignore)]
        public ManifestPatch Patch { get; set; }
    }

    /// <summary>
    /// 制作来源
    /// </summary>
    public class ManifestSource
    {
        /// <summary>机器名</summary>
        [JsonProperty("machineName", NullValueHandling = NullValueHandling.Ignore)]
        public string MachineName { get; set; }

        /// <summary>用户名</summary>
        [JsonProperty("userName", NullValueHandling = NullValueHandling.Ignore)]
        public string UserName { get; set; }

        /// <summary>制作工具 — "DaemonKit" / "UnityCI" / "Manual"</summary>
        [JsonProperty("builder")]
        public string Builder { get; set; } = "DaemonKit";

        /// <summary>制作工具版本</summary>
        [JsonProperty("builderVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string BuilderVersion { get; set; }
    }

    /// <summary>
    /// 目标程序匹配信息 — NodeFull / NodePatch 使用
    /// </summary>
    public class ManifestTarget
    {
        /// <summary>主可执行文件名（主匹配键，如 "MyApp.exe"）</summary>
        [JsonProperty("exeName")]
        public string ExeName { get; set; }

        /// <summary>节点显示名（回退匹配键，可选）</summary>
        [JsonProperty("nodeName", NullValueHandling = NullValueHandling.Ignore)]
        public string NodeName { get; set; }

        /// <summary>程序类型 — "Unity" / "UnrealEngine" / "Other"</summary>
        [JsonProperty("programType", NullValueHandling = NullValueHandling.Ignore)]
        public string ProgramType { get; set; }

        /// <summary>程序版本号（如 "1.2.3"）</summary>
        [JsonProperty("version", NullValueHandling = NullValueHandling.Ignore)]
        public string Version { get; set; }
    }

    /// <summary>
    /// 进程树信息 — 仅 TreeBundle 使用
    /// </summary>
    public class ManifestTree
    {
        /// <summary>项目名称（根节点名）</summary>
        [JsonProperty("projectName", NullValueHandling = NullValueHandling.Ignore)]
        public string ProjectName { get; set; }

        /// <summary>包含的配置文件名列表</summary>
        [JsonProperty("includedConfigs", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> IncludedConfigs { get; set; } = new List<string>();

        /// <summary>包含的程序概要列表</summary>
        [JsonProperty("programs", NullValueHandling = NullValueHandling.Ignore)]
        public List<ManifestProgramInfo> Programs { get; set; } = new List<ManifestProgramInfo>();
    }

    /// <summary>
    /// 程序概要信息（TreeBundle 内的节目列表）
    /// </summary>
    public class ManifestProgramInfo
    {
        /// <summary>程序目录名</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>可执行文件相对路径</summary>
        [JsonProperty("exePath")]
        public string ExePath { get; set; }

        /// <summary>目录总大小（字节）</summary>
        [JsonProperty("sizeBytes")]
        public long SizeBytes { get; set; }

        /// <summary>程序类型</summary>
        [JsonProperty("programType", NullValueHandling = NullValueHandling.Ignore)]
        public string ProgramType { get; set; }
    }

    /// <summary>
    /// 补丁专属信息 — 仅 NodePatch 使用
    /// </summary>
    public class ManifestPatch
    {
        /// <summary>补丁应用模式</summary>
        [JsonProperty("patchMode")]
        public PatchMode PatchMode { get; set; } = PatchMode.Overlay;

        /// <summary>基于的程序版本（可选，用于校验）</summary>
        [JsonProperty("baseVersion", NullValueHandling = NullValueHandling.Ignore)]
        public string BaseVersion { get; set; }
    }
}
