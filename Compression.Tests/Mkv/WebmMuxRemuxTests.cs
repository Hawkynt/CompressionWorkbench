#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Matroska;

namespace Compression.Tests.Mkv;

[TestFixture]
public sealed class WebmMuxRemuxTests {
  private const ulong IdSegment = 0x18538067;
  private const ulong IdTracks = 0x1654AE6B;
  private const ulong IdTrackEntry = 0xAE;
  private const ulong IdCodecDelay = 0x56AA;
  private const ulong IdSeekPreRoll = 0x56BB;
  private const ulong IdCluster = 0x1F43B675;

  [Test]
  public void Descriptor_ExposesPacketMuxAndDemux() {
    var descriptor = new MkvFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IAudioContainerFormat>());
      Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
      Assert.That(descriptor, Is.InstanceOf<IAudioDemuxSource>());

      // The descriptor's own set is Matroska's, because that is what the container holds. WebM is
      // the strict subset that permits Vorbis and Opus alone, and a caller asks for it by name.
      Assert.That(
        descriptor.SupportedMuxCodecs,
        Is.SupersetOf(new[] { "opus", "vorbis" }),
        "Matroska carries at least what WebM does");
      Assert.That(
        descriptor.SupportedMuxCodecsFor(_WebmOptions()),
        Is.EquivalentTo(new[] { "opus", "vorbis" }),
        "the WebM profile is exactly Vorbis and Opus");
    });
  }

  /// <summary>
  /// Options asking for a file that satisfies WebM rather than Matroska at large.
  /// </summary>
  /// <remarks>
  /// Every mux in this fixture is named for WebM's rules, so each states the profile it means rather
  /// than relying on the descriptor's default -- which is Matroska, and deliberately wider.
  /// </remarks>
  private static FormatCreateOptions _WebmOptions() => new() {
    FormatSpecific = FormatCreateOptions.FormatSpecificFrom([
      new KeyValuePair<string, string>("Profile", "WebM"),
    ]),
  };

  [Test]
  public void OpusMux_WritesWebmHeaderTrackAndRequiredTimingMetadata() {
    var descriptor = new MkvFormatDescriptor();
    var packets = new[] {
      new AudioPacket([0xF8, 0x11], 960),
      new AudioPacket([0xF8, 0x22], 960),
    };
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("opus", 48_000, 2),
      packets,
      MakeOpusHead(channels: 2, sampleRate: 48_000, preSkip: 312));

    using var output = new MemoryStream();
    descriptor.Mux(output, encoded, _WebmOptions());
    var webm = output.ToArray();

    Assert.That(IndexOf(webm, "webm"u8), Is.GreaterThanOrEqualTo(0));

    var demuxed = new MkvDemuxer().Demux(webm);
    Assert.That(demuxed.Tracks, Has.Count.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(demuxed.Tracks[0].CodecId, Is.EqualTo("A_OPUS"));
      Assert.That(demuxed.Tracks[0].AudioChannels, Is.EqualTo(2));
      Assert.That(demuxed.Tracks[0].AudioSampleRate, Is.EqualTo(48_000));
      Assert.That(demuxed.Tracks[0].Frames.Select(static frame => frame.Data),
        Is.EqualTo(packets.Select(static packet => packet.Data)));
      Assert.That(ReadTrackUnsigned(webm, IdCodecDelay), Is.EqualTo(6_500_000UL));
      Assert.That(ReadTrackUnsigned(webm, IdSeekPreRoll), Is.EqualTo(80_000_000UL));
    });
  }

  [Test]
  public void OpusRemux_PreservesPacketsAndDurations() {
    var descriptor = new MkvFormatDescriptor();
    var originalPackets = new[] {
      new AudioPacket([0xF8, 0x10], 960),
      new AudioPacket([0xF8, 0x20], 960),
      new AudioPacket([0xF8, 0x30], 960),
    };
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("opus", 48_000, 2),
      originalPackets,
      MakeOpusHead(2, 48_000, 312));

    using var first = new MemoryStream();
    descriptor.Mux(first, encoded, _WebmOptions());
    first.Position = 0;

    Assert.That(descriptor.TryDemux(first, out var remuxInput), Is.True);
    Assert.That(remuxInput, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(remuxInput!.Format.CodecId, Is.EqualTo("opus"));
      Assert.That(remuxInput.Packets.Select(static packet => packet.Data),
        Is.EqualTo(originalPackets.Select(static packet => packet.Data)));
      Assert.That(remuxInput.Packets.Select(static packet => packet.DurationSamples),
        Is.EqualTo(new long[] { 960, 960, 960 }));
      Assert.That(remuxInput.CodecPrivateData, Is.EqualTo(encoded.CodecPrivateData));
    });

    using var second = new MemoryStream();
    descriptor.Mux(second, remuxInput!, _WebmOptions());
    var secondDemux = new MkvDemuxer().Demux(second.ToArray());
    Assert.That(secondDemux.Tracks[0].Frames.Select(static frame => frame.Data),
      Is.EqualTo(originalPackets.Select(static packet => packet.Data)));
  }

  [Test]
  public void VorbisRemux_PreservesPacketsAndUsesBlockTimestampsForDuration() {
    var descriptor = new MkvFormatDescriptor();
    var privateData = MakeVorbisPrivate(channels: 2, sampleRate: 48_000);
    var originalPackets = new[] {
      new AudioPacket([0x00, 0x11, 0x22], 480),
      new AudioPacket([0x00, 0x33, 0x44], 480),
      new AudioPacket([0x00, 0x55, 0x66], 480),
    };
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("vorbis", 48_000, 2),
      originalPackets,
      privateData);

    using var first = new MemoryStream();
    descriptor.Mux(first, encoded, _WebmOptions());
    first.Position = 0;

    Assert.That(descriptor.TryDemux(first, out var remuxInput), Is.True);
    Assert.That(remuxInput, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(remuxInput!.Format.CodecId, Is.EqualTo("vorbis"));
      Assert.That(remuxInput.Format.SampleRate, Is.EqualTo(48_000));
      Assert.That(remuxInput.Format.Channels, Is.EqualTo(2));
      Assert.That(remuxInput.Packets.Select(static packet => packet.Data),
        Is.EqualTo(originalPackets.Select(static packet => packet.Data)));
      Assert.That(remuxInput.Packets.Select(static packet => packet.DurationSamples),
        Is.EqualTo(new long[] { 480, 480, 480 }));
      Assert.That(remuxInput.CodecPrivateData, Is.EqualTo(privateData));
    });
  }

  [Test]
  public void Mux_RollsClusterBeforeWebmFiveSecondGuideline() {
    var descriptor = new MkvFormatDescriptor();
    var packets = Enumerable.Range(0, 301)
      .Select(static i => new AudioPacket([0xF8, (byte)i], 960))
      .ToArray();
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("opus", 48_000, 1),
      packets,
      MakeOpusHead(1, 48_000, 0));

    using var output = new MemoryStream();
    descriptor.Mux(output, encoded, _WebmOptions());
    var webm = output.ToArray();

    Assert.That(CountSegmentChildren(webm, IdCluster), Is.GreaterThanOrEqualTo(2));
    output.Position = 0;
    Assert.That(descriptor.TryDemux(output, out var demuxed), Is.True);
    Assert.That(demuxed!.Packets, Has.Count.EqualTo(packets.Length));
  }

  [Test]
  public void CanMux_RejectsCodecOutsideWebmAudioProfile() {
    var descriptor = new MkvFormatDescriptor();

    Assert.That(descriptor.CanMux(
      new AudioStreamFormat("aac", 48_000, 2),
      _WebmOptions(),
      out var reason), Is.False);
    Assert.That(reason, Does.Contain("Opus").And.Contain("Vorbis"));
  }

  private static byte[] MakeOpusHead(int channels, int sampleRate, ushort preSkip) {
    var result = new byte[19];
    "OpusHead"u8.CopyTo(result);
    result[8] = 1;
    result[9] = (byte)channels;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10, 2), preSkip);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)sampleRate);
    result[18] = 0;
    return result;
  }

  private static byte[] MakeVorbisPrivate(int channels, int sampleRate) {
    var identification = new byte[30];
    identification[0] = 0x01;
    "vorbis"u8.CopyTo(identification.AsSpan(1));
    identification[11] = (byte)channels;
    BinaryPrimitives.WriteInt32LittleEndian(identification.AsSpan(12, 4), sampleRate);
    identification[28] = 0xB8;
    identification[29] = 1;

    var comment = new byte[] { 0x03, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', 0 };
    var setup = new byte[] { 0x05, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', 0 };

    using var result = new MemoryStream();
    result.WriteByte(2);
    WriteXiphLength(result, identification.Length);
    WriteXiphLength(result, comment.Length);
    result.Write(identification);
    result.Write(comment);
    result.Write(setup);
    return result.ToArray();
  }

  private static void WriteXiphLength(Stream output, int length) {
    while (length >= 255) {
      output.WriteByte(255);
      length -= 255;
    }
    output.WriteByte((byte)length);
  }

  private static ulong ReadTrackUnsigned(byte[] file, ulong wantedId) {
    var ebml = new EbmlReader(file);
    var pos = 0L;
    while (pos < file.Length) {
      var top = ebml.Read(ref pos);
      if (top is null) break;
      if (top.Value.Id != IdSegment) continue;
      foreach (var child in ebml.Children(top.Value)) {
        if (child.Id != IdTracks) continue;
        foreach (var entry in ebml.Children(child)) {
          if (entry.Id != IdTrackEntry) continue;
          foreach (var field in ebml.Children(entry))
            if (field.Id == wantedId)
              return ebml.ReadUnsigned(field);
        }
      }
    }
    throw new AssertionException($"Track element 0x{wantedId:X} not found.");
  }

  private static int CountSegmentChildren(byte[] file, ulong wantedId) {
    var ebml = new EbmlReader(file);
    var pos = 0L;
    while (pos < file.Length) {
      var top = ebml.Read(ref pos);
      if (top is null) break;
      if (top.Value.Id == IdSegment)
        return ebml.Children(top.Value).Count(child => child.Id == wantedId);
    }
    return 0;
  }

  private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0) return 0;
    for (var i = 0; i <= data.Length - needle.Length; ++i)
      if (data.Slice(i, needle.Length).SequenceEqual(needle))
        return i;
    return -1;
  }
}
