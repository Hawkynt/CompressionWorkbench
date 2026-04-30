#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Wbn;

/// <summary>
/// Read-only walker for Web Bundle (Bundled HTTP Exchanges) files. Validates the magic
/// prefix and uses a minimal CBOR walker to extract the version string, primary URL,
/// and resource count from the <c>index</c> section. Web Bundle b1 (no primary URL
/// element) and b2 (with primary URL) layouts are tolerated. The walker downgrades to
/// <see cref="ParseStatus"/> = "partial" if any structural surprise is encountered.
/// Resource bodies are not extracted — they live inside the responses section as
/// CBOR-encoded HTTP response triples and would require a full CBOR decoder plus an
/// HTTP framing pass to surface as individual files.
/// </summary>
public sealed class WbnReader {

  /// <summary>True if the leading 10 magic bytes matched the Web Bundle CBOR-array-of-4 + emoji-byte-string preamble.</summary>
  public bool MagicOk { get; }

  /// <summary>Parsed version tag — typically "b1" or "b2". "unknown" if the version field could not be decoded.</summary>
  public string Version { get; }

  /// <summary>Primary URL string for b2 bundles; "unknown" for b1 bundles or when extraction failed.</summary>
  public string PrimaryUrl { get; }

  /// <summary>Number of URL keys discovered in the <c>index</c> section. 0 when the section is absent or could not be walked.</summary>
  public int ResourceCount { get; }

  /// <summary>"full" when magic, version, and (for b2) primary URL plus index were all walked successfully. "partial" otherwise — never throws on structural mismatch.</summary>
  public string ParseStatus { get; }

  public WbnReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Seek(0, SeekOrigin.Begin);
    var magic = ReadExact(stream, WbnConstants.MagicLength);
    if (magic == null || !magic.AsSpan().SequenceEqual(WbnConstants.Magic))
      throw new InvalidDataException("Not a Web Bundle (bad magic).");

    MagicOk = true;
    Version = "unknown";
    PrimaryUrl = "unknown";
    ResourceCount = 0;
    ParseStatus = "partial";

    var versionBytes = TryReadVersionField(stream);
    if (versionBytes == null) return;
    Version = DecodeVersion(versionBytes);

    // The next outer-array element is either the primary URL (b2) or the section-lengths
    // byte string (b1). We probe the header once to decide.
    var probePos = stream.Position;
    if (!CborWalker.TryReadHeader(stream, out var nextHeader)) return;
    string? primaryUrl = null;
    if (nextHeader.MajorType == WbnConstants.MajorTypeTextString && !nextHeader.IsIndefinite) {
      var url = ReadTextStringBody(stream, nextHeader);
      if (url == null) return;
      primaryUrl = url;
    } else if (nextHeader.MajorType == WbnConstants.MajorTypeByteString) {
      // b1 layout: rewind so SectionLengths walker can consume this byte string.
      stream.Position = probePos;
    } else {
      return;
    }

    if (primaryUrl != null) PrimaryUrl = primaryUrl;

    var sectionLengths = ReadSectionLengthsBytes(stream);
    if (sectionLengths == null) return;

    var indexLength = TryFindIndexLength(sectionLengths);
    if (indexLength < 0) {
      // No index section in the table — sections-array still walks but resource count stays 0.
      ParseStatus = "full";
      return;
    }

    if (!CborWalker.TryReadHeader(stream, out var sectionsHeader)) return;
    if (sectionsHeader.MajorType != WbnConstants.MajorTypeArray || sectionsHeader.IsIndefinite) return;

    // Sections array elements appear in declared order. Find the index section's position
    // by counting which entry of the section-lengths table comes first; we assumed
    // index is at position 0 below (matches b2 conformant bundles), but we tolerate it
    // being elsewhere by walking sequentially until we hit a map-of-array-of-2-uints.
    var sectionsCount = sectionsHeader.Value;
    var indexCount = 0;
    var sawIndex = false;
    for (ulong i = 0; i < sectionsCount; i++) {
      if (!CborWalker.TryReadHeader(stream, out var inner)) return;
      if (!sawIndex && inner.MajorType == WbnConstants.MajorTypeMap && !inner.IsIndefinite) {
        if (TryWalkIndexMap(stream, inner.Value, out var keyCount)) {
          indexCount = keyCount;
          sawIndex = true;
          continue;
        }
        return;
      }
      // Skip any other section body without inspecting it.
      if (!CborWalker.TrySkip(stream, depth: 0))
        return;
    }

    ResourceCount = indexCount;
    ParseStatus = "full";
  }

  private static byte[]? TryReadVersionField(Stream stream) {
    if (!CborWalker.TryReadHeader(stream, out var header)) return null;
    if (header.MajorType != WbnConstants.MajorTypeByteString) return null;
    if (header.IsIndefinite) return null;
    if (header.Value > 64) return null;
    var bytes = ReadExact(stream, (int)header.Value);
    return bytes;
  }

  private static string DecodeVersion(byte[] bytes) {
    var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    return text.Length == 0 ? "unknown" : text;
  }

  private static string? ReadTextStringBody(Stream stream, in CborWalker.Header header) {
    if (header.Value > int.MaxValue) return null;
    var bytes = ReadExact(stream, (int)header.Value);
    return bytes == null ? null : Encoding.UTF8.GetString(bytes);
  }

  private static byte[]? ReadSectionLengthsBytes(Stream stream) {
    if (!CborWalker.TryReadHeader(stream, out var header)) return null;
    return CborWalker.TryReadByteStringRaw(stream, header);
  }

  /// <summary>
  /// Returns the declared length of the "index" section if it appears in the section-lengths
  /// CBOR byte string, or -1 if absent. The section-lengths byte string is itself a CBOR
  /// array alternating section-name strings and uint lengths.
  /// </summary>
  private static long TryFindIndexLength(byte[] sectionLengths) {
    using var ms = new MemoryStream(sectionLengths);
    if (!CborWalker.TryReadHeader(ms, out var arr)) return -1;
    if (arr.MajorType != WbnConstants.MajorTypeArray || arr.IsIndefinite) return -1;

    var pairs = arr.Value / 2;
    for (ulong i = 0; i < pairs; i++) {
      if (!CborWalker.TryReadHeader(ms, out var nameHeader)) return -1;
      if (nameHeader.MajorType != WbnConstants.MajorTypeTextString || nameHeader.IsIndefinite) return -1;
      var nameBytes = new byte[nameHeader.Value];
      var read = 0;
      while (read < nameBytes.Length) {
        var n = ms.Read(nameBytes, read, nameBytes.Length - read);
        if (n <= 0) return -1;
        read += n;
      }
      var name = Encoding.UTF8.GetString(nameBytes);

      if (!CborWalker.TryReadHeader(ms, out var lenHeader)) return -1;
      if (lenHeader.MajorType != WbnConstants.MajorTypeUnsignedInt || lenHeader.IsIndefinite) return -1;
      if (string.Equals(name, "index", StringComparison.Ordinal))
        return (long)Math.Min(lenHeader.Value, long.MaxValue);
    }
    return -1;
  }

  private static bool TryWalkIndexMap(Stream stream, ulong pairCount, out int keyCount) {
    keyCount = 0;
    for (ulong i = 0; i < pairCount; i++) {
      if (!CborWalker.TryReadHeader(stream, out var keyHeader)) return false;
      if (keyHeader.MajorType != WbnConstants.MajorTypeTextString || keyHeader.IsIndefinite) return false;
      if (keyHeader.Value > int.MaxValue) return false;
      stream.Position += (long)keyHeader.Value;
      if (!CborWalker.TrySkip(stream, depth: 0)) return false;
      keyCount++;
    }
    return true;
  }

  private static byte[]? ReadExact(Stream stream, int count) {
    var buf = new byte[count];
    var read = 0;
    while (read < count) {
      var n = stream.Read(buf, read, count - read);
      if (n <= 0) return null;
      read += n;
    }
    return buf;
  }
}
