#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jfs1;

/// <summary>
/// Parsed OS/2 JFS1 superblock at offset 0. Refuses any image with
/// <c>s_version &gt;= 2</c> so it does not steal detection from
/// <c>FileSystem.Jfs</c> (Linux JFS2). All multi-byte fields are little-endian
/// on OS/2.
///
/// Layout (selected — first 64 bytes per IBM JFS for OS/2 spec):
///   0x00  s_magic       4 B  — "JFS1" ASCII
///   0x04  s_version     u32  — 1 (OS/2 original); 2 means Linux-port (refused)
///   0x08  s_size        u64  — aggregate size in s_bsize blocks
///   0x10  s_bsize       u32  — block size (1024 or 4096)
///   0x14  s_l2bsize     u16  — log2(s_bsize)
///   ...
/// </summary>
internal sealed class Jfs1Superblock {
  public static readonly byte[] Jfs1Magic = [(byte)'J', (byte)'F', (byte)'S', (byte)'1'];

  public bool Valid { get; init; }
  public string MagicString { get; init; } = "";
  public uint Version { get; init; }
  public ulong Size { get; init; }
  public uint BlockSize { get; init; }
  public ushort Log2BlockSize { get; init; }
  public byte[] RawBytes { get; init; } = [];

  public static Jfs1Superblock TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < 0x40) return new Jfs1Superblock();
    if (!image.Slice(0, 4).SequenceEqual(Jfs1Magic.AsSpan())) return new Jfs1Superblock();
    var ver = ReadU32Le(image, 0x04);
    // Refuse Linux JFS2 — its superblock has s_version >= 2 and lives at offset
    // 32768, but if someone hands us a raw header that happens to start with
    // "JFS1" and version 2 we still want to defer to FileSystem.Jfs.
    if (ver >= 2) return new Jfs1Superblock();

    var raw = image.Slice(0, Math.Min(512, image.Length)).ToArray();
    if (raw.Length < 512) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    return new Jfs1Superblock {
      Valid = true,
      MagicString = Encoding.ASCII.GetString(image.Slice(0, 4)),
      Version = ver,
      Size = ReadU64Le(image, 0x08),
      BlockSize = ReadU32Le(image, 0x10),
      Log2BlockSize = ReadU16Le(image, 0x14),
      RawBytes = raw,
    };
  }

  private static ushort ReadU16Le(ReadOnlySpan<byte> s, int off) =>
    off + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(off, 2)) : (ushort)0;
  private static uint ReadU32Le(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(off, 4)) : 0u;
  private static ulong ReadU64Le(ReadOnlySpan<byte> s, int off) =>
    off + 8 <= s.Length ? BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(off, 8)) : 0ul;
}
