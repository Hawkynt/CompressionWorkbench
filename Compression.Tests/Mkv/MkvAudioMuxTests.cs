using System;
#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Matroska;

namespace Compression.Tests.Mkv;

[TestFixture]
public sealed class MkvAudioMuxTests {

  /// <summary>The EBML identifier of a Cluster element.</summary>
  private static ReadOnlySpan<byte> _ClusterId => [0x1F, 0x43, 0xB6, 0x75];


  [Test, Category("HappyPath")]
  public void DescriptorAdvertisesPacketMuxAndCreation() {
    var descriptor = new MkvFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IAudioDemuxSource>());
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
      Assert.That(descriptor.SupportedMuxCodecs, Does.Contain("aac"));
      Assert.That(descriptor.SupportedMuxCodecs, Does.Contain("mp3"));
      Assert.That(descriptor.SupportedMuxCodecs, Does.Contain("opus"));
    });
  }

  [Test, Category("HappyPath")]
  public void AacPacketsAndCodecPrivateRoundTripWithoutReencoding() {
    byte[][] payloads = [
      [0x21, 0x10, 0x56, 0xE5],
      [0x21, 0x11, 0x22],
      [0x21, 0x12, 0x33, 0x44, 0x55],
    ];
    var asc = new byte[] { 0x11, 0x90 }; // AAC-LC, 48 kHz, stereo
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("aac", 48_000, 2),
      payloads.Select(static payload => new AudioPacket(payload, DurationSamples: 1024)).ToArray(),
      asc);

    var descriptor = new MkvFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Mux(output, encoded, new FormatCreateOptions());

    var demuxed = new MkvDemuxer().Demux(output.ToArray());
    Assert.That(demuxed.Tracks, Has.Count.EqualTo(1));
    var track = demuxed.Tracks[0];
    Assert.Multiple(() => {
      Assert.That(track.TrackType, Is.EqualTo("audio"));
      Assert.That(track.CodecId, Is.EqualTo("A_AAC"));
      Assert.That(track.AudioSampleRate, Is.EqualTo(48_000));
      Assert.That(track.AudioChannels, Is.EqualTo(2));
      Assert.That(track.CodecPrivate, Is.EqualTo(asc));
      Assert.That(track.Frames.Select(static frame => frame.Data).ToArray(), Is.EqualTo(payloads));
    });

    output.Position = 0;
    Assert.That(descriptor.TryDemux(output, out var remux), Is.True);
    Assert.That(remux, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(remux!.Format.CodecId, Is.EqualTo("aac"));
      Assert.That(remux.Format.SampleRate, Is.EqualTo(48_000));
      Assert.That(remux.Format.Channels, Is.EqualTo(2));
      Assert.That(remux.CodecPrivateData, Is.EqualTo(asc));
      Assert.That(remux.Packets.Select(static packet => packet.Data).ToArray(), Is.EqualTo(payloads));
      Assert.That(remux.Packets.All(static packet => packet.DurationSamples == 1024), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void WriterEmitsCuesWhoseClusterPositionsPointAtClusterIds() {
    var packets = Enumerable.Range(0, 260)
      .Select(i => new AudioPacket([(byte)i, 0xA5], DurationSamples: 1024))
      .ToArray();
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("aac", 48_000, 2),
      packets,
      [0x11, 0x90]);

    using var output = new MemoryStream();
    new MkvFormatDescriptor().Mux(output, encoded, new FormatCreateOptions());
    var file = output.ToArray();
    var reader = new EbmlReader(file);
    var position = 0L;
    _ = reader.Read(ref position); // EBML header
    var segment = reader.Read(ref position);
    Assert.That(segment, Is.Not.Null);

    var children = reader.Children(segment!.Value).ToArray();
    var clusters = children.Where(static element => element.Id == 0x1F43B675).ToArray();
    var cues = children.Single(static element => element.Id == 0x1C53BB6B);
    Assert.That(clusters.Length, Is.GreaterThanOrEqualTo(2), "5-second cluster limit should split this stream");

    var cuePoints = reader.Children(cues).Where(static element => element.Id == 0xBB).ToArray();
    Assert.That(cuePoints, Has.Length.EqualTo(clusters.Length));
    foreach (var cuePoint in cuePoints) {
      var cueTrackPositions = reader.Children(cuePoint).Single(static element => element.Id == 0xB7);
      var clusterPosition = reader.Children(cueTrackPositions)
        .Single(static element => element.Id == 0xF1);
      var relative = reader.ReadUnsigned(clusterPosition);
      var absolute = checked(segment.Value.BodyOffset + (long)relative);
      // A collection expression has no type of its own for SequenceEqual to infer from, and this sits
      // inside a loop, so the comparand is a shared constant rather than a fresh stack allocation.
      Assert.That(file.AsSpan((int)absolute, 4).SequenceEqual(_ClusterId), Is.True,
        $"CueClusterPosition {relative} does not point to a Cluster element");
    }
  }

  [Test, Category("HappyPath")]
  public void OpusTrackWritesRequiredCodecDelayAndSeekPreRoll() {
    var head = BuildOpusHead(channels: 2, preSkip: 312, inputRate: 48_000);
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("opus", 48_000, 2),
      [new AudioPacket([0xF8, 0xFF], DurationSamples: 960)],
      head);

    using var output = new MemoryStream();
    new MkvFormatDescriptor().Mux(output, encoded, new FormatCreateOptions());
    var file = output.ToArray();
    var reader = new EbmlReader(file);
    var segment = Segment(reader);
    var tracks = reader.Children(segment).Single(static element => element.Id == 0x1654AE6B);
    var track = reader.Children(tracks).Single(static element => element.Id == 0xAE);
    var fields = reader.Children(track).ToArray();

    Assert.Multiple(() => {
      Assert.That(reader.ReadString(fields.Single(static element => element.Id == 0x86)), Is.EqualTo("A_OPUS"));
      Assert.That(reader.ReadBinary(fields.Single(static element => element.Id == 0x63A2)), Is.EqualTo(head));
      Assert.That(reader.ReadUnsigned(fields.Single(static element => element.Id == 0x56AA)), Is.EqualTo(6_500_000));
      Assert.That(reader.ReadUnsigned(fields.Single(static element => element.Id == 0x56BB)), Is.EqualTo(80_000_000));
    });
  }

  [Test, Category("Regression")]
  public void OpusTimingUses48KhzPacketClockIndependentlyOfInputSampleRate() {
    const uint originalInputRate = 44_100;
    var head = BuildOpusHead(channels: 2, preSkip: 312, inputRate: originalInputRate);
    var packets = Enumerable.Range(0, 251)
      .Select(static _ => new AudioPacket([0xF8, 0xFF], DurationSamples: 960))
      .ToArray();
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("opus", (int)originalInputRate, 2),
      packets,
      head);

    using var output = new MemoryStream();
    new MkvFormatDescriptor().Mux(output, encoded, new FormatCreateOptions());

    var reader = new EbmlReader(output.ToArray());
    var segment = Segment(reader);
    var children = reader.Children(segment).ToArray();

    var info = children.Single(static element => element.Id == 0x1549A966);
    var durationElement = reader.Children(info).Single(static element => element.Id == 0x4489);
    var duration = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(reader.Body(durationElement)));

    var tracks = children.Single(static element => element.Id == 0x1654AE6B);
    var track = reader.Children(tracks).Single(static element => element.Id == 0xAE);
    var audio = reader.Children(track).Single(static element => element.Id == 0xE1);
    var samplingFrequencyElement = reader.Children(audio).Single(static element => element.Id == 0xB5);
    var samplingFrequency = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(reader.Body(samplingFrequencyElement)));

    var clusterTimestamps = children
      .Where(static element => element.Id == 0x1F43B675)
      .Select(cluster => reader.ReadUnsigned(reader.Children(cluster).Single(static element => element.Id == 0xE7)))
      .ToArray();

    Assert.Multiple(() => {
      Assert.That(samplingFrequency, Is.EqualTo(originalInputRate),
        "Matroska A_OPUS SamplingFrequency must retain the OpusHead input-rate metadata");
      Assert.That(duration, Is.EqualTo(5_020d),
        "251 20-ms Opus packets are 5.02 seconds on the mandatory 48 kHz packet clock");
      Assert.That(clusterTimestamps, Is.EqualTo(new ulong[] { 0, 5_000 }),
        "cluster timestamps must use the 48 kHz Opus packet clock, not the original input sample rate");
    });
  }

  [Test, Category("Validation")]
  public void AacWithoutAudioSpecificConfigIsRejected() {
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("aac", 48_000, 2),
      [new AudioPacket([0x01, 0x02], DurationSamples: 1024)]);

    using var output = new MemoryStream();
    Assert.That(
      () => new MkvFormatDescriptor().Mux(output, encoded, new FormatCreateOptions()),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("AudioSpecificConfig"));
  }

  [Test, Category("HappyPath")]
  public void Mp3FrameDurationIsRecoveredForRemux() {
    var frame = new byte[417];
    frame[0] = 0xFF;
    frame[1] = 0xFB; // MPEG-1 Layer III
    frame[2] = 0x90;
    frame[3] = 0x64;
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("mp3", 44_100, 2),
      [new AudioPacket(frame, DurationSamples: 1152)]);

    var descriptor = new MkvFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Mux(output, encoded, new FormatCreateOptions());
    output.Position = 0;

    Assert.That(descriptor.TryDemux(output, out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);
    Assert.That(demuxed!.Packets.Single().DurationSamples, Is.EqualTo(1152));
    Assert.That(demuxed.Packets.Single().Data, Is.EqualTo(frame));
  }

  private static EbmlReader.Element Segment(EbmlReader reader) {
    var position = 0L;
    _ = reader.Read(ref position);
    return reader.Read(ref position) ?? throw new AssertionException("missing Segment element");
  }

  private static byte[] BuildOpusHead(byte channels, ushort preSkip, uint inputRate) {
    var result = new byte[19];
    "OpusHead"u8.CopyTo(result);
    result[8] = 1;
    result[9] = channels;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), preSkip);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), inputRate);
    return result;
  }
}
