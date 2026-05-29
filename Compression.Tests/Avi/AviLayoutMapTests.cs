#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Avi;

namespace Compression.Tests.Avi;

[TestFixture]
public class AviLayoutMapTests {

  /// <summary>
  /// Builds a minimal AVI file: RIFF header + hdrl + movi with one video chunk + idx1.
  /// </summary>
  private static MemoryStream BuildTestAvi() {
    var ms = new MemoryStream();

    // We'll build the full file then patch the RIFF size at the end.
    var riffStart = ms.Position;
    ms.Write("RIFF"u8);
    ms.Write(new byte[4]); // placeholder for size
    ms.Write("AVI "u8);

    // LIST/hdrl — minimal avih
    var hdrlBody = new MemoryStream();
    hdrlBody.Write("hdrl"u8);
    // avih chunk: 56 bytes
    var avihData = new byte[56];
    BinaryPrimitives.WriteUInt32LittleEndian(avihData.AsSpan(0), 33333); // microseconds per frame
    BinaryPrimitives.WriteUInt32LittleEndian(avihData.AsSpan(16), 10);   // total frames
    BinaryPrimitives.WriteUInt32LittleEndian(avihData.AsSpan(32), 320);  // width
    BinaryPrimitives.WriteUInt32LittleEndian(avihData.AsSpan(36), 240);  // height
    hdrlBody.Write("avih"u8);
    WriteU32LE(hdrlBody, (uint)avihData.Length);
    hdrlBody.Write(avihData);

    WriteList(ms, hdrlBody.ToArray());

    // LIST/movi — one video chunk (00dc)
    var moviBody = new MemoryStream();
    moviBody.Write("movi"u8);
    var videoData = new byte[64];
    Array.Fill(videoData, (byte)0xAB);
    moviBody.Write("00dc"u8);
    WriteU32LE(moviBody, (uint)videoData.Length);
    moviBody.Write(videoData);

    WriteList(ms, moviBody.ToArray());

    // idx1 chunk
    var idx1Data = new byte[16]; // one 16-byte index entry
    ms.Write("idx1"u8);
    WriteU32LE(ms, (uint)idx1Data.Length);
    ms.Write(idx1Data);

    // Patch RIFF size
    var totalSize = ms.Length;
    ms.Position = 4;
    WriteU32LE(ms, (uint)(totalSize - 8));

    ms.Position = 0;
    return ms;
  }

  private static void WriteList(MemoryStream ms, byte[] listBody) {
    ms.Write("LIST"u8);
    WriteU32LE(ms, (uint)listBody.Length);
    ms.Write(listBody);
  }

  private static void WriteU32LE(Stream ms, uint value) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
    ms.Write(buf);
  }

  [Test]
  public void EnumerateChunks_HasRiffHeader() {
    using var ms = BuildTestAvi();
    var chunks = AviLayoutMap.Enumerate(ms).ToList();

    var header = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("RIFF"));
    Assert.That(header, Is.Not.Null);
    Assert.That(header!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(header.Offset, Is.EqualTo(0));
    Assert.That(header.Length, Is.EqualTo(12));
  }

  [Test]
  public void EnumerateChunks_HasHdrl() {
    using var ms = BuildTestAvi();
    var chunks = AviLayoutMap.Enumerate(ms).ToList();

    var hdrl = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("hdrl"));
    Assert.That(hdrl, Is.Not.Null);
    Assert.That(hdrl!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_HasVideoChunks() {
    using var ms = BuildTestAvi();
    var chunks = AviLayoutMap.Enumerate(ms).ToList();

    var video = chunks.Where(c => c.FileName != null && c.FileName.Contains("video")).ToList();
    Assert.That(video, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(video[0].Kind, Is.EqualTo(DefragBlockKind.Used));
  }

  [Test]
  public void EnumerateChunks_HasIdx1() {
    using var ms = BuildTestAvi();
    var chunks = AviLayoutMap.Enumerate(ms).ToList();

    var idx1 = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("idx1"));
    Assert.That(idx1, Is.Not.Null);
    Assert.That(idx1!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void Descriptor_ImplementsInterfaces() {
    var d = new AviFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalLayoutMap>());
    Assert.That(d, Is.InstanceOf<IFileInternalChunkMover>());
  }

  [Test]
  public void EnumerateChunks_EmptyStream_ReturnsNothing() {
    using var ms = new MemoryStream();
    var chunks = AviLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }
}
