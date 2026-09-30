#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Ktx2;

/// <summary>
/// Surfaces a Khronos KTX2 texture container as a read-only pseudo-archive of its
/// own structural elements: the verbatim file, parsed header metadata, each mip
/// level's raw (possibly supercompressed) image blob and the key/value metadata.
/// Works purely from the byte layout — it never transcodes Basis/Zstd/ZLIB level
/// data and never throws from listing (malformed input yields FULL + partial meta).
/// </summary>
public static class Ktx2Decomposer {

  /// <summary>
  /// Represents an entry kinds.
  /// </summary>
  public static class EntryKinds {
    /// <summary>
    /// Defines the track constant value.
    /// </summary>
    public const string Track = "Track";
    /// <summary>
    /// Defines the tag constant value.
    /// </summary>
    public const string Tag = "Tag";
    /// <summary>
    /// Defines the frame constant value.
    /// </summary>
    public const string Frame = "Frame";
  }

  /// <summary>
  /// Represents an entry.
  /// </summary>
  public readonly record struct Entry(string Name, byte[] Data, string Kind);

  internal readonly record struct LayoutEntry(
    string Name,
    string Kind,
    int Offset,
    int Length,
    byte[]? GeneratedData = null) {
    public long Size => this.GeneratedData?.LongLength ?? this.Length;
  }

  internal sealed record Layout(IReadOnlyList<LayoutEntry> Entries);

  /// <summary>The 12-byte KTX2 file identifier «KTX 20»\r\n\x1A\n.</summary>
  public static ReadOnlySpan<byte> Identifier =>
    [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

  private static readonly string[] Supercompression =
    ["none", "BasisLZ", "Zstandard", "ZLIB"];

  /// <summary>
  /// Performs the decompose operation.
  /// </summary>
  public static List<Entry> Decompose(byte[] file) {
    ArgumentNullException.ThrowIfNull(file);

    var layout = DecomposeLayout(file);
    var entries = new List<Entry>(layout.Entries.Count);
    foreach (var entry in layout.Entries) {
      var data = entry.GeneratedData
        ?? (entry.Offset == 0 && entry.Length == file.Length
          ? file
          : file.AsSpan(entry.Offset, entry.Length).ToArray());
      entries.Add(new Entry(entry.Name, data, entry.Kind));
    }

    return entries;
  }

  internal static Layout DecomposeLayout(ReadOnlySpan<byte> file) {
    var entries = new List<LayoutEntry> {
      new("FULL.ktx2", EntryKinds.Track, 0, file.Length),
    };
    var meta = new IniBuilder("ktx2");
    var ok = false;
    var kvd = new IniBuilder("key_value_data");
    var hasKvd = false;

    try {
      if (file.Length >= 80 && file[..12].SequenceEqual(Identifier)) {
        var vkFormat = BinaryPrimitives.ReadUInt32LittleEndian(file[12..16]);
        var typeSize = BinaryPrimitives.ReadUInt32LittleEndian(file[16..20]);
        var pixelWidth = BinaryPrimitives.ReadUInt32LittleEndian(file[20..24]);
        var pixelHeight = BinaryPrimitives.ReadUInt32LittleEndian(file[24..28]);
        var pixelDepth = BinaryPrimitives.ReadUInt32LittleEndian(file[28..32]);
        var layerCount = BinaryPrimitives.ReadUInt32LittleEndian(file[32..36]);
        var faceCount = BinaryPrimitives.ReadUInt32LittleEndian(file[36..40]);
        var levelCount = BinaryPrimitives.ReadUInt32LittleEndian(file[40..44]);
        var scheme = BinaryPrimitives.ReadUInt32LittleEndian(file[44..48]);

        meta.Add("vk_format", vkFormat);
        meta.Add("type_size", typeSize);
        meta.Add("pixel_width", pixelWidth);
        meta.Add("pixel_height", pixelHeight);
        meta.Add("pixel_depth", pixelDepth);
        meta.Add("layer_count", layerCount);
        meta.Add("face_count", faceCount);
        meta.Add("level_count", levelCount);
        meta.Add("supercompression_scheme",
          scheme < Supercompression.Length ? Supercompression[scheme] : $"unknown({scheme})");

        var dfdByteOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[48..52]);
        var dfdByteLength = BinaryPrimitives.ReadUInt32LittleEndian(file[52..56]);
        var kvdByteOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[56..60]);
        var kvdByteLength = BinaryPrimitives.ReadUInt32LittleEndian(file[60..64]);
        var sgdByteLength = BinaryPrimitives.ReadUInt64LittleEndian(file[72..80]);

        var levels = levelCount == 0 ? 1u : levelCount;
        const int indexPos = 80;
        var indexBytes = (ulong)levels * 24;
        if (indexBytes <= (ulong)(file.Length - indexPos)) {
          for (var i = 0u; i < levels; ++i) {
            var off = indexPos + checked((int)(i * 24));
            var byteOffset = BinaryPrimitives.ReadUInt64LittleEndian(file[off..(off + 8)]);
            var byteLength = BinaryPrimitives.ReadUInt64LittleEndian(file[(off + 8)..(off + 16)]);
            var uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(file[(off + 16)..(off + 24)]);
            if (TryRange(byteOffset, byteLength, file.Length, out var levelOffset, out var levelLength))
              entries.Add(new LayoutEntry(
                $"levels/level_{i:D2}.bin", EntryKinds.Frame, levelOffset, levelLength));
            meta.Add($"level_{i}_uncompressed_length", (long)uncompressed);
          }

          if (dfdByteLength > 0 &&
              TryRange(dfdByteOffset, dfdByteLength, file.Length, out var dfdOffset, out var dfdLength))
            entries.Add(new LayoutEntry("dfd.bin", EntryKinds.Tag, dfdOffset, dfdLength));

          if (kvdByteLength > 0 &&
              TryRange(kvdByteOffset, kvdByteLength, file.Length, out var kvdOffset, out var kvdLength)) {
            hasKvd = ParseKeyValue(file.Slice(kvdOffset, kvdLength), kvd);
            entries.Add(new LayoutEntry("kvd.bin", EntryKinds.Tag, kvdOffset, kvdLength));
          }

          meta.Add("supercompression_global_length", (long)sgdByteLength);
          ok = true;
        }
      }
    } catch {
      // Keep FULL + partial metadata.
    }

    meta.AddStatus(ok);
    entries.Insert(1, new LayoutEntry(
      "metadata.ini", EntryKinds.Tag, 0, 0, meta.ToBytes()));
    if (hasKvd)
      entries.Add(new LayoutEntry(
        "kvd.ini", EntryKinds.Tag, 0, 0, kvd.ToBytes()));

    return new Layout(entries);
  }

  private static bool TryRange(
      ulong offset, ulong length, int fileLength, out int rangeOffset, out int rangeLength) {
    if (offset > (ulong)fileLength || length > (ulong)fileLength - offset ||
        offset > int.MaxValue || length > int.MaxValue) {
      rangeOffset = 0;
      rangeLength = 0;
      return false;
    }

    rangeOffset = (int)offset;
    rangeLength = (int)length;
    return true;
  }

  private static bool ParseKeyValue(ReadOnlySpan<byte> data, IniBuilder kvd) {
    var pos = 0;
    var any = false;
    while (pos + 4 <= data.Length) {
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
      pos += 4;
      if (len <= 0 || pos + len > data.Length) break;
      var pair = data.Slice(pos, len);
      var nul = pair.IndexOf((byte)0);
      if (nul >= 0) {
        var key = Encoding.UTF8.GetString(pair[..nul]);
        var rawVal = pair[(nul + 1)..];
        // Values are often UTF-8 strings (sometimes NUL-terminated); fall back to hex.
        var value = LooksTextual(rawVal)
          ? Encoding.UTF8.GetString(rawVal).TrimEnd('\0')
          : Convert.ToHexString(rawVal);
        kvd.Add(SanitizeKey(key), value);
        any = true;
      }
      pos += len;
      // Each entry is padded with NUL to a 4-byte boundary.
      pos = (pos + 3) & ~3;
    }
    return any;
  }

  private static bool LooksTextual(ReadOnlySpan<byte> bytes) {
    foreach (var b in bytes)
      if (b != 0 && b < 0x09) return false;
    return true;
  }

  private static string SanitizeKey(string key) {
    Span<char> buf = stackalloc char[Math.Max(1, key.Length)];
    for (var i = 0; i < key.Length; i++) {
      var c = key[i];
      buf[i] = char.IsLetterOrDigit(c) ? c : '_';
    }
    return key.Length == 0 ? "_" : new string(buf[..key.Length]);
  }

  private sealed class IniBuilder(string section) {
    private readonly StringBuilder _sb = new StringBuilder().AppendLine($"[{section}]");
    public void Add(string key, long value) => _sb.Append(CultureInfo.InvariantCulture, $"{key} = {value}\n");
    public void Add(string key, string value) => _sb.Append(CultureInfo.InvariantCulture, $"{key} = {value}\n");
    public void AddStatus(bool ok) {
      if (!ok) _sb.Append("parse_status = partial\n");
    }
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(_sb.ToString());
  }
}
