#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Amf;

namespace Compression.Tests.Amf;

[TestFixture]
public class AmfTests {
  private static readonly byte[] Sample8 = [0, 32, 96, 128, 192, 255];

  [Test]
  public void List_ReferenceV14_SkipsTrackMapAndTrackEventsBeforeSampleData() {
    var amf = MakeReferenceV14();
    var descriptor = new AmfFormatDescriptor();
    var entries = descriptor.List(new MemoryStream(amf), null);

    Assert.That(entries.Any(e => e.Name == "FULL.amf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    var sampleName = entries.Single(e => e.Name.StartsWith("samples/01_", StringComparison.Ordinal)).Name;
    var wav = Extract(descriptor, amf, sampleName);

    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)), Is.EqualTo(8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)), Is.EqualTo(16000u));
      Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(Sample8));
    });
  }

  [TestCase("0.1", 1)]
  [TestCase("0.8", 8)]
  [TestCase("0.9", 9)]
  [TestCase("1.0", 10)]
  [TestCase("1.1", 11)]
  [TestCase("1.2", 12)]
  [TestCase("1.3", 13)]
  [TestCase("1.4", 14)]
  public void Create_AllSupportedVersions_RoundTripUnsignedSample(string version, byte versionByte) {
    var descriptor = new AmfFormatDescriptor();
    var options = Options(("Version", version), ("Title", "VersionSweep"));
    var wav = PcmCodec.ToWavBlob(Sample8, 1, 16000, 8);

    var amf = Create(descriptor, [ArchiveInputInfo.InMemory("tone.wav", wav)], options);
    Assert.That(amf[3], Is.EqualTo(versionByte));

    var entries = descriptor.List(new MemoryStream(amf), null);
    var sampleName = entries.Single(e => e.Name.StartsWith("samples/01_", StringComparison.Ordinal)).Name;
    var decoded = Extract(descriptor, amf, sampleName);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(24, 4)), Is.EqualTo(16000u));
      Assert.That(decoded.AsSpan(44).ToArray(), Is.EqualTo(Sample8));
    });
  }

  [Test]
  public void Create_V01_EmptyTracks_OmitUncountedTerminators() {
    var descriptor = new AmfFormatDescriptor();
    var wav = PcmCodec.ToWavBlob(Sample8, 1, 16000, 8);

    var amf = Create(
      descriptor,
      [ArchiveInputInfo.InMemory("tone.wav", wav)],
      Options(("Version", "0.1")));

    // v0.1: 40-byte header, 8 bytes of pattern track refs, 59-byte sample header,
    // 8-byte logical->physical map. Track 1 has two events plus an uncounted terminator;
    // tracks 2-4 are zero-count headers and must not carry stray terminators.
    const int firstTrackOffset = 115;
    Assert.Multiple(() => {
      Assert.That(amf.AsSpan(firstTrackOffset, 3).ToArray(), Is.EqualTo(new byte[] { 2, 0, 0 }));
      Assert.That(amf.AsSpan(firstTrackOffset + 12, 3).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0 }));
      Assert.That(amf.AsSpan(firstTrackOffset + 15, 3).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0 }));
      Assert.That(amf.AsSpan(firstTrackOffset + 18, 3).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0 }));
      Assert.That(amf.AsSpan(firstTrackOffset + 21, Sample8.Length).ToArray(), Is.EqualTo(Sample8));
    });
  }

  [Test]
  public void Create_16BitPcm_RequantizesToUnsigned8() {
    var descriptor = new AmfFormatDescriptor();
    byte[] pcm16 = [0x00, 0x80, 0x00, 0x00, 0xFF, 0x7F];
    var wav = PcmCodec.ToWavBlob(pcm16, 1, 22050, 16);

    var amf = Create(descriptor, [ArchiveInputInfo.InMemory("wide.wav", wav)], Options());
    var sampleName = descriptor.List(new MemoryStream(amf), null)
      .Single(e => e.Name.StartsWith("samples/01_", StringComparison.Ordinal)).Name;
    var decoded = Extract(descriptor, amf, sampleName);

    Assert.That(decoded.AsSpan(44).ToArray(), Is.EqualTo(new byte[] { 0, 128, 255 }));
  }

  [Test]
  public void Create_MultipleSamples_WritesEnoughPatternsAndTracks() {
    var descriptor = new AmfFormatDescriptor();
    var inputs = Enumerable.Range(0, 130)
      .Select(i => ArchiveInputInfo.InMemory($"s{i:D3}.wav", PcmCodec.ToWavBlob([(byte)i], 1, 8000 + i, 8)))
      .ToArray();
    var options = Options(("Version", "1.0"), ("Channels", "1"));

    var amf = Create(descriptor, inputs, options);
    var metadata = Encoding.UTF8.GetString(Extract(descriptor, amf, "metadata.ini"));

    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("num_samples=130"));
      Assert.That(metadata, Does.Contain("num_orders=3"));
      Assert.That(metadata, Does.Contain("num_channels=1"));
      Assert.That(descriptor.List(new MemoryStream(amf), null).Count(e => e.Kind == "Sample"), Is.EqualTo(130));
    });
  }

  [Test]
  public void Create_V14_CustomRowsTempoSpeedAndPanning_AreDemuxed() {
    var descriptor = new AmfFormatDescriptor();
    var options = Options(
      ("Version", "1.4"),
      ("Channels", "2"),
      ("Rows", "17"),
      ("Tempo", "150"),
      ("Speed", "3"),
      ("Panning", "-32,31"));
    var input = ArchiveInputInfo.InMemory("tone.wav", PcmCodec.ToWavBlob(Sample8, 1, 11025, 8));

    var amf = Create(descriptor, [input], options);
    var metadata = Encoding.UTF8.GetString(Extract(descriptor, amf, "metadata.ini"));

    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("version=1.4"));
      Assert.That(metadata, Does.Contain("num_channels=2"));
      Assert.That(metadata, Does.Contain("pattern.001.rows=17"));
      Assert.That(metadata, Does.Contain("tempo=150"));
      Assert.That(metadata, Does.Contain("speed=3"));
      Assert.That(metadata, Does.Contain("panning=-32,31"));
    });
  }

  [Test]
  public void Create_MetadataOverridesSampleNameVolumeAndLoop() {
    var descriptor = new AmfFormatDescriptor();
    var metadata = Encoding.UTF8.GetBytes("sample.001.name=Lead\nsample.001.filename=LEAD.RAW\nsample.001.volume=23\nsample.001.loop_start=1\nsample.001.loop_end=5\n");
    var inputs = new[] {
      ArchiveInputInfo.InMemory("source.wav", PcmCodec.ToWavBlob(Sample8, 1, 12345, 8)),
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
    };

    var amf = Create(descriptor, inputs, Options(("Version", "1.4")));
    var extractedMetadata = Encoding.UTF8.GetString(Extract(descriptor, amf, "metadata.ini"));

    Assert.Multiple(() => {
      Assert.That(extractedMetadata, Does.Contain("sample.001.name=Lead"));
      Assert.That(extractedMetadata, Does.Contain("sample.001.filename=LEAD.RAW"));
      Assert.That(extractedMetadata, Does.Contain("sample.001.volume=23"));
      Assert.That(extractedMetadata, Does.Contain("sample.001.loop_start=1"));
      Assert.That(extractedMetadata, Does.Contain("sample.001.loop_end=5"));
    });
  }

  [Test]
  public void Create_FullAmf_IsByteExactRemux() {
    var descriptor = new AmfFormatDescriptor();
    var original = MakeReferenceV14();

    var remuxed = Create(
      descriptor,
      [ArchiveInputInfo.InMemory("FULL.amf", original)],
      Options(("Version", "0.8"), ("Title", "Ignored")));

    Assert.That(remuxed, Is.EqualTo(original));
  }

  [TestCase("0.8", "2", "AMF 0.8 has exactly four channels")]
  [TestCase("1.1", "17", "supports 1..16 channels")]
  [TestCase("1.2", "33", "supports 1..32 channels")]
  public void Create_InvalidChannelCombinations_AreRejected(string version, string channels, string message) {
    var descriptor = new AmfFormatDescriptor();
    var input = ArchiveInputInfo.InMemory("tone.wav", PcmCodec.ToWavBlob(Sample8, 1, 8000, 8));

    var exception = Assert.Catch<Exception>(() =>
      Create(descriptor, [input], Options(("Version", version), ("Channels", channels))));

    Assert.That(exception!.Message, Does.Contain(message));
  }

  [Test]
  public void Create_Pre14CustomRows_IsRejected() {
    var descriptor = new AmfFormatDescriptor();
    var input = ArchiveInputInfo.InMemory("tone.wav", PcmCodec.ToWavBlob(Sample8, 1, 8000, 8));

    var exception = Assert.Throws<NotSupportedException>(() =>
      Create(descriptor, [input], Options(("Version", "1.3"), ("Rows", "32"))));

    Assert.That(exception!.Message, Does.Contain("fixed 64-row patterns"));
  }

  [Test]
  public void Create_Pre13CustomTempo_IsRejected() {
    var descriptor = new AmfFormatDescriptor();
    var input = ArchiveInputInfo.InMemory("tone.wav", PcmCodec.ToWavBlob(Sample8, 1, 8000, 8));

    var exception = Assert.Throws<NotSupportedException>(() =>
      Create(descriptor, [input], Options(("Version", "1.2"), ("Tempo", "150"))));

    Assert.That(exception!.Message, Does.Contain("does not store initial tempo/speed"));
  }

  [Test]
  public void Create_Pre10SampleOver65535Frames_IsRejected() {
    var descriptor = new AmfFormatDescriptor();
    var wav = PcmCodec.ToWavBlob(new byte[ushort.MaxValue + 1], 1, 8000, 8);

    var exception = Assert.Throws<NotSupportedException>(() =>
      Create(descriptor, [ArchiveInputInfo.InMemory("long.wav", wav)], Options(("Version", "0.9"))));

    Assert.That(exception!.Message, Does.Contain("maximum is 65535"));
  }

  [Test]
  public void Create_StereoSample_IsRejectedInsteadOfDownmixed() {
    var descriptor = new AmfFormatDescriptor();
    var wav = PcmCodec.ToWavBlob([0, 255, 64, 192], 2, 8000, 8);

    var exception = Assert.Throws<NotSupportedException>(() =>
      Create(descriptor, [ArchiveInputInfo.InMemory("stereo.wav", wav)], Options()));

    Assert.That(exception!.Message, Does.Contain("DSMI AMF samples are mono"));
  }

  [Test]
  public void CanAccept_AdvertisesOnlyRemuxMetadataAndWavInputs() {
    var descriptor = new AmfFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("FULL.amf", [1]), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("metadata.ini", [1]), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("samples/a.wav", [1]), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("track.bin", [1]), out _), Is.False);
    });
  }

  [Test]
  public void GracefulFallback_GarbageYieldsFullAndMetadataOnly() {
    var entries = new AmfFormatDescriptor().List(new MemoryStream("XYZ junk"u8.ToArray()), null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name.StartsWith("samples/", StringComparison.Ordinal)), Is.False);
      Assert.That(entries[0].Name, Is.EqualTo("FULL.amf"));
      Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    });
  }

  private static FormatCreateOptions Options(params (string Key, string Value)[] values) {
    var options = new FormatCreateOptions();
    foreach (var (key, value) in values)
      options.FormatSpecific[key] = value;
    return options;
  }

  private static byte[] Create(
    AmfFormatDescriptor descriptor,
    IReadOnlyList<ArchiveInputInfo> inputs,
    FormatCreateOptions options) {
    using var output = new MemoryStream();
    descriptor.Create(output, inputs, options);
    return output.ToArray();
  }

  private static byte[] Extract(AmfFormatDescriptor descriptor, byte[] amf, string name) {
    using var output = new MemoryStream();
    descriptor.ExtractEntry(new MemoryStream(amf), name, output, null);
    return output.ToArray();
  }

  // Independent, specification-shaped v1.4 fixture: 32-byte pan table, one 64-row
  // order, one sample header, four logical->physical tracks, packed track events,
  // then sample PCM. This deliberately does not use the production writer.
  private static byte[] MakeReferenceV14() {
    using var output = new MemoryStream();
    output.Write("AMF"u8);
    output.WriteByte(14);
    WriteAscii(output, "Reference", 32);
    output.WriteByte(1); // instruments/samples
    output.WriteByte(1); // orders
    WriteU16(output, 4); // logical tracks
    output.WriteByte(4); // channels
    output.Write(new byte[32]); // panning
    output.WriteByte(125);
    output.WriteByte(6);
    WriteU16(output, 64); // pattern rows
    for (ushort track = 1; track <= 4; ++track) WriteU16(output, track);

    output.WriteByte(0); // no loop
    WriteAscii(output, "UnsignedSmp", 32);
    WriteAscii(output, "SAMPLE.RAW", 13);
    WriteU32(output, 1);
    WriteU32(output, (uint)Sample8.Length);
    WriteU16(output, 16000);
    output.WriteByte(64);
    WriteU32(output, 0);
    WriteU32(output, 0);

    for (ushort track = 1; track <= 4; ++track) WriteU16(output, track);

    // Physical track 1: instrument select + note + end marker.
    output.Write([3, 0, 0]);
    output.Write([0, 0x80, 0]);
    output.Write([0, 48, 0xFF]);
    output.Write([0xFF, 0xFF, 0xFF]);
    // Tracks 2-4 are empty, represented by the end marker only.
    for (var track = 0; track < 3; ++track) {
      output.Write([1, 0, 0]);
      output.Write([0xFF, 0xFF, 0xFF]);
    }

    output.Write(Sample8);
    return output.ToArray();
  }

  private static void WriteAscii(Stream output, string text, int length) {
    var field = new byte[length];
    Encoding.ASCII.GetBytes(text.AsSpan(), field);
    output.Write(field);
  }

  private static void WriteU16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteU32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }
}
