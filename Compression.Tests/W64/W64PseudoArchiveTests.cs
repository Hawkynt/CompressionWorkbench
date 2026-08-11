using Codec.Pcm;
using Compression.Registry;
using FileFormat.W64;

namespace Compression.Tests.W64;

/// <summary>
/// Given a Sony Wave64 file, When the descriptor lists/extracts it, Then it
/// surfaces FULL.w64 + metadata.ini + per-channel WAVs, never throws on malformed
/// input, and Create round-trips per-channel WAVs through a GUID-keyed container.
/// </summary>
[TestFixture]
public class W64PseudoArchiveTests {

  private static byte[] BuildStereoInterleaved(out int channels, out int sampleRate, out int bits) {
    channels = 2; sampleRate = 48000; bits = 16;
    const int frames = 5;
    var pcm = new byte[frames * 2 * 2];
    for (var f = 0; f < frames; ++f) {
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((f * 2 + 0) * 2), (short)(f + 1));
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((f * 2 + 1) * 2), (short)(-(f + 1)));
    }
    return pcm;
  }

  private static byte[] BuildStereoW64(out byte[] interleaved) {
    interleaved = BuildStereoInterleaved(out var ch, out var sr, out var bits);
    var split = PcmCodec.SplitInterleavedPcm(interleaved, ch, sr, bits);
    var inputs = split.Select(c => ArchiveInputInfo.InMemory($"{c.Name}.wav", c.WavBlob)).ToList();
    using var ms = new MemoryStream();
    new W64FormatDescriptor().Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndChannels() {
    var w64 = BuildStereoW64(out _);
    using var ms = new MemoryStream(w64);
    var names = new W64FormatDescriptor().List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.w64"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("LEFT.wav"));
    Assert.That(names, Does.Contain("RIGHT.wav"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullIsByteIdentical() {
    var w64 = BuildStereoW64(out _);
    var tmp = Path.Combine(Path.GetTempPath(), $"w64-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(w64);
      new W64FormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.w64")), Is.EqualTo(w64));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "metadata.ini")), Does.Contain("sample_rate=48000"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_FallsBackToFull() {
    var bogus = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
    using var ms = new MemoryStream(bogus);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new W64FormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.w64"));
  }

  [Test, Category("HappyPath")]
  public void Create_FromChannelWavs_RoundTripsToSameChannels() {
    var w64 = BuildStereoW64(out var interleaved);
    var split = PcmCodec.SplitInterleavedPcm(interleaved, 2, 48000, 16);
    var inputs = split.Select(c => ArchiveInputInfo.InMemory($"{c.Name}.wav", c.WavBlob)).ToList();

    using var created = new MemoryStream();
    new W64FormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    created.Position = 0;

    using var left = new MemoryStream();
    new W64FormatDescriptor().ExtractEntry(created, "LEFT.wav", left, null);
    Assert.That(left.ToArray(), Is.EqualTo(split[0].WavBlob));
  }

  [Test, Category("HappyPath")]
  public void Create_FromFullPassthrough_IsByteIdentical() {
    var w64 = BuildStereoW64(out _);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.w64", w64) };
    using var created = new MemoryStream();
    new W64FormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    Assert.That(created.ToArray(), Is.EqualTo(w64));
  }
}
