using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Rf64;

namespace Compression.Tests.Rf64;

/// <summary>
/// Given an EBU RF64 / Broadcast Wave file, When the descriptor lists/extracts
/// it, Then it surfaces FULL.rf64 + metadata.ini (with ds64/bext fields) +
/// per-channel WAVs, never throws on malformed input, and Create round-trips
/// per-channel WAVs through a ds64 + fmt + data container.
/// </summary>
[TestFixture]
public class Rf64PseudoArchiveTests {

  private static byte[] BuildStereoInterleaved() {
    const int frames = 6;
    var pcm = new byte[frames * 2 * 2];
    for (var f = 0; f < frames; ++f) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((f * 2 + 0) * 2), (short)(10 + f));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((f * 2 + 1) * 2), (short)(-(10 + f)));
    }
    return pcm;
  }

  private static byte[] BuildRf64WithBext(out byte[] interleaved) {
    interleaved = BuildStereoInterleaved();

    // Build the RF64+ds64+fmt+data skeleton via Create from channel WAVs, then
    // splice in a bext chunk before the data chunk to exercise broadcast metadata.
    var split = PcmCodec.SplitInterleavedPcm(interleaved, 2, 44100, 16);
    var skelInputs = split.Select(c => ArchiveInputInfo.InMemory($"{c.Name}.wav", c.WavBlob)).ToList();
    byte[] baseFile;
    using (var skel = new MemoryStream()) {
      new Rf64FormatDescriptor().Create(skel, skelInputs, new FormatCreateOptions());
      baseFile = skel.ToArray();
    }

    // Build a bext chunk (348-byte minimal body) and insert it just before "data".
    var bext = new byte[348];
    Encoding.ASCII.GetBytes("Test broadcast").CopyTo(bext, 0);      // description (256)
    Encoding.ASCII.GetBytes("Workbench").CopyTo(bext, 256);          // originator (32)
    Encoding.ASCII.GetBytes("2026-06-11").CopyTo(bext, 320);         // date (10)
    BinaryPrimitives.WriteInt64LittleEndian(bext.AsSpan(338), 123456); // time reference

    // Find "data" chunk start in baseFile.
    var dataIdx = IndexOf(baseFile, "data"u8.ToArray(), 12);
    using var ms = new MemoryStream();
    ms.Write(baseFile.AsSpan(0, dataIdx));
    ms.Write("bext"u8);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)bext.Length); ms.Write(u32);
    ms.Write(bext);
    ms.Write(baseFile.AsSpan(dataIdx));
    return ms.ToArray();
  }

  private static int IndexOf(byte[] haystack, byte[] needle, int start) {
    for (var i = start; i + needle.Length <= haystack.Length; ++i) {
      var ok = true;
      for (var j = 0; j < needle.Length; ++j)
        if (haystack[i + j] != needle[j]) { ok = false; break; }
      if (ok) return i;
    }
    return -1;
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndChannels() {
    var rf64 = BuildRf64WithBext(out _);
    using var ms = new MemoryStream(rf64);
    var names = new Rf64FormatDescriptor().List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.rf64"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("LEFT.wav"));
    Assert.That(names, Does.Contain("RIGHT.wav"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdentical_MetadataHasBextAndDs64() {
    var rf64 = BuildRf64WithBext(out _);
    var tmp = Path.Combine(Path.GetTempPath(), $"rf64-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(rf64);
      new Rf64FormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.rf64")), Is.EqualTo(rf64));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("bext_description=Test broadcast"));
      Assert.That(meta, Does.Contain("bext_originator=Workbench"));
      Assert.That(meta, Does.Contain("bext_time_reference=123456"));
      Assert.That(meta, Does.Contain("ds64_data_size="));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_FallsBackToFull() {
    var bogus = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
    using var ms = new MemoryStream(bogus);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new Rf64FormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.rf64"));
  }

  [Test, Category("HappyPath")]
  public void Create_FromChannelWavs_RoundTripsToSameChannels() {
    var interleaved = BuildStereoInterleaved();
    var split = PcmCodec.SplitInterleavedPcm(interleaved, 2, 44100, 16);
    var inputs = split.Select(c => ArchiveInputInfo.InMemory($"{c.Name}.wav", c.WavBlob)).ToList();

    using var created = new MemoryStream();
    new Rf64FormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    created.Position = 0;

    var names = new Rf64FormatDescriptor().List(created, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("RIGHT.wav"));

    created.Position = 0;
    using var right = new MemoryStream();
    new Rf64FormatDescriptor().ExtractEntry(created, "RIGHT.wav", right, null);
    Assert.That(right.ToArray(), Is.EqualTo(split[1].WavBlob));
  }

  [Test, Category("HappyPath")]
  public void Create_FromFullPassthrough_IsByteIdentical() {
    var rf64 = BuildRf64WithBext(out _);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.rf64", rf64) };
    using var created = new MemoryStream();
    new Rf64FormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    Assert.That(created.ToArray(), Is.EqualTo(rf64));
  }
}
