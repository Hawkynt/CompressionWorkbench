#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.SquashFs;

/// <summary>
/// Reads the inode table out of its metadata blocks and finds the field in each
/// regular file's inode that says where its data starts.
/// </summary>
/// <remarks>
/// <para>The table is a run of metadata blocks, each a two-byte length followed
/// by that many bytes — deflated unless the top bit of the length says
/// otherwise. So a field inside it cannot simply be written to: the block has
/// to be taken apart, changed, and put together again.</para>
///
/// <para>Which is only expressible if the result still fits. A block's length is
/// its own header, and every table after it is found by an offset in the
/// superblock, so a block that grew would move all of them. One that shrinks is
/// padded back to the length it had, which a deflate stream tolerates because
/// it ends where its own final block ends.</para>
/// </remarks>
internal static class SquashFsLayout {

  private const int InodeTableStartOffset = 0x40;
  private const int DirectoryTableStartOffset = 0x48;

  /// <summary>One metadata block of the inode table.</summary>
  /// <param name="Offset">Where the block's two-byte length sits.</param>
  /// <param name="OnDiskLength">How many bytes follow that length.</param>
  /// <param name="Stored">Whether the bytes are the data itself rather than deflated.</param>
  /// <param name="Data">The bytes the block holds once unpacked.</param>
  internal sealed record MetadataBlock(long Offset, int OnDiskLength, bool Stored, byte[] Data);

  /// <summary>Where a file's starting block is recorded.</summary>
  /// <param name="Path">The file.</param>
  /// <param name="StartBlock">What the field says today.</param>
  /// <param name="BlockIndex">Which metadata block holds the field.</param>
  /// <param name="FieldOffset">Where in that block's unpacked bytes it sits.</param>
  internal readonly record struct InodeField(
    string Path, uint StartBlock, int BlockIndex, int FieldOffset);

  /// <summary>What the inode table is made of.</summary>
  internal sealed class Layout {
    public List<MetadataBlock> Blocks { get; } = [];
    public List<InodeField> Fields { get; } = [];
  }

  /// <summary>Reads the inode table, or returns null when it cannot be unpacked.</summary>
  public static Layout? Read(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || image.Length < 0x60) return null;

    var superblock = new byte[0x60];
    image.Position = 0;
    image.ReadExactly(superblock);

    var start = BinaryPrimitives.ReadInt64LittleEndian(superblock.AsSpan(InodeTableStartOffset));
    var end = BinaryPrimitives.ReadInt64LittleEndian(superblock.AsSpan(DirectoryTableStartOffset));
    if (start <= 0 || end <= start || end > image.Length) return null;

    var layout = new Layout();
    var unpacked = new List<byte>();
    var at = start;
    while (at + 2 <= end) {
      var header = new byte[2];
      image.Position = at;
      image.ReadExactly(header);

      var word = BinaryPrimitives.ReadUInt16LittleEndian(header);
      var length = word & 0x7FFF;
      var stored = (word & 0x8000) != 0;
      if (length == 0 || at + 2 + length > end) break;

      var raw = new byte[length];
      image.Position = at + 2;
      image.ReadExactly(raw);

      byte[] data;
      if (stored) {
        data = raw;
      } else {
        try {
          data = InflateZlib(raw);
        } catch {
          return null;
        }
      }

      layout.Blocks.Add(new MetadataBlock(at, length, stored, data));
      unpacked.AddRange(data);
      at += 2 + length;
    }

    if (layout.Blocks.Count == 0) return null;

    // The inodes sit end to end in the unpacked bytes; each says what it is and
    // how much of it there is.
    var table = unpacked.ToArray();
    foreach (var (startBlock, fieldOffset) in RegularFiles(table))
      layout.Fields.Add(ToField(layout, startBlock, fieldOffset));

    return layout;
  }

  /// <summary>Turns an offset in the unpacked table into a block and an offset inside it.</summary>
  private static InodeField ToField(Layout layout, uint startBlock, int fieldOffset) {
    var remaining = fieldOffset;
    for (var i = 0; i < layout.Blocks.Count; ++i) {
      if (remaining < layout.Blocks[i].Data.Length)
        return new InodeField("", startBlock, i, remaining);
      remaining -= layout.Blocks[i].Data.Length;
    }

    return new InodeField("", startBlock, -1, -1);
  }

  /// <summary>
  /// Walks the unpacked inode table, yielding each regular file's starting
  /// block and where that field sits.
  /// </summary>
  private static IEnumerable<(uint StartBlock, int FieldOffset)> RegularFiles(byte[] table) {
    const int header = 16;
    var at = 0;

    while (at + header <= table.Length) {
      var type = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at));
      var body = at + header;

      switch (type) {
        case SquashFsConstants.InodeBasicDir:
          at = body + 16;
          break;

        case SquashFsConstants.InodeBasicFile: {
          if (body + 16 > table.Length) yield break;

          var startBlock = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(body));
          var fragment = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(body + 4));
          var size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(body + 12));
          yield return (startBlock, body);

          var blocks = BlockCount(size, fragment);
          at = body + 16 + blocks * 4;
          break;
        }

        default:
          // A shape this does not walk; stop rather than guess where the next
          // inode begins.
          yield break;
      }
    }
  }

  /// <summary>How many block-size entries a file's inode carries.</summary>
  private static int BlockCount(uint size, uint fragment) {
    const int blockSize = 131072;
    var whole = (int)(size / blockSize);
    return fragment != SquashFsConstants.NoFragment ? whole : (int)((size + blockSize - 1) / blockSize);
  }

  /// <summary>Packs a block again, keeping the length it had.</summary>
  /// <returns>The bytes to write, or null when the result does not fit.</returns>
  public static byte[]? Repack(MetadataBlock block) {
    if (block.Stored) return block.Data.Length == block.OnDiskLength ? block.Data : null;

    var packed = DeflateZlib(block.Data);
    if (packed.Length > block.OnDiskLength) return null;
    if (packed.Length == block.OnDiskLength) return packed;

    // A deflate stream ends where its own final block ends, so what follows is
    // never read; padding back to the length the header records is what keeps
    // every table after this one where it is.
    var padded = new byte[block.OnDiskLength];
    packed.CopyTo(padded, 0);
    return padded;
  }

  private static byte[] InflateZlib(byte[] data) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    using (var stream = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true))
      stream.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] DeflateZlib(byte[] data) {
    using var output = new MemoryStream();
    using (var stream = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
      stream.Write(data, 0, data.Length);
    return output.ToArray();
  }
}
