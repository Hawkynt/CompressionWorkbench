#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Reads a bcachefs volume: its superblock, the roots that superblock names, and
/// the keys in the nodes those roots point at.
/// </summary>
/// <remarks>
/// <para>Keys come in two forms. One writes every field at its full width and says
/// so in a byte; the other packs the fields against the node's own format, which
/// gives each field a width and a bias, and stores the result as one large integer
/// in machine word order. Both are read here, because a volume written by
/// <c>mkfs.bcachefs</c> uses the packed form throughout and a volume written by
/// this project uses the plain one.</para>
/// </remarks>
internal sealed class BcacheFsVolume {

  private readonly Stream _image;

  private BcacheFsVolume(Stream image) => this._image = image;

  /// <summary>Whether the superblock read as one.</summary>
  internal bool Valid { get; private set; }

  /// <summary>Why not, when it did not.</summary>
  internal string Status { get; private set; } = "";

  internal ulong InternalMagic { get; private set; }
  internal string Label { get; private set; } = "";
  internal long DeviceSectors { get; private set; }
  internal int BucketSectorCount { get; private set; } = BucketSectors;

  /// <summary>Where each b-tree's root node sits, by tree id.</summary>
  internal Dictionary<int, long> Roots { get; } = [];

  /// <summary>One key read out of a node.</summary>
  internal readonly record struct Entry(byte Type, Bpos Position, uint Size, byte[] Value);

  /// <summary>Opens the volume at the front of <paramref name="image" />.</summary>
  internal static BcacheFsVolume Open(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var volume = new BcacheFsVolume(image);
    volume.Read();
    return volume;
  }

  private void Read() {
    this.DeviceSectors = this._image.Length / SectorSize;

    var header = this.ReadAt(PrimarySbSector * SectorSize, SbFixedBytes);
    if (header == null || !header.AsSpan(24, 16).SequenceEqual(Magic)) {
      this.Status = "bcachefs: no superblock at the offset one sits at.";
      return;
    }

    var u64s = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(124));
    if (u64s > 1 << 20) {
      this.Status = "bcachefs: the superblock claims more sections than a superblock holds.";
      return;
    }

    var superblock = this.ReadAt(PrimarySbSector * SectorSize, SbFixedBytes + (int)u64s * 8);
    if (superblock == null) {
      this.Status = "bcachefs: the superblock runs past the end of the volume.";
      return;
    }

    this.InternalMagic = BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(40));
    this.Label = Encoding.ASCII.GetString(superblock, 72, 32).TrimEnd('\0');

    var flags0 = BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(144));
    var nodeSectors = (int)((flags0 >> 12) & 0xFFFF);
    if (nodeSectors > 0) this.BucketSectorCount = nodeSectors;

    foreach (var (type, offset, length) in EnumerateSections(superblock)) {
      switch (type) {
        case FieldMembersV2: {
          var memberBytes = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(offset + 8));
          if (memberBytes >= 28 && offset + 16 + 28 <= superblock.Length)
            this.BucketSectorCount = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(offset + 16 + 26));
          break;
        }
        case FieldClean:
          this.ReadRoots(superblock, offset + 24, offset + length);
          break;
      }
    }

    if (this.Roots.Count == 0) {
      this.Status = "bcachefs: the superblock names no b-tree roots; its clean section is missing.";
      return;
    }

    this.Valid = true;
  }

  private static IEnumerable<(uint Type, int Offset, int Length)> EnumerateSections(byte[] superblock) {
    var offset = SbFixedBytes;
    while (offset + 8 <= superblock.Length) {
      var words = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(offset));
      var type = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(offset + 4));
      if (words == 0) break;

      var length = (int)words * 8;
      if (offset + length > superblock.Length) break;
      yield return (type, offset, length);
      offset += length;
    }
  }

  private void ReadRoots(byte[] superblock, int start, int end) {
    var offset = start;
    while (offset + 8 <= end) {
      var words = BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(offset));
      var btree = superblock[offset + 2];
      var type = superblock[offset + 4];
      var total = (words + 1) * 8;
      if (offset + total > end) break;

      // A root entry holds one key, and that key's value says where the node is.
      if (type == 1 && words >= BkeyU64s && superblock[offset + 8 + 2] == KeyBtreePtrV2) {
        var value = superblock.AsSpan(offset + 8 + BkeyBytes, (words - BkeyU64s) * 8);
        if (value.Length >= 48) {
          var pointer = BinaryPrimitives.ReadUInt64LittleEndian(value[40..]);
          if (IsPointer(pointer)) this.Roots[btree] = PointerSector(pointer);
        }
      }

      offset += total;
    }
  }

  /// <summary>Every key in the tree whose root is <paramref name="btree" />.</summary>
  /// <remarks>
  /// A tree may be one node or a root of pointers over a row of leaves; the node's
  /// own header says which, and a pointer's value says where the next one is.
  /// </remarks>
  internal IEnumerable<Entry> Keys(int btree) {
    if (!this.Roots.TryGetValue(btree, out var sector)) yield break;

    foreach (var entry in this.KeysOfNode(sector, depth: 0))
      yield return entry;
  }

  /// <summary>Where every node of a tree sits, roots and leaves alike.</summary>
  internal IEnumerable<long> NodeSectors(int btree) {
    if (!this.Roots.TryGetValue(btree, out var sector)) yield break;

    var pending = new Stack<(long Sector, int Depth)>();
    pending.Push((sector, 0));
    while (pending.Count > 0) {
      var (at, depth) = pending.Pop();
      yield return at;
      if (depth > 8) continue;

      var node = this.ReadAt(at * SectorSize, this.BucketSectorCount * SectorSize);
      if (node == null || ReadLevel(node) == 0) continue;

      foreach (var child in this.ChildSectors(node))
        pending.Push((child, depth + 1));
    }
  }

  private static int ReadLevel(byte[] node)
    => (int)((BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(24)) >> 4) & 0xF);

  private IEnumerable<long> ChildSectors(byte[] node) {
    foreach (var key in this.NodeEntries(node)) {
      if (key.Type != KeyBtreePtrV2 || key.Value.Length < 48) continue;
      var pointer = BinaryPrimitives.ReadUInt64LittleEndian(key.Value.AsSpan(40));
      if (IsPointer(pointer)) yield return PointerSector(pointer);
    }
  }

  private IEnumerable<Entry> KeysOfNode(long sector, int depth) {
    if (depth > 8) yield break;

    var node = this.ReadAt(sector * SectorSize, this.BucketSectorCount * SectorSize);
    if (node == null || node.Length < BcacheFsNodeBuilder.KeysOffset) yield break;

    if (ReadLevel(node) == 0) {
      foreach (var entry in this.NodeEntries(node))
        yield return entry;
      yield break;
    }

    foreach (var child in this.ChildSectors(node))
      foreach (var entry in this.KeysOfNode(child, depth + 1))
        yield return entry;
  }

  /// <summary>The keys one node holds, across every run of them it carries.</summary>
  private IEnumerable<Entry> NodeEntries(byte[] node) {
    var format = ReadFormat(node.AsSpan(80));
    // The first run of keys starts inside the node header; further runs, if the
    // node was appended to, follow at sector boundaries with a header of their own.
    var offset = BcacheFsNodeBuilder.KeysOffset;
    var words = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(158));

    while (true) {
      var end = offset + words * 8;
      if (end > node.Length) yield break;

      foreach (var entry in ReadKeys(node, offset, end, format))
        yield return entry;

      // Round up to the next sector and look for another run.
      var next = (end + SectorSize - 1) / SectorSize * SectorSize;
      if (next + 40 > node.Length) yield break;

      var runWords = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(next + 38));
      var runSeq = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(next + 16));
      if (runWords == 0 || runSeq == 0) yield break;

      offset = next + 40;
      words = runWords;
    }
  }

  /// <summary>The widths and biases a node's packed keys are read under.</summary>
  private readonly record struct Format(int KeyU64s, int[] Bits, ulong[] Offsets);

  private static Format ReadFormat(ReadOnlySpan<byte> source) {
    var bits = new int[6];
    var offsets = new ulong[6];
    for (var i = 0; i < 6; ++i) {
      bits[i] = source[2 + i];
      offsets[i] = BinaryPrimitives.ReadUInt64LittleEndian(source[(8 + 8 * i)..]);
    }

    return new Format(source[0], bits, offsets);
  }

  private static IEnumerable<Entry> ReadKeys(byte[] node, int start, int end, Format format) {
    var offset = start;
    while (offset + 8 <= end) {
      var words = node[offset];
      if (words == 0) yield break;

      var bytes = words * 8;
      if (offset + bytes > end) yield break;

      var keyFormat = node[offset + 1] & 0x7F;
      var type = node[offset + 2];

      if (keyFormat == KeyFormatCurrent) {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(offset + 16));
        var position = ReadBpos(node.AsSpan(offset + 20));
        yield return new Entry(type, position, size, node[(offset + BkeyBytes)..(offset + bytes)]);
      } else if (format.KeyU64s > 0 && format.KeyU64s * 8 <= bytes) {
        var unpacked = Unpack(node.AsSpan(offset + 3, format.KeyU64s * 8 - 3), format);
        yield return new Entry(type, unpacked.Position, unpacked.Size,
          node[(offset + format.KeyU64s * 8)..(offset + bytes)]);
      }

      offset += bytes;
    }
  }

  /// <summary>
  /// Reads a packed key's fields.
  /// </summary>
  /// <remarks>
  /// The fields are one integer, most significant field first, held in the machine's
  /// word order — so the bytes are reversed before anything is read out of them, and
  /// what is left over at the bottom is padding.
  /// </remarks>
  private static (Bpos Position, uint Size) Unpack(ReadOnlySpan<byte> packed, Format format) {
    var reversed = new byte[packed.Length];
    for (var i = 0; i < packed.Length; ++i)
      reversed[i] = packed[packed.Length - 1 - i];

    var bit = 0;

    ulong Next(int width, ulong bias) {
      if (width == 0) return bias;

      ulong value = 0;
      for (var i = 0; i < width; ++i, ++bit) {
        var index = bit >> 3;
        var taken = index < reversed.Length && (reversed[index] & (0x80 >> (bit & 7))) != 0;
        value = (value << 1) | (taken ? 1UL : 0UL);
      }

      return value + bias;
    }

    var inode = Next(format.Bits[0], format.Offsets[0]);
    var offset = Next(format.Bits[1], format.Offsets[1]);
    var snapshot = Next(format.Bits[2], format.Offsets[2]);
    var size = Next(format.Bits[3], format.Offsets[3]);
    return (new Bpos(inode, offset, (uint)snapshot), (uint)size);
  }

  private byte[]? ReadAt(long offset, int length) {
    if (offset < 0 || length <= 0 || offset + length > this._image.Length) return null;

    var bytes = new byte[length];
    this._image.Position = offset;
    this._image.ReadExactly(bytes);
    return bytes;
  }
}
