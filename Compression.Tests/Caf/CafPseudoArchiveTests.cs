using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Caf;

namespace Compression.Tests.Caf;

/// <summary>
/// Given an Apple Core Audio Format file, When the descriptor lists/extracts it,
/// Then it surfaces FULL.caf + metadata.ini + per-channel WAVs + info.ini, never
/// throws on malformed input, and the lpcm Create round-trips back to channels.
/// </summary>
[TestFixture]
public class CafPseudoArchiveTests {

  // Builds a minimal stereo 16-bit lpcm CAF (caff + desc + info + data).
  private static byte[] BuildStereoCaf(out byte[] interleavedLe) {
    const int channels = 2, sampleRate = 44100, bits = 16, frames = 4;
    interleavedLe = new byte[frames * channels * 2];
    for (var f = 0; f < frames; ++f) {
      BinaryPrimitives.WriteInt16LittleEndian(interleavedLe.AsSpan((f * 2 + 0) * 2), (short)(100 + f));   // L
      BinaryPrimitives.WriteInt16LittleEndian(interleavedLe.AsSpan((f * 2 + 1) * 2), (short)(-(100 + f))); // R
    }

    using var ms = new MemoryStream();
    ms.Write("caff"u8);
    WriteU16Be(ms, 1); WriteU16Be(ms, 0);

    // desc chunk (32-byte body), big-endian; little-endian audio flag set.
    ms.Write("desc"u8);
    WriteI64Be(ms, 32);
    Span<byte> f64 = stackalloc byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(f64, sampleRate); ms.Write(f64);
    ms.Write("lpcm"u8);
    WriteU32Be(ms, 1u << 1);                 // little-endian
    WriteU32Be(ms, (uint)(channels * 2));    // bytes per packet
    WriteU32Be(ms, 1);                       // frames per packet
    WriteU32Be(ms, channels);
    WriteU32Be(ms, bits);

    // info chunk: 2 key/value pairs.
    using var info = new MemoryStream();
    WriteU32Be(info, 2);
    WriteCString(info, "artist"); WriteCString(info, "Synthwave");
    WriteCString(info, "title"); WriteCString(info, "Test");
    var infoBody = info.ToArray();
    ms.Write("info"u8);
    WriteI64Be(ms, infoBody.Length);
    ms.Write(infoBody);

    // data chunk: edit count u32 + audio.
    ms.Write("data"u8);
    WriteI64Be(ms, 4L + interleavedLe.Length);
    WriteU32Be(ms, 0);
    ms.Write(interleavedLe);
    return ms.ToArray();
  }

  private static void WriteU16Be(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
  private static void WriteU32Be(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
  private static void WriteI64Be(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }
  private static void WriteCString(Stream s, string v) { s.Write(System.Text.Encoding.UTF8.GetBytes(v)); s.WriteByte(0); }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataChannelsAndInfo() {
    var caf = BuildStereoCaf(out _);
    using var ms = new MemoryStream(caf);
    var names = new CafFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.caf"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("info.ini"));
    Assert.That(names, Does.Contain("LEFT.wav"));
    Assert.That(names, Does.Contain("RIGHT.wav"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullIsByteIdentical_AndInfoCarriesTags() {
    var caf = BuildStereoCaf(out _);
    var tmp = Path.Combine(Path.GetTempPath(), $"caf-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(caf);
      new CafFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.caf")), Is.EqualTo(caf));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "info.ini")), Does.Contain("artist=Synthwave"));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "metadata.ini")), Does.Contain("format_id=lpcm"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_FallsBackToFull() {
    var bogus = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0 };
    using var ms = new MemoryStream(bogus);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new CafFormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.caf"));
  }

  [Test, Category("HappyPath")]
  public void Create_FromChannelWavs_RoundTripsToSameChannels() {
    var caf = BuildStereoCaf(out var interleavedLe);
    var split = PcmCodec.SplitInterleavedPcm(interleavedLe, 2, 44100, 16);

    var inputs = split
      .Select(c => ArchiveInputInfo.InMemory($"{c.Name}.wav", c.WavBlob))
      .ToList();

    using var created = new MemoryStream();
    new CafFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    created.Position = 0;

    var names = new CafFormatDescriptor().List(created, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("LEFT.wav"));
    Assert.That(names, Does.Contain("RIGHT.wav"));

    // Extracted LEFT channel must match the original LEFT samples.
    created.Position = 0;
    using var left = new MemoryStream();
    new CafFormatDescriptor().ExtractEntry(created, "LEFT.wav", left, null);
    Assert.That(left.ToArray(), Is.EqualTo(split[0].WavBlob));
  }

  [Test, Category("HappyPath")]
  public void Create_FromFullPassthrough_IsByteIdentical() {
    var caf = BuildStereoCaf(out _);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.caf", caf) };
    using var created = new MemoryStream();
    new CafFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    Assert.That(created.ToArray(), Is.EqualTo(caf));
  }
}
