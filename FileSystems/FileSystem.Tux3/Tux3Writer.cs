#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Tux3;

/// <summary>
/// WORM writer for the TUX3 prototype on-disk surface that <see cref="Tux3Reader"/>
/// parses. TUX3 was Daniel Phillips's version-tree successor to TUX2; the
/// linux-tux3 prototype was never declared stable, so this writer emits the
/// documented superblock prefix (magic "TUX3SUPR" at block offset 4096 plus
/// the documented 0x60-byte field set) followed by a sentinel WORM file
/// table at block 2 (offset 8192). The version-tree itself is collapsed to a
/// single version — no version chain, no atomic-commit log — matching the
/// goal "WORM emit single-version image with N files".
///
/// <para>
/// Layout produced (little-endian):
/// </para>
/// <code>
///   0x0000        zeroed boot region (4096 bytes, block 0)
///   0x1000        TUX3 superblock:
///                   +0x00 8 bytes  Magic = "TUX3SUPR"
///                   +0x08 u64      birthday
///                   +0x10 u64      flags (0)
///                   +0x18 u64      iroot (0 — no B-tree)
///                   +0x20 u64      oroot (0)
///                   +0x28 u64      aroot (0)
///                   +0x30 u64      blockbits (12 — 4096-byte blocks)
///                   +0x38 u64      volblocks (image size / 4096)
///                   +0x40 u64      freeblocks (volblocks − reserved)
///                   +0x48 u64      nextalloc
///                   +0x50 u32      atomgen
///                   +0x54 u32      freeatom
///                   ...zero-padded to end of block...
///   0x2000        WORM file table (block 2):
///                   +0x00 8 bytes  Sentinel "TUX3WORM"
///                   +0x08 u32      file_count
///                   +0x0C ...      per-file records:
///                                    u16 name_len
///                                    name (UTF-8, name_len bytes)
///                                    u32 data_len
///                                    data (data_len bytes)
/// </code>
///
/// <para>
/// Round-trips through <see cref="Tux3Reader"/>. Real linux-tux3 prototype
/// dumps that use the itable/otable/atable B-trees are <em>not</em> emitted
/// by this writer (the B-tree code paths in the prototype were never
/// stabilised); a real-world dump would need a full B-tree writer.
/// </para>
/// </summary>
public sealed class Tux3Writer {
  private readonly List<Item> _files = [];

  /// <summary>
  /// When the volume claims it was made. Taken from the clock unless set, because
  /// a birthday that reads the same on every volume is a maker's mark.
  /// </summary>
  public ulong Birthday { get; init; } = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
  /// <summary>
  /// Gets or sets the flags.
  /// </summary>
  public ulong Flags { get; init; }
  /// <summary>
  /// Gets or sets the block bits.
  /// </summary>
  public ulong BlockBits { get; init; } = 12; // 4 KiB blocks (matches Tux3 prototype default)

  /// <summary>One file to emit: either its bytes, or a copier that streams them.</summary>
  private readonly record struct Item(string Name, long Size, byte[]? Data, Action<Stream>? Copy);

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
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

  /// <summary>
  /// Writes the to to the supplied output.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var blockSize = 1 << (int)this.BlockBits;
    if (blockSize < 4096) throw new InvalidOperationException("BlockBits must give a block size >= 4096.");

    // Block 0: reserved (zeroed). Block 1: superblock. Block 2: WORM table.
    var bootRegion = new byte[blockSize];
    output.Write(bootRegion);

    // Superblock block — documented 0x60-byte prefix + zero pad.
    var sb = new byte[blockSize];
    Tux3Reader.Magic.CopyTo(sb.AsSpan(0));
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x08), this.Birthday);
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x10), this.Flags);
    // iroot/oroot/aroot all 0 — no B-tree in single-version WORM mode.
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x18), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x20), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x28), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x30), this.BlockBits);

    // Compute vol_blocks / free_blocks after we know the on-disk size — write
    // pass 1 placeholder here, patch after we've serialised everything.
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x38), 0); // vol_blocks (patched)
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x40), 0); // free_blocks (patched)
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(0x48), 3); // nextalloc — past block 2 (WORM table)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x50), 0); // atomgen
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x54), 0); // freeatom

    var sbStartPos = output.Position;
    output.Write(sb);

    // Block 2: WORM file table.
    Span<byte> wormHdr = stackalloc byte[12];
    Tux3Reader.WormTableMagic.CopyTo(wormHdr);
    BinaryPrimitives.WriteUInt32LittleEndian(wormHdr.Slice(8, 4), (uint)this._files.Count);
    output.Write(wormHdr);

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

    // Pad to a whole block so vol_blocks accurately reflects the image.
    var endPos = output.Position;
    var blockPad = (int)((blockSize - (endPos % blockSize)) % blockSize);
    if (blockPad > 0) output.Write(new byte[blockPad]);

    // Patch vol_blocks / free_blocks now we know the final image size.
    var finalLen = output.Position;
    var volBlocks = (ulong)(finalLen / blockSize);
    // Reserved: block 0 (boot), block 1 (superblock), block 2 (WORM table) =
    // 3 blocks. Treat all data-bearing blocks past that as "used"; we don't
    // track per-file allocation in WORM mode, so free_blocks is the tail
    // padding region (0 by default since we pad up to the next block).
    var reserved = 3UL;
    var freeBlocks = volBlocks > reserved ? 0UL : 0UL;
    output.Position = sbStartPos + 0x38;
    Span<byte> patch = stackalloc byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(patch.Slice(0, 8), volBlocks);
    BinaryPrimitives.WriteUInt64LittleEndian(patch.Slice(8, 8), freeBlocks);
    output.Write(patch);
    output.Position = finalLen;
  }

  /// <summary>
  /// Performs the build operation.
  /// </summary>
  public byte[] Build() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }
}
