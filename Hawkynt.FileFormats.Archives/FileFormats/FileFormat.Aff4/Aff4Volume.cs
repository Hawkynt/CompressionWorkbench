using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Snappy;

namespace FileFormat.Aff4;

/// <summary>How a logical file's primary data stream is stored.</summary>
internal enum Aff4Storage {
  /// <summary>One ZIP member named after the ARN (AFF4-L section 6.1).</summary>
  ZipSegment,
  /// <summary>Chunked bevies plus <c>.index</c> members (AFF4 Standard v1.0 section 3).</summary>
  ImageStream,
  /// <summary>Base64 bytes inside the Turtle graph (AFF4-L section 6.2).</summary>
  InMetadata,
}

/// <summary>A file or folder described by the metadata graph of an AFF4 logical image.</summary>
internal sealed record Aff4LogicalItem(
  string Arn,
  string Path,
  bool IsDirectory,
  long Size,
  DateTimeOffset? LastWritten,
  Aff4Storage Storage,
  string? Member,
  byte[]? InlineData,
  long ChunkSize,
  long ChunksInSegment,
  string? Compression);

/// <summary>
/// Reads the logical view of an AFF4 ZIP volume: the FileImage and Folder objects of
/// <c>information.turtle</c>, mapped to their storage per AFF4 Standard v1.0 section 5 (relative
/// addressing below the volume ARN, URL-escaped segment names, and the escaped "aff4%3A%2F%2F"
/// prefix for foreign ARNs) and AFF4-L section 1.2 (unescaped GUID ARNs as member names). Data is
/// served from ZipSegments, ImageStreams (stored, Snappy, LZ4, Deflate and zlib chunks) and
/// in-metadata streams. Map-backed images (AFF4-L section 6.3) are not decoded; such files are
/// left out of the logical view and their segments stay visible as ordinary members.
/// </summary>
internal sealed class Aff4Volume {
  public const string Aff4Ns = "http://aff4.org/Schema#";
  private const int MaxChunkSize = 64 << 20;

  private readonly ZipArchive _zip;
  private readonly Dictionary<string, ZipArchiveEntry> _members;

  public string? VolumeArn { get; }
  public string? Turtle { get; }
  public IReadOnlyList<Aff4LogicalItem> Items { get; }
  /// <summary>ZIP members that carry logical data and so are represented by <see cref="Items"/>.</summary>
  public IReadOnlySet<string> ConsumedMembers { get; }
  public IReadOnlyCollection<ZipArchiveEntry> Entries => this._zip.Entries;

  private Aff4Volume(ZipArchive zip) {
    this._zip = zip;
    this._members = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
    foreach (var entry in zip.Entries) this._members.TryAdd(entry.FullName, entry);
    this.VolumeArn = this.ReadVolumeArn();
    this.Turtle = this.ReadText("information.turtle");
    var consumed = new HashSet<string>(StringComparer.Ordinal);
    this.Items = this.Turtle == null ? [] : this.BuildItems(Aff4Turtle.Parse(this.Turtle), consumed);
    this.ConsumedMembers = consumed;
  }

  /// <summary>Opens <paramref name="stream"/> as a ZIP volume; the caller owns both objects.</summary>
  public static Aff4Volume Open(Stream stream, out ZipArchive zip) {
    stream.Seek(0, SeekOrigin.Begin);
    zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    return new Aff4Volume(zip);
  }

  private string? ReadVolumeArn() {
    // AFF4 Standard v1.0 section 5.4: container.description, else the start of the ZIP comment.
    return FirstArn(this.ReadText("container.description")) ?? FirstArn(this._zip.Comment);

    static string? FirstArn(string? text) {
      var trimmed = text?.Trim();
      if (trimmed is null || !trimmed.StartsWith("aff4://", StringComparison.Ordinal)) return null;
      var end = trimmed.AsSpan().IndexOfAny("\r\n\t \0");
      return end < 0 ? trimmed : trimmed[..end];
    }
  }

  private string? ReadText(string member) {
    if (!this._members.TryGetValue(member, out var entry)) return null;
    using var source = entry.Open();
    using var reader = new StreamReader(source, new UTF8Encoding(false, false));
    return reader.ReadToEnd();
  }

  private List<Aff4LogicalItem> BuildItems(List<Aff4Triple> triples, HashSet<string> consumed) {
    var bySubject = new Dictionary<string, List<Aff4Triple>>(StringComparer.Ordinal);
    foreach (var t in triples) {
      if (!t.Subject.IsIri) continue;
      if (!bySubject.TryGetValue(t.Subject.Value, out var list)) bySubject[t.Subject.Value] = list = [];
      list.Add(t);
    }

    var items = new List<Aff4LogicalItem>();
    foreach (var (arn, props) in bySubject) {
      var types = props.Where(p => p.Predicate == Aff4Turtle.RdfType && p.Object.IsIri).Select(p => LocalName(p.Object.Value)).ToHashSet(StringComparer.Ordinal);
      var isFile = types.Contains("FileImage");
      var isFolder = !isFile && (types.Contains("Folder") || types.Contains("FolderImage"));
      if (!isFile && !isFolder) continue;
      var path = LogicalPath(props);
      if (path == null) continue;
      var lastWritten = ParseTimestamp(Literal(props, "lastWritten"));
      if (isFolder) {
        items.Add(new(arn, path, true, 0, lastWritten, Aff4Storage.ZipSegment, null, null, 0, 0, null));
        continue;
      }

      var size = long.TryParse(Literal(props, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : -1;
      var inline = Literal(props, "dataStream") ?? Literal(props, "dataSteam");
      if (types.Contains("ImageStream")) {
        var chunkSize = long.TryParse(Literal(props, "chunkSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) ? cs : 32768;
        var perSegment = long.TryParse(Literal(props, "chunksInSegment"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cis) ? cis : 1024;
        var compression = props.Where(p => LocalName(p.Predicate) is "compressionMethod" or "CompressionMethod").Select(p => (string?)p.Object.Value).FirstOrDefault();
        var bevyBase = this.ResolveMember(arn, "/00000000.index");
        if (bevyBase == null || size < 0 || chunkSize is <= 0 or > MaxChunkSize || perSegment <= 0) continue;
        for (var bevy = 0L; ; ++bevy) {
          var name = $"{bevyBase}/{bevy:D8}";
          if (!this._members.ContainsKey(name + ".index")) break;
          consumed.Add(name);
          consumed.Add(name + ".index");
        }
        items.Add(new(arn, path, false, size, lastWritten, Aff4Storage.ImageStream, bevyBase, null, chunkSize, perSegment, compression));
        continue;
      }
      if (types.Contains("Map")) continue; // not decoded; see the class remarks
      var member = this.ResolveMember(arn, null);
      if (member != null) {
        consumed.Add(member);
        items.Add(new(arn, path, false, this._members[member].Length, lastWritten, Aff4Storage.ZipSegment, member, null, 0, 0, null));
        continue;
      }
      if (inline != null) {
        try {
          var data = Convert.FromBase64String(inline);
          items.Add(new(arn, path, false, data.LongLength, lastWritten, Aff4Storage.InMetadata, null, data, 0, 0, null));
        } catch (FormatException) { /* not base64: no data to offer */ }
      }
    }
    return items;
  }

  /// <summary>
  /// The ZIP member (or member prefix, when <paramref name="probeSuffix"/> is given) that stores
  /// <paramref name="arn"/>, trying the AFF4-L unescaped ARN, then AFF4 Standard v1.0 relative
  /// addressing (with and without URL escaping), then the escaped foreign-ARN form.
  /// </summary>
  private string? ResolveMember(string arn, string? probeSuffix) {
    var candidates = new List<string> { arn };
    if (this.VolumeArn != null && arn.StartsWith(this.VolumeArn, StringComparison.Ordinal) && arn.Length > this.VolumeArn.Length) {
      var relative = arn[this.VolumeArn.Length..];
      if (relative.StartsWith('/')) relative = relative[1..];
      candidates.Add(relative);
      candidates.Add(Unescape(relative));
    }
    if (arn.StartsWith("aff4://", StringComparison.Ordinal)) {
      var escaped = "aff4%3A%2F%2F" + arn["aff4://".Length..];
      candidates.Add(escaped);
      candidates.Add(Unescape(arn));
    }
    foreach (var candidate in candidates)
      if (this._members.ContainsKey(candidate + (probeSuffix ?? string.Empty))) return candidate;
    return null;
  }

  private static string Unescape(string value) {
    try { return Uri.UnescapeDataString(value); } catch (UriFormatException) { return value; }
  }

  /// <summary>Copies the logical content of <paramref name="item"/> to <paramref name="target"/>.</summary>
  public void CopyTo(Aff4LogicalItem item, Stream target) {
    switch (item.Storage) {
      case Aff4Storage.ZipSegment: {
        using var source = this._members[item.Member!].Open();
        source.CopyTo(target);
        return;
      }
      case Aff4Storage.InMetadata:
        target.Write(item.InlineData);
        return;
      case Aff4Storage.ImageStream:
        this.CopyImageStream(item, target);
        return;
    }
  }

  private void CopyImageStream(Aff4LogicalItem item, Stream target) {
    var chunkSize = (int)item.ChunkSize;
    var remaining = item.Size;
    var method = CompressionOf(item.Compression);
    for (var bevy = 0L; remaining > 0; ++bevy) {
      var name = $"{item.Member}/{bevy:D8}";
      if (!this._members.TryGetValue(name + ".index", out var indexEntry) || !this._members.TryGetValue(name, out var bevyEntry))
        throw new InvalidDataException($"AFF4 ImageStream {item.Arn} is missing bevy {bevy}.");
      var index = ReadAll(indexEntry);
      var data = ReadAll(bevyEntry);
      // AFF4 Standard v1.0 section 3: index entries are (u64 bevy offset, u32 stored length).
      for (var i = 0; i + 12 <= index.Length && remaining > 0; i += 12) {
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(index.AsSpan(i));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i + 8));
        if (offset > (ulong)data.Length || length > (ulong)data.Length - offset)
          throw new InvalidDataException($"AFF4 ImageStream {item.Arn} chunk exceeds bevy {bevy}.");
        var stored = data.AsSpan((int)offset, (int)length);
        var chunk = length == chunkSize ? stored.ToArray() : Decompress(stored, chunkSize, method, item.Arn);
        // The final chunk is padded to the chunk size; aff4:size trims it.
        var take = (int)Math.Min(remaining, chunk.Length);
        target.Write(chunk, 0, take);
        remaining -= take;
      }
    }
  }

  private enum ChunkCompression { Stored, Snappy, Lz4, Deflate, Zlib, Unknown }

  private static ChunkCompression CompressionOf(string? iri) {
    if (iri == null) return ChunkCompression.Stored; // section 3.3: no method means stored chunks
    var lower = iri.ToLowerInvariant();
    if (lower.Contains("snappy", StringComparison.Ordinal)) return ChunkCompression.Snappy;
    if (lower.Contains("lz4", StringComparison.Ordinal)) return ChunkCompression.Lz4;
    if (lower.Contains("rfc1951", StringComparison.Ordinal)) return ChunkCompression.Deflate;
    if (lower.Contains("rfc1950", StringComparison.Ordinal) || lower.Contains("zlib", StringComparison.Ordinal)) return ChunkCompression.Zlib;
    if (lower.EndsWith("#nullcompressor", StringComparison.Ordinal) || lower.EndsWith("compression/stored", StringComparison.Ordinal)) return ChunkCompression.Stored;
    return ChunkCompression.Unknown;
  }

  private static byte[] Decompress(ReadOnlySpan<byte> stored, int chunkSize, ChunkCompression method, string arn) {
    switch (method) {
      case ChunkCompression.Stored:
        return stored.ToArray();
      case ChunkCompression.Snappy:
        return SnappyDecompressor.Decompress(stored);
      case ChunkCompression.Lz4: {
        var output = new byte[chunkSize];
        try {
          return output[..Lz4BlockDecompressor.Decompress(stored, output)];
        } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException && stored.Length > 4) {
          // python-lz4's block.compress prefixes the uncompressed size; pyaff4 writes that form.
          return output[..Lz4BlockDecompressor.Decompress(stored[4..], output)];
        }
      }
      case ChunkCompression.Deflate or ChunkCompression.Zlib: {
        using var input = new MemoryStream(stored.ToArray());
        using Stream inflater = method == ChunkCompression.Zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(chunkSize);
        inflater.CopyTo(output);
        return output.ToArray();
      }
      default:
        throw new NotSupportedException($"AFF4 ImageStream {arn} uses an unsupported compression method.");
    }
  }

  private static byte[] ReadAll(ZipArchiveEntry entry) {
    using var source = entry.Open();
    using var buffer = new MemoryStream(entry.Length is > 0 and < int.MaxValue ? (int)entry.Length : 0);
    source.CopyTo(buffer);
    return buffer.ToArray();
  }

  /// <summary>
  /// The logical path: aff4:originalPathNameRaw (base64) when present, else aff4:originalPathName,
  /// else the AFF4 v1.1 / pyaff4 aff4:originalFileName. Windows separators become '/', and a
  /// leading "./", "/" or drive colon is dropped, as pyaff4 does when extracting.
  /// </summary>
  private static string? LogicalPath(List<Aff4Triple> props) {
    string? path = null;
    var raw = Literal(props, "originalPathNameRaw");
    if (raw != null) {
      try { path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(raw)); } catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { path = null; }
    }
    path ??= Literal(props, "originalPathName") ?? Literal(props, "originalFileName");
    if (path == null) return null;
    path = path.Replace('\\', '/');
    while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
    if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) path = path[0] + path[2..];
    path = path.Trim('/');
    if (path.Length == 0 || path == ".") return null;
    return path.Split('/').Any(part => part is ".." or ".") ? System.IO.Path.GetFileName(path) : path;
  }

  private static string? Literal(List<Aff4Triple> props, string localName) {
    foreach (var p in props)
      if (p.Object.Kind == Aff4RdfNodeKind.Literal && LocalName(p.Predicate) == localName) return p.Object.Value;
    return null;
  }

  // Predicates and classes are matched by local name: AFF4 v1.0 (http://aff4.org/Schema#), the
  // AFF4-L draft namespace and the legacy pre-standard one name the same concepts.
  private static string LocalName(string iri) {
    var cut = Math.Max(iri.LastIndexOf('#'), iri.LastIndexOf('/'));
    return cut >= 0 ? iri[(cut + 1)..] : iri;
  }

  private static DateTimeOffset? ParseTimestamp(string? value)
    => value != null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
