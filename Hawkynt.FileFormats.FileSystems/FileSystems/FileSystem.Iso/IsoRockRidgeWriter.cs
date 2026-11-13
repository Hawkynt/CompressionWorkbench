using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using FileFormat.Zlib;

namespace FileSystem.Iso;

/// <summary>
/// What a Rock Ridge (RRIP 1.10 / IEEE P1282) record carries: its type and mode (PX), time
/// (TF), name (NM), a symbolic link's target (SL), directory relocation (RE, CL, PL) and zisofs
/// compression (ZF).
/// </summary>
internal sealed record RockRidgeRecord(
  char Kind,                 // 'd' directory, 'f' file, 'l' symbolic link
  int? Mode,
  DateTime? Modified,
  string? Name,
  string? SymlinkTarget = null,
  bool Relocated = false,    // RE: this directory was moved here from too deep a place
  int? ChildLink = null,     // CL: this placeholder is the directory at that block
  int? ParentLink = null,    // PL: in a relocated directory's "..", its real parent's block
  long? ZisofsSize = null);  // ZF: the file data is zisofs, this many bytes uncompressed

/// <summary>
/// Builds the System Use entries of a Rock Ridge record. The fixed entries (PX, TF, RE, CL, PL,
/// ZF) always stay in the record; the name (NM) and link target (SL) go with them while the
/// record stays within 255 bytes, and otherwise into a continuation area a SUSP CE entry points
/// to.
/// </summary>
internal static class IsoRockRidgeWriter {
  private const int MaxRecordLength = 255;
  private const int CeLength = 28;
  private const int MaxEntryPayload = 250;   // a SUSP entry is at most 255 bytes, 5 of them header
  private const int MaxSlComponent = 248;    // 255 - SL header (5) - component header (2)

  /// <summary>The record's System Use; <paramref name="continuation"/> is where the overflow went (zeros while sizing).</summary>
  public static byte[] Fields(RockRidgeRecord r, int idLen, IsoWriter.ContinuationArea? continuation) {
    var fixedPart = FixedEntries(r);
    var tail = TailEntries(r);
    if (tail.Length == 0 || IsoWriter.RecordLength(idLen, fixedPart.Length + tail.Length) <= MaxRecordLength)
      return [.. fixedPart, .. tail];
    var ce = new byte[CeLength];
    "CE"u8.CopyTo(ce);
    ce[2] = CeLength;
    ce[3] = 1;
    Both(ce.AsSpan(4), (uint)(continuation?.Lba ?? 0));
    Both(ce.AsSpan(12), (uint)(continuation?.Offset ?? 0));
    Both(ce.AsSpan(20), (uint)(continuation?.Length ?? 0));
    return [.. fixedPart, .. ce];
  }

  /// <summary>The entries that go into a continuation area, or null when the record holds them.</summary>
  public static byte[]? Overflow(RockRidgeRecord r, int idLen) {
    var tail = TailEntries(r);
    return tail.Length == 0 || IsoWriter.RecordLength(idLen, FixedEntries(r).Length + tail.Length) <= MaxRecordLength ? null : tail;
  }

  private static byte[] FixedEntries(RockRidgeRecord r) {
    var su = new List<byte>(96);
    var (type, defaultMode, links) = r.Kind switch {
      'd' => (0x4000u, 0x1EDu, 2u),   // 0755
      'l' => (0xA000u, 0x1FFu, 1u),   // 0777
      _ => (0x8000u, 0x1A4u, 1u),     // 0644
    };
    var mode = ((uint)(r.Mode ?? (int)defaultMode) & 0xFFF) | type;
    su.AddRange([(byte)'P', (byte)'X', 36, 1]);
    AddBoth(su, mode);
    AddBoth(su, links);
    AddBoth(su, 0);
    AddBoth(su, 0);
    var t = (r.Modified ?? DateTime.UtcNow).ToUniversalTime();
    su.AddRange([(byte)'T', (byte)'F', 12, 1, 0x02, (byte)(t.Year - 1900), (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute, (byte)t.Second, 0]);
    if (r.Relocated) su.AddRange([(byte)'R', (byte)'E', 4, 1]);
    if (r.ChildLink is { } child) {
      su.AddRange([(byte)'C', (byte)'L', 12, 1]);
      AddBoth(su, (uint)child);
    }
    if (r.ParentLink is { } parent) {
      su.AddRange([(byte)'P', (byte)'L', 12, 1]);
      AddBoth(su, (uint)parent);
    }
    if (r.ZisofsSize is { } size) {
      // ZF (zisofs 1): algorithm "pz", header size / 4, log2 of the block size, uncompressed length.
      su.AddRange([(byte)'Z', (byte)'F', 16, 1, (byte)'p', (byte)'z', Zisofs.HeaderSize / 4, Zisofs.BlockLog2]);
      AddBoth(su, (uint)size);
    }
    return [.. su];
  }

  private static byte[] TailEntries(RockRidgeRecord r) {
    var su = new List<byte>();
    if (r.Name != null) {
      var bytes = Encoding.UTF8.GetBytes(r.Name);
      for (var start = 0; start == 0 || start < bytes.Length;) {
        var take = Math.Min(MaxEntryPayload, bytes.Length - start);
        su.AddRange([(byte)'N', (byte)'M', (byte)(5 + take), 1, (byte)(start + take < bytes.Length ? 0x01 : 0x00)]);
        su.AddRange(bytes.AsSpan(start, take).ToArray());
        start += take;
        if (take == 0) break;
      }
    }
    if (r.SymlinkTarget != null) su.AddRange(SymbolicLink(r.SymlinkTarget));
    return [.. su];
  }

  /// <summary>
  /// SL entries (RRIP 1.10 4.1.3.1): the target as components — ROOT for a leading "/", CURRENT
  /// for ".", PARENT for "..", otherwise the name (split with the component CONTINUE flag when
  /// longer than an entry holds) — packed into entries of at most 255 bytes, each but the last
  /// flagged CONTINUE.
  /// </summary>
  internal static byte[] SymbolicLink(string target) {
    var components = new List<byte[]>();
    if (target.StartsWith('/')) components.Add([0x08, 0]);
    foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      if (part == ".") {
        components.Add([0x02, 0]);
        continue;
      }
      if (part == "..") {
        components.Add([0x04, 0]);
        continue;
      }
      var bytes = Encoding.UTF8.GetBytes(part);
      for (var start = 0; start < bytes.Length; start += MaxSlComponent) {
        var take = Math.Min(MaxSlComponent, bytes.Length - start);
        components.Add([(byte)(start + take < bytes.Length ? 0x01 : 0x00), (byte)take, .. bytes.AsSpan(start, take)]);
      }
    }
    if (components.Count == 0) components.Add([0x02, 0]); // an empty target reads as "."

    var entries = new List<List<byte>>();
    var current = new List<byte>();
    foreach (var c in components) {
      if (current.Count + c.Length > MaxEntryPayload) {
        entries.Add(current);
        current = [];
      }
      current.AddRange(c);
    }
    entries.Add(current);
    var su = new List<byte>();
    for (var i = 0; i < entries.Count; i++) {
      su.AddRange([(byte)'S', (byte)'L', (byte)(5 + entries[i].Count), 1, (byte)(i < entries.Count - 1 ? 0x01 : 0x00)]);
      su.AddRange(entries[i]);
    }
    return [.. su];
  }

  private static void AddBoth(List<byte> l, uint v) {
    Span<byte> b = stackalloc byte[8];
    Both(b, v);
    l.AddRange(b.ToArray());
  }

  private static void Both(Span<byte> b, uint v) {
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    BinaryPrimitives.WriteUInt32BigEndian(b[4..], v);
  }
}

/// <summary>
/// zisofs 1, the compressed file form the ZF entry announces: a 16-byte header (magic, the
/// uncompressed length, header size / 4, log2 of the block size), a table of block pointers
/// (one per block plus the end), and each block as a zlib stream; an all-zero block is stored
/// as nothing, its two pointers equal. Blocks are 32 KiB, as mkzftree and the kernel default.
/// </summary>
internal static class Zisofs {
  public const byte HeaderSize = 16;
  public const byte BlockLog2 = 15;
  private static readonly byte[] Magic = [0x37, 0xE4, 0x53, 0x96, 0xC9, 0xDB, 0xD6, 0x07];

  /// <summary>The zisofs form of <paramref name="data"/>, or null when it is too large for the 32-bit length.</summary>
  public static byte[]? Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0 || (long)data.Length > uint.MaxValue) return null;
    var block = 1 << BlockLog2;
    var blocks = (data.Length + block - 1) / block;
    var table = HeaderSize;
    using var ms = new MemoryStream();
    var header = new byte[HeaderSize + (blocks + 1) * 4];
    Magic.CopyTo(header, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)data.Length);
    header[12] = HeaderSize / 4;
    header[13] = BlockLog2;
    ms.Write(header);
    for (var i = 0; i < blocks; i++) {
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(table + i * 4), (uint)ms.Length);
      var chunk = data.Slice(i * block, Math.Min(block, data.Length - i * block));
      if (chunk.ContainsAnyExcept((byte)0)) ms.Write(ZlibStream.Compress(chunk, DeflateCompressionLevel.Default));
    }
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(table + blocks * 4), (uint)ms.Length);
    var result = ms.ToArray();
    header.AsSpan(table).CopyTo(result.AsSpan(table));
    return result;
  }
}
