#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Asf;

/// <summary>
/// Clean-room ASF packet writer used for codec-preserving mux/remux. The writer emits
/// fixed-size, single-payload packets plus per-video Simple Index Objects. Elementary
/// stream bytes are never decoded or re-encoded.
/// <para>
/// This is the <em>container</em> writer: it is handed whole Stream Properties bodies and the
/// preserved Header Object children read off an existing file, so it can carry any number of
/// audio and video streams through a rebuild unchanged. <see cref="AsfWriter"/> is the separate
/// <em>audio</em> writer that synthesises a header from a decoded
/// <c>AudioEncodedStream</c> for the single-stream audio mux route; the two are not
/// interchangeable and neither subsumes the other.
/// </para>
/// </summary>
internal static class AsfContainerWriter {

  internal const int DefaultPacketSize = 4096;
  internal const int MinimumPacketSize = 100;
  internal const int MaximumPacketSize = 65536;

  private const int PacketHeaderBytes = 14;
  private const int PayloadHeaderBytes = 18;
  private const int PacketOverheadBytes = PacketHeaderBytes + PayloadHeaderBytes;
  private const ulong SimpleIndexInterval100Ns = 10_000_000UL;
  private const uint SimpleIndexIntervalMs = 1000;

  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] FilePropertiesObject =
    [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] StreamPropertiesObject =
    [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] HeaderExtensionObject =
    [0xB5, 0x03, 0xBF, 0x5F, 0x2E, 0xA9, 0xCF, 0x11, 0x8E, 0xE3, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] HeaderExtensionReserved1 =
    [0x11, 0xD2, 0xD3, 0xAB, 0xBA, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] ContentDescriptionObject =
    [0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] ExtendedContentDescriptionObject =
    [0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11, 0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] SimpleIndexObject =
    [0x90, 0x08, 0x00, 0x33, 0xB1, 0xE5, 0xCF, 0x11, 0x89, 0xF4, 0x00, 0xA0, 0xC9, 0x03, 0x49, 0xCB];
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  internal sealed record StreamSource(
    int StreamNumber,
    byte[] StreamPropertiesBody,
    byte[] Payload,
    IReadOnlyList<AsfMediaObjectInfo> Objects
  );

  internal sealed class FileMetadata {
    public ulong CreationDate;
    public ulong PlayDuration100ns;
    public ulong SendDuration100ns;
    public ulong PrerollMs;
    public uint MaxBitrate;
    public int PacketSize = DefaultPacketSize;

    public FileMetadata Clone() => new() {
      CreationDate = this.CreationDate,
      PlayDuration100ns = this.PlayDuration100ns,
      SendDuration100ns = this.SendDuration100ns,
      PrerollMs = this.PrerollMs,
      MaxBitrate = this.MaxBitrate,
      PacketSize = this.PacketSize,
    };

    public static FileMetadata FromParsed(AsfReader.Parsed parsed) {
      var packetSize = parsed.MinPacketSize is { } min && parsed.MaxPacketSize == min && min is >= MinimumPacketSize and <= MaximumPacketSize
        ? (int)min
        : DefaultPacketSize;
      return new FileMetadata {
        CreationDate = parsed.CreationDate ?? 0,
        PlayDuration100ns = parsed.PlayDuration100ns ?? 0,
        SendDuration100ns = parsed.SendDuration100ns ?? 0,
        PrerollMs = parsed.Preroll ?? 0,
        MaxBitrate = parsed.MaxBitrate ?? 0,
        PacketSize = packetSize,
      };
    }

    public static FileMetadata ParseIni(ReadOnlySpan<byte> bytes) {
      var result = new FileMetadata();
      var text = Encoding.UTF8.GetString(bytes);
      foreach (var rawLine in text.Split('\n')) {
        var line = rawLine.Trim();
        if (line.Length == 0 || line[0] is '[' or ';' or '#')
          continue;
        var equals = line.IndexOf('=');
        if (equals <= 0)
          continue;
        var key = line[..equals].Trim();
        var value = line[(equals + 1)..].Trim();
        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
          continue;
        switch (key.ToLowerInvariant()) {
          case "creation_date_filetime": result.CreationDate = unsigned; break;
          case "play_duration_100ns": result.PlayDuration100ns = unsigned; break;
          case "send_duration_100ns": result.SendDuration100ns = unsigned; break;
          case "preroll_ms": result.PrerollMs = unsigned; break;
          case "max_bitrate" when unsigned <= uint.MaxValue: result.MaxBitrate = (uint)unsigned; break;
          case "min_packet_size" when unsigned is >= MinimumPacketSize and <= MaximumPacketSize:
            result.PacketSize = (int)unsigned;
            break;
        }
      }
      return result;
    }
  }

  private sealed record MuxObject(
    StreamSource Stream,
    AsfMediaObjectInfo Info,
    int ObjectNumber,
    int PayloadOffset,
    ulong FirstPacketNumber = 0,
    int PacketCount = 0
  );

  private readonly record struct SimpleIndexEntry(uint PacketNumber, ushort PacketCount);

  private sealed record SimpleIndex(StreamSource Stream, IReadOnlyList<SimpleIndexEntry> Entries) {
    public ulong Size => checked(56UL + (ulong)this.Entries.Count * 6UL);
    public uint MaximumPacketCount => this.Entries.Count == 0
      ? 0
      : this.Entries.Max(static entry => (uint)entry.PacketCount);
    public bool IsUsable => this.Entries.Count > 0;
  }

  /// <summary>Returns the stream number encoded in an ASF Stream Properties body.</summary>
  internal static int GetStreamNumber(ReadOnlySpan<byte> body) {
    if (body.Length < 54)
      throw new InvalidDataException("ASF Stream Properties body is shorter than 54 bytes.");
    return BinaryPrimitives.ReadUInt16LittleEndian(body[48..]) & 0x7F;
  }

  internal static bool IsEncrypted(ReadOnlySpan<byte> body) {
    if (body.Length < 54)
      throw new InvalidDataException("ASF Stream Properties body is shorter than 54 bytes.");
    return (BinaryPrimitives.ReadUInt16LittleEndian(body[48..]) & 0x8000) != 0;
  }

  /// <summary>Splits concatenated preserved header objects emitted by the descriptor.</summary>
  internal static List<byte[]> ParsePreservedHeaderObjects(ReadOnlySpan<byte> blob) {
    var result = new List<byte[]>();
    var pos = 0;
    while (pos < blob.Length) {
      if (pos + 24 > blob.Length)
        throw new InvalidDataException("Preserved ASF header object is truncated.");
      var size = BinaryPrimitives.ReadUInt64LittleEndian(blob[(pos + 16)..]);
      if (size < 24 || size > int.MaxValue || pos + (long)size > blob.Length)
        throw new InvalidDataException("Preserved ASF header object has an invalid size.");
      var objectBytes = blob.Slice(pos, (int)size).ToArray();
      ValidatePreservedHeaderObject(objectBytes);
      result.Add(objectBytes);
      pos += (int)size;
    }
    ValidatePreservedHeaderSet(result);
    return result;
  }

  internal static byte[] JoinPreservedHeaderObjects(IEnumerable<byte[]> objects) {
    var materialized = objects.ToArray();
    ValidatePreservedHeaderSet(materialized);
    using var output = new MemoryStream();
    foreach (var objectBytes in materialized)
      output.Write(objectBytes);
    return output.ToArray();
  }

  /// <summary>
  /// Keeps only Header Object children that remain valid when the stream topology or
  /// Stream Properties change. Stream-dependent opaque objects are intentionally dropped
  /// rather than being retained with stale stream-number, bitrate or language references.
  /// </summary>
  internal static List<byte[]> KeepStreamIndependentHeaderObjects(IEnumerable<byte[]> objects)
    => objects.Where(static objectBytes => IsStreamIndependentHeaderObject(objectBytes)).ToList();

  public static void Write(
      Stream output,
      IReadOnlyList<StreamSource> streams,
      FileMetadata metadata,
      IReadOnlyList<byte[]>? preservedHeaderObjects = null,
      int? packetSizeOverride = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(streams);
    ArgumentNullException.ThrowIfNull(metadata);
    if (!output.CanWrite)
      throw new ArgumentException("ASF output stream must be writable.", nameof(output));
    if (streams.Count == 0)
      throw new NotSupportedException("ASF muxing requires at least one media stream.");
    if (streams.Count > 127)
      throw new NotSupportedException("ASF supports at most 127 numbered streams.");

    var packetSize = packetSizeOverride ?? metadata.PacketSize;
    if (packetSize is < MinimumPacketSize or > MaximumPacketSize)
      throw new ArgumentOutOfRangeException(nameof(packetSizeOverride), $"ASF packet size must be {MinimumPacketSize}..{MaximumPacketSize} bytes.");
    var maxFragment = packetSize - PacketOverheadBytes;
    if (maxFragment <= 0)
      throw new InvalidOperationException("ASF packet size leaves no room for payload bytes.");

    var normalized = streams
      .OrderBy(static s => s.StreamNumber)
      .Select(NormalizeStream)
      .ToArray();
    if (normalized.Select(static s => s.StreamNumber).Distinct().Count() != normalized.Length)
      throw new InvalidDataException("ASF stream numbers must be unique.");

    var preserved = preservedHeaderObjects?.ToArray() ?? [];
    ValidatePreservedHeaderSet(preserved);
    var hasPreservedHeaderExtension = preserved.Any(static objectBytes => HasGuid(objectBytes, HeaderExtensionObject));

    var muxObjects = BuildMuxObjects(normalized, maxFragment);
    ulong packetCount = 0;
    foreach (var item in muxObjects)
      packetCount = checked(packetCount + (ulong)item.PacketCount);

    var simpleIndexes = BuildSimpleIndexes(normalized, muxObjects);
    ulong simpleIndexBytes = 0;
    foreach (var index in simpleIndexes)
      simpleIndexBytes = checked(simpleIndexBytes + index.Size);

    var syntheticHeaderExtensionBytes = hasPreservedHeaderExtension ? 0UL : 46UL;
    var headerSize = checked((ulong)30
      + 24 + 80
      + (ulong)normalized.Sum(static s => 24L + s.StreamPropertiesBody.Length)
      + syntheticHeaderExtensionBytes
      + (ulong)preserved.Sum(static o => (long)o.Length));
    var dataSize = checked(50UL + packetCount * (ulong)packetSize);
    var fileSize = checked(headerSize + dataSize + simpleIndexBytes);
    var fileId = BuildFileId(normalized);

    var derivedPlayDuration = muxObjects.Count == 0
      ? 0UL
      : (ulong)muxObjects.Max(static o => o.Info.PresentationTimeMs) * 10_000UL;
    var playDuration = Math.Max(metadata.PlayDuration100ns, derivedPlayDuration);
    var sendDuration = Math.Max(metadata.SendDuration100ns, playDuration);
    var maxBitrate = metadata.MaxBitrate != 0 ? metadata.MaxBitrate : DeriveAudioBitrate(normalized);

    var hasAudio = normalized.Any(static stream => IsStreamType(stream, AudioStreamType));
    // Read "has video" off the declared stream types, not off how many Simple Index Objects
    // came back. Deriving it from the index list makes an unindexable video stream look like
    // no video stream at all, which is the shape that produces a false seekable claim.
    var hasVideo = normalized.Any(static stream => IsStreamType(stream, VideoStreamType));
    var allVideoStreamsIndexed = simpleIndexes.Count > 0 && simpleIndexes.All(static index => index.IsUsable);
    // The packet size is fixed by construction here, so a file carrying audio is seekable
    // unless it also carries video, in which case every separately declared video stream needs
    // a matching Simple Index Object. A zero-entry index is structurally valid but cannot be
    // seeked with, so it does not earn the flag.
    var fileFlags = hasAudio && (!hasVideo || allVideoStreamsIndexed) ? 0x00000002u : 0u;

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }

    WriteGuid(output, HeaderObject);
    WriteU64(output, headerSize);
    WriteU32(output, checked((uint)(normalized.Length + preserved.Length + (hasPreservedHeaderExtension ? 1 : 2))));
    output.WriteByte(0x01);
    output.WriteByte(0x02);

    using (var fileProperties = new MemoryStream()) {
      fileProperties.Write(fileId);
      WriteU64(fileProperties, fileSize);
      WriteU64(fileProperties, metadata.CreationDate);
      WriteU64(fileProperties, packetCount);
      WriteU64(fileProperties, playDuration);
      WriteU64(fileProperties, sendDuration);
      WriteU64(fileProperties, metadata.PrerollMs);
      WriteU32(fileProperties, fileFlags);
      WriteU32(fileProperties, (uint)packetSize);
      WriteU32(fileProperties, (uint)packetSize);
      WriteU32(fileProperties, maxBitrate);
      WriteObject(output, FilePropertiesObject, fileProperties.ToArray());
    }

    foreach (var stream in normalized)
      WriteObject(output, StreamPropertiesObject, stream.StreamPropertiesBody);

    if (!hasPreservedHeaderExtension) {
      using var extension = new MemoryStream();
      extension.Write(HeaderExtensionReserved1);
      WriteU16(extension, 6); // Reserved2, mandated by the ASF specification.
      WriteU32(extension, 0); // no nested extension objects in the normalized writer profile.
      WriteObject(output, HeaderExtensionObject, extension.ToArray());
    }

    foreach (var objectBytes in preserved)
      output.Write(objectBytes);

    WriteGuid(output, DataObject);
    WriteU64(output, dataSize);
    output.Write(fileId);
    WriteU64(output, packetCount);
    WriteU16(output, 0x0101);

    foreach (var item in muxObjects) {
      var remaining = item.Info.Length;
      var fragmentOffset = 0;
      do {
        var fragmentLength = Math.Min(remaining, maxFragment);
        WritePacket(output, packetSize, item, fragmentOffset, fragmentLength);
        remaining -= fragmentLength;
        fragmentOffset += fragmentLength;
      } while (remaining > 0);
    }

    // Simple Index Objects are top-level ASF objects and, when present, must be last.
    // Their association with video streams is positional, so stream-number ordering is
    // significant and BuildSimpleIndexes deliberately returns them in that order.
    foreach (var index in simpleIndexes)
      WriteSimpleIndex(output, fileId, index);
  }

  private static StreamSource NormalizeStream(StreamSource stream) {
    ArgumentNullException.ThrowIfNull(stream.StreamPropertiesBody);
    ArgumentNullException.ThrowIfNull(stream.Payload);
    if (stream.StreamNumber is < 1 or > 127)
      throw new InvalidDataException($"ASF stream number {stream.StreamNumber} is outside 1..127.");
    if (GetStreamNumber(stream.StreamPropertiesBody) != stream.StreamNumber)
      throw new InvalidDataException($"Stream Properties body carries stream number {GetStreamNumber(stream.StreamPropertiesBody)}, not {stream.StreamNumber}.");
    if (IsEncrypted(stream.StreamPropertiesBody))
      throw new NotSupportedException("Encrypted ASF streams cannot be remuxed without preserving their encryption header/payload-extension system.");

    IReadOnlyList<AsfMediaObjectInfo> objects = stream.Objects;
    if (objects.Count == 0 && stream.Payload.Length > 0)
      objects = [new AsfMediaObjectInfo(stream.Payload.Length, 0, false)];

    long total = 0;
    foreach (var mediaObject in objects) {
      if (mediaObject.Length < 0)
        throw new InvalidDataException("ASF media-object length cannot be negative.");
      total = checked(total + mediaObject.Length);
    }
    if (total != stream.Payload.Length)
      throw new InvalidDataException($"ASF media-object manifest totals {total} bytes but stream payload contains {stream.Payload.Length} bytes.");

    return stream with { Objects = objects };
  }

  private static List<MuxObject> BuildMuxObjects(IReadOnlyList<StreamSource> streams, int maxFragment) {
    var result = new List<MuxObject>();
    foreach (var stream in streams) {
      var payloadOffset = 0;
      for (var i = 0; i < stream.Objects.Count; ++i) {
        var info = stream.Objects[i];
        result.Add(new MuxObject(stream, info, i, payloadOffset));
        payloadOffset = checked(payloadOffset + info.Length);
      }
    }
    result.Sort(static (a, b) => {
      var time = a.Info.PresentationTimeMs.CompareTo(b.Info.PresentationTimeMs);
      return time != 0 ? time
        : a.Stream.StreamNumber != b.Stream.StreamNumber ? a.Stream.StreamNumber.CompareTo(b.Stream.StreamNumber)
        : a.ObjectNumber.CompareTo(b.ObjectNumber);
    });

    ulong packetNumber = 0;
    for (var i = 0; i < result.Count; ++i) {
      var item = result[i];
      var itemPacketCount = Math.Max(1, checked((int)(((long)item.Info.Length + maxFragment - 1) / maxFragment)));
      result[i] = item with { FirstPacketNumber = packetNumber, PacketCount = itemPacketCount };
      packetNumber = checked(packetNumber + (ulong)itemPacketCount);
    }
    return result;
  }

  private static List<SimpleIndex> BuildSimpleIndexes(
      IReadOnlyList<StreamSource> streams,
      IReadOnlyList<MuxObject> muxObjects) {
    var result = new List<SimpleIndex>();
    foreach (var stream in streams.Where(static stream => IsStreamType(stream, VideoStreamType))) {
      var keyFrames = muxObjects
        .Where(item => item.Stream.StreamNumber == stream.StreamNumber && item.Info.KeyFrame)
        .ToArray();

      // Simple Index packet numbers are DWORDs and per-entry packet counts are WORDs. If
      // one keyframe cannot be represented, an apparently complete index would be wrong;
      // emit the required stream-positioned object with zero entries and leave the file
      // unflagged as seekable instead.
      if (keyFrames.Length == 0 || keyFrames.Any(static item => item.FirstPacketNumber > uint.MaxValue || item.PacketCount > ushort.MaxValue)) {
        result.Add(new SimpleIndex(stream, []));
        continue;
      }

      var lastPresentationTimeMs = stream.Objects.Max(static mediaObject => mediaObject.PresentationTimeMs);
      var entryCount = checked((int)(lastPresentationTimeMs / SimpleIndexIntervalMs) + 1);
      var entries = new SimpleIndexEntry[entryCount];

      var keyFrameIndex = 0;
      var selected = keyFrames[0];
      for (var i = 0; i < entries.Length; ++i) {
        var entryTimeMs = checked((uint)i * SimpleIndexIntervalMs);
        while (keyFrameIndex + 1 < keyFrames.Length
               && keyFrames[keyFrameIndex + 1].Info.PresentationTimeMs <= entryTimeMs)
          selected = keyFrames[++keyFrameIndex];

        // Before the first cleanpoint there is no literal "closest past" keyframe. Using
        // the first keyframe is the established interoperable fallback (and is preferable
        // to inventing an invalid packet number in a format with no invalid-entry sentinel).
        entries[i] = new SimpleIndexEntry(
          checked((uint)selected.FirstPacketNumber),
          checked((ushort)selected.PacketCount));
      }

      result.Add(new SimpleIndex(stream, entries));
    }
    return result;
  }

  private static void WriteSimpleIndex(Stream output, ReadOnlySpan<byte> fileId, SimpleIndex index) {
    using var body = new MemoryStream();
    body.Write(fileId);
    WriteU64(body, SimpleIndexInterval100Ns);
    WriteU32(body, index.MaximumPacketCount);
    WriteU32(body, checked((uint)index.Entries.Count));
    foreach (var entry in index.Entries) {
      WriteU32(body, entry.PacketNumber);
      WriteU16(body, entry.PacketCount);
    }
    WriteObject(output, SimpleIndexObject, body.ToArray());
  }

  private static void WritePacket(Stream output, int packetSize, MuxObject item, int fragmentOffset, int fragmentLength) {
    var padding = packetSize - PacketOverheadBytes - fragmentLength;
    if ((uint)padding > ushort.MaxValue)
      throw new InvalidOperationException("ASF packet padding does not fit the selected u16 padding field.");

    // PPI: packet length = DWORD, sequence omitted, padding length = WORD, one payload.
    output.WriteByte(0x70);
    // Payload properties: stream number BYTE, object number DWORD, offset DWORD, replicated length BYTE.
    output.WriteByte(0x7D);
    WriteU32(output, (uint)packetSize);
    WriteU16(output, (ushort)padding);
    WriteU32(output, item.Info.PresentationTimeMs);
    WriteU16(output, 0); // packet duration is optional for this fixed-packet profile.

    output.WriteByte((byte)(item.Stream.StreamNumber | (item.Info.KeyFrame ? 0x80 : 0)));
    WriteU32(output, checked((uint)item.ObjectNumber));
    WriteU32(output, checked((uint)fragmentOffset));
    output.WriteByte(8);
    WriteU32(output, checked((uint)item.Info.Length));
    WriteU32(output, item.Info.PresentationTimeMs);

    if (fragmentLength > 0)
      output.Write(item.Stream.Payload.AsSpan(item.PayloadOffset + fragmentOffset, fragmentLength));
    WriteZeros(output, padding);
  }

  private static byte[] BuildFileId(IReadOnlyList<StreamSource> streams) {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var stream in streams) {
      hash.AppendData(stream.StreamPropertiesBody);
      hash.AppendData(stream.Payload);
    }
    return hash.GetHashAndReset()[..16];
  }

  private static uint DeriveAudioBitrate(IEnumerable<StreamSource> streams) {
    ulong sum = 0;
    foreach (var stream in streams) {
      var body = stream.StreamPropertiesBody;
      if (body.Length < 72 || !body.AsSpan(0, 16).SequenceEqual(AudioStreamType))
        continue;
      var typeSpecificLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(40, 4));
      if (typeSpecificLength < 18 || body.Length < 54 + 12)
        continue;
      sum += (ulong)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(62, 4)) * 8UL;
      if (sum >= uint.MaxValue)
        return uint.MaxValue;
    }
    return (uint)sum;
  }

  private static bool IsStreamType(StreamSource stream, ReadOnlySpan<byte> streamType)
    => stream.StreamPropertiesBody.Length >= 16 && stream.StreamPropertiesBody.AsSpan(0, 16).SequenceEqual(streamType);

  private static bool HasGuid(ReadOnlySpan<byte> objectBytes, ReadOnlySpan<byte> guid)
    => objectBytes.Length >= 16 && objectBytes[..16].SequenceEqual(guid);

  private static bool IsStreamIndependentHeaderObject(ReadOnlySpan<byte> objectBytes)
    => HasGuid(objectBytes, ContentDescriptionObject) || HasGuid(objectBytes, ExtendedContentDescriptionObject);

  private static void ValidatePreservedHeaderSet(IEnumerable<byte[]> objects) {
    var headerExtensionCount = 0;
    foreach (var objectBytes in objects) {
      ValidatePreservedHeaderObject(objectBytes);
      if (HasGuid(objectBytes, HeaderExtensionObject))
        ++headerExtensionCount;
    }
    if (headerExtensionCount > 1)
      throw new InvalidDataException("ASF Header Object may contain only one Header Extension Object.");
  }

  private static void ValidatePreservedHeaderObject(ReadOnlySpan<byte> objectBytes) {
    if (objectBytes.Length < 24)
      throw new InvalidDataException("Preserved ASF header object is shorter than 24 bytes.");
    var size = BinaryPrimitives.ReadUInt64LittleEndian(objectBytes[16..]);
    if (size != (ulong)objectBytes.Length)
      throw new InvalidDataException("Preserved ASF header object size does not match its bytes.");

    var guid = objectBytes[..16];
    if (guid.SequenceEqual(HeaderObject) || guid.SequenceEqual(FilePropertiesObject)
        || guid.SequenceEqual(StreamPropertiesObject) || guid.SequenceEqual(DataObject))
      throw new NotSupportedException("Managed ASF Header/Data objects cannot be injected through preserved-header artifacts.");

    if (!guid.SequenceEqual(HeaderExtensionObject))
      return;
    if (objectBytes.Length < 46)
      throw new InvalidDataException("ASF Header Extension Object is shorter than its mandatory fields.");
    if (!objectBytes.Slice(24, 16).SequenceEqual(HeaderExtensionReserved1))
      throw new InvalidDataException("ASF Header Extension Object has an invalid Reserved1 GUID.");
    if (BinaryPrimitives.ReadUInt16LittleEndian(objectBytes[40..]) != 6)
      throw new InvalidDataException("ASF Header Extension Object Reserved2 must equal 6.");
    var extensionDataSize = BinaryPrimitives.ReadUInt32LittleEndian(objectBytes[42..]);
    if (extensionDataSize != objectBytes.Length - 46)
      throw new InvalidDataException("ASF Header Extension Object data size does not match its bytes.");
  }

  private static void WriteObject(Stream output, ReadOnlySpan<byte> guid, ReadOnlySpan<byte> body) {
    WriteGuid(output, guid);
    WriteU64(output, checked((ulong)(24 + body.Length)));
    output.Write(body);
  }

  private static void WriteGuid(Stream output, ReadOnlySpan<byte> guid) {
    if (guid.Length != 16)
      throw new ArgumentException("ASF GUID must contain exactly 16 bytes.", nameof(guid));
    output.Write(guid);
  }

  private static void WriteZeros(Stream output, int count) {
    Span<byte> zeros = stackalloc byte[256];
    while (count > 0) {
      var chunk = Math.Min(count, zeros.Length);
      output.Write(zeros[..chunk]);
      count -= chunk;
    }
  }

  private static void WriteU16(Stream output, ushort value) {
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteU32(Stream output, uint value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteU64(Stream output, ulong value) {
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
    output.Write(buffer);
  }
}
