#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Tux2;

/// <summary>
/// WORM writer for the TUX2 synthetic image layout that <see cref="Tux2Reader"/>
/// parses. TUX2 was a 2002-era phase-tree research filesystem (Daniel Phillips,
/// kernel.org/doc/ols/2002/) whose on-disk format never stabilised — no
/// canonical real-world images exist. The reader documents (and round-trips)
/// a deterministic synthetic header that we emit here:
///
/// <code>
///   0x00 8 bytes  Magic = "TUX2FS\0\0"
///   0x08 u32      version (1)
///   0x0C u32      file_count
///   0x10 ...      per-file records:
///                   u16 name_len
///                   name (UTF-8, name_len bytes)
///                   u32 data_len
///                   data (data_len bytes)
/// </code>
///
/// Single-phase only (no alpha/beta phases, no version chain) — matches the
/// goal of "WORM emit single-phase image with N files (no research-level
/// snapshots)". Round-trips through <see cref="Tux2Reader"/>.
/// </summary>
public sealed class Tux2Writer {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public uint Version { get; init; } = 1;

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length == 0) throw new ArgumentException("Name cannot be empty.", nameof(name));
    var nameBytes = Encoding.UTF8.GetBytes(name);
    if (nameBytes.Length > ushort.MaxValue)
      throw new ArgumentException("Name UTF-8 length exceeds 65535 bytes.", nameof(name));
    if (data.LongLength > uint.MaxValue)
      throw new ArgumentException("File data length exceeds 4 GiB.", nameof(data));
    this._files.Add((name, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> hdr = stackalloc byte[16];
    Tux2Reader.Magic.CopyTo(hdr);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8, 4), this.Version);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12, 4), (uint)this._files.Count);
    output.Write(hdr);

    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];

    foreach (var (name, data) in this._files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)nameBytes.Length);
      output.Write(u16);
      output.Write(nameBytes);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)data.Length);
      output.Write(u32);
      if (data.Length > 0) output.Write(data);
    }
  }

  public byte[] Build() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }
}
