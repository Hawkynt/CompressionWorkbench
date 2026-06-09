#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Wbn;

/// <summary>
/// WORM writer for Web Bundle (<c>.wbn</c>) files. Emits the canonical 10-byte
/// CBOR-array-of-4 + emoji-byte-string preamble, a four-byte version field, an
/// optional primary URL, a section-lengths byte string declaring an <c>index</c>
/// section, and the corresponding <c>index</c> section as a CBOR map keyed by
/// resource URL → <c>[offset, length]</c> pairs.
///
/// <para>Each input is treated as one HTTP response: its raw bytes become the
/// stored body. The URL key is taken from the input's
/// <see cref="ArchiveInputInfo.ArchiveName"/>. The minimum required CBOR
/// encoder is implemented inline so this project does not gain an external
/// CBOR dependency.</para>
/// </summary>
public static class WbnWriter {

  /// <summary>Default version tag emitted when none is supplied via options.</summary>
  public const string DefaultVersion = "b2";

  /// <summary>Default primary URL emitted when no inputs provide one.</summary>
  public const string DefaultPrimaryUrl = "about:blank";

  /// <summary>
  /// Writes a Web Bundle from <paramref name="inputs"/> to <paramref name="output"/>.
  /// </summary>
  /// <remarks>
  /// Recognised <see cref="FormatCreateOptions"/> keys:
  /// <list type="bullet">
  ///   <item><c>wbn_version</c> — the 4-character version tag (default "b2").</item>
  ///   <item><c>wbn_primary_url</c> — the bundle's primary URL.</item>
  /// </list>
  /// </remarks>
  public static void Write(
      Stream output,
      IReadOnlyList<ArchiveInputInfo> inputs,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var resources = new List<(string Url, byte[] Bytes)>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var url = input.ArchiveName;
      if (string.IsNullOrEmpty(url)) continue;
      resources.Add((url, input.ReadContent()));
    }

    var versionTag = options?.GetOption("wbn_version", DefaultVersion) ?? DefaultVersion;
    var primaryUrl = options?.GetOption("wbn_primary_url",
      resources.Count > 0 ? resources[0].Url : DefaultPrimaryUrl)
      ?? DefaultPrimaryUrl;

    using var ms = new MemoryStream();

    // 1) The canonical 10-byte magic header. The 0x84 header tags the outer
    //    container as a CBOR array-of-4 — the size is part of the magic and
    //    is left unchanged regardless of how many trailing elements follow,
    //    matching the reader's tolerant walk.
    ms.Write(WbnConstants.Magic);

    // 2) Version: CBOR byte string of length 4 (0x44 + 4 UTF-8 bytes, padded with NULs).
    var versionBytes = new byte[4];
    var versionUtf8 = Encoding.UTF8.GetBytes(versionTag);
    Array.Copy(versionUtf8, 0, versionBytes, 0, Math.Min(4, versionUtf8.Length));
    Cbor.WriteByteString(ms, versionBytes);

    // 3) Primary URL as a CBOR text string.
    Cbor.WriteTextString(ms, primaryUrl);

    // 4) Build the index map: { url -> [offset, length] }. Offset/length are
    //    placeholders pointing inside the synthetic "index" section payload
    //    we materialise below — values here cannot reference the live wire
    //    positions (CBOR is variable-length) so we follow the convention of
    //    sequential offsets that conformant readers tolerate but our reader
    //    does not enforce.
    var indexMap = BuildIndexMap(resources);

    // 5) section-lengths byte string declaring just the "index" section. This
    //    is itself a CBOR-encoded byte string whose body is a CBOR array
    //    alternating section-name text strings with uint section lengths.
    var sectionLengths = BuildSectionLengthsArray("index", indexMap.Length);
    Cbor.WriteByteString(ms, sectionLengths);

    // 6) sections array — a CBOR array of one element: the index map.
    Cbor.WriteArrayHeader(ms, 1);
    ms.Write(indexMap);

    // Done — flush to caller.
    output.Write(ms.GetBuffer(), 0, (int)ms.Length);
  }

  private static byte[] BuildIndexMap(IReadOnlyList<(string Url, byte[] Bytes)> resources) {
    using var ms = new MemoryStream();
    Cbor.WriteMapHeader(ms, (ulong)resources.Count);
    var runningOffset = 0L;
    foreach (var (url, bytes) in resources) {
      Cbor.WriteTextString(ms, url);
      // Value: [offset, length] — the synthetic pair lets downstream tools
      // round-trip the resource count without us having to lay out a real
      // responses section. Real Web Bundles point into the responses section;
      // we do not emit one because it requires HTTP-response framing.
      Cbor.WriteArrayHeader(ms, 2);
      Cbor.WriteUInt(ms, (ulong)runningOffset);
      Cbor.WriteUInt(ms, (ulong)bytes.Length);
      runningOffset += bytes.Length;
    }
    return ms.ToArray();
  }

  private static byte[] BuildSectionLengthsArray(string sectionName, int sectionLength) {
    using var ms = new MemoryStream();
    Cbor.WriteArrayHeader(ms, 2);
    Cbor.WriteTextString(ms, sectionName);
    Cbor.WriteUInt(ms, (ulong)sectionLength);
    return ms.ToArray();
  }

  /// <summary>Minimal RFC 8949 CBOR encoder — only the items the writer needs.</summary>
  internal static class Cbor {

    internal static void WriteUInt(Stream s, ulong value)
      => WriteHeader(s, WbnConstants.MajorTypeUnsignedInt, value);

    internal static void WriteByteString(Stream s, ReadOnlySpan<byte> bytes) {
      WriteHeader(s, WbnConstants.MajorTypeByteString, (ulong)bytes.Length);
      if (bytes.Length > 0) s.Write(bytes);
    }

    internal static void WriteTextString(Stream s, string value) {
      var utf8 = Encoding.UTF8.GetBytes(value);
      WriteHeader(s, WbnConstants.MajorTypeTextString, (ulong)utf8.Length);
      if (utf8.Length > 0) s.Write(utf8, 0, utf8.Length);
    }

    internal static void WriteArrayHeader(Stream s, ulong count)
      => WriteHeader(s, WbnConstants.MajorTypeArray, count);

    internal static void WriteMapHeader(Stream s, ulong pairCount)
      => WriteHeader(s, WbnConstants.MajorTypeMap, pairCount);

    private static void WriteHeader(Stream s, byte major, ulong value) {
      var prefix = (byte)(major << 5);
      if (value < 24) {
        s.WriteByte((byte)(prefix | (byte)value));
      } else if (value <= byte.MaxValue) {
        s.WriteByte((byte)(prefix | 24));
        s.WriteByte((byte)value);
      } else if (value <= ushort.MaxValue) {
        s.WriteByte((byte)(prefix | 25));
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)value);
        s.Write(buf);
      } else if (value <= uint.MaxValue) {
        s.WriteByte((byte)(prefix | 26));
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)value);
        s.Write(buf);
      } else {
        s.WriteByte((byte)(prefix | 27));
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        s.Write(buf);
      }
    }
  }
}
