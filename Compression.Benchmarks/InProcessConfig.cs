#pragma warning disable CS1591

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Compression.Benchmarks;

/// <summary>
/// BenchmarkDotNet configuration that uses the InProcessEmitToolchain so benchmarks
/// run without spawning a child process. Required for .NET 10 where BDN's auto-generated
/// project may fail to build with a preview SDK.
/// </summary>
internal sealed class InProcessConfig : ManualConfig {
  public InProcessConfig() {
    AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
    AddDiagnoser(MemoryDiagnoser.Default);
    AddColumn(StatisticColumn.Mean, StatisticColumn.StdDev, StatisticColumn.Median);
  }
}
