using System.Buffers.Binary;
using System.Text;
using FileFormat.Ape;

namespace Compression.Tests.Ape;

/// <summary>
/// Given a Monkey's Audio file with a seek table and an APEv2 tag, When the
/// descriptor lists/extracts it, Then it surfaces per-frame blocks and a
/// tags.ini alongside the verbatim FULL.ape — and never throws on malformed input.
/// </summary>
[TestFixture]
public class ApePseudoArchiveTests {

  private const int DescSize = 52;
  private const int HeaderSize = 24;

  // Builds a modern (3.98+) APE file whose seek table points at three frames
  // inside the frame-data region and whose terminating region carries an
  // APEv2 tag with two text items.
  private static byte[] BuildApeWithFramesAndTags(out byte[][] frames, out byte[] apeTag) {
    var wav = "RIFF"u8.ToArray();
    frames = [
      [0xA0, 0xA1, 0xA2, 0xA3],
      [0xB0, 0xB1],
      [0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5],
    ];
    var frameBlob = frames.SelectMany(f => f).ToArray();

    // Layout: desc(52) + header(24) + seekTable + wavHeader + frameData + terminating.
    var seekTableBytes = (uint)(frames.Length * 4);
    var frameStart = (long)DescSize + HeaderSize + seekTableBytes + wav.Length;

    using var seek = new MemoryStream();
    Span<byte> u32 = stackalloc byte[4];
    var running = frameStart;
    foreach (var f in frames) {
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)running);
      seek.Write(u32);
      running += f.Length;
    }
    var seekTable = seek.ToArray();

    apeTag = BuildApeV2Tag(("ARTIST", "Synthwave"), ("TITLE", "Test Track"));

    using var ms = new MemoryStream();
    ms.Write("MAC "u8);
    Span<byte> u16 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(u16, 3990); ms.Write(u16); // version
    ms.Write(new byte[2]);                                              // padding
    BinaryPrimitives.WriteUInt32LittleEndian(u32, DescSize); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, HeaderSize); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, seekTableBytes); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)wav.Length); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)frameBlob.Length); ms.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); ms.Write(u32);    // frameDataBytesHigh
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)apeTag.Length); ms.Write(u32); // terminating
    ms.Write(new byte[16]);                                            // md5

    // APE_HEADER
    BinaryPrimitives.WriteUInt16LittleEndian(u16, 2000); ms.Write(u16); // compressionLevel
    BinaryPrimitives.WriteUInt16LittleEndian(u16, 0); ms.Write(u16);    // formatFlags
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 73728); ms.Write(u32);// blocksPerFrame
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 1000); ms.Write(u32); // finalFrameBlocks
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)frames.Length); ms.Write(u32); // totalFrames
    BinaryPrimitives.WriteUInt16LittleEndian(u16, 16); ms.Write(u16);   // bitsPerSample
    BinaryPrimitives.WriteUInt16LittleEndian(u16, 2); ms.Write(u16);    // channels
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 44100); ms.Write(u32);// sampleRate

    ms.Write(seekTable);
    ms.Write(wav);
    ms.Write(frameBlob);
    ms.Write(apeTag);
    return ms.ToArray();
  }

  // Minimal APEv2 tag: items + 32-byte footer. No header flag set.
  private static byte[] BuildApeV2Tag(params (string Key, string Value)[] items) {
    using var body = new MemoryStream();
    Span<byte> u32 = stackalloc byte[4];
    foreach (var (key, value) in items) {
      var valBytes = Encoding.UTF8.GetBytes(value);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)valBytes.Length); body.Write(u32);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); body.Write(u32); // flags: UTF-8 text
      body.Write(Encoding.ASCII.GetBytes(key));
      body.WriteByte(0);
      body.Write(valBytes);
    }
    var itemBytes = body.ToArray();
    var tagSize = (uint)(itemBytes.Length + 32); // items + footer

    using var tag = new MemoryStream();
    tag.Write(itemBytes);
    tag.Write(ApeTagMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 2000); tag.Write(u32); // version
    BinaryPrimitives.WriteUInt32LittleEndian(u32, tagSize); tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)items.Length); tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0); tag.Write(u32);    // flags (no header)
    tag.Write(new byte[8]);                                              // reserved
    return tag.ToArray();
  }

  private static byte[] ApeTagMagic => "APETAGEX"u8.ToArray();

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndDecomposedEntries() {
    var ape = BuildApeWithFramesAndTags(out var frames, out _);
    using var ms = new MemoryStream(ape);
    var entries = new ApeFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.ape"));
    Assert.That(names, Does.Contain("metadata.ini"));
    for (var i = 0; i < frames.Length; ++i)
      Assert.That(names, Does.Contain($"frames/frame_{i:D4}.bin"));
    Assert.That(names, Does.Contain("tags.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesPerFrameBlocksAndTags_FullIsByteIdentical() {
    var ape = BuildApeWithFramesAndTags(out var frames, out _);
    var tmp = Path.Combine(Path.GetTempPath(), $"ape-pa-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(ape);
      new ApeFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.ape")), Is.EqualTo(ape));
      for (var i = 0; i < frames.Length; ++i) {
        var path = Path.Combine(tmp, "frames", $"frame_{i:D4}.bin");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(frames[i]), $"frame {i} mismatch");
      }
      var tags = File.ReadAllText(Path.Combine(tmp, "tags.ini"));
      Assert.That(tags, Does.Contain("ARTIST=Synthwave"));
      Assert.That(tags, Does.Contain("TITLE=Test Track"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_FallsBackToFull() {
    var bogus = new byte[16];
    bogus[0] = 0xDE; bogus[1] = 0xAD;
    using var ms = new MemoryStream(bogus);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new ApeFormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ape"));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_ExtractedFullRereadsIdentically() {
    var ape = BuildApeWithFramesAndTags(out _, out _);
    using var first = new MemoryStream(ape);
    using var full = new MemoryStream();
    new ApeFormatDescriptor().ExtractEntry(first, "FULL.ape", full, null);
    Assert.That(full.ToArray(), Is.EqualTo(ape));

    // Re-read the extracted FULL and confirm decomposition is stable.
    using var second = new MemoryStream(full.ToArray());
    var names = new ApeFormatDescriptor().List(second, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("frames/frame_0000.bin"));
    Assert.That(names, Does.Contain("tags.ini"));
  }
}
