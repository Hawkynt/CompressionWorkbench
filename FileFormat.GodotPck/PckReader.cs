#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.GodotPck;

/// <summary>
/// Reads Godot Engine PCK (resource pack) files.
/// Supports pack_version 1 (Godot 3.x) and pack_version 2 (Godot 4.x).
/// </summary>
public sealed class PckReader {
  private readonly Stream _stream;

  /// <summary>All file entries found in the PCK directory.</summary>
  public IReadOnlyList<PckEntry> Entries { get; }

  /// <summary>The pack format version (1 = Godot 3.x, 2 = Godot 4.x).</summary>
  public uint PackVersion { get; }

  /// <summary>The Godot engine major version recorded in the header.</summary>
  public uint VersionMajor { get; }

  /// <summary>The Godot engine minor version recorded in the header.</summary>
  public uint VersionMinor { get; }

  /// <summary>The Godot engine patch version recorded in the header.</summary>
  public uint VersionPatch { get; }

  /// <summary>
  /// Opens a PCK stream and parses the header and file directory.
  /// </summary>
  /// <param name="stream">A readable, seekable stream positioned at the start of the PCK data.</param>
  /// <exception cref="InvalidDataException">Thrown when the magic bytes are missing or the stream is too small.</exception>
  public PckReader(Stream stream) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    if (stream.Length < 40)
      throw new InvalidDataException("Stream is too small to be a valid PCK file.");

    var buf4 = new byte[4];
    stream.Position = 0;
    ReadExact(stream, buf4);

    if (buf4[0] != 'G' || buf4[1] != 'D' || buf4[2] != 'P' || buf4[3] != 'C')
      throw new InvalidDataException("Not a valid Godot PCK file: bad magic.");

    PackVersion  = ReadUInt32LE(stream);
    VersionMajor = ReadUInt32LE(stream);
    VersionMinor = ReadUInt32LE(stream);
    VersionPatch = ReadUInt32LE(stream);

    // pack_version 1 layout (Godot 3.x):
    //   offset  0: "GDPC"
    //   offset  4: uint32 pack_version = 1
    //   offset  8: uint32 ver_major
    //   offset 12: uint32 ver_minor
    //   offset 16: uint32 ver_patch
    //   offset 20: 16 bytes reserved (zeros)
    //   offset 36: uint32 file_count
    //
    // pack_version 2 layout (Godot 4.x):
    //   offset  0: "GDPC"
    //   offset  4: uint32 pack_version = 2
    //   offset  8: uint32 ver_major
    //   offset 12: uint32 ver_minor
    //   offset 16: uint32 ver_patch
    //   offset 20: uint32 flags
    //   offset 24: uint64 files_base (offset added to each entry's offset field)
    //   offset 32: 16 bytes reserved (zeros)
    //   offset 48: uint32 file_count

    uint fileCount;
    long filesBase = 0;

    if (PackVersion >= 2) {
      // flags (4) + files_base (8) + reserved (16) = 28 bytes to skip past patch
      var flags    = ReadUInt32LE(stream);  // offset 20
      filesBase    = (long)ReadUInt64LE(stream); // offset 24
      stream.Seek(16, SeekOrigin.Current);  // skip 16-byte reserved block (offset 32-47)
      fileCount    = ReadUInt32LE(stream);  // offset 48
      _ = flags;
    } else {
      // skip 16-byte reserved block (offset 20-35)
      stream.Seek(16, SeekOrigin.Current);
      fileCount = ReadUInt32LE(stream);     // offset 36
    }

    var entries = new List<PckEntry>((int)fileCount);

    for (var i = 0u; i < fileCount; i++) {
      var pathLen = ReadUInt32LE(stream);
      var pathBytes = new byte[pathLen];
      ReadExact(stream, pathBytes);
      var path = Encoding.UTF8.GetString(pathBytes).TrimEnd('\0');

      // Path is padded to 4-byte boundary (the path_length field already includes any padding
      // in some implementations, but in others the raw bytes are padded separately).
      // The path bytes themselves are already read; no additional padding needed since
      // path_length includes the null terminator and is already aligned in Godot's writer.

      var offset = (long)ReadUInt64LE(stream) + filesBase;
      var size   = (long)ReadUInt64LE(stream);
      var md5    = new byte[16];
      ReadExact(stream, md5);

      // Godot 4.x adds a uint32 flags field per entry
      if (PackVersion >= 2)
        ReadUInt32LE(stream); // per-entry flags — skip

      entries.Add(new PckEntry { Path = path, Offset = offset, Size = size, Md5 = md5 });
    }

    Entries = entries;
  }

  /// <summary>Reads and returns the raw bytes for the given entry.</summary>
  public byte[] Extract(PckEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    _stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(_stream, data);
    return data;
  }

  // ── helpers ────────────────────────────────────────────────────────────────

  private static uint ReadUInt32LE(Stream s) {
    var b = new byte[4];
    ReadExact(s, b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  private static ulong ReadUInt64LE(Stream s) {
    var b = new byte[8];
    ReadExact(s, b);
    return BinaryPrimitives.ReadUInt64LittleEndian(b);
  }

  private static void ReadExact(Stream s, byte[] buf) {
    var total = 0;
    while (total < buf.Length) {
      var n = s.Read(buf, total, buf.Length - total);
      if (n == 0) throw new EndOfStreamException("Unexpected end of PCK stream.");
      total += n;
    }
  }
}
