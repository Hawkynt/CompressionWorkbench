#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Tests.Wav;

[TestFixture]
public class WavLayoutMapTests {

  /// <summary>
  /// Builds a minimal WAV file: RIFF header + fmt + data + optional LIST/INFO.
  /// </summary>
  private static MemoryStream BuildTestWav(bool includeMetadata = false) {
    var ms = new MemoryStream();

    ms.Write("RIFF"u8);
    ms.Write(new byte[4]); // placeholder for size
    ms.Write("WAVE"u8);

    // fmt chunk (16 bytes for PCM)
    ms.Write("fmt "u8);
    WriteU32LE(ms, 16);
    WriteU16LE(ms, 1);     // format = PCM
    WriteU16LE(ms, 1);     // channels = 1
    WriteU32LE(ms, 44100); // sample rate
    WriteU32LE(ms, 44100); // byte rate
    WriteU16LE(ms, 1);     // block align
    WriteU16LE(ms, 8);     // bits per sample

    // data chunk (100 bytes of audio)
    var audioData = new byte[100];
    Array.Fill(audioData, (byte)0x80);
    ms.Write("data"u8);
    WriteU32LE(ms, (uint)audioData.Length);
    ms.Write(audioData);

    if (includeMetadata) {
      // LIST/INFO chunk
      var infoBody = new byte[] {
        (byte)'I', (byte)'N', (byte)'F', (byte)'O',
        (byte)'I', (byte)'N', (byte)'A', (byte)'M',
        0x04, 0x00, 0x00, 0x00,
        (byte)'T', (byte)'e', (byte)'s', (byte)'t',
      };
      ms.Write("LIST"u8);
      WriteU32LE(ms, (uint)infoBody.Length);
      ms.Write(infoBody);
    }

    // Patch RIFF size
    var totalSize = ms.Length;
    ms.Position = 4;
    WriteU32LE(ms, (uint)(totalSize - 8));

    ms.Position = 0;
    return ms;
  }

  private static void WriteU32LE(Stream s, uint v) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
    s.Write(buf);
  }

  private static void WriteU16LE(Stream s, ushort v) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buf, v);
    s.Write(buf);
  }

  [Test]
  public void EnumerateChunks_HasRiffHeader() {
    using var ms = BuildTestWav();
    var chunks = WavLayoutMap.Enumerate(ms).ToList();

    var header = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("RIFF"));
    Assert.That(header, Is.Not.Null);
    Assert.That(header!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(header.Offset, Is.EqualTo(0));
    Assert.That(header.Length, Is.EqualTo(12));
  }

  [Test]
  public void EnumerateChunks_HasFmtChunk() {
    using var ms = BuildTestWav();
    var chunks = WavLayoutMap.Enumerate(ms).ToList();

    var fmt = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("fmt"));
    Assert.That(fmt, Is.Not.Null);
    Assert.That(fmt!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_HasDataChunk() {
    using var ms = BuildTestWav();
    var chunks = WavLayoutMap.Enumerate(ms).ToList();

    var data = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("data"));
    Assert.That(data, Is.Not.Null);
    Assert.That(data!.Kind, Is.EqualTo(DefragBlockKind.Used));
  }

  [Test]
  public void EnumerateChunks_MetadataIsCold() {
    using var ms = BuildTestWav(includeMetadata: true);
    var chunks = WavLayoutMap.Enumerate(ms).ToList();

    var list = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("LIST"));
    Assert.That(list, Is.Not.Null);
    Assert.That(list!.Classification, Is.EqualTo(DefragBlockClass.Cold));
  }

  [Test]
  public void EnumerateChunks_CoversFullFile() {
    using var ms = BuildTestWav(includeMetadata: true);
    var chunks = WavLayoutMap.Enumerate(ms).ToList();
    var total = chunks.Sum(c => c.Length);
    Assert.That(total, Is.EqualTo(ms.Length));
  }

  [Test]
  public void EnumerateChunks_Contiguous() {
    using var ms = BuildTestWav(includeMetadata: true);
    var chunks = WavLayoutMap.Enumerate(ms).OrderBy(c => c.Offset).ToList();
    for (var i = 1; i < chunks.Count; i++)
      Assert.That(chunks[i].Offset, Is.EqualTo(chunks[i - 1].Offset + chunks[i - 1].Length),
        $"Gap between '{chunks[i - 1].FileName}' and '{chunks[i].FileName}'");
  }

  [Test]
  public void Descriptor_ImplementsInterfaces() {
    var d = new WavFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalLayoutMap>());
    Assert.That(d, Is.InstanceOf<IFileInternalChunkMover>());
  }

  [Test]
  public void Optimize_DataFirst_AlreadyOptimal() {
    using var ms = BuildTestWav(includeMetadata: false);
    var originalLength = ms.Length;

    new WavOptimizer().Optimize(ms);

    Assert.That(ms.Length, Is.EqualTo(originalLength),
      "Already-optimal WAV should not change");
  }

  [Test]
  public void EnumerateChunks_EmptyStream_ReturnsNothing() {
    using var ms = new MemoryStream();
    var chunks = WavLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }
}
