using Xunit;

// EdgeConfig 的密钥/环境变量覆盖测试会读写进程级环境变量，禁用并行以隔离。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
