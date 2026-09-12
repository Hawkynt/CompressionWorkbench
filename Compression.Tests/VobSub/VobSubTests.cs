using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.MpegPs;
using FileFormat.VobSub;

namespace Compression.Tests.VobSub;

[TestFixture]
public class VobSubTests {

  private const string SampleIdx =
    "# VobSub index file, v7\n" +
    "# Created by test\n" +
    "size: 720x480\n" +
    "palette: 000000, ffffff, ff0000, 00ff00, 0000ff, ffff00\n" +
    "id: en, index: 0\n" +
    "timestamp: 00:00:01:000, filepos: 0000000000\n" +
    "timestamp: 00:00:03:500, filepos: 0000000020\n" +
    "timestamp: 00:00:06:250, filepos: 0000000040\n";

  private const string WritableIdx =
    "# VobSub index file, v7\n" +
    "size: 720x480\n" +
    "palette: 000000, ffffff, ff0000, 00ff00, 0000ff, ffff00, 00ffff, ff00ff, 808080, c0c0c0, 800000, 008000, 000080, 808000, 008080, 800080\n" +
    "id: en, index: 0\n" +
    "timestamp: 00:00:01:000, filepos: 0000000000\n" +
    "timestamp: 00:00:03:500, filepos: 0000000000\n";

  private static byte[] BuildSubBytes() {
    var buf = new byte[64];
    for (var i = 0; i < buf.Length; i++) buf[i] = (byte)(i & 0xFF);
    return buf;
  }

  private static byte[] RawSpu(byte data) => [
    0x00, 0x0A, // packet length
    0x00, 0x05, // control sequence starts after one byte of bitmap data
    data,
    0x00, 0x00, // control date
    0x00, 0x05, // next control sequence points to itself
    0xFF,       // end commands
  ];

  private static byte[] LargeRawSpu() {
    const int length = 5000;
    const int controlOffset = length - 5;
    var result = new byte[length];
    BinaryPrimitives.WriteUInt16BigEndian(result, length);
    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), controlOffset);
    for (var i = 4; i < controlOffset; ++i)
      result[i] = (byte)(i * 29 + 17);
    result[controlOffset] = 0;
    result[controlOffset + 1] = 0;
    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(controlOffset + 2), controlOffset);
    result[controlOffset + 4] = 0xFF;
    return result;
  }

  private static VobSubWriter.Pair BuildWritablePair(byte first = 0x11, byte second = 0x22)
    => VobSubWriter.Build(WritableIdx, [
      new VobSubWriter.Frame(RawSpu(first), VobSubWriter.FrameKind.RawSpu),
      new VobSubWriter.Frame(RawSpu(second), VobSubWriter.FrameKind.RawSpu),
    ]);

  [Test, Category("HappyPath")]
  public void ReadIndex_ParsesAllFields() {
    var idx = VobSubReader.ReadIndex(SampleIdx);
    Assert.That(idx.Width, Is.EqualTo(720));
    Assert.That(idx.Height, Is.EqualTo(480));
    Assert.That(idx.Palette, Has.Count.EqualTo(6));
    Assert.That(idx.Language, Is.EqualTo("en"));
    Assert.That(idx.Entries, Has.Count.EqualTo(3));
    Assert.That(idx.Entries[0].Timestamp, Is.EqualTo(TimeSpan.FromMilliseconds(1000)));
    Assert.That(idx.Entries[1].FilePos, Is.EqualTo(0x20));
  }

  [Test, Category("HappyPath")]
  public void SliceFrames_RespectsBoundaries() {
    var idx = VobSubReader.ReadIndex(SampleIdx);
    var sub = BuildSubBytes();
    var frames = VobSubReader.SliceFrames(idx, sub);
    Assert.That(frames, Has.Count.EqualTo(3));
    Assert.That(frames[0], Has.Length.EqualTo(0x20));
    Assert.That(frames[1], Has.Length.EqualTo(0x20));
    Assert.That(frames[2], Has.Length.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListPair_ReturnsMetadataAndFrames() {
    var idxBytes = Encoding.UTF8.GetBytes(SampleIdx);
    var subBytes = BuildSubBytes();
    var entries = new VobSubFormatDescriptor().ListPair(idxBytes, subBytes);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "index.idx"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("subtitle_")), Is.EqualTo(3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_ExtractPair_WritesAllFiles() {
    var idxBytes = Encoding.UTF8.GetBytes(SampleIdx);
    var subBytes = BuildSubBytes();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      new VobSubFormatDescriptor().ExtractPair(idxBytes, subBytes, tmp, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "index.idx")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "subtitle_000.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmp, "subtitle_000.bin")).Length, Is.EqualTo(0x20));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListFromMemoryStream_StillProducesMetadata() {
    var idxBytes = Encoding.UTF8.GetBytes(SampleIdx);
    using var ms = new MemoryStream(idxBytes);
    var entries = new VobSubFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "index.idx"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesMuxAndRemuxCapabilities() {
    var descriptor = new VobSubFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveWriteConstraints>());
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RawSpuMux_ProducesDvdSizedMpegProgramStreamSectors() {
    var pair = BuildWritablePair();
    var index = VobSubReader.ReadIndex(Encoding.UTF8.GetString(pair.IndexBytes));

    Assert.Multiple(() => {
      Assert.That(pair.SubBytes, Has.Length.EqualTo(4096));
      Assert.That(pair.SubBytes.AsSpan(0, 4).SequenceEqual<byte>([0x00, 0x00, 0x01, 0xBA]), Is.True);
      Assert.That(pair.SubBytes.AsSpan(14, 4).SequenceEqual<byte>([0x00, 0x00, 0x01, 0xBD]), Is.True);
      Assert.That(pair.SubBytes[28], Is.EqualTo(0x20));
      Assert.That(pair.SubBytes.AsSpan(29, 10).SequenceEqual(RawSpu(0x11)), Is.True);
      Assert.That(pair.SubBytes.AsSpan(2048, 4).SequenceEqual<byte>([0x00, 0x00, 0x01, 0xBA]), Is.True);
      Assert.That(index.Entries[0].FilePos, Is.Zero);
      Assert.That(index.Entries[1].FilePos, Is.EqualTo(2048));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_LargeSpu_SpansPesPacketsAndDemuxesBackByteExact() {
    var raw = LargeRawSpu();
    var pair = VobSubWriter.Build(
      "# VobSub index file, v7\nid: en, index: 0\ntimestamp: 00:00:01:000, filepos: 0000000000\n",
      [new VobSubWriter.Frame(raw, VobSubWriter.FrameKind.RawSpu)]);
    var program = MpegPsReader.Read(pair.SubBytes);
    var subtitle = program.Streams.Single(stream => stream.StreamId == 0xBD && stream.SubstreamId == 0x20);

    Assert.Multiple(() => {
      Assert.That(pair.SubBytes.Length, Is.EqualTo(3 * 2048));
      Assert.That(program.PackCount, Is.EqualTo(3));
      Assert.That(subtitle.PacketCount, Is.EqualTo(3));
      Assert.That(subtitle.FirstPts, Is.EqualTo(90000));
      Assert.That(subtitle.LastPts, Is.EqualTo(90000));
      Assert.That(subtitle.Payload, Is.EqualTo(raw));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RawSpuMux_WritesIdxAndSiblingSub() {
    var temp = MakeTempPairPath();
    try {
      var descriptor = new VobSubFormatDescriptor();
      using (var output = File.Open(temp.Idx, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        descriptor.Create(output, [
          ArchiveInputInfo.InMemory("index.idx", Encoding.UTF8.GetBytes(WritableIdx)),
          ArchiveInputInfo.InMemory("subtitle_000.spu", RawSpu(0x31)),
          ArchiveInputInfo.InMemory("subtitle_001.spu", RawSpu(0x32)),
        ], new FormatCreateOptions());

      var idxBytes = File.ReadAllBytes(temp.Idx);
      var subBytes = File.ReadAllBytes(temp.Sub);
      var parsed = VobSubReader.Read(idxBytes, subBytes);
      Assert.Multiple(() => {
        Assert.That(parsed.Frames, Has.Count.EqualTo(2));
        Assert.That(parsed.Index.Entries[1].FilePos, Is.EqualTo(2048));
        Assert.That(subBytes.Length % 2048, Is.Zero);
      });
    } finally {
      Cleanup(temp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_FromExtractedBinFrames_RemuxesSubByteExact() {
    var source = BuildWritablePair();
    var parsed = VobSubReader.Read(source.IndexBytes, source.SubBytes);
    var temp = MakeTempPairPath();
    try {
      var descriptor = new VobSubFormatDescriptor();
      using (var output = File.Open(temp.Idx, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        descriptor.Create(output, [
          ArchiveInputInfo.InMemory("index.idx", source.IndexBytes),
          ArchiveInputInfo.InMemory("subtitle_000.bin", parsed.Frames[0]),
          ArchiveInputInfo.InMemory("subtitle_001.bin", parsed.Frames[1]),
        ], new FormatCreateOptions());

      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(temp.Sub), Is.EqualTo(source.SubBytes));
        Assert.That(File.ReadAllBytes(temp.Idx), Is.EqualTo(source.IndexBytes));
      });
    } finally {
      Cleanup(temp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Add_ReplacesOnePreframedChunkAndPreservesTheOther() {
    var source = BuildWritablePair();
    var original = VobSubReader.Read(source.IndexBytes, source.SubBytes);
    var replacementPair = VobSubWriter.Build(
      "# VobSub index file, v7\nid: en, index: 0\ntimestamp: 00:00:01:000, filepos: 0000000000\n",
      [new VobSubWriter.Frame(RawSpu(0x77), VobSubWriter.FrameKind.RawSpu)]);
    var replacement = VobSubReader.Read(replacementPair.IndexBytes, replacementPair.SubBytes).Frames[0];
    var temp = WriteTempPair(source);
    try {
      var descriptor = new VobSubFormatDescriptor();
      using (var archive = File.Open(temp.Idx, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        descriptor.Add(archive, [ArchiveInputInfo.InMemory("subtitle_000.bin", replacement)]);

      var edited = VobSubReader.Read(File.ReadAllBytes(temp.Idx), File.ReadAllBytes(temp.Sub));
      Assert.Multiple(() => {
        Assert.That(edited.Frames[0], Is.EqualTo(replacement));
        Assert.That(edited.Frames[1], Is.EqualTo(original.Frames[1]));
        Assert.That(edited.Index.Entries.Select(e => e.Timestamp), Is.EqualTo(original.Index.Entries.Select(e => e.Timestamp)));
      });
    } finally {
      Cleanup(temp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Remove_DropsFrameAndRebasesFilePositions() {
    var source = BuildWritablePair();
    var original = VobSubReader.Read(source.IndexBytes, source.SubBytes);
    var temp = WriteTempPair(source);
    try {
      var descriptor = new VobSubFormatDescriptor();
      using (var archive = File.Open(temp.Idx, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        descriptor.Remove(archive, ["subtitle_000.bin"]);

      var edited = VobSubReader.Read(File.ReadAllBytes(temp.Idx), File.ReadAllBytes(temp.Sub));
      Assert.Multiple(() => {
        Assert.That(edited.Frames, Has.Count.EqualTo(1));
        Assert.That(edited.Frames[0], Is.EqualTo(original.Frames[1]));
        Assert.That(edited.Index.Entries[0].Timestamp, Is.EqualTo(original.Index.Entries[1].Timestamp));
        Assert.That(edited.Index.Entries[0].FilePos, Is.Zero);
      });
    } finally {
      Cleanup(temp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Purge_LeavesValidEmptyPair() {
    var temp = WriteTempPair(BuildWritablePair());
    try {
      var descriptor = new VobSubFormatDescriptor();
      using (var archive = File.Open(temp.Idx, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        descriptor.Purge(archive);

      var edited = VobSubReader.Read(File.ReadAllBytes(temp.Idx), File.ReadAllBytes(temp.Sub));
      Assert.Multiple(() => {
        Assert.That(edited.Index.Entries, Is.Empty);
        Assert.That(edited.Frames, Is.Empty);
        Assert.That(new FileInfo(temp.Sub).Length, Is.Zero);
      });
    } finally {
      Cleanup(temp);
    }
  }

  [Test, Category("EdgeCase")]
  public void Writer_RawSpuWithWrongDeclaredLength_Throws() {
    var malformed = RawSpu(0x42);
    malformed[1]--;
    Assert.That(() => VobSubWriter.Build(
        "# VobSub index file, v7\ntimestamp: 00:00:00:000, filepos: 0000000000\n",
        [new VobSubWriter.Frame(malformed, VobSubWriter.FrameKind.RawSpu)]),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_WriteConstraints_RejectGenericArchiveInputs() {
    var descriptor = new VobSubFormatDescriptor();
    var accepted = descriptor.CanAccept(ArchiveInputInfo.InMemory("PROBE.TXT", [1, 2, 3]), out var reason);
    Assert.Multiple(() => {
      Assert.That(accepted, Is.False);
      Assert.That(reason, Does.Contain("index.idx"));
    });
  }

  [Test, Category("EdgeCase")]
  public void ReadIndex_MissingHeader_Throws() {
    Assert.That(() => VobSubReader.ReadIndex("size: 720x480\n"),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void SliceFrames_FilePosBeyondEof_ClampsAtEof() {
    var idx = VobSubReader.ReadIndex(
      "# VobSub index file, v7\n" +
      "size: 1x1\n" +
      "timestamp: 00:00:00:000, filepos: 0000000000\n" +
      "timestamp: 00:00:01:000, filepos: 00000000FF\n");
    var sub = new byte[16];
    var frames = VobSubReader.SliceFrames(idx, sub);
    Assert.That(frames, Has.Count.EqualTo(2));
    Assert.That(frames[0], Has.Length.EqualTo(16));
  }

  private static (string Directory, string Idx, string Sub) MakeTempPairPath() {
    var directory = Path.Combine(Path.GetTempPath(), "cwb_vobsub_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return (directory, Path.Combine(directory, "sample.idx"), Path.Combine(directory, "sample.sub"));
  }

  private static (string Directory, string Idx, string Sub) WriteTempPair(VobSubWriter.Pair pair) {
    var temp = MakeTempPairPath();
    File.WriteAllBytes(temp.Idx, pair.IndexBytes);
    File.WriteAllBytes(temp.Sub, pair.SubBytes);
    return temp;
  }

  private static void Cleanup((string Directory, string Idx, string Sub) temp) {
    try { Directory.Delete(temp.Directory, recursive: true); } catch { /* best effort */ }
  }
}
