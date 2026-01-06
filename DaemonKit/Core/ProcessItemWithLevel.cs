using System;

namespace DaemonKit.Core
{
    /// <summary>
    /// 带层级信息的进程项（用于显示）
    /// </summary>
    public class ProcessItemWithLevel
    {
        /// <summary>进程项</summary>
        public ProcessItem Item { get; set; }

        /// <summary>层级深度（0为根节点）</summary>
        public int Level { get; set; }

        /// <summary>带缩进的显示名称</summary>
        public string DisplayName
        {
            get
            {
                var indent = new string(' ', Level * 4); // 每层缩进4个空格
                var prefix = Level > 0 ? "└─ " : "";
                return $"{indent}{prefix}{Item.Name} [{Item.ShortNodeId}]";
            }
        }

        public ProcessItemWithLevel(ProcessItem item, int level)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
            Level = level;
        }
    }
}
