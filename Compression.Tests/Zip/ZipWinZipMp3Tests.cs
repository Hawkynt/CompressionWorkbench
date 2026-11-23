using System.Buffers.Binary;
using System.Reflection;
using Compression.Core.Entropy.PackMp3;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP method 94 (WinZip MP3, a packMP3 v1.0g stream): the archive WinZip 21 wrote
/// (Zip/WinZipMp3Vectors) must extract to its source MP3 byte for byte, and for every MP3/PMP pair
/// packMP3 wrote, our encoder must write the very same PMP bytes and our decoder must restore the MP3.
/// </summary>
[TestFixture]
public class ZipWinZipMp3Tests {
  private const int PayloadOffset = 59;
  private const int PayloadSize = 216;

  private static readonly string[] Pairs = [
    "stereo_intensity", "mono_crc", "stereo_crc", "stereo_dual", "stereo_force_ms", "mono_emphasis_50_15", "mono_id3v2",
    "mono_vbr_9", "mono_44100_032", "stereo_32000_128", "mono_no_reservoir",
    "cwb_headcut", "cwb_truncated", "cwb_garbagehead", "cwb_transient",
  ];

  internal static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZipWinZipMp3Vectors.{name}")
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] SampleMp3() => Vector("method94-project-authored.mp3");

  private static byte[] Payload() => Vector("winzip21-method94.zipx").AsSpan(PayloadOffset, PayloadSize).ToArray();

  [Test, Category("Spec")]
  public void Given_WinZip21Archive_When_Extracted_Then_TheSourceMp3ComesBackByteForByte() {
    using var reader = new ZipReader(new MemoryStream(Vector("winzip21-method94.zipx")));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Mp3));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(SampleMp3()));
  }

  [Test, Category("Spec")]
  public void Given_SourceMp3_When_Compressed_Then_ItIsTheWinZipPayloadByteForByte()
    => Assert.That(PackMp3Codec.Compress(SampleMp3()), Is.EqualTo(Payload()));

  [TestCaseSource(nameof(Pairs)), Category("Spec")]
  public void Given_PackMp3Pair_When_Compressed_Then_ItIsWhatPackMp3Writes(string name)
    => Assert.That(PackMp3Codec.Compress(Vector(name + ".mp3")), Is.EqualTo(Vector(name + ".pmp")));

  [TestCaseSource(nameof(Pairs)), Category("Spec")]
  public void Given_PackMp3Pair_When_Decompressed_Then_TheMp3ComesBack(string name)
    => Assert.That(PackMp3Codec.Decompress(Vector(name + ".pmp")), Is.EqualTo(Vector(name + ".mp3")));

  [Test, Category("HappyPath")]
  public void Given_Mp3_When_WrittenWithMethod94_Then_TheEntryCarriesThePackMp3StreamAndExtracts() {
    var mp3 = Vector("stereo_crc.mp3");
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true))
      writer.AddEntry("track.mp3", mp3, ZipCompressionMethod.Mp3);
    var archive = ms.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(8)), Is.EqualTo(94));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(4)), Is.EqualTo(20), "WinZip sets version needed 2.0");
    var size = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(18));
    var start = 30 + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(28));
    Assert.That(archive.AsSpan(start, size).ToArray(), Is.EqualTo(Vector("stereo_crc.pmp")));
    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(mp3));
  }

  [Test, Category("HappyPath")]
  public void Given_NonMp3Data_When_WrittenWithMethod94_Then_ItFallsBackToDeflate() {
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("not an MP3 file at all. ", 200)));
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true))
      writer.AddEntry("notes.txt", text, ZipCompressionMethod.Mp3);
    using var reader = new ZipReader(new MemoryStream(ms.ToArray()));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Deflate));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(text));
  }

  [Test, Category("BoundaryCase")]
  public void Given_FewerThanFiveFrames_When_Compressed_Then_ItIsRefused()
    => Assert.That(PackMp3Codec.TryCompress(SampleMp3().AsSpan(0, 1000), out _), Is.False);

  [Test, Category("BoundaryCase")]
  public void Given_SilentMp3WithIntensityStereo_When_RoundTripped_Then_ItIsRestored() {
    var mp3 = Vector("stereo_intensity.mp3");
    Assert.That(PackMp3Codec.TryCompress(mp3, out var pmp), Is.True);
    Assert.That(PackMp3Codec.Decompress(pmp), Is.EqualTo(mp3));
  }

  [Test, Category("ExceptionalCase")]
  public void Given_Mpeg1LayerIIHeaders_When_Compressed_Then_NotSupported() {
    var mp3 = SampleMp3();
    // Every frame header claims Layer II; its 128 kbit/s bitrate index is 8 rather than 9, and
    // the frames keep their length, so they still chain.
    for (var i = 0; i + 2 < mp3.Length; ++i)
      if (mp3[i] == 0xFF && mp3[i + 1] == 0xFB) {
        mp3[i + 1] = 0xFD;
        mp3[i + 2] = (byte)(0x80 | (mp3[i + 2] & 0x0F));
      }
    Assert.Throws<NotSupportedException>(() => PackMp3Codec.Compress(mp3));
  }

  [Test, Category("ExceptionalCase")]
  public void Given_RandomBytes_When_Compressed_Then_InvalidData() {
    var noise = new byte[20_000];
    new Random(94).NextBytes(noise);
    Assert.Multiple(() => {
      Assert.Throws<InvalidDataException>(() => PackMp3Codec.Compress(noise));
      Assert.Throws<InvalidDataException>(() => PackMp3Codec.Compress([]));
      Assert.That(PackMp3Codec.TryCompress(noise, out _), Is.False);
    });
  }

  [Test, Category("ExceptionalCase")]
  public void Given_WrongSignatureOrVersion_When_Decompressed_Then_InvalidData() {
    var wrongMagic = Payload();
    wrongMagic[0] = (byte)'X';
    var wrongVersion = Payload();
    wrongVersion[2] = 11;
    Assert.Multiple(() => {
      Assert.Throws<InvalidDataException>(() => PackMp3Codec.Decompress(wrongMagic));
      Assert.Throws<InvalidDataException>(() => PackMp3Codec.Decompress(wrongVersion));
      Assert.Throws<InvalidDataException>(() => PackMp3Codec.Decompress([]));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void Given_TruncatedStream_When_Decompressed_Then_InvalidData() {
    var pmp = Vector("cwb_transient.pmp");
    Assert.Throws<InvalidDataException>(() => PackMp3Codec.Decompress(pmp.AsSpan(0, pmp.Length / 2)));
  }

  [Test, Category("ExceptionalCase")]
  public void Given_CorruptPayloadByte_When_Extracted_Then_NoWrongBytesComeBack() {
    var mp3 = Vector("mono_crc.mp3");
    var pmp = Vector("mono_crc.pmp");
    pmp[pmp.Length / 2] ^= 0x5A;
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true))
      writer.AddRawEntry("track.mp3", pmp, ZipCompressionMethod.Mp3, Compression.Core.Checksums.Crc32.Compute(mp3), mp3.Length);
    using var reader = new ZipReader(new MemoryStream(ms.ToArray()));
    Assert.That(() => reader.ExtractEntry(reader.Entries[0]), Throws.InstanceOf<InvalidDataException>());
  }
}
