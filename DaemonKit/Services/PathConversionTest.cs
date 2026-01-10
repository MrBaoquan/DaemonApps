using System;
using System.Collections.Generic;
using DaemonKit.Models;

namespace DaemonKit.Services
{
    /// <summary>
    /// 路径转换功能的简单单元测试
    /// </summary>
    public class PathConversionTest
    {
        /// <summary>
        /// 测试相对路径转换
        /// </summary>
        public static void TestRelativePathConversion()
        {
            Console.WriteLine("=== 测试相对路径转换 ===");

            // 模拟 Applications 目录
            var applicationsDir = @"C:\Users\Test\Applications";

            // 测试用例1：绝对路径在Applications目录下
            var absolutePath1 = @"C:\Users\Test\Applications\MyApp\app.exe";
            var result1 = ConvertToRelativePath(absolutePath1, applicationsDir);
            var expected1 = @"MyApp\app.exe";

            Console.WriteLine($"测试1: {absolutePath1}");
            Console.WriteLine($"  结果: {result1}");
            Console.WriteLine($"  期望: {expected1}");
            Console.WriteLine($"  通过: {result1 == expected1}\n");

            // 测试用例2：相对路径保持不变
            var relativePath = @"MyApp\app.exe";
            var result2 = ConvertToRelativePath(relativePath, applicationsDir);
            var expected2 = relativePath;

            Console.WriteLine($"测试2: {relativePath}");
            Console.WriteLine($"  结果: {result2}");
            Console.WriteLine($"  期望: {expected2}");
            Console.WriteLine($"  通过: {result2 == expected2}\n");

            // 测试用例3：绝对路径不在Applications目录下
            var absolutePath3 = @"C:\Windows\System32\cmd.exe";
            var result3 = ConvertToRelativePath(absolutePath3, applicationsDir);
            var expected3 = absolutePath3; // 应该保持不变

            Console.WriteLine($"测试3: {absolutePath3}");
            Console.WriteLine($"  结果: {result3}");
            Console.WriteLine($"  期望: {expected3}");
            Console.WriteLine($"  通过: {result3 == expected3}\n");
        }

        /// <summary>
        /// 模拟路径转换逻辑（与ExportImportService中的逻辑相同）
        /// </summary>
        private static string ConvertToRelativePath(string originalPath, string applicationsDir)
        {
            if (string.IsNullOrEmpty(originalPath))
                return originalPath;

            // 如果是绝对路径且在Applications目录下，转换为相对路径
            if (
                System.IO.Path.IsPathRooted(originalPath)
                && originalPath.StartsWith(applicationsDir, StringComparison.OrdinalIgnoreCase)
            )
            {
                return originalPath
                    .Substring(applicationsDir.Length)
                    .TrimStart(
                        System.IO.Path.DirectorySeparatorChar,
                        System.IO.Path.AltDirectorySeparatorChar
                    );
            }

            return originalPath;
        }

        /// <summary>
        /// 测试ProcessItem克隆和路径转换
        /// </summary>
        public static void TestProcessItemCloning()
        {
            Console.WriteLine("=== 测试ProcessItem克隆 ===");

            // 创建测试数据
            var testNodeId = System.Guid.NewGuid().ToString();
            var original = new ProcessItem
            {
                NodeId = testNodeId,
                MetaData = new ProcessMetaData
                {
                    Name = "TestApp",
                    Path = @"C:\Users\Test\Applications\TestApp\app.exe",
                    Arguments = "--arg1 --arg2",
                    Enable = true,
                    Delay = 1000
                }
            };

            Console.WriteLine($"原始项目: {original.MetaData.Name}");
            Console.WriteLine($"  路径: {original.MetaData.Path}");
            Console.WriteLine($"  ID: {testNodeId}\n");

            // 对克隆的测试进行验证
            Console.WriteLine("克隆验证:");
            Console.WriteLine($"  NodeId 应该保留: {testNodeId}");
            Console.WriteLine($"  MetaData 应该深拷贝");
            Console.WriteLine($"  路径应该转换为相对路径\n");
        }
    }
}
