#pragma warning disable CS1591

using BenchmarkDotNet.Attributes;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Benchmarks;

/// <summary>
/// Benchmarks the FAT defragmentation paths: planner-driven ConsolidateAtStart
/// and rebuild-based defrag, with and without interleave.
/// Setup creates a fragmented FAT image by adding then removing files to create gaps.
/// </summary>
[Config(typeof(InProcessConfig))]
public class DefragBenchmarks {

  private byte[] _fragmentedImage = null!;
  private FatFormatDescriptor _descriptor = null!;

  [GlobalSetup]
  public void Setup() {
    FormatRegistration.EnsureInitialized();

    _descriptor = FormatRegistry.All
      .OfType<FatFormatDescriptor>()
      .First();

    // Build a 1 MB FAT image with files, then remove every other file to create gaps
    var writer = new FatWriter();
    for (var i = 0; i < 20; i++)
      writer.AddFile($"FILE{i:D4}.TXT", GenerateData(2048 + i * 100));
    _fragmentedImage = writer.Build(totalSectors: 2048);

    // Remove every other file to fragment the layout
    using var ms = new MemoryStream(_fragmentedImage, writable: true);
    var modifiable = (IArchiveModifiable)_descriptor;
    var toRemove = Enumerable.Range(0, 20).Where(i => i % 2 == 0).Select(i => $"FILE{i:D4}.TXT").ToArray();
    modifiable.Remove(ms, toRemove);
    _fragmentedImage = ms.ToArray();
  }

  private static byte[] GenerateData(int size) {
    var data = new byte[size];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 256);
    return data;
  }

  [Benchmark(Description = "Defrag ConsolidateAtStart")]
  public void DefragConsolidate() {
    using var stream = new MemoryStream((byte[])_fragmentedImage.Clone(), writable: true);
    _descriptor.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
  }

  [Benchmark(Description = "Defrag ConsolidateAtStart (interleave=2)")]
  public void DefragConsolidateInterleave() {
    using var stream = new MemoryStream((byte[])_fragmentedImage.Clone(), writable: true);
    _descriptor.Defragment(stream, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      InterleaveStride = 2
    });
  }

  [Benchmark(Description = "Defrag Rebuild (ConsolidateAtEnd)")]
  public void DefragRebuildConsolidateAtEnd() {
    using var stream = new MemoryStream((byte[])_fragmentedImage.Clone(), writable: true);
    _descriptor.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });
  }
}
