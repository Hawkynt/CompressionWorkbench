using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Sup;

namespace Compression.Tests.Sup;

[TestFixture]
public class SupTests {

  /// <summary>Builds one PGS segment with the given type, PTS, and body bytes.</summary>
  private static byte[] BuildSegment(byte type, uint pts, byte[] body, uint dts = 0) {
    var seg = new byte[13 + body.Length];
    seg[0] = (byte)'P';
    seg[1] = (byte)'G';
    BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(2, 4), pts);
    BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(6, 4), dts);
    seg[10] = type;
    BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(11, 2), (ushort)body.Length);
    body.CopyTo(seg.AsSpan(13));
    return seg;
  }

  /// <summary>Builds one complete PGS display set.</summary>
  private static byte[] BuildDisplaySet(uint pts, byte objectMarker = 0xCC) {
    using var ms = new MemoryStream();
    ms.Write(BuildSegment(SupReader.SegPresentationComposition, pts, [0xAA, 0xBB]));
    ms.Write(BuildSegment(SupReader.SegWindowDefinition, pts, [0x00, 0x01]));
    ms.Write(BuildSegment(SupReader.SegPaletteDefinition, pts, [0x01, 0x02, 0x03]));
    ms.Write(BuildSegment(SupReader.SegObjectDefinition, pts, [objectMarker]));
    ms.Write(BuildSegment(SupReader.SegEnd, pts + 1000, []));
    return ms.ToArray();
  }

  /// <summary>Builds a PGS file with <paramref name="epochCount"/> complete display sets.</summary>
  private static byte[] BuildSup(int epochCount) {
    using var ms = new MemoryStream();
    for (var i = 0; i < epochCount; i++)
      ms.Write(BuildDisplaySet((uint)((i + 1) * 90_000)));
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesAllSegments() {
    var data = BuildSup(2);
    var stream = SupReader.Read(data);
    Assert.That(stream.Segments, Has.Count.EqualTo(10)); // 5 segments × 2 display sets
    Assert.That(stream.Epochs, Has.Count.EqualTo(2));
    Assert.That(stream.Epochs[0].SegmentCount, Is.EqualTo(5));
    Assert.That(stream.Epochs[0].StartPtsRaw, Is.EqualTo(90_000u));
  }

  [Test, Category("HappyPath")]
  public void Writer_WritesCanonicalSupEnvelopeAndPreservesDts() {
    var segment = new SupReader.Segment(
      PtsRaw: 0x01020304,
      DtsRaw: 0x11223344,
      Type: SupReader.SegWindowDefinition,
      Body: [0xAA, 0xBB],
      FileOffset: 1234);

    var actual = SupWriter.Write([segment]);

    Assert.That(actual, Is.EqualTo(new byte[] {
      0x50, 0x47,
      0x01, 0x02, 0x03, 0x04,
      0x11, 0x22, 0x33, 0x44,
      SupReader.SegWindowDefinition,
      0x00, 0x02,
      0xAA, 0xBB,
    }));
  }

  [Test, Category("EdgeCase")]
  public void Writer_RejectsBodyLargerThanSupLengthField() {
    var segment = new SupReader.Segment(0, 0, SupReader.SegObjectDefinition, new byte[ushort.MaxValue + 1], 0);
    using var output = new MemoryStream();
    Assert.That(
      () => SupWriter.Write(output, [segment]),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataAndEpochs() {
    var data = BuildSup(3);
    using var ms = new MemoryStream(data);
    var entries = new SupFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("subtitle_")), Is.EqualTo(3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesEpochFiles() {
    var data = BuildSup(2);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new SupFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "subtitle_000.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "subtitle_001.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsDemuxedDisplaySetsByteExactly() {
    var original = BuildSup(3);
    var parsed = SupReader.ReadStrict(original);
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("metadata.ini", "derived and deliberately not PGS"u8),
    };
    inputs.AddRange(parsed.Epochs.Select((epoch, index) =>
      ArchiveInputInfo.InMemory($"subtitle_{index:D3}.bin", epoch.RawBytes)));

    using var output = new MemoryStream();
    new SupFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_ReordersGeneratedSubtitleNamesDeterministically() {
    var first = BuildDisplaySet(90_000, 0x11);
    var second = BuildDisplaySet(180_000, 0x22);
    using var expected = new MemoryStream();
    expected.Write(first);
    expected.Write(second);

    using var output = new MemoryStream();
    new SupFormatDescriptor().Create(output, [
      ArchiveInputInfo.InMemory("subtitle_001.bin", second),
      ArchiveInputInfo.InMemory("subtitle_000.bin", first),
    ], new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(expected.ToArray()));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Modify_ReplacesDisplaySetThroughRemux() {
    var original = BuildSup(2);
    var replacement = BuildDisplaySet(90_000, 0xEF);
    var descriptor = new SupFormatDescriptor();
    using var archive = new MemoryStream(original, writable: true);

    ((IArchiveModifiable)descriptor).Add(archive, [
      ArchiveInputInfo.InMemory("subtitle_000.bin", replacement),
    ]);

    archive.Position = 0;
    using var extracted = new MemoryStream();
    descriptor.ExtractEntry(archive, "subtitle_000.bin", extracted, null);
    Assert.That(extracted.ToArray(), Is.EqualTo(replacement));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Modify_RemovesLastDisplaySetThroughRemux() {
    var original = BuildSup(2);
    var first = SupReader.ReadStrict(original).Epochs[0].RawBytes;
    var descriptor = new SupFormatDescriptor();
    using var archive = new MemoryStream(original, writable: true);

    ((IArchiveModifiable)descriptor).Remove(archive, ["subtitle_001.bin"]);

    archive.Position = 0;
    var parsed = SupReader.ReadStrict(archive.ToArray());
    Assert.That(parsed.Epochs, Has.Count.EqualTo(1));
    Assert.That(parsed.Epochs[0].RawBytes, Is.EqualTo(first));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesMuxAndRemuxCapabilities() {
    var descriptor = new SupFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(descriptor.CanPurgeToEmpty, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedFile_Throws() {
    var data = new byte[5];
    Assert.That(() => SupReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_InvalidMagic_Throws() {
    var data = new byte[20];
    data[0] = (byte)'X';
    data[1] = (byte)'Y';
    Assert.That(() => SupReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void ReadStrict_RejectsGarbageTailThatRecoveryReaderIgnores() {
    var valid = BuildDisplaySet(90_000);
    var data = new byte[valid.Length + 3];
    valid.CopyTo(data, 0);
    data[^3] = 0xDE;
    data[^2] = 0xAD;
    data[^1] = 0xBE;

    Assert.That(SupReader.Read(data).Epochs, Has.Count.EqualTo(1));
    Assert.That(() => SupReader.ReadStrict(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_Create_RejectsTruncatedDisplaySetWithoutTouchingTarget() {
    var data = BuildDisplaySet(90_000);
    Array.Resize(ref data, data.Length - 1);
    using var output = new MemoryStream([0xCA, 0xFE], writable: true);

    Assert.That(
      () => new SupFormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("subtitle_000.bin", data)], new FormatCreateOptions()),
      Throws.InstanceOf<InvalidOperationException>());
    Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 0xCA, 0xFE }));
  }

  /// <summary>
  /// A .sup file is one subtitle stream, not a container for a file tree. The writer must say so
  /// through the declared-constraint path the generic create/convert surfaces recognise —
  /// <see cref="InvalidOperationException"/> — instead of leaking the parser's
  /// <see cref="InvalidDataException"/>, which those surfaces read as a broken writer.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void Descriptor_Create_RefusesAnArbitraryFileTreeThroughTheDeclaredConstraintPath() {
    using var output = new MemoryStream();

    Assert.That(
      () => new SupFormatDescriptor().Create(output, [
        ArchiveInputInfo.InMemory("HELLO.TXT", "hello"u8.ToArray()),
        ArchiveInputInfo.InMemory("DATA.BIN", new byte[] { 1, 2, 3, 4 }),
      ], new FormatCreateOptions()),
      Throws.InstanceOf<InvalidOperationException>()
        .With.Message.Contains("SUP creation needs"));
    Assert.That(output.Length, Is.Zero);
  }

  /// <summary>An input list holding nothing but the derived metadata entry carries no display set.</summary>
  [Test, Category("EdgeCase")]
  public void Descriptor_Create_RefusesMetadataOnlyInput() {
    using var output = new MemoryStream();

    Assert.That(
      () => new SupFormatDescriptor().Create(output, [
        ArchiveInputInfo.InMemory("metadata.ini", "[sup]\n"u8.ToArray()),
      ], new FormatCreateOptions()),
      Throws.InstanceOf<InvalidOperationException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_SegmentsBeforeFirstPcs_AreIgnoredForEpochGrouping() {
    using var ms = new MemoryStream();
    // Stray PDS+ODS before any PCS — should not produce an epoch.
    ms.Write(BuildSegment(SupReader.SegPaletteDefinition, 0, [0x10]));
    ms.Write(BuildSegment(SupReader.SegObjectDefinition, 0, [0x11]));
    ms.Write(BuildSegment(SupReader.SegEnd, 0, []));
    // Then one full epoch.
    ms.Write(BuildSegment(SupReader.SegPresentationComposition, 9000, [0xAA]));
    ms.Write(BuildSegment(SupReader.SegEnd, 10000, []));

    var parsed = SupReader.Read(ms.ToArray());
    Assert.That(parsed.Epochs, Has.Count.EqualTo(1));
    Assert.That(parsed.Segments, Has.Count.EqualTo(5));
  }
}
