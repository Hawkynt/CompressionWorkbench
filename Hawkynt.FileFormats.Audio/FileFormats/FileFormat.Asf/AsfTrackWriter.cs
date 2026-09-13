#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Asf;

/// <summary>
/// Managed ASF audio muxer. The object and packet layout follows the Microsoft ASF
/// specification; codec bitstreams are treated as opaque media objects and are never
/// rewritten by the container layer.
/// </summary>
internal static class AsfTrackWriter {
  internal const int DefaultPacketSize = 3200;
  internal const int MinPacketSize = 100;
  internal const int MaxPacketSize = 65536;

  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] FilePropertiesObject =
    [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] StreamPropertiesObject =
    [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] HeaderExtensionObject =
    [0xB5, 0x03, 0xBF, 0x5F, 0x2E, 0xA9, 0xCF, 0x11, 0x8E, 0xE3, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] HeaderExtensionReserved =
    [0x11, 0xD2, 0xD3, 0xAB, 0xBA, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] ContentDescriptionObject =
    [0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] ExtendedContentDescriptionObject =
    [0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11, 0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50];
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] NoErrorCorrection =
    [0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  internal sealed record AudioStream(
    int StreamNumber,
    ushort FormatTag,
    ushort Channels,
    uint SampleRate,
    uint ByteRate,
    ushort BlockAlign,
    ushort BitsPerSample,
    byte[] ExtraData,
    byte[] Payload,
    IReadOnlyList<int>? ObjectSizes = null
  );

  internal sealed record Metadata(
    string Title = "",
    string Author = "",
    string Copyright = "",
    string Description = "",
    string Rating = "",
    IReadOnlyList<(string Name, string Value)>? ExtendedTags = null
  );

  private sealed record MediaObject(
    AudioStream Stream,
    int Number,
    int Offset,
    int Length,
    uint PresentationTimeMs
  );

  /// <summary>Builds a seekable ASF file containing one or more audio streams.</summary>
  internal static byte[] Write(
    IReadOnlyList<AudioStream> streams,
    Metadata? metadata = null,
    int packetSize = DefaultPacketSize,
    long? creationFileTimeUtc = null) {
    ArgumentNullException.ThrowIfNull(streams);
    if (streams.Count is < 1 or > 127)
      throw new ArgumentOutOfRangeException(nameof(streams), "ASF requires 1..127 streams.");
    if (packetSize is < MinPacketSize or > MaxPacketSize)
      throw new ArgumentOutOfRangeException(nameof(packetSize), $"ASF packet size must be {MinPacketSize}..{MaxPacketSize} bytes.");

    var seen = new HashSet<int>();
    foreach (var stream in streams) {
      if (stream.StreamNumber is < 1 or > 127 || !seen.Add(stream.StreamNumber))
        throw new InvalidDataException("ASF stream numbers must be unique values in 1..127.");
      if (stream.Channels == 0)
        throw new InvalidDataException($"ASF audio stream {stream.StreamNumber} has zero channels.");
      if (stream.SampleRate == 0)
        throw new InvalidDataException($"ASF audio stream {stream.StreamNumber} has zero sample rate.");
      if (stream.ExtraData.Length > ushort.MaxValue)
        throw new InvalidDataException($"ASF audio stream {stream.StreamNumber} has more than 65535 bytes of WAVEFORMATEX extradata.");
      ValidateObjectSizes(stream);
    }

    metadata ??= new Metadata();
    var fileId = Guid.NewGuid().ToByteArray();
    var creation = creationFileTimeUtc ?? DateTime.UtcNow.ToFileTimeUtc();
    var mediaObjects = BuildMediaObjects(streams);
    var packetRegion = BuildPackets(mediaObjects, packetSize, out var packetCount);
    var duration100ns = ComputeDuration100ns(streams);
    var maxBitrate = ComputeMaxBitrate(streams);

    var provisionalHeader = BuildHeader(fileId, fileSize: 0, creation, packetCount,
      duration100ns, packetSize, maxBitrate, streams, metadata);
    var dataSize = checked(50L + packetRegion.Length);
    var fileSize = checked((ulong)(provisionalHeader.LongLength + dataSize));
    var header = BuildHeader(fileId, fileSize, creation, packetCount,
      duration100ns, packetSize, maxBitrate, streams, metadata);
    var data = BuildDataObject(fileId, packetCount, packetRegion);

    using var output = new MemoryStream(checked(header.Length + data.Length));
    output.Write(header);
    output.Write(data);
    return output.ToArray();
  }

  private static void ValidateObjectSizes(AudioStream stream) {
    if (stream.ObjectSizes is not { Count: > 0 })
      return;
    long total = 0;
    foreach (var size in stream.ObjectSizes) {
      if (size <= 0)
        throw new InvalidDataException($"ASF stream {stream.StreamNumber} has a non-positive media-object size.");
      total += size;
    }
    if (total != stream.Payload.LongLength)
      throw new InvalidDataException(
        $"ASF stream {stream.StreamNumber} media-object sizes total {total} bytes, payload has {stream.Payload.LongLength} bytes.");
  }

  private static List<MediaObject> BuildMediaObjects(IReadOnlyList<AudioStream> streams) {
    var result = new List<MediaObject>();
    foreach (var stream in streams) {
      var sizes = EffectiveObjectSizes(stream);
      var payloadOffset = 0;
      for (var i = 0; i < sizes.Count; ++i) {
        var presentation = stream.ByteRate == 0
          ? 0u
          : checked((uint)Math.Min(uint.MaxValue, payloadOffset * 1000L / stream.ByteRate));
        result.Add(new MediaObject(stream, i, payloadOffset, sizes[i], presentation));
        payloadOffset += sizes[i];
      }
    }

    result.Sort(static (a, b) => {
      var byTime = a.PresentationTimeMs.CompareTo(b.PresentationTimeMs);
      if (byTime != 0) return byTime;
      var byStream = a.Stream.StreamNumber.CompareTo(b.Stream.StreamNumber);
      return byStream != 0 ? byStream : a.Number.CompareTo(b.Number);
    });
    return result;
  }

  private static IReadOnlyList<int> EffectiveObjectSizes(AudioStream stream) {
    if (stream.Payload.Length == 0)
      return [];
    if (stream.ObjectSizes is { Count: > 0 })
      return stream.ObjectSizes;

    // WMA-family decoders consume one ASF media object per block-align packet. Preserve
    // that convention when a raw stream has no explicit object-boundary ledger.
    if (stream.FormatTag is >= 0x0160 and <= 0x0163 && stream.BlockAlign > 0) {
      var sizes = new List<int>((stream.Payload.Length + stream.BlockAlign - 1) / stream.BlockAlign);
      for (var offset = 0; offset < stream.Payload.Length; offset += stream.BlockAlign)
        sizes.Add(Math.Min(stream.BlockAlign, stream.Payload.Length - offset));
      return sizes;
    }

    // Other codecs may legally span ASF packets; one logical media object preserves the
    // elementary bytes without guessing codec-specific frame boundaries.
    return [stream.Payload.Length];
  }

  private static byte[] BuildPackets(IReadOnlyList<MediaObject> objects, int packetSize, out ulong packetCount) {
    // Single-payload packet overhead: PPI(10) + payload header(15). We intentionally use
    // the fixed-packet-size default for packet length and an explicit WORD padding field.
    const int overhead = 25;
    var maxPayload = packetSize - overhead;
    if (maxPayload <= 0)
      throw new InvalidDataException("ASF packet size leaves no room for payload data.");

    using var packets = new MemoryStream();
    ulong count = 0;
    foreach (var mediaObject in objects) {
      var fragmentOffset = 0;
      do {
        var fragmentLength = Math.Min(maxPayload, mediaObject.Length - fragmentOffset);
        if (fragmentLength < 0)
          throw new InvalidDataException("ASF media-object fragment underflow.");
        var padding = packetSize - overhead - fragmentLength;

        // Length Type Flags: single payload; fixed packet length; no sequence field;
        // WORD padding length. Property Flags: BYTE media-object number, DWORD offset,
        // BYTE replicated-data length. The stream-number field itself is always one byte.
        packets.WriteByte(0x10);
        packets.WriteByte(0x1D);
        WriteU16(packets, checked((ushort)padding));
        WriteU32(packets, mediaObject.PresentationTimeMs);
        WriteU16(packets, 0);

        packets.WriteByte((byte)(0x80 | mediaObject.Stream.StreamNumber));
        packets.WriteByte((byte)mediaObject.Number);
        WriteU32(packets, checked((uint)fragmentOffset));
        packets.WriteByte(8);
        WriteU32(packets, checked((uint)mediaObject.Length));
        WriteU32(packets, mediaObject.PresentationTimeMs);
        packets.Write(mediaObject.Stream.Payload, mediaObject.Offset + fragmentOffset, fragmentLength);
        if (padding > 0)
          packets.Write(new byte[padding]);

        ++count;
        fragmentOffset += fragmentLength;
      } while (fragmentOffset < mediaObject.Length);
    }

    packetCount = count;
    return packets.ToArray();
  }

  private static byte[] BuildHeader(
    byte[] fileId,
    ulong fileSize,
    long creationFileTimeUtc,
    ulong packetCount,
    ulong duration100ns,
    int packetSize,
    uint maxBitrate,
    IReadOnlyList<AudioStream> streams,
    Metadata metadata) {
    var children = new List<byte[]> {
      WrapObject(FilePropertiesObject, BuildFilePropertiesBody(fileId, fileSize, creationFileTimeUtc,
        packetCount, duration100ns, packetSize, maxBitrate)),
      WrapObject(HeaderExtensionObject, BuildHeaderExtensionBody()),
    };

    if (HasContentDescription(metadata))
      children.Add(WrapObject(ContentDescriptionObject, BuildContentDescriptionBody(metadata)));
    if (metadata.ExtendedTags is { Count: > 0 })
      children.Add(WrapObject(ExtendedContentDescriptionObject, BuildExtendedContentDescriptionBody(metadata.ExtendedTags)));
    foreach (var stream in streams.OrderBy(static s => s.StreamNumber))
      children.Add(WrapObject(StreamPropertiesObject, BuildAudioStreamPropertiesBody(stream)));

    using var body = new MemoryStream();
    foreach (var child in children)
      body.Write(child);
    var bodyBytes = body.ToArray();

    using var header = new MemoryStream();
    header.Write(HeaderObject);
    WriteU64(header, checked((ulong)(30 + bodyBytes.Length)));
    WriteU32(header, checked((uint)children.Count));
    header.WriteByte(1);
    header.WriteByte(2);
    header.Write(bodyBytes);
    return header.ToArray();
  }

  private static byte[] BuildFilePropertiesBody(
    byte[] fileId,
    ulong fileSize,
    long creationFileTimeUtc,
    ulong packetCount,
    ulong duration100ns,
    int packetSize,
    uint maxBitrate) {
    using var body = new MemoryStream(80);
    body.Write(fileId);
    WriteU64(body, fileSize);
    WriteU64(body, unchecked((ulong)Math.Max(0, creationFileTimeUtc)));
    WriteU64(body, packetCount);
    WriteU64(body, duration100ns);
    WriteU64(body, duration100ns);
    WriteU64(body, 0);
    WriteU32(body, 2); // seekable
    WriteU32(body, checked((uint)packetSize));
    WriteU32(body, checked((uint)packetSize));
    WriteU32(body, maxBitrate);
    return body.ToArray();
  }

  private static byte[] BuildHeaderExtensionBody() {
    using var body = new MemoryStream(22);
    body.Write(HeaderExtensionReserved);
    WriteU16(body, 6);
    WriteU32(body, 0);
    return body.ToArray();
  }

  private static byte[] BuildAudioStreamPropertiesBody(AudioStream stream) {
    using var wave = new MemoryStream();
    WriteU16(wave, stream.FormatTag);
    WriteU16(wave, stream.Channels);
    WriteU32(wave, stream.SampleRate);
    WriteU32(wave, stream.ByteRate);
    WriteU16(wave, stream.BlockAlign);
    WriteU16(wave, stream.BitsPerSample);
    WriteU16(wave, checked((ushort)stream.ExtraData.Length));
    wave.Write(stream.ExtraData);
    var waveBytes = wave.ToArray();

    using var body = new MemoryStream();
    body.Write(AudioStreamType);
    body.Write(NoErrorCorrection);
    WriteU64(body, 0);
    WriteU32(body, checked((uint)waveBytes.Length));
    WriteU32(body, 0);
    WriteU16(body, checked((ushort)stream.StreamNumber));
    WriteU32(body, 0);
    body.Write(waveBytes);
    return body.ToArray();
  }

  private static bool HasContentDescription(Metadata metadata)
    => metadata.Title.Length != 0 || metadata.Author.Length != 0 || metadata.Copyright.Length != 0 ||
       metadata.Description.Length != 0 || metadata.Rating.Length != 0;

  private static byte[] BuildContentDescriptionBody(Metadata metadata) {
    var values = new[] {
      Utf16(metadata.Title), Utf16(metadata.Author), Utf16(metadata.Copyright),
      Utf16(metadata.Description), Utf16(metadata.Rating),
    };
    foreach (var value in values)
      if (value.Length > ushort.MaxValue)
        throw new InvalidDataException("ASF content-description strings are limited to 65535 UTF-16 bytes.");

    using var body = new MemoryStream();
    foreach (var value in values)
      WriteU16(body, checked((ushort)value.Length));
    foreach (var value in values)
      body.Write(value);
    return body.ToArray();
  }

  private static byte[] BuildExtendedContentDescriptionBody(IReadOnlyList<(string Name, string Value)> tags) {
    if (tags.Count > ushort.MaxValue)
      throw new InvalidDataException("ASF Extended Content Description has more than 65535 entries.");

    using var body = new MemoryStream();
    WriteU16(body, checked((ushort)tags.Count));
    foreach (var (name, value) in tags) {
      var nameBytes = Utf16(name);
      var valueBytes = Utf16(value);
      if (nameBytes.Length > ushort.MaxValue || valueBytes.Length > ushort.MaxValue)
        throw new InvalidDataException("ASF extended tag names/values are limited to 65535 UTF-16 bytes.");
      WriteU16(body, checked((ushort)nameBytes.Length));
      body.Write(nameBytes);
      WriteU16(body, 0); // Unicode string
      WriteU16(body, checked((ushort)valueBytes.Length));
      body.Write(valueBytes);
    }
    return body.ToArray();
  }

  private static byte[] BuildDataObject(byte[] fileId, ulong packetCount, byte[] packets) {
    using var data = new MemoryStream(checked(50 + packets.Length));
    data.Write(DataObject);
    WriteU64(data, checked((ulong)(50L + packets.Length)));
    data.Write(fileId);
    WriteU64(data, packetCount);
    data.WriteByte(1);
    data.WriteByte(1);
    data.Write(packets);
    return data.ToArray();
  }

  private static byte[] WrapObject(byte[] guid, byte[] body) {
    using var output = new MemoryStream(checked(24 + body.Length));
    output.Write(guid);
    WriteU64(output, checked((ulong)(24L + body.Length)));
    output.Write(body);
    return output.ToArray();
  }

  private static ulong ComputeDuration100ns(IReadOnlyList<AudioStream> streams) {
    ulong maximum = 0;
    foreach (var stream in streams) {
      if (stream.ByteRate == 0)
        continue;
      var durationMs = ((ulong)stream.Payload.LongLength * 1000UL + stream.ByteRate - 1UL) / stream.ByteRate;
      var duration = durationMs > ulong.MaxValue / 10_000UL ? ulong.MaxValue : durationMs * 10_000UL;
      maximum = Math.Max(maximum, duration);
    }
    return maximum;
  }

  private static uint ComputeMaxBitrate(IReadOnlyList<AudioStream> streams) {
    ulong bitrate = 0;
    foreach (var stream in streams)
      bitrate += (ulong)stream.ByteRate * 8;
    return checked((uint)Math.Min(uint.MaxValue, bitrate));
  }

  private static byte[] Utf16(string value) => Encoding.Unicode.GetBytes(value + "\0");

  private static void WriteU16(Stream stream, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteU32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteU64(Stream stream, ulong value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    stream.Write(bytes);
  }
}
