using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Zlib;
using Bcl = System.IO.Compression;

namespace Compression.Tests.Zlib;

/// <summary>
/// <see cref="ZlibStream"/> used as a stream: interop with the platform's zlib as an oracle,
/// checksum enforcement, and where it leaves the inner stream.
/// </summary>
[TestFixture]
public class ZlibStreamingTests {

  private static byte[] Data(int size) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i)
      data[i] = (byte)("zlib streaming "[i % 15] ^ (i / 1000));
    return data;
  }

  private static byte[] Compress(byte[] data, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var output = new MemoryStream();
    using (var zlib = new ZlibStream(output, CompressionStreamMode.Compress, level, leaveOpen: true))
      zlib.Write(data);
    return output.ToArray();
  }

  [Test, Category("ThemVsUs"), Category("RoundTrip")]
  public void OurStream_ReadByPlatformZLibStream(
    [Values(DeflateCompressionLevel.None, DeflateCompressionLevel.Fast, DeflateCompressionLevel.Default, DeflateCompressionLevel.Best)] DeflateCompressionLevel level) {
    var data = Data(150_000);
    using var input = new MemoryStream(Compress(data, level));
    using var zlib = new Bcl.ZLibStream(input, Bcl.CompressionMode.Decompress);
    using var output = new MemoryStream();
    zlib.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("ThemVsUs"), Category("RoundTrip")]
  public void PlatformZLibStream_ReadByOurStream() {
    var data = Data(150_000);
    using var compressed = new MemoryStream();
    using (var zlib = new Bcl.ZLibStream(compressed, Bcl.CompressionLevel.Optimal, leaveOpen: true))
      zlib.Write(data);

    compressed.Position = 0;
    using var ours = new ZlibStream(compressed, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    ours.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Decompress_LeavesSeekableInnerStreamRightAfterTheTrailer() {
    var data = Data(40_000);
    var compressed = Compress(data);
    using var input = new MemoryStream([.. compressed, 0xAB, 0xCD]);
    using (var zlib = new ZlibStream(input, CompressionStreamMode.Decompress, leaveOpen: true)) {
      var output = new byte[data.Length];
      zlib.ReadExactly(output);
      Assert.That(output, Is.EqualTo(data));
      Assert.That(zlib.Read(new byte[1]), Is.Zero);
    }

    Assert.That(input.Position, Is.EqualTo(compressed.Length));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_AdlerMismatch_IsInvalidData() {
    var compressed = Compress(Data(1000));
    compressed[^1] ^= 0xFF;
    using var zlib = new ZlibStream(new MemoryStream(compressed), CompressionStreamMode.Decompress);
    Assert.Throws<InvalidDataException>(() => zlib.CopyTo(Stream.Null));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_MissingTrailer_IsInvalidData() {
    var compressed = Compress(Data(1000));
    using var zlib = new ZlibStream(new MemoryStream(compressed[..^4]), CompressionStreamMode.Decompress);
    Assert.Throws<InvalidDataException>(() => zlib.CopyTo(Stream.Null));
  }

  [Test, Category("EdgeCase")]
  public void Instance_EmptyInput_ReadsNothing_StaticHelper_Refuses() {
    using var zlib = new ZlibStream(new MemoryStream(), CompressionStreamMode.Decompress);
    Assert.That(zlib.Read(new byte[16]), Is.Zero);
    Assert.Throws<InvalidDataException>(() => ZlibStream.Decompress(new MemoryStream(), new MemoryStream()));
  }

  [Test, Category("EdgeCase")]
  public void NothingWritten_StillProducesAValidEmptyFrame() {
    var empty = Compress([]);
    Assert.That(ZlibStream.Decompress(empty), Is.Empty);
    using var zlib = new Bcl.ZLibStream(new MemoryStream(empty), Bcl.CompressionMode.Decompress);
    Assert.That(zlib.Read(new byte[1]), Is.Zero);
  }

  [Test, Category("HappyPath")]
  public void StaticStreamHelpers_RoundTrip() {
    var data = Data(70_000);
    using var compressed = new MemoryStream();
    ZlibStream.Compress(new MemoryStream(data), compressed, DeflateCompressionLevel.Best);
    compressed.Position = 0;
    using var output = new MemoryStream();
    ZlibStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }
}
