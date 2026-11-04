// Apple Archive item parsing, converted from libNeoAppleArchive
// (https://github.com/0xilis/libNeoAppleArchive, MIT, Copyright (c) 2024 Snoolie Keffaber).
// See THIRD-PARTY-NOTICE.libNeoAppleArchive.txt.

using System.Buffers.Binary;
using System.Text;

namespace FileFormat.AppleArchive;

/// <summary>
/// Reads an Apple Archive: a plain AA01/YAA1 item stream, or one wrapped in a pbz (LZFSE, zlib,
/// xz, LZ4 or LZBITMAP) container.
/// </summary>
public sealed class AppleArchiveReader {
  private readonly byte[] _plain;
  private readonly List<AppleArchiveEntry> _entries = [];

  /// <summary>Gets the items in archive order.</summary>
  public IReadOnlyList<AppleArchiveEntry> Entries => this._entries;

  /// <summary>Gets the container compression: "raw", "lzfse", "zlib", "lzma", "lz4" or "lzbitmap".</summary>
  public string Compression { get; }

  /// <summary>Gets whether an item header did not parse; <see cref="Entries"/> holds those before it.</summary>
  public bool IsTruncated { get; private set; }

  /// <summary>Gets the decoded (plain) item stream.</summary>
  internal ReadOnlySpan<byte> Plain => this._plain;

  /// <summary>Gets the byte range of each item (header and blobs) in <see cref="Plain"/>.</summary>
  internal IReadOnlyList<(int Offset, int Length)> ItemRanges => this._ranges;

  private readonly List<(int Offset, int Length)> _ranges = [];

  /// <summary>Reads an Apple Archive from <paramref name="stream"/> (from offset 0).</summary>
  /// <exception cref="InvalidDataException">The stream is neither an Apple Archive nor a pbz container.</exception>
  public AppleArchiveReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();

    if (IsPlainMagic(file)) {
      this._plain = file;
      this.Compression = "raw";
    } else if (AppleArchiveContainer.IsContainer(file)) {
      this.Compression = AppleArchiveContainer.Algorithm(file) switch {
        'e' => "lzfse", 'z' => "zlib", 'x' => "lzma", '4' => "lz4", 'b' => "lzbitmap",
        var c => $"pbz{c}",
      };
      this._plain = AppleArchiveContainer.Decode(file);
    } else
      throw new InvalidDataException("Not an Apple Archive (no AA01/YAA1 or pbz signature).");

    this._Parse();
  }

  internal static bool IsPlainMagic(ReadOnlySpan<byte> data) =>
    data.Length >= 4 && (data[..4].SequenceEqual("AA01"u8) || data[..4].SequenceEqual("YAA1"u8));

  private void _Parse() {
    var position = 0;
    while (position < this._plain.Length) {
      var entry = TryParseItem(this._plain, position, out var length);
      if (entry == null) {
        this.IsTruncated = true;
        return;
      }

      this._entries.Add(entry);
      this._ranges.Add((position, length));
      position += length;
    }
  }

  // Size of the value that follows a field key, by subtype; -1 for a string ('P', u16-prefixed).
  private static int ValueSize(byte subtype) => subtype switch {
    (byte)'*' => 0,
    (byte)'1' => 1, (byte)'2' => 2, (byte)'4' => 4, (byte)'8' => 8,
    (byte)'A' => 2, (byte)'B' => 4, (byte)'C' => 8,
    (byte)'F' => 4, (byte)'G' => 20, (byte)'H' => 32, (byte)'I' => 48, (byte)'J' => 64,
    (byte)'S' => 8, (byte)'T' => 12,
    (byte)'P' => -1,
    _ => -2,
  };

  private static ulong ReadUInt(ReadOnlySpan<byte> value) {
    ulong v = 0;
    for (var i = value.Length - 1; i >= 0; --i)
      v = (v << 8) | value[i];
    return v;
  }

  /// <summary>Parses one item at <paramref name="offset"/>; null when it is malformed or truncated.</summary>
  internal static AppleArchiveEntry? TryParseItem(ReadOnlySpan<byte> data, int offset, out int itemLength) {
    itemLength = 0;
    if (data.Length - offset < 6 || !IsPlainMagic(data[offset..]))
      return null;
    var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
    if (headerLength < 6 || headerLength > data.Length - offset)
      return null;

    var header = data.Slice(offset, headerLength);
    var keys = new List<string>();
    string path = string.Empty, link = null!;
    var type = 'F';
    long blobs = 0, dataBlob = -1, dataSize = 0;
    int? mode = null;
    long? uid = null, gid = null;
    DateTime? modified = null;
    var hasLink = false;

    for (var p = 6; p < headerLength;) {
      if (headerLength - p < 4)
        return null;
      var key = Encoding.ASCII.GetString(header.Slice(p, 3));
      var subtype = header[p + 3];
      p += 4;
      var size = ValueSize(subtype);
      if (size == -2)
        return null;

      if (size == -1) {
        if (headerLength - p < 2)
          return null;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header[p..]);
        p += 2;
        if (headerLength - p < length)
          return null;
        var text = Encoding.UTF8.GetString(header.Slice(p, length));
        p += length;
        if (key == "PAT") path = text;
        else if (key == "LNK") { link = text; hasLink = true; }
        keys.Add(key);
        continue;
      }

      if (headerLength - p < size)
        return null;
      var value = header.Slice(p, size);
      p += size;
      keys.Add(key);

      if (subtype is (byte)'A' or (byte)'B' or (byte)'C') {
        var blob = (long)ReadUInt(value);
        if (blob < 0)
          return null;
        if (key == "DAT") {
          dataBlob = blobs;
          dataSize = blob;
        }

        blobs += blob;
        continue;
      }

      switch (key) {
        case "TYP" when subtype is (byte)'1':
          type = (char)value[0];
          break;
        case "MOD" when size <= 8 && subtype is >= (byte)'1' and <= (byte)'8':
          mode = (int)ReadUInt(value);
          break;
        case "UID" when subtype is >= (byte)'1' and <= (byte)'8':
          uid = (long)ReadUInt(value);
          break;
        case "GID" when subtype is >= (byte)'1' and <= (byte)'8':
          gid = (long)ReadUInt(value);
          break;
        case "MTM" when subtype is (byte)'S' or (byte)'T':
          var seconds = BinaryPrimitives.ReadInt64LittleEndian(value);
          var nanos = subtype == (byte)'T' ? BinaryPrimitives.ReadUInt32LittleEndian(value[8..]) : 0u;
          if (seconds is > -62135596800 and < 253402300799)
            modified = DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100);
          break;
      }
    }

    if (blobs > data.Length - offset - headerLength)
      return null;
    itemLength = headerLength + (int)blobs;
    return new AppleArchiveEntry {
      Path = path,
      Type = type,
      LinkTarget = hasLink ? link : null,
      Size = dataSize,
      Mode = mode,
      Uid = uid,
      Gid = gid,
      LastModified = modified,
      FieldKeys = keys,
      DataOffset = dataBlob < 0 ? -1 : offset + headerLength + dataBlob,
    };
  }

  /// <summary>Returns a file item's data (empty for items without a DAT blob).</summary>
  /// <exception cref="NotSupportedException">The item is not a regular file.</exception>
  public byte[] Extract(AppleArchiveEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Type != 'F')
      throw new NotSupportedException($"Apple Archive item '{entry.Path}' is of type '{entry.Type}', not a file.");
    return entry.DataOffset < 0 ? [] : this._plain.AsSpan((int)entry.DataOffset, (int)entry.Size).ToArray();
  }
}
