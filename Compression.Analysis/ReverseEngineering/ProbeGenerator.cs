#pragma warning disable CS1591

namespace Compression.Analysis.ReverseEngineering;

/// <summary>
/// Generates controlled test inputs for black-box format probing.
/// Each probe has a known structure so correlations in the output can be detected.
/// </summary>
public static class ProbeGenerator {

  /// <summary>A single probe input with metadata about its content.</summary>
  public sealed class Probe {
    public required string Name { get; init; }
    public required byte[] Data { get; init; }
    public required string Description { get; init; }
    /// <summary>Suggested filename for tools that use the filename.</summary>
    public string FileName { get; init; } = "probe.bin";
  }

  /// <summary>
  /// Generates a standard suite of probes for format reverse engineering.
  /// </summary>
  public static List<Probe> GenerateStandardProbes() => [
    // Size probes — detect size fields in headers.
    new() { Name = "empty", Data = [], Description = "Empty input (0 bytes)", FileName = "empty.bin" },
    new() { Name = "1byte-00", Data = [0x00], Description = "Single null byte" },
    new() { Name = "1byte-ff", Data = [0xFF], Description = "Single 0xFF byte" },
    new() { Name = "4bytes", Data = [0x41, 0x42, 0x43, 0x44], Description = "4 bytes: ABCD" },
    new() { Name = "16bytes-zero", Data = new byte[16], Description = "16 null bytes" },
    new() { Name = "256bytes-zero", Data = new byte[256], Description = "256 null bytes" },
    new() { Name = "256bytes-inc", Data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), Description = "256 incrementing bytes 0x00-0xFF" },
    new() { Name = "1k-zero", Data = new byte[1024], Description = "1KB null bytes" },
    new() { Name = "1k-random", Data = MakeRandom(1024, 42), Description = "1KB pseudorandom (seed=42)" },
    new() { Name = "4k-text", Data = MakeText(4096), Description = "4KB repeating ASCII text" },
    new() { Name = "4k-random", Data = MakeRandom(4096, 123), Description = "4KB pseudorandom (seed=123)" },
    new() { Name = "16k-zero", Data = new byte[16384], Description = "16KB null bytes" },
    new() { Name = "16k-random", Data = MakeRandom(16384, 999), Description = "16KB pseudorandom (seed=999)" },
    new() { Name = "64k-text", Data = MakeText(65536), Description = "64KB repeating ASCII text" },

    // Pattern probes — detect compression behavior.
    new() { Name = "repeat-A", Data = MakeRepeated(4096, 0x41), Description = "4KB of repeated 'A' (maximal compression)" },
    new() { Name = "repeat-AB", Data = MakeAlternating(4096, 0x41, 0x42), Description = "4KB alternating AB (moderate compression)" },
    new() { Name = "incompressible", Data = MakeRandom(4096, 77), Description = "4KB random (incompressible)" },

    // Filename probes — detect if tool stores the filename.
    new() { Name = "name-test1", Data = [0x41, 0x42, 0x43], Description = "3 bytes with filename test1.dat", FileName = "test1.dat" },
    new() { Name = "name-test2", Data = [0x41, 0x42, 0x43], Description = "Same 3 bytes with filename test2.dat", FileName = "test2.dat" },

    // Determinism probe — same data twice.
    new() { Name = "determinism-a", Data = MakeRandom(512, 42), Description = "512 bytes (seed=42) run A" },
    new() { Name = "determinism-b", Data = MakeRandom(512, 42), Description = "512 bytes (seed=42) run B (should match A)" },
  ];

  /// <summary>
  /// Generates probes with specific sizes for size-field correlation analysis.
  /// </summary>
  public static List<Probe> GenerateSizeProbes() {
    var sizes = new[] { 0, 1, 2, 3, 4, 7, 8, 15, 16, 31, 32, 63, 64, 127, 128, 255, 256, 511, 512, 1023, 1024, 4096, 8192, 16384, 32768, 65536 };
    return sizes.Select(s => new Probe {
      Name = $"size-{s}",
      Data = new byte[s],
      Description = $"Zero-filled, {s} bytes",
      FileName = $"size{s}.bin"
    }).ToList();
  }

  private static byte[] MakeRandom(int size, int seed) {
    var rng = new Random(seed);
    var data = new byte[size];
    rng.NextBytes(data);
    return data;
  }

  private static byte[] MakeText(int size) {
    var text = "The quick brown fox jumps over the lazy dog. Lorem ipsum dolor sit amet. "u8;
    var data = new byte[size];
    for (var i = 0; i < size; i++)
      data[i] = text[i % text.Length];
    return data;
  }

  private static byte[] MakeRepeated(int size, byte value) {
    var data = new byte[size];
    Array.Fill(data, value);
    return data;
  }

  private static byte[] MakeAlternating(int size, byte a, byte b) {
    var data = new byte[size];
    for (var i = 0; i < size; i++)
      data[i] = (i & 1) == 0 ? a : b;
    return data;
  }
}
