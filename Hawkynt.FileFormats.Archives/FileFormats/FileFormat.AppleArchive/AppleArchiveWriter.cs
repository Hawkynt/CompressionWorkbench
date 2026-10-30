using System.Buffers.Binary;
using System.Text;

namespace FileFormat.AppleArchive;

/// <summary>
/// Writes an Apple Archive as <c>aa archive</c> lays one out: AA01 items with TYP, PAT, MOD, MTM
/// and DAT fields (integers in their smallest width), optionally wrapped in a pbz LZFSE or zlib
/// container.
/// </summary>
public sealed class AppleArchiveWriter {
  private readonly MemoryStream _plain = new();

  /// <summary>Adds a regular file.</summary>
  public void AddFile(string path, ReadOnlySpan<byte> data, DateTime? modifiedUtc = null, int mode = 0x1A4) {
    var item = BuildItem('F', path, mode, modifiedUtc, data.Length);
    this._plain.Write(item);
    this._plain.Write(data);
  }

  /// <summary>Adds a directory.</summary>
  public void AddDirectory(string path, DateTime? modifiedUtc = null, int mode = 0x1ED)
    => this._plain.Write(BuildItem('D', path, mode, modifiedUtc, -1));

  internal static byte[] BuildItem(char type, string path, int mode, DateTime? modifiedUtc, long dataLength) {
    var name = Encoding.UTF8.GetBytes(Normalise(path));
    if (name.Length > ushort.MaxValue)
      throw new ArgumentException("An Apple Archive path is at most 65535 bytes.", nameof(path));

    using var ms = new MemoryStream();
    ms.Write("AA01\0\0"u8);
    Field(ms, "TYP", '1', [(byte)type]);
    Span<byte> length = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)name.Length);
    ms.Write("PATP"u8);
    ms.Write(length);
    ms.Write(name);
    UInt(ms, "MOD", (ulong)mode);
    var seconds = (long)Math.Floor(((modifiedUtc ?? DateTime.UtcNow).ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds);
    Span<byte> time = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(time, seconds);
    Field(ms, "MTM", 'S', time);
    if (dataLength >= 0) {
      var (subtype, width) = dataLength <= ushort.MaxValue ? ('A', 2) : dataLength <= uint.MaxValue ? ('B', 4) : ('C', 8);
      Span<byte> blob = stackalloc byte[8];
      BinaryPrimitives.WriteInt64LittleEndian(blob, dataLength);
      Field(ms, "DAT", subtype, blob[..width]);
    }

    var bytes = ms.ToArray();
    if (bytes.Length > ushort.MaxValue)
      throw new ArgumentException("Apple Archive item header is longer than 65535 bytes.", nameof(path));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)bytes.Length);
    return bytes;
  }

  private static string Normalise(string path) =>
    string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

  private static void Field(Stream s, string key, char subtype, ReadOnlySpan<byte> value) {
    s.Write(Encoding.ASCII.GetBytes(key));
    s.WriteByte((byte)subtype);
    s.Write(value);
  }

  private static void UInt(Stream s, string key, ulong value) {
    var (subtype, width) = value <= byte.MaxValue ? ('1', 1) : value <= ushort.MaxValue ? ('2', 2) : value <= uint.MaxValue ? ('4', 4) : ('8', 8);
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    Field(s, key, subtype, bytes[..width]);
  }

  /// <summary>Returns the archive: plain for "raw", else a pbz LZFSE ('lzfse') or zlib ('zlib') container.</summary>
  public byte[] ToArray(string compression = "lzfse") => Wrap(this._plain.ToArray(), compression);

  internal static byte[] Wrap(byte[] plain, string compression) => compression switch {
    "raw" => plain,
    "lzfse" => AppleArchiveContainer.Encode(plain, 'e'),
    "zlib" => AppleArchiveContainer.Encode(plain, 'z'),
    _ => throw new NotSupportedException($"Writing Apple Archive compression '{compression}' is not supported."),
  };
}
