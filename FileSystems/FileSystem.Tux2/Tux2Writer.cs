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
  private readonly List<Item> _files = [];

  public uint Version { get; init; } = 1;

  /// <summary>One file to emit: either its bytes, or a copier that streams them.</summary>
  private readonly record struct Item(string Name, long Size, byte[]? Data, Action<Stream>? Copy);

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add(new Item(CheckName(name), data.LongLength, data, null));
    if (data.LongLength > uint.MaxValue)
      throw new ArgumentException("File data length exceeds 4 GiB.", nameof(data));
  }

  /// <summary>
  /// Adds a file whose bytes are written straight into the output by
  /// <paramref name="copy" />. Nothing is buffered, so a record may be as large
  /// as the record header's u32 length field allows.
  /// </summary>
  public void AddStreamingFile(string name, long size, Action<Stream> copy) {
    ArgumentNullException.ThrowIfNull(copy);
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    if (size > uint.MaxValue)
      throw new ArgumentException("File data length exceeds 4 GiB.", nameof(size));
    this._files.Add(new Item(CheckName(name), size, null, copy));
  }

  private static string CheckName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length == 0) throw new ArgumentException("Name cannot be empty.", nameof(name));
    if (Encoding.UTF8.GetByteCount(name) > ushort.MaxValue)
      throw new ArgumentException("Name UTF-8 length exceeds 65535 bytes.", nameof(name));
    return name;
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

    foreach (var file in this._files) {
      var nameBytes = Encoding.UTF8.GetBytes(file.Name);
      BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)nameBytes.Length);
      output.Write(u16);
      output.Write(nameBytes);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)file.Size);
      output.Write(u32);
      if (file.Size <= 0) continue;

      var before = output.Position;
      if (file.Data != null)
        output.Write(file.Data);
      else
        file.Copy!(output);

      var written = output.Position - before;
      if (written != file.Size)
        throw new InvalidOperationException(
          $"'{file.Name}' was announced as {file.Size:N0} bytes but {written:N0} were written; " +
          "the record length and the record body would disagree.");
    }
  }

  public byte[] Build() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }
}
