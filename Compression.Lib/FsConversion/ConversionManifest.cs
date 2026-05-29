using System.Buffers.Binary;
using System.Text;

namespace Compression.Lib.FsConversion;

/// <summary>
/// Per-file migration status recorded in the on-disk
/// <see cref="ConversionManifest"/>. Transitions are
/// <see cref="Pending"/> → <see cref="Copying"/> → <see cref="Done"/>,
/// written-and-flushed at each boundary so a crash anywhere leaves the
/// manifest in a state that <see cref="MigrationConverter.Resume"/> can
/// reconcile against the actual source/destination contents.
/// </summary>
public enum ConversionEntryStatus {
  /// <summary>Not yet attempted — file lives only on the source.</summary>
  Pending = 0,

  /// <summary>Copy is in flight. After a crash the file may be on src,
  /// dst, or both — recovery inspects the destination to decide.</summary>
  Copying = 1,

  /// <summary>Successfully copied to dst and deleted from src.</summary>
  Done = 2,
}

/// <summary>
/// One row of the migration manifest — the per-file unit of work tracked by
/// <see cref="MigrationConverter"/>. Equality is by <see cref="SourcePath"/>
/// alone since that is the cross-FS identifier.
/// </summary>
public sealed record ConversionManifestEntry {
  /// <summary>Source-filesystem entry name (the bare archive/FS path).</summary>
  public required string SourcePath { get; init; }

  /// <summary>Current migration status.</summary>
  public ConversionEntryStatus Status { get; set; }

  /// <summary>Original byte length of the source file. Stored so the
  /// caller can sanity-check the copy without re-reading both sides.</summary>
  public long Size { get; set; }
}

/// <summary>
/// Persistent record of which files have been migrated from a source
/// filesystem to a destination filesystem by <see cref="MigrationConverter"/>.
///
/// <para>
/// Stored as a hidden sidecar file <c>.conversion-manifest</c> inside the
/// destination filesystem. The format is a tiny custom binary blob with a
/// magic header, version, count, per-entry records, and a trailing CRC32 so
/// torn writes are detectable. JSON would also work, but the binary form
/// makes torn-write detection trivial (the trailing CRC won't match the
/// truncated body).
/// </para>
///
/// <para>
/// Layout:
/// <list type="bullet">
///   <item>magic "CWMIGR\0\0" (8 bytes)</item>
///   <item>version u32 LE (currently 1)</item>
///   <item>entry count u32 LE</item>
///   <item>for each entry: u8 status, u64 size LE, u32 nameLen LE, UTF-8 name bytes</item>
///   <item>trailing u32 LE CRC32 over everything before the CRC</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConversionManifest {

  /// <summary>The filename used inside the destination filesystem.</summary>
  public const string FileName = ".conversion-manifest";

  /// <summary>Magic prefix written at offset 0.</summary>
  public static ReadOnlySpan<byte> Magic => "CWMIGR\0\0"u8;

  /// <summary>Current on-disk format version.</summary>
  public const uint CurrentVersion = 1;

  /// <summary>The per-file migration entries, in their migration order.</summary>
  public List<ConversionManifestEntry> Entries { get; } = [];

  /// <summary>
  /// Serializes the manifest to a single contiguous blob suitable for atomic
  /// write to the destination filesystem.
  /// </summary>
  public byte[] Serialize() {
    using var ms = new MemoryStream();
    ms.Write(Magic);
    Span<byte> buf = stackalloc byte[8];

    BinaryPrimitives.WriteUInt32LittleEndian(buf, CurrentVersion);
    ms.Write(buf[..4]);

    BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)this.Entries.Count);
    ms.Write(buf[..4]);

    foreach (var entry in this.Entries) {
      ms.WriteByte((byte)entry.Status);
      BinaryPrimitives.WriteInt64LittleEndian(buf, entry.Size);
      ms.Write(buf[..8]);
      var nameBytes = Encoding.UTF8.GetBytes(entry.SourcePath);
      BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)nameBytes.Length);
      ms.Write(buf[..4]);
      ms.Write(nameBytes);
    }

    var body = ms.ToArray();
    var crc = ComputeCrc32(body);
    var output = new byte[body.Length + 4];
    Buffer.BlockCopy(body, 0, output, 0, body.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(body.Length), crc);
    return output;
  }

  /// <summary>
  /// Parses a manifest blob. Returns null when the blob is missing the magic,
  /// the version is unknown, the body is truncated, or the trailing CRC does
  /// not match — all of which the caller treats as "no usable manifest, rebuild
  /// from scratch".
  /// </summary>
  public static ConversionManifest? TryParse(byte[] data) {
    if (data is null || data.Length < Magic.Length + 4 + 4 + 4) return null;
    if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;

    // Verify trailing CRC32 over everything before the CRC. Torn writes that
    // chop a manifest mid-entry will fail this check and force a full restart.
    var crcStored = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4));
    var crcActual = ComputeCrc32(data.AsSpan(0, data.Length - 4));
    if (crcStored != crcActual) return null;

    var offset = Magic.Length;
    var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    offset += 4;
    if (version != CurrentVersion) return null;

    var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    offset += 4;

    var manifest = new ConversionManifest();
    for (var i = 0u; i < count; ++i) {
      if (offset + 1 + 8 + 4 > data.Length - 4) return null;
      var status = (ConversionEntryStatus)data[offset++];
      var size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
      offset += 8;
      var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
      offset += 4;
      if (offset + nameLen > data.Length - 4) return null;
      var name = Encoding.UTF8.GetString(data, offset, (int)nameLen);
      offset += (int)nameLen;
      manifest.Entries.Add(new ConversionManifestEntry {
        SourcePath = name,
        Status = status,
        Size = size,
      });
    }

    // Trailing CRC accounts for the remaining 4 bytes.
    return offset == data.Length - 4 ? manifest : null;
  }

  // ── CRC32 (IEEE 802.3 polynomial) ──────────────────────────────────────
  // Local copy so this type has no dependency on Compression.Core; the
  // manifest must survive any future refactor that moves CRC primitives.

  private static readonly uint[] _crcTable = BuildCrcTable();

  private static uint[] BuildCrcTable() {
    var table = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[i] = c;
    }
    return table;
  }

  private static uint ComputeCrc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }
}
