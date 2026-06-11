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

  public static class EntryKinds {
    public const string Track = "Track";
    public const string Tag = "Tag";
    public const string Frame = "Frame";
  }

  public readonly record struct Entry(string Name, byte[] Data, string Kind);

  /// <summary>The 12-byte KTX2 file identifier «KTX 20»\r\n\x1A\n.</summary>
  public static ReadOnlySpan<byte> Identifier =>
    [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

  private static readonly string[] Supercompression =
    ["none", "BasisLZ", "Zstandard", "ZLIB"];

  public static List<Entry> Decompose(byte[] file) {
    var entries = new List<Entry> { new("FULL.ktx2", file, EntryKinds.Track) };
    var meta = new IniBuilder("ktx2");
    var ok = false;
    var kvd = new IniBuilder("key_value_data");
    var hasKvd = false;

    try {
      if (file.Length >= 80 && file.AsSpan(0, 12).SequenceEqual(Identifier)) {
        var vkFormat = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12, 4));
        var typeSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16, 4));
        var pixelWidth = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20, 4));
        var pixelHeight = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24, 4));
        var pixelDepth = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(28, 4));
        var layerCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32, 4));
        var faceCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(36, 4));
        var levelCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40, 4));
        var scheme = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(44, 4));

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

        // Section index (offsets 48..79): DFD/KVD/SGD offsets+lengths.
        var dfdByteOffset = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48, 4));
        var dfdByteLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(52, 4));
        var kvdByteOffset = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(56, 4));
        var kvdByteLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(60, 4));
        var sgdByteLength = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(72, 8));

        // KTX2 stores at least one level even when levelCount == 0 (treated as 1).
        var levels = levelCount == 0 ? 1u : levelCount;
        var indexPos = 80;
        var indexBytes = (long)levels * 24;
        if (indexPos + indexBytes <= file.Length) {
          for (var i = 0u; i < levels; i++) {
            var off = indexPos + (int)(i * 24);
            var byteOffset = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(off, 8));
            var byteLength = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(off + 8, 8));
            var uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(off + 16, 8));
            if (byteOffset <= (ulong)file.Length && byteLength <= (ulong)file.Length &&
                byteOffset + byteLength <= (ulong)file.Length) {
              var blob = file.AsSpan((int)byteOffset, (int)byteLength).ToArray();
              entries.Add(new($"levels/level_{i:D2}.bin", blob, EntryKinds.Frame));
            }
            meta.Add($"level_{i}_uncompressed_length", (long)uncompressed);
          }

          // Data format descriptor blob.
          if (dfdByteLength > 0 && dfdByteOffset + dfdByteLength <= file.Length)
            entries.Add(new("dfd.bin",
              file.AsSpan((int)dfdByteOffset, (int)dfdByteLength).ToArray(), EntryKinds.Tag));

          // Key/value metadata: a run of [keyAndValueByteLength u32][key\0value] entries.
          if (kvdByteLength > 0 && kvdByteOffset + kvdByteLength <= file.Length) {
            hasKvd = ParseKeyValue(file.AsSpan((int)kvdByteOffset, (int)kvdByteLength), kvd);
            entries.Add(new("kvd.bin",
              file.AsSpan((int)kvdByteOffset, (int)kvdByteLength).ToArray(), EntryKinds.Tag));
          }

          meta.Add("supercompression_global_length", (long)sgdByteLength);
          ok = true;
        }
      }
    } catch { /* fall through to partial */ }

    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    if (hasKvd)
      entries.Add(new("kvd.ini", kvd.ToBytes(), EntryKinds.Tag));
    return entries;
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
