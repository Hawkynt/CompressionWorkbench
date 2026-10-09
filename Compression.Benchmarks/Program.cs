#pragma warning disable CS1591

using BenchmarkDotNet.Running;
using Compression.Benchmarks;

if (args is ["deflate-table", .. var rest]) {
  DeflateThroughputTable.Run(rest);
  return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
