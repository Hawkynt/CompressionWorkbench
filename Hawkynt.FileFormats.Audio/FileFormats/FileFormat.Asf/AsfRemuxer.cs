#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Asf;

/// <summary>
/// Bridges the archive-shaped ASF demux surface to <see cref="AsfContainerWriter"/>. Canonical
/// remux artifacts are the elementary stream bytes, the exact Stream Properties body,
/// and a media-object manifest carrying boundaries/timestamps/keyframe flags.
/// </summary>
internal static class AsfRemuxer {

  internal const string PreservedHeaderPath = "metadata/preserved-header.bin";

  private sealed record Model(
    List<AsfContainerWriter.StreamSource> Streams,
    AsfContainerWriter.FileMetadata Metadata,
    List<byte[]> PreservedHeaderObjects
  );

  private sealed class ArtifactChange {
    public byte[]? Properties;
    public byte[]? Payload;
    public List<AsfMediaObjectInfo>? Objects;
  }

  internal static string PropertiesPath(int streamNumber) => $"streams/stream_{streamNumber:D2}.properties.bin";
  internal static string ObjectsPath(int streamNumber) => $"streams/stream_{streamNumber:D2}.objects.csv";
  internal static string PayloadPath(int streamNumber) => $"streams/stream_{streamNumber:D2}.bin";

  internal static byte[] RenderObjects(IReadOnlyList<AsfMediaObjectInfo> objects) {
    var sb = new StringBuilder("offset,length,presentation_time_ms,keyframe\n");
    var offset = 0L;
    foreach (var mediaObject in objects) {
      sb.Append(offset.ToString(CultureInfo.InvariantCulture)).Append(',')
        .Append(mediaObject.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
        .Append(mediaObject.PresentationTimeMs.ToString(CultureInfo.InvariantCulture)).Append(',')
        .AppendLine(mediaObject.KeyFrame ? "true" : "false");
      offset = checked(offset + mediaObject.Length);
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  internal static List<AsfMediaObjectInfo> ParseObjects(ReadOnlySpan<byte> bytes) {
    var result = new List<AsfMediaObjectInfo>();
    var text = Encoding.UTF8.GetString(bytes);
    long expectedOffset = 0;
    foreach (var rawLine in text.Split('\n')) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line.StartsWith("offset,", StringComparison.OrdinalIgnoreCase) || line[0] is '#' or ';')
        continue;
      var fields = line.Split(',');
      if (fields.Length != 4
          || !long.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
          || !int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
          || !uint.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var presentationTimeMs))
        throw new InvalidDataException($"Invalid ASF media-object manifest line: '{line}'.");
      if (offset != expectedOffset || length < 0)
        throw new InvalidDataException("ASF media-object manifest offsets must be contiguous and lengths non-negative.");
      var keyText = fields[3].Trim();
      var keyFrame = keyText.Equals("true", StringComparison.OrdinalIgnoreCase) || keyText == "1"
        ? true
        : keyText.Equals("false", StringComparison.OrdinalIgnoreCase) || keyText == "0"
          ? false
          : throw new InvalidDataException($"Invalid ASF keyframe value '{keyText}'.");
      result.Add(new AsfMediaObjectInfo(length, presentationTimeMs, keyFrame));
      expectedOffset = checked(expectedOffset + length);
    }
    return result;
  }

  /// <summary>
  /// What the ASF writer accepts, quoted verbatim in every create-side refusal. ASF is a media
  /// container keyed on Stream Properties bodies, not a file archive, so an arbitrary file tree is
  /// refused through this declared-constraint path rather than mangled into a stream.
  /// </summary>
  private const string CreateContract =
    "ASF creation needs the canonical artifacts an ASF demux produces — " +
    "streams/stream_NN.properties.bin plus streams/stream_NN.bin (optionally .objects.csv) — " +
    "or a FULL.asf passthrough; an arbitrary file tree cannot be represented as a media stream.";

  internal static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var changes = new Dictionary<int, ArtifactChange>();
    ArchiveInputInfo? full = null;
    byte[]? metadataIni = null;
    byte[]? preservedHeader = null;

    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var name = Normalize(input.ArchiveName);
      if (name.Equals("FULL.asf", StringComparison.OrdinalIgnoreCase)) {
        full = input;
        continue;
      }
      if (name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)) {
        metadataIni = input.ReadContent();
        continue;
      }
      if (name.Equals(PreservedHeaderPath, StringComparison.OrdinalIgnoreCase)) {
        preservedHeader = input.ReadContent();
        continue;
      }
      if (IsDerivedArtifact(name))
        continue;
      if (!TryApplyArtifact(changes, name, input.ReadContent()))
        throw new InvalidOperationException($"{CreateContract} Input '{input.ArchiveName}' maps to no ASF artifact.");
    }

    if (changes.Count == 0) {
      if (full == null)
        throw new InvalidOperationException(CreateContract);
      if (output.CanSeek) {
        output.Position = 0;
        output.SetLength(0);
      }
      output.Write(full.ReadContent());
      return;
    }

    var streams = new List<AsfContainerWriter.StreamSource>();
    foreach (var (streamNumber, change) in changes.OrderBy(static p => p.Key)) {
      if (change.Properties == null || change.Payload == null)
        throw new InvalidOperationException($"{CreateContract} Stream {streamNumber} is missing its .properties.bin or .bin input.");
      var objects = change.Objects ?? (change.Payload.Length == 0
        ? []
        : [new AsfMediaObjectInfo(change.Payload.Length, 0, false)]);
      streams.Add(new AsfContainerWriter.StreamSource(streamNumber, change.Properties, change.Payload, objects));
    }

    var metadata = metadataIni != null ? AsfContainerWriter.FileMetadata.ParseIni(metadataIni) : new AsfContainerWriter.FileMetadata();
    var preserved = preservedHeader != null
      ? AsfContainerWriter.ParsePreservedHeaderObjects(preservedHeader)
      : [];
    var packetSize = options.TryGetInt("packet-size", out var configuredPacketSize)
      ? configuredPacketSize
      : metadata.PacketSize;
    AsfContainerWriter.Write(output, streams, metadata, preserved, packetSize);
  }

  internal static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var model = LoadModel(archive);
    var changes = new Dictionary<int, ArtifactChange>();
    byte[]? metadataIni = null;
    byte[]? preservedHeader = null;

    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var name = Normalize(input.ArchiveName);
      if (name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)) {
        metadataIni = input.ReadContent();
        continue;
      }
      if (name.Equals(PreservedHeaderPath, StringComparison.OrdinalIgnoreCase)) {
        preservedHeader = input.ReadContent();
        continue;
      }
      if (name.Equals("FULL.asf", StringComparison.OrdinalIgnoreCase) || IsDerivedArtifact(name))
        continue;
      if (!TryApplyArtifact(changes, name, input.ReadContent()))
        throw new NotSupportedException($"ASF remux edit does not know how to map input '{input.ArchiveName}'.");
    }

    if (metadataIni != null) {
      var replacement = AsfContainerWriter.FileMetadata.ParseIni(metadataIni);
      replacement.PacketSize = model.Metadata.PacketSize;
      model = model with { Metadata = replacement };
    }
    if (preservedHeader != null)
      model = model with { PreservedHeaderObjects = AsfContainerWriter.ParsePreservedHeaderObjects(preservedHeader) };

    var invalidatesInheritedStreamMetadata = false;
    foreach (var (streamNumber, change) in changes.OrderBy(static p => p.Key)) {
      var index = model.Streams.FindIndex(s => s.StreamNumber == streamNumber);
      if (index < 0) {
        if (change.Properties == null || change.Payload == null)
          throw new NotSupportedException($"Adding ASF stream {streamNumber} requires both .properties.bin and .bin inputs.");
        var addedObjects = change.Objects ?? (change.Payload.Length == 0
          ? []
          : [new AsfMediaObjectInfo(change.Payload.Length, 0, false)]);
        model.Streams.Add(new AsfContainerWriter.StreamSource(streamNumber, change.Properties, change.Payload, addedObjects));
        invalidatesInheritedStreamMetadata = true;
        continue;
      }

      var current = model.Streams[index];
      var properties = change.Properties ?? current.StreamPropertiesBody;
      var payload = change.Payload ?? current.Payload;
      IReadOnlyList<AsfMediaObjectInfo> objects;
      if (change.Objects != null) {
        objects = change.Objects;
      } else if (change.Payload == null || payload.Length == current.Payload.Length) {
        objects = current.Objects;
      } else if (current.Objects.Count == 1) {
        var old = current.Objects[0];
        objects = [old with { Length = payload.Length }];
      } else {
        throw new NotSupportedException(
          $"Replacing multi-object ASF stream {streamNumber} with a different byte length requires a matching .objects.csv manifest.");
      }

      if (change.Properties != null || change.Objects != null || payload.Length != current.Payload.Length)
        invalidatesInheritedStreamMetadata = true;
      model.Streams[index] = new AsfContainerWriter.StreamSource(streamNumber, properties, payload, objects);
    }

    // Opaque Header Extension / Codec List / mutual-exclusion / bitrate objects can refer
    // to stream numbers and Stream Properties. Preserve them byte-exactly for payload-only
    // remuxes, but never carry stale references through a topology/format/layout edit. A caller
    // can explicitly provide a replacement preserved-header artifact alongside the edit.
    if (invalidatesInheritedStreamMetadata && preservedHeader == null)
      model = model with {
        PreservedHeaderObjects = AsfContainerWriter.KeepStreamIndependentHeaderObjects(model.PreservedHeaderObjects),
      };

    Commit(archive, model);
  }

  internal static void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    var model = LoadModel(archive);
    var removeStreams = new HashSet<int>();
    var clearPreservedHeader = false;

    foreach (var rawName in entryNames ?? []) {
      var name = Normalize(rawName);
      if (name.Equals(PreservedHeaderPath, StringComparison.OrdinalIgnoreCase)) {
        clearPreservedHeader = true;
        continue;
      }
      if (TryGetStreamNumber(name, out var streamNumber)) {
        removeStreams.Add(streamNumber);
        continue;
      }
      if (name.Equals("FULL.asf", StringComparison.OrdinalIgnoreCase)
          || name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
          || name.Equals("metadata/tags.ini", StringComparison.OrdinalIgnoreCase))
        throw new NotSupportedException($"'{rawName}' is a derived ASF view and cannot be removed independently of the container.");
      throw new FileNotFoundException($"ASF entry not found: {rawName}");
    }

    if (removeStreams.Count > 0) {
      model.Streams.RemoveAll(s => removeStreams.Contains(s.StreamNumber));
      if (!clearPreservedHeader)
        model = model with {
          PreservedHeaderObjects = AsfContainerWriter.KeepStreamIndependentHeaderObjects(model.PreservedHeaderObjects),
        };
    }
    if (clearPreservedHeader)
      model.PreservedHeaderObjects.Clear();
    if (model.Streams.Count == 0)
      throw new NotSupportedException("The ASF remux profile does not expose an empty zero-stream container.");
    Commit(archive, model);
  }

  private static Model LoadModel(Stream archive) {
    if (!archive.CanRead || !archive.CanSeek)
      throw new ArgumentException("ASF remux requires a readable, seekable source stream.", nameof(archive));
    archive.Position = 0;
    using var buffer = new MemoryStream();
    archive.CopyTo(buffer);
    var blob = buffer.ToArray();
    var parsed = AsfReader.Parse(blob);
    if (parsed.FileSize is { } declaredFileSize && declaredFileSize > (ulong)blob.LongLength)
      throw new InvalidDataException("ASF source is truncated: the declared file size exceeds the available bytes.");
    if (parsed.Streams.Count == 0)
      throw new InvalidDataException("ASF source contains no readable Stream Properties objects.");
    // A container whose Data Object never parsed has no packet region to carry forward. Rebuilding
    // it would emit a structurally complete file whose media is silently gone, so refuse instead:
    // the caller keeps the bytes it has and can still reach them through FULL.asf pass-through.
    if (parsed.DataPayload == null)
      throw new NotSupportedException(
        "ASF remux requires a readable Data Object; this container has none, so its packet region cannot be preserved.");

    var streams = new List<AsfContainerWriter.StreamSource>();
    foreach (var stream in parsed.Streams) {
      if (stream.StreamPropertiesBody.Length == 0)
        throw new InvalidDataException($"ASF stream {stream.StreamNumber} has no preserved Stream Properties body.");
      if (stream.Encrypted)
        throw new NotSupportedException("Encrypted ASF streams are not remuxed because their encryption header/payload-extension system is not preserved.");
      if (!parsed.StreamPayloads.TryGetValue(stream.StreamNumber, out var payload)) {
        if ((parsed.DataPacketCount ?? 0) != 0)
          throw new NotSupportedException($"ASF stream {stream.StreamNumber} could not be fully depayloaded; refusing a lossy remux.");
        payload = [];
      }
      var objects = parsed.StreamObjects.TryGetValue(stream.StreamNumber, out var manifest)
        ? manifest
        : payload.Length == 0 ? [] : [new AsfMediaObjectInfo(payload.Length, 0, false)];
      streams.Add(new AsfContainerWriter.StreamSource(stream.StreamNumber, stream.StreamPropertiesBody, payload, objects));
    }

    return new Model(streams, AsfContainerWriter.FileMetadata.FromParsed(parsed), [.. parsed.PreservedHeaderObjects]);
  }

  private static void Commit(Stream archive, Model model) {
    if (!archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("ASF remux requires a writable, seekable destination stream.", nameof(archive));

    var tempPath = Path.Combine(Path.GetTempPath(), "cwb_asf_" + Guid.NewGuid().ToString("N") + ".tmp");
    try {
      using var staged = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
        FileOptions.SequentialScan);
      AsfContainerWriter.Write(staged, model.Streams, model.Metadata, model.PreservedHeaderObjects, model.Metadata.PacketSize);
      staged.Flush(flushToDisk: true);
      Verify(staged, model);

      staged.Position = 0;

      // Resize the destination BEFORE overwriting a single byte of it. Truncating first and
      // discovering only mid-copy that the destination cannot take the new length — a
      // MemoryStream over a fixed buffer, a full volume — leaves the caller holding a
      // half-written archive, which is exactly what the staging file exists to prevent.
      try {
        archive.SetLength(staged.Length);
      } catch (Exception e) when (e is NotSupportedException or IOException) {
        throw new NotSupportedException(
          $"ASF remux needs a destination that can be resized to {staged.Length} bytes; the supplied "
          + "stream is fixed-size. The original archive has not been modified.", e);
      }

      archive.Position = 0;
      staged.CopyTo(archive);
      archive.Flush();
    } finally {
      try { File.Delete(tempPath); } catch { /* best effort */ }
    }
  }

  private static void Verify(Stream staged, Model expected) {
    staged.Position = 0;
    using var buffer = new MemoryStream();
    staged.CopyTo(buffer);
    var parsed = AsfReader.Parse(buffer.ToArray());
    if (parsed.Streams.Count != expected.Streams.Count)
      throw new InvalidOperationException("ASF staged remux changed the stream count.");

    foreach (var stream in expected.Streams) {
      var reparsed = parsed.Streams.SingleOrDefault(s => s.StreamNumber == stream.StreamNumber)
        ?? throw new InvalidOperationException($"ASF staged remux lost stream {stream.StreamNumber}.");
      if (!reparsed.StreamPropertiesBody.AsSpan().SequenceEqual(stream.StreamPropertiesBody))
        throw new InvalidOperationException($"ASF staged remux changed Stream Properties for stream {stream.StreamNumber}.");
      if (!parsed.StreamPayloads.TryGetValue(stream.StreamNumber, out var payload) || !payload.AsSpan().SequenceEqual(stream.Payload))
        throw new InvalidOperationException($"ASF staged remux changed elementary bytes for stream {stream.StreamNumber}.");
      if (!parsed.StreamObjects.TryGetValue(stream.StreamNumber, out var objects) || !objects.SequenceEqual(stream.Objects))
        throw new InvalidOperationException($"ASF staged remux changed media-object timing/layout for stream {stream.StreamNumber}.");
    }

    var unmatched = parsed.PreservedHeaderObjects.ToList();
    foreach (var expectedObject in expected.PreservedHeaderObjects) {
      var index = unmatched.FindIndex(actual => actual.AsSpan().SequenceEqual(expectedObject));
      if (index < 0)
        throw new InvalidOperationException("ASF staged remux lost or changed a preserved Header Object child.");
      unmatched.RemoveAt(index);
    }
  }

  private static bool TryApplyArtifact(Dictionary<int, ArtifactChange> changes, string name, byte[] data) {
    if (!TryGetStreamNumber(name, out var streamNumber, out var suffix))
      return false;
    if (!changes.TryGetValue(streamNumber, out var change)) {
      change = new ArtifactChange();
      changes.Add(streamNumber, change);
    }
    if (suffix.Equals(".properties.bin", StringComparison.OrdinalIgnoreCase)) {
      if (AsfContainerWriter.GetStreamNumber(data) != streamNumber)
        throw new InvalidDataException($"Stream Properties artifact '{name}' carries a different stream number.");
      change.Properties = data;
      return true;
    }
    if (suffix.Equals(".objects.csv", StringComparison.OrdinalIgnoreCase)) {
      change.Objects = ParseObjects(data);
      return true;
    }
    if (suffix.Equals(".bin", StringComparison.OrdinalIgnoreCase)) {
      change.Payload = data;
      return true;
    }
    return false;
  }

  private static bool TryGetStreamNumber(string name, out int streamNumber)
    => TryGetStreamNumber(name, out streamNumber, out _);

  private static bool TryGetStreamNumber(string name, out int streamNumber, out string suffix) {
    const string Prefix = "streams/stream_";
    streamNumber = 0;
    suffix = "";
    if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
      return false;
    var start = Prefix.Length;
    var end = start;
    while (end < name.Length && char.IsAsciiDigit(name[end]))
      ++end;
    if (end == start || !int.TryParse(name.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out streamNumber)
        || streamNumber is < 1 or > 127)
      return false;
    suffix = name[end..];
    return true;
  }

  private static bool IsDerivedArtifact(string name) {
    if (name.Equals("metadata/tags.ini", StringComparison.OrdinalIgnoreCase))
      return true;
    if (!TryGetStreamNumber(name, out _, out var suffix))
      return false;
    return suffix.Equals(".info.txt", StringComparison.OrdinalIgnoreCase)
      || suffix.StartsWith("/", StringComparison.Ordinal);
  }

  private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
