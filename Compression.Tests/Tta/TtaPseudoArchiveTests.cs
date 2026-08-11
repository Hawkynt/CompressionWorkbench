using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Tta;

namespace Compression.Tests.Tta;

/// <summary>
/// Given a True Audio (.tta) file with a seek table and an APEv2 tag, When the
/// descriptor lists/extracts it, Then it surfaces FULL.tta + metadata.ini +
/// per-frame blocks + tags.ini, with FULL byte-identical and no throw on
/// malformed input. Audio decode is deferred (structural-only).
/// </summary>
[TestFixture]
public class TtaPseudoArchiveTests {

  // Build a TTA1 file with 2 frames and an APEv2 tag. sampleRate=44100 =>
  // frameLength = 44100*256/245 = 46080 samples; dataLength = 46081 => 2 frames.
  private static byte[] BuildTta(out byte[][] frames) {
    const int channels = 2, bits = 16, sampleRate = 44100;
    const uint dataLength = 46081; // forces frameCount = 2
    frames = [
      [0x11, 0x22, 0x33, 0x44],
      [0xAA, 0xBB],
    ];

    using var ms = new MemoryStream();
    ms.Write("TTA1"u8);
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(u16, 1); ms.Write(u16);            // format
    BinaryPrimitives.WriteUInt16LittleEndian(u16, channels); ms.Write(u16);
    BinaryPrimitives.WriteUInt16LittleEndian(u16, bits); ms.Write(u16);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, sampleRate); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, dataLength); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); ms.Write(u32);            // header CRC (unchecked)

    // Seek table: frameCount u32 sizes + trailing CRC u32.
    foreach (var f in frames) {
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)f.Length); ms.Write(u32);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); ms.Write(u32);            // seek table CRC

    // Frame data.
    foreach (var f in frames) ms.Write(f);

    // APEv2 tag.
    ms.Write(BuildApeV2Tag(("ARTIST", "Synthwave"), ("TITLE", "Test")));
    return ms.ToArray();
  }

  private static byte[] BuildApeV2Tag(params (string Key, string Value)[] items) {
    using var body = new MemoryStream();
    Span<byte> u32 = stackalloc byte[4];
    foreach (var (key, value) in items) {
      var valBytes = Encoding.UTF8.GetBytes(value);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)valBytes.Length); body.Write(u32);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); body.Write(u32);
      body.Write(Encoding.ASCII.GetBytes(key));
      body.WriteByte(0);
      body.Write(valBytes);
    }
    var itemBytes = body.ToArray();
    var tagSize = (uint)(itemBytes.Length + 32);

    using var tag = new MemoryStream();
    tag.Write(itemBytes);
    tag.Write("APETAGEX"u8);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 2000); tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, tagSize); tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)items.Length); tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); tag.Write(u32);
    tag.Write(new byte[8]);
    return tag.ToArray();
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataFramesAndTags() {
    var tta = BuildTta(out var frames);
    using var ms = new MemoryStream(tta);
    var names = new TtaFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.tta"));
    Assert.That(names, Does.Contain("metadata.ini"));
    for (var i = 0; i < frames.Length; ++i)
      Assert.That(names, Does.Contain($"frames/frame_{i:D4}.bin"));
    Assert.That(names, Does.Contain("tags.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FramesByteIdentical_FullByteIdentical() {
    var tta = BuildTta(out var frames);
    var tmp = Path.Combine(Path.GetTempPath(), $"tta-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(tta);
      new TtaFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.tta")), Is.EqualTo(tta));
      for (var i = 0; i < frames.Length; ++i)
        Assert.That(File.ReadAllBytes(Path.Combine(tmp, "frames", $"frame_{i:D4}.bin")),
          Is.EqualTo(frames[i]), $"frame {i}");
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("channels=2"));
      Assert.That(meta, Does.Contain("sample_rate=44100"));
      Assert.That(meta, Does.Contain("frame_count=2"));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "tags.ini")), Does.Contain("ARTIST=Synthwave"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_FallsBackToFull() {
    var bogus = new byte[] { 0x54, 0x54, 0x00, 0x00 }; // "TT" then junk
    using var ms = new MemoryStream(bogus);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new TtaFormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.tta"));
  }

  [Test, Category("HappyPath")]
  public void ExtractEntry_FullRoundTrips() {
    var tta = BuildTta(out _);
    using var ms = new MemoryStream(tta);
    using var full = new MemoryStream();
    new TtaFormatDescriptor().ExtractEntry(ms, "FULL.tta", full, null);
    Assert.That(full.ToArray(), Is.EqualTo(tta));
  }
}
