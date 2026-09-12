#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Ahx;

namespace Compression.Tests.Ahx;

[TestFixture]
public class AhxTests {

  private static byte[] MakeAhx(
    int version = 1,
    int speedMultiplier = 1,
    bool trackZeroStored = false,
    int trackLength = 1,
    int maxTrack = 0,
    byte[]? logicalTracks = null,
    byte[]? instruments = null,
    int instrumentCount = 0,
    string title = "AhxSong"
  ) {
    var positions = new byte[8];
    var trackBytesPerTrack = trackLength * 3;
    logicalTracks ??= new byte[(maxTrack + 1) * trackBytesPerTrack];
    Assert.That(logicalTracks.Length, Is.EqualTo((maxTrack + 1) * trackBytesPerTrack));

    instruments ??= instrumentCount == 0 ? [] : MakeInstruments(instrumentCount);
    var names = new List<byte>(Encoding.Latin1.GetBytes(title)) { 0 };
    for (var i = 1; i <= instrumentCount; ++i) {
      names.AddRange(Encoding.Latin1.GetBytes($"Instrument {i}"));
      names.Add(0);
    }

    var storedTrackLength = (maxTrack + (trackZeroStored ? 1 : 0)) * trackBytesPerTrack;
    var namesOffset = 14 + positions.Length + storedTrackLength + instruments.Length;
    var result = new byte[namesOffset + names.Count];
    "THX"u8.CopyTo(result);
    result[3] = checked((byte)version);
    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), unchecked((ushort)namesOffset));
    result[6] = (byte)((trackZeroStored ? 0x80 : 0) | ((speedMultiplier - 1) << 5));
    result[7] = 1;
    result[8] = 0;
    result[9] = 0;
    result[10] = checked((byte)trackLength);
    result[11] = checked((byte)maxTrack);
    result[12] = checked((byte)instrumentCount);
    result[13] = 0;

    positions.CopyTo(result, 14);
    var cursor = 14 + positions.Length;
    if (trackZeroStored) {
      logicalTracks.CopyTo(result, cursor);
      cursor += logicalTracks.Length;
    } else {
      logicalTracks.AsSpan(trackBytesPerTrack).CopyTo(result.AsSpan(cursor));
      cursor += logicalTracks.Length - trackBytesPerTrack;
    }
    instruments.CopyTo(result, cursor);
    cursor += instruments.Length;
    names.ToArray().CopyTo(result, cursor);
    return result;
  }

  private static byte[] MakeInstruments(int count) {
    var result = new byte[count * 22];
    for (var i = 0; i < count; ++i) {
      var offset = i * 22;
      result[offset] = 64;
      result[offset + 2] = 1;
      result[offset + 3] = 64;
      result[offset + 4] = 1;
      result[offset + 5] = 64;
      result[offset + 6] = 1;
      result[offset + 7] = 1;
      result[offset + 8] = 64;
      result[offset + 20] = 1;
      result[offset + 21] = 0;
    }
    return result;
  }

  private static FormatCreateOptions Options(string version = "Auto", string speed = "Auto", string trackZero = "Auto")
    => new() {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["Version"] = version,
        ["SpeedMultiplier"] = speed,
        ["TrackZeroStorage"] = trackZero,
      },
    };

  private static byte[] CreateFromFull(byte[] source, FormatCreateOptions? options = null) {
    using var output = new MemoryStream();
    new AhxFormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("FULL.ahx", source)], options ?? new());
    return output.ToArray();
  }

  private static byte[] ExtractEntry(byte[] source, string name) {
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    new AhxFormatDescriptor().ExtractEntry(input, name, output, null);
    return output.ToArray();
  }

  [Test]
  public void List_ExposesNormalizedStructureAndCorrectMetadata() {
    var blob = MakeAhx(version: 1, speedMultiplier: 4, trackZeroStored: false);
    using var input = new MemoryStream(blob);
    var entries = new AhxFormatDescriptor().List(input, null);

    Assert.Multiple(() => {
      Assert.That(entries.Select(static entry => entry.Name), Is.EquivalentTo(new[] {
        "FULL.ahx", "metadata.ini", "subsongs.bin", "positions.bin", "tracks.bin", "instruments.bin", "names.bin",
      }));
      Assert.That(entries.Single(static entry => entry.Name == "tracks.bin").OriginalSize, Is.EqualTo(3),
        "logical tracks always include an explicit blank track 0 even when storage omits it");
    });

    var metadata = Encoding.UTF8.GetString(ExtractEntry(blob, "metadata.ini"));
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("parse_status = ok"));
      Assert.That(metadata, Does.Contain("version = 1"));
      Assert.That(metadata, Does.Contain("positions = 1"));
      Assert.That(metadata, Does.Contain("track_length = 1"));
      Assert.That(metadata, Does.Contain("max_track = 0"));
      Assert.That(metadata, Does.Contain("stored_tracks = 0"));
      Assert.That(metadata, Does.Contain("speed_multiplier = 4"));
      Assert.That(metadata, Does.Contain("tick_rate_hz = 200"));
      Assert.That(metadata, Does.Contain("track_zero_stored = false"));
      Assert.That(metadata, Does.Contain("title = AhxSong"));
    });
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeAhx();
    var tmp = Path.Combine(Path.GetTempPath(), "ahx_" + Guid.NewGuid().ToString("N"));
    try {
      using var input = new MemoryStream(blob);
      new AhxFormatDescriptor().Extract(input, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.ahx")), Is.EqualTo(blob));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "tracks.bin")), Is.EqualTo(new byte[3]));
    } finally {
      if (Directory.Exists(tmp))
        Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrowAndReportsPartial() {
    using var input = new MemoryStream([(byte)'T', (byte)'H', (byte)'X', 0, 1]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new AhxFormatDescriptor().List(input, null));
    Assert.That(entries.Select(static entry => entry.Name), Is.EquivalentTo(new[] { "FULL.ahx", "metadata.ini" }));

    using var malformed = new MemoryStream([(byte)'T', (byte)'H', (byte)'X', 0, 1]);
    using var metadata = new MemoryStream();
    new AhxFormatDescriptor().ExtractEntry(malformed, "metadata.ini", metadata, null);
    Assert.That(Encoding.UTF8.GetString(metadata.ToArray()), Does.Contain("parse_status = partial"));
  }

  [Test]
  public void Reader_IgnoresStoredNamesOffsetAsRequiredByFormat() {
    var blob = MakeAhx();
    blob[4] = 0;
    blob[5] = 1;
    var metadata = Encoding.UTF8.GetString(ExtractEntry(blob, "metadata.ini"));
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("names_offset_stored = 1"));
      Assert.That(metadata, Does.Contain("title = AhxSong"));
      Assert.That(metadata, Does.Contain("parse_status = ok"));
    });
  }

  [Test]
  public void Reader_UsesTrackZeroFlagWithPublishedSemantics() {
    var logicalTracks = new byte[6];
    logicalTracks[3] = 4;
    var omitted = MakeAhx(trackZeroStored: false, maxTrack: 1, logicalTracks: logicalTracks);
    var tracks = ExtractEntry(omitted, "tracks.bin");
    Assert.That(tracks, Is.EqualTo(logicalTracks));

    var stored = MakeAhx(trackZeroStored: true, maxTrack: 1, logicalTracks: logicalTracks);
    Assert.That(ExtractEntry(stored, "tracks.bin"), Is.EqualTo(logicalTracks));
  }

  [Test]
  public void Create_FullWithAutoOptionsIsByteExactEvenForNonCanonicalNamesPointer() {
    var blob = MakeAhx();
    blob[4] = 0x12;
    blob[5] = 0x34;
    Assert.That(CreateFromFull(blob, Options()), Is.EqualTo(blob));
  }

  [Test]
  public void Create_StructuralPseudoArchiveRoundTrips() {
    var blob = MakeAhx(version: 1, speedMultiplier: 3, trackZeroStored: false, instrumentCount: 1);
    var descriptor = new AhxFormatDescriptor();
    var names = new[] { "metadata.ini", "subsongs.bin", "positions.bin", "tracks.bin", "instruments.bin", "names.bin" };
    var inputs = names.Select(name => ArchiveInputInfo.InMemory(name, ExtractEntry(blob, name))).ToArray();

    using var output = new MemoryStream();
    descriptor.Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Create_CoversEveryRepresentableVersionSpeedAndTrackZeroCombination() {
    var source = MakeAhx(version: 1, speedMultiplier: 1, trackZeroStored: false);
    var combinations = new List<(string Version, int Speed, string TrackZero)>();
    foreach (var version in new[] { "AHX0", "AHX1" })
      foreach (var speed in Enumerable.Range(1, 4))
        foreach (var trackZero in new[] { "Stored", "Omitted" })
          if (version == "AHX1" || speed == 1)
            combinations.Add((version, speed, trackZero));

    Assert.That(combinations, Has.Count.EqualTo(10));
    foreach (var combination in combinations) {
      var encoded = CreateFromFull(source, Options(combination.Version, combination.Speed.ToString(), combination.TrackZero));
      Assert.Multiple(() => {
        Assert.That(encoded[3], Is.EqualTo(combination.Version == "AHX0" ? 0 : 1), combination.ToString());
        Assert.That(((encoded[6] >> 5) & 0x03) + 1, Is.EqualTo(combination.Speed), combination.ToString());
        Assert.That((encoded[6] & 0x80) != 0, Is.EqualTo(combination.TrackZero == "Stored"), combination.ToString());
      });

      using var stream = new MemoryStream(encoded);
      Assert.That(new AhxFormatDescriptor().TryDemux(stream, out _), Is.True, combination.ToString());
    }
  }

  [TestCase("2")]
  [TestCase("3")]
  [TestCase("4")]
  public void Create_RejectsAhx0TimingModesThatAhx0CannotRepresent(string speed) {
    var source = MakeAhx(version: 1);
    Assert.That(
      () => CreateFromFull(source, Options("AHX0", speed, "Auto")),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("AHX0"));
  }

  [Test]
  public void Create_RejectsTrackZeroOmissionWhenTrackZeroHasData() {
    var track = new byte[] { 4, 0, 0 };
    var source = MakeAhx(trackZeroStored: true, logicalTracks: track);
    Assert.That(
      () => CreateFromFull(source, Options("Auto", "Auto", "Omitted")),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("Track 0"));
  }

  [Test]
  public void Create_RejectsAhx1TrackCommandWhenDowngradingToAhx0() {
    var track = new byte[] { 0, 4, 1 };
    var source = MakeAhx(version: 1, trackZeroStored: true, logicalTracks: track);
    Assert.That(
      () => CreateFromFull(source, Options("AHX0", "1", "Stored")),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("command 4"));
  }

  [Test]
  public void Create_RejectsAhx1InstrumentFilterWhenDowngradingToAhx0() {
    var instrument = MakeInstruments(1);
    instrument[12] = 1;
    var source = MakeAhx(version: 1, instruments: instrument, instrumentCount: 1);
    Assert.That(
      () => CreateFromFull(source, Options("AHX0", "1", "Auto")),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("AHX1-only"));
  }

  [Test]
  public void DemuxMux_AutoRoundTripsByteExact() {
    var source = MakeAhx(version: 1, speedMultiplier: 4, trackZeroStored: true, instrumentCount: 1);
    var descriptor = new AhxFormatDescriptor();
    using var input = new MemoryStream(source);
    Assert.That(descriptor.TryDemux(input, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(encoded!.Format.CodecId, Is.EqualTo("ahx"));
      Assert.That(encoded.Format.Channels, Is.EqualTo(4));
      Assert.That(encoded.Format.SampleRate, Is.Zero);
      Assert.That(encoded.Format.Properties!["SpeedMultiplier"], Is.EqualTo("4"));
      Assert.That(encoded.Packets, Has.Count.EqualTo(1));
    });

    using var output = new MemoryStream();
    descriptor.Mux(output, encoded!, Options());
    Assert.That(output.ToArray(), Is.EqualTo(source));
  }

  [Test]
  public void Mux_CanRewriteRepresentableContainerParametersWithoutReencodingScore() {
    var source = MakeAhx(version: 1, speedMultiplier: 1, trackZeroStored: false);
    var descriptor = new AhxFormatDescriptor();
    using var input = new MemoryStream(source);
    Assert.That(descriptor.TryDemux(input, out var encoded), Is.True);

    using var output = new MemoryStream();
    descriptor.Mux(output, encoded!, Options("AHX1", "4", "Stored"));
    var result = output.ToArray();
    Assert.Multiple(() => {
      Assert.That(result[3], Is.EqualTo(1));
      Assert.That(((result[6] >> 5) & 3) + 1, Is.EqualTo(4));
      Assert.That(result[6] & 0x80, Is.EqualTo(0x80));
      Assert.That(result.Length, Is.EqualTo(source.Length + 3));
    });
  }

  [Test]
  public void Demux_MalformedReturnsFalse() {
    using var input = new MemoryStream("THX\x01"u8.ToArray());
    Assert.That(new AhxFormatDescriptor().TryDemux(input, out var encoded), Is.False);
    Assert.That(encoded, Is.Null);
  }

  [Test]
  public void Mux_RejectsWrongCodecOrChannelCount() {
    var descriptor = new AhxFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.CanMux(new AudioStreamFormat("aac", 0, 4), new(), out _), Is.False);
      Assert.That(descriptor.CanMux(new AudioStreamFormat("ahx", 0, 2), new(), out _), Is.False);
      Assert.That(descriptor.CanMux(new AudioStreamFormat("ahx", 0, 4), Options("AHX0", "2"), out _), Is.False);
    });
  }

  [Test]
  public void AudioInventory_AdvertisesStructuralMuxDemuxButNotPcmTranscription() {
    var capability = AudioConversionInventory.Describe(new AhxFormatDescriptor());
    Assert.Multiple(() => {
      Assert.That(capability.CanDecodePcm, Is.False);
      Assert.That(capability.CanEncodePcm, Is.False);
      Assert.That(capability.CanDemuxEncoded, Is.True);
      Assert.That(capability.CanMuxEncoded, Is.True);
      Assert.That(capability.CanReadPseudoArchive, Is.True);
      Assert.That(capability.CanCreatePseudoArchive, Is.True);
      Assert.That(capability.MuxCodecs, Is.EqualTo(new[] { "ahx" }));
    });
  }

  [Test]
  public void WriteConstraints_AcceptOnlyStructuralAhxInputs() {
    var descriptor = new AhxFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("FULL.ahx", []), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("metadata.ini", []), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("tracks.bin", []), out _), Is.True);
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("sample.wav", []), out var reason), Is.False);
      Assert.That(reason, Does.Contain("not an AHX structural input"));
    });
  }

  [Test]
  public void DetectionAndCapabilities_AreWired() {
    var descriptor = new AhxFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo("THX"u8.ToArray()));
      Assert.That(descriptor.Extensions, Does.Contain(".ahx"));
      Assert.That(descriptor.Extensions, Does.Contain(".thx"));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IAudioDemuxSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveInMemoryExtract>());
      Assert.That(descriptor, Is.InstanceOf<IFormatOptionsSchema>());
    });
  }
}
