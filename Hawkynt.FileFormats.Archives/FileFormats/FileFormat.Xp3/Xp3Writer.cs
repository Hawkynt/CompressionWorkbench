using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Deflate;
using FileFormat.Zlib;

namespace FileFormat.Xp3;

/// <summary>A segment to write: its bytes exactly as they go into the archive.</summary>
public sealed record Xp3WriteSegment(bool IsCompressed, long OriginalSize, byte[] Stored);

/// <summary>A member to write.</summary>
public sealed record Xp3WriteEntry(
  string Name, uint Flags, uint? Adler32, IReadOnlyList<Xp3WriteSegment> Segments, IReadOnlyList<Xp3Section> ExtraSections) {

  /// <summary>Builds a single-segment member from its content, zlib-compressing it when that saves space.</summary>
  public static Xp3WriteEntry FromData(string name, byte[] data, bool compress) {
    ArgumentNullException.ThrowIfNull(data);
    var segment = new Xp3WriteSegment(false, data.Length, data);
    if (compress && data.Length > 0) {
      var packed = ZlibStream.Compress(data, DeflateCompressionLevel.Best);
      if (packed.Length < data.Length)
        segment = new Xp3WriteSegment(true, data.Length, packed);
    }
    return new Xp3WriteEntry(name, 0, Compression.Core.Checksums.Adler32.Compute(data), [segment], []);
  }

  /// <summary>Carries a member of an existing archive across without re-encoding any segment.</summary>
  public static Xp3WriteEntry FromExisting(Xp3Reader reader, Xp3Entry entry) {
    ArgumentNullException.ThrowIfNull(reader);
    ArgumentNullException.ThrowIfNull(entry);
    return new Xp3WriteEntry(entry.Name, entry.Flags, entry.Adler32,
      [.. entry.Segments.Select(s => new Xp3WriteSegment(s.IsCompressed, s.OriginalSize, reader.ReadSegment(s)))],
      entry.ExtraSections);
  }
}

/// <summary>
/// Writes a KiriKiri XP3 archive the way the KiriKiri 2 Releaser lays one out: header, member
/// data in index order (identical segments stored once, as the Releaser does), then the index,
/// zlib-compressed or raw. <see cref="Xp3Reader"/> documents the layout.
/// </summary>
public static class Xp3Writer {

  /// <summary>Writes the archive.</summary>
  /// <param name="output">Destination.</param>
  /// <param name="entries">Members, in index order.</param>
  /// <param name="version">2 (default) writes KiriKiri 2.32+'s cushioned header; 1 the original one.</param>
  /// <param name="compressIndex">Whether to zlib-compress the index.</param>
  public static void Write(Stream output, IReadOnlyList<Xp3WriteEntry> entries, int version = 2, bool compressIndex = true) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(entries);
    if (version is not (1 or 2))
      throw new ArgumentOutOfRangeException(nameof(version), "XP3 header version is 1 or 2.");

    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var e in entries) {
      if (string.IsNullOrEmpty(e.Name) || e.Name.Length > ushort.MaxValue || e.Name.Contains('\0'))
        throw new ArgumentException($"Invalid XP3 member name '{e.Name}'.", nameof(entries));
      if (!names.Add(e.Name))
        throw new ArgumentException($"Duplicate XP3 member name '{e.Name}'.", nameof(entries));
    }

    using var image = new MemoryStream();
    image.Write(Xp3Reader.Magic);
    long indexOffsetField;
    if (version == 2) {
      WriteInt64(image, 0x17);        // -> the empty continuation index below
      WriteUInt32(image, 1);
      image.WriteByte(Xp3Reader.IndexContinue);
      WriteInt64(image, 0);           // its size
      indexOffsetField = image.Position;
      WriteInt64(image, 0);           // the real index offset, patched below
    } else {
      indexOffsetField = image.Position;
      WriteInt64(image, 0);
    }

    // Member data; byte-identical segments share one copy, as the Releaser stores duplicates.
    var placed = new Dictionary<string, long>(StringComparer.Ordinal);
    var offsets = new List<long[]>(entries.Count);
    foreach (var e in entries) {
      var segOffsets = new long[e.Segments.Count];
      for (var i = 0; i < e.Segments.Count; ++i) {
        var s = e.Segments[i];
        var key = (s.IsCompressed ? "z" : "s") + s.OriginalSize + ":" + Convert.ToHexString(SHA256.HashData(s.Stored));
        if (!placed.TryGetValue(key, out var at)) {
          at = image.Position;
          image.Write(s.Stored);
          placed[key] = at;
        }
        segOffsets[i] = at;
      }
      offsets.Add(segOffsets);
    }

    var index = BuildIndex(entries, offsets);
    var indexOffset = image.Position;
    if (compressIndex) {
      var packed = ZlibStream.Compress(index, DeflateCompressionLevel.Best);
      image.WriteByte(Xp3Reader.EncodeZlib);
      WriteInt64(image, packed.Length);
      WriteInt64(image, index.Length);
      image.Write(packed);
    } else {
      image.WriteByte(Xp3Reader.EncodeRaw);
      WriteInt64(image, index.Length);
      image.Write(index);
    }

    var bytes = image.GetBuffer().AsSpan(0, (int)image.Length);
    BinaryPrimitives.WriteInt64LittleEndian(bytes[(int)indexOffsetField..], indexOffset);
    output.Write(bytes);
  }

  private static byte[] BuildIndex(IReadOnlyList<Xp3WriteEntry> entries, List<long[]> offsets) {
    using var index = new MemoryStream();
    for (var n = 0; n < entries.Count; ++n) {
      var e = entries[n];
      using var file = new MemoryStream();

      var name = Encoding.Unicode.GetBytes(e.Name);
      WriteUInt32(file, Xp3Reader.InfoTag);
      WriteInt64(file, 22 + name.Length);
      WriteUInt32(file, e.Flags);
      WriteInt64(file, e.Segments.Sum(s => s.OriginalSize));
      WriteInt64(file, e.Segments.Sum(s => (long)s.Stored.Length));
      file.WriteByte((byte)e.Name.Length);
      file.WriteByte((byte)(e.Name.Length >> 8));
      file.Write(name);

      WriteUInt32(file, Xp3Reader.SegmTag);
      WriteInt64(file, 28L * e.Segments.Count);
      for (var i = 0; i < e.Segments.Count; ++i) {
        var s = e.Segments[i];
        WriteUInt32(file, s.IsCompressed ? (uint)Xp3Reader.EncodeZlib : (uint)Xp3Reader.EncodeRaw);
        WriteInt64(file, offsets[n][i]);
        WriteInt64(file, s.OriginalSize);
        WriteInt64(file, s.Stored.Length);
      }

      if (e.Adler32 is { } adler) {
        WriteUInt32(file, Xp3Reader.AdlrTag);
        WriteInt64(file, 4);
        WriteUInt32(file, adler);
      }
      foreach (var x in e.ExtraSections) {
        WriteUInt32(file, x.Tag);
        WriteInt64(file, x.Body.Length);
        file.Write(x.Body);
      }

      WriteUInt32(index, Xp3Reader.FileTag);
      WriteInt64(index, file.Length);
      file.Position = 0;
      file.CopyTo(index);
    }
    return index.ToArray();
  }

  private static void WriteUInt32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    s.Write(b);
  }

  private static void WriteInt64(Stream s, long v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(b, v);
    s.Write(b);
  }
}
