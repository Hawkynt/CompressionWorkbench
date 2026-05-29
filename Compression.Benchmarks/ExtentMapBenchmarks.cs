#pragma warning disable CS1591

using BenchmarkDotNet.Attributes;
using Compression.Lib;
using FileSystem.Fat;

namespace Compression.Benchmarks;

/// <summary>
/// Benchmarks FAT extent map enumeration — walking the full on-disk layout
/// of a FAT image with 100 files.
/// </summary>
[Config(typeof(InProcessConfig))]
public class ExtentMapBenchmarks {

  private byte[] _image = null!;

  [GlobalSetup]
  public void Setup() {
    FormatRegistration.EnsureInitialized();

    // Build a FAT image with 100 files
    var writer = new FatWriter();
    for (var i = 0; i < 100; i++)
      writer.AddFile($"F{i:D5}.TXT", GenerateData(512 + i * 10));
    // Use enough sectors to hold all data (~56 KB of file data + overhead)
    _image = writer.Build(totalSectors: 2880);
  }

  private static byte[] GenerateData(int size) {
    var data = new byte[size];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 256);
    return data;
  }

  [Benchmark(Description = "FatExtentMap.Enumerate (100 files)")]
  public int EnumerateExtents() {
    using var stream = new MemoryStream(_image);
    var count = 0;
    foreach (var extent in FatExtentMap.Enumerate(stream))
      count++;
    return count;
  }
}
