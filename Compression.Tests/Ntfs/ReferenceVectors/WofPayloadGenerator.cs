using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Compression.Core.Dictionary.Lzx;

namespace Compression.Tests.Ntfs.ReferenceVectors;

/// <summary>
/// Makes the <c>WofCompressedData</c> payloads <c>generate.sh</c> plants into an NTFS volume.
/// Documentation of how <c>ntfs-wof.img.gz</c> was produced, not a test: run it by hand on
/// Windows with <c>CWB_WOF_OUT</c> naming an output directory.
/// </summary>
/// <remarks>
/// XPRESS chunks come from Windows' own encoder, <c>RtlCompressBuffer</c> with
/// <c>COMPRESSION_FORMAT_XPRESS_HUFF</c> (ntdll), so the reader is checked against bytes
/// Microsoft's code wrote. Windows exposes no LZX encoder outside <c>compact.exe</c>, which
/// needs an NTFS volume this generator cannot image without administrator rights; the LZX
/// payload therefore comes from this repository's encoder, and its oracle is the Linux
/// ntfs3 driver reading it back.
/// </remarks>
[TestFixture, Explicit("generator for Ntfs/ReferenceVectors; run by hand on Windows")]
public sealed class WofPayloadGenerator {

  [DllImport("ntdll.dll")]
  private static extern uint RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkSpace, out uint fragmentWorkSpace);

  [DllImport("ntdll.dll")]
  private static extern uint RtlCompressBuffer(ushort format, byte[] uncompressed, uint uncompressedSize,
    byte[] compressed, uint compressedSize, uint chunkSize, out uint finalSize, byte[] workspace);

  private const ushort CompressionFormatXpressHuff = 4;
  private const ushort CompressionEngineMaximum = 0x0100;

  /// <summary>The file every payload encodes: text, a zero run, and incompressible bytes.</summary>
  public static byte[] Source() {
    var r = new Random(20261019);
    var words = "system compression overlay filter chunk table "u8.ToArray();
    var data = new byte[150_000];
    for (var i = 0; i < 90_000; i++) data[i] = words[(i * 7 + r.Next(3)) % words.Length];
    // 90 000 .. 120 000 stays zero; the tail is incompressible so some chunks are stored raw.
    r.NextBytes(data.AsSpan(120_000));
    return data;
  }

  [Test]
  public void Generate() {
    if (!OperatingSystem.IsWindows()) Assert.Ignore("RtlCompressBuffer is a Windows API.");
    var dir = Environment.GetEnvironmentVariable("CWB_WOF_OUT");
    if (string.IsNullOrEmpty(dir)) { Assert.Ignore("set CWB_WOF_OUT to the output directory"); return; }
    Directory.CreateDirectory(dir);
    var source = Source();
    File.WriteAllBytes(Path.Combine(dir, "source.bin"), source);
    foreach (var (name, chunk) in new[] { ("xpress4k", 4096), ("xpress8k", 8192), ("xpress16k", 16384) })
      File.WriteAllBytes(Path.Combine(dir, name + ".wof"), Frame(source, chunk, XpressWindows));
    File.WriteAllBytes(Path.Combine(dir, "lzx.wof"), Frame(source, 32768, Lzx));
  }

  private static byte[] XpressWindows(byte[] chunk) {
    var status = RtlGetCompressionWorkSpaceSize(CompressionFormatXpressHuff | CompressionEngineMaximum, out var ws, out _);
    Assert.That(status, Is.Zero);
    var workspace = new byte[ws];
    var output = new byte[chunk.Length + 1024];
    status = RtlCompressBuffer(CompressionFormatXpressHuff | CompressionEngineMaximum, chunk, (uint)chunk.Length,
      output, (uint)output.Length, 4096, out var size, workspace);
    Assert.That(status, Is.Zero);
    return output.AsSpan(0, (int)size).ToArray();
  }

  private static byte[] Lzx(byte[] chunk) {
    var filtered = (byte[])chunk.Clone();
    LzxWimE8Filter.Preprocess(filtered);
    return new LzxCompressor(15).Compress(filtered);
  }

  /// <summary>Chunk table (u32 offsets of chunks 1..n-1, relative to its end) followed by the chunks.</summary>
  private static byte[] Frame(byte[] source, int chunkSize, Func<byte[], byte[]> compress) {
    var chunks = (source.Length + chunkSize - 1) / chunkSize;
    var bodies = new List<byte[]>();
    for (var i = 0; i < chunks; i++) {
      var raw = source.AsSpan(i * chunkSize, Math.Min(chunkSize, source.Length - i * chunkSize)).ToArray();
      var packed = compress(raw);
      bodies.Add(packed.Length < raw.Length ? packed : raw);
    }
    var table = new byte[(chunks - 1) * 4];
    var offset = 0;
    for (var i = 0; i < chunks - 1; i++) {
      offset += bodies[i].Length;
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), (uint)offset);
    }
    return [.. table, .. bodies.SelectMany(b => b)];
  }
}
