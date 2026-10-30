using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using FileFormat.Zlib;

namespace FileFormat.LiveMaker;

/// <summary>A member to write: its bytes as they go into the archive plus the directory fields.</summary>
public sealed record LiveMakerWriteEntry(string Name, byte[] Stored, LiveMakerMethod Method, uint Unknown, uint Checksum, byte EncryptFlag) {

  /// <summary>Builds a member from content: zlib when that is smaller (and allowed), else stored.</summary>
  public static LiveMakerWriteEntry FromData(string name, byte[] data, bool compress) {
    ArgumentNullException.ThrowIfNull(data);
    if (compress && data.Length > 0) {
      var packed = ZlibStream.Compress(data, DeflateCompressionLevel.Default);
      if (packed.Length < data.Length)
        return new LiveMakerWriteEntry(name, packed, LiveMakerMethod.Zlib, 0, Crc32.Compute(packed), 0);
    }
    return new LiveMakerWriteEntry(name, data, LiveMakerMethod.Stored, 0, Crc32.Compute(data), 0);
  }
}

/// <summary>
/// Writes a standalone LiveMaker "VF" archive (<c>game.dat</c> layout, no executable, no trailer) as
/// LiveMaker writes its own: the directory, then member data in directory order.
/// <see cref="LiveMakerReader"/> documents the layout.
/// </summary>
public static class LiveMakerWriter {

  /// <summary>Writes the archive.</summary>
  public static void Write(Stream output, IReadOnlyList<LiveMakerWriteEntry> entries, uint version = LiveMakerReader.MaxVersion) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(entries);
    if (version is < LiveMakerReader.MinVersion or > LiveMakerReader.MaxVersion)
      throw new ArgumentOutOfRangeException(nameof(version), "LiveMaker archive version is 100 to 102.");

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var names = new List<byte[]>(entries.Count);
    foreach (var e in entries) {
      if (string.IsNullOrEmpty(e.Name) || !seen.Add(e.Name))
        throw new ArgumentException($"Invalid or duplicate LiveMaker member name '{e.Name}'.", nameof(entries));
      byte[] bytes;
      try {
        bytes = LiveMakerReader.Names.GetBytes(e.Name.Replace('/', '\\'));
      } catch (System.Text.EncoderFallbackException) {
        throw new ArgumentException($"LiveMaker member name '{e.Name}' has no Shift-JIS (CP932) spelling.", nameof(entries));
      }
      names.Add(bytes);
    }

    var directorySize = 2 + 4 + 4 + names.Sum(n => 4L + n.Length) + 8L * (entries.Count + 1) + 10L * entries.Count;
    using var dir = new MemoryStream();
    dir.Write("vf"u8);
    WriteUInt32(dir, version);
    WriteUInt32(dir, (uint)entries.Count);

    var key = new LiveMakerReader.TpRandom(LiveMakerReader.XorSeed);
    foreach (var n in names) {
      WriteUInt32(dir, (uint)n.Length);
      foreach (var b in n)
        dir.WriteByte((byte)(b ^ (byte)key.Next()));
    }

    key = new LiveMakerReader.TpRandom(LiveMakerReader.XorSeed);
    var offset = directorySize;
    for (var i = 0; i <= entries.Count; ++i) {
      var k = key.Next();
      WriteUInt32(dir, (uint)offset ^ k);
      var high = (uint)(offset >> 32);
      if (version >= 101)
        high ^= (k & 0x80000000) != 0 ? 0xFFFFFFFFu : 0u;
      WriteUInt32(dir, high);
      if (i < entries.Count)
        offset += entries[i].Stored.Length;
    }

    foreach (var e in entries) dir.WriteByte((byte)e.Method);
    foreach (var e in entries) WriteUInt32(dir, e.Unknown);
    foreach (var e in entries) WriteUInt32(dir, e.Checksum);
    foreach (var e in entries) dir.WriteByte(e.EncryptFlag);

    if (dir.Length != directorySize)
      throw new InvalidOperationException("LiveMaker directory size mismatch.");
    dir.Position = 0;
    dir.CopyTo(output);
    foreach (var e in entries)
      output.Write(e.Stored);
  }

  private static void WriteUInt32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    s.Write(b);
  }
}
