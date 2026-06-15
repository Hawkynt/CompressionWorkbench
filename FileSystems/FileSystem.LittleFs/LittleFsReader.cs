#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.LittleFs.LittleFsFormat;

namespace FileSystem.LittleFs;

/// <summary>
/// A described file inside a littlefs image: its full slash-joined path plus the
/// information needed to read its bytes (an inline payload or a CTZ skip-list head).
/// </summary>
public sealed class LittleFsFileEntry {
  public required string Path { get; init; }
  public long Size { get; init; }
  internal byte[]? Inline { get; init; }
  internal uint CtzHead { get; init; }
  internal bool IsCtz { get; init; }
}

/// <summary>
/// Reads a littlefs v2 image: walks each directory's metadata-pair commit log
/// (validating the commit CRC), follows hard-tail and directory-struct links, and
/// resolves file structs (inline payloads and CTZ skip-lists) into byte content.
/// </summary>
/// <remarks>
/// This is a focused decoder for the subset emitted by <see cref="LittleFsWriter"/>
/// — a single-commit metadata pair per directory, inline structs for small files,
/// CTZ skip-lists for the rest. It validates structure against the on-disk format
/// (revision, delta-encoded tags, commit CRC) rather than assuming fixed offsets.
/// </remarks>
public sealed class LittleFsReader {
  private readonly byte[] _image;
  private readonly uint _blockSize;
  private readonly List<LittleFsFileEntry> _files = new();

  public IReadOnlyList<LittleFsFileEntry> Files => this._files;
  public uint BlockSize => this._blockSize;

  public LittleFsReader(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    this._image = image;

    var sb = LittleFsSuperblock.TryParse(image);
    if (!sb.Valid)
      throw new InvalidDataException("not a recognised littlefs image (no valid superblock).");
    this._blockSize = sb.BlockSize;

    // Root directory is the metadata pair at blocks 0,1.
    this.WalkDirectory(0, 1, parentPath: string.Empty, new HashSet<ulong>());
  }

  /// <summary>Returns the bytes of <paramref name="entry"/>.</summary>
  public byte[] ReadFile(LittleFsFileEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!entry.IsCtz)
      return entry.Inline ?? [];
    return this.ReadCtz(entry.CtzHead, (uint)entry.Size);
  }

  private void WalkDirectory(uint blockA, uint blockB, string parentPath, HashSet<ulong> visited) {
    var key = ((ulong)blockA << 32) | blockB;
    if (!visited.Add(key)) return;

    // Pick the valid half with the higher revision (both halves carry the same
    // commit here, but honour the ping-pong rule for robustness).
    var a = this.TryReadCommit(blockA, out var revA, out var entriesA);
    var b = this.TryReadCommit(blockB, out var revB, out var entriesB);
    if (!a && !b) return;

    List<CommitEntry> entries;
    if (a && (!b || revA >= revB)) entries = entriesA!;
    else entries = entriesB!;

    // Group tags by id: each id is one directory entry (name + struct).
    var byId = new Dictionary<uint, EntryAccumulator>();
    foreach (var e in entries) {
      if (!byId.TryGetValue(e.Id, out var acc)) {
        acc = new EntryAccumulator();
        byId[e.Id] = acc;
      }
      acc.Apply(e);
    }

    foreach (var (id, acc) in byId.OrderBy(kv => kv.Key)) {
      if (id == 0 && acc.IsSuperblock) continue; // the superblock entry, not a file
      if (acc.Name == null) continue;

      var fullPath = parentPath.Length == 0 ? acc.Name : parentPath + "/" + acc.Name;

      if (acc.IsDirectory && acc.DirPair is { } pair) {
        this.WalkDirectory(pair.Item1, pair.Item2, fullPath, visited);
      } else if (acc.IsRegular) {
        if (acc.Inline != null) {
          this._files.Add(new LittleFsFileEntry {
            Path = fullPath, Size = acc.Inline.Length, Inline = acc.Inline, IsCtz = false,
          });
        } else if (acc.Ctz is { } ctz) {
          this._files.Add(new LittleFsFileEntry {
            Path = fullPath, Size = ctz.Size, CtzHead = ctz.Head, IsCtz = true,
          });
        }
      }
    }
  }

  /// <summary>
  /// Parses the first commit in <paramref name="blockIndex"/>, returning its tag
  /// entries iff the commit CRC validates.
  /// </summary>
  private bool TryReadCommit(uint blockIndex, out uint revision, out List<CommitEntry>? entries) {
    revision = 0;
    entries = null;

    var blockStart = (long)blockIndex * this._blockSize;
    if (blockStart + 4 > this._image.Length) return false;

    var span = this._image.AsSpan((int)blockStart, (int)this._blockSize);
    revision = BinaryPrimitives.ReadUInt32LittleEndian(span);

    var off = 4;
    var ptag = 0xFFFFFFFFu;
    var crc = Crc(0xFFFFFFFFu, span.Slice(0, 4));
    var collected = new List<CommitEntry>();

    while (off + 4 <= span.Length) {
      var onDisk = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(off, 4));
      var tag = (onDisk ^ ptag) & 0x7FFFFFFF;
      crc = Crc(crc, span.Slice(off, 4));
      off += 4;

      var type = TagType(tag);
      var id = TagId(tag);
      var len = (int)TagLength(tag);

      if ((type & 0x700) == TypeCrc) {
        // Commit-CRC tag: the next `len` bytes start with the stored CRC dword.
        if (off + 4 > span.Length) return false;
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off, 4));
        if (stored != crc) return false;
        entries = collected;
        return true;
      }

      if (off + len > span.Length) return false;
      collected.Add(new CommitEntry(type, id, span.Slice(off, len).ToArray()));
      crc = Crc(crc, span.Slice(off, len));
      off += len;
      ptag = tag;
    }

    return false; // ran off the block without a terminating CRC tag
  }

  private byte[] ReadCtz(uint head, uint size) {
    if (size == 0) return [];
    var blockSize = (int)this._blockSize;
    var result = new byte[size];

    // The head is the LAST block of the skip-list (highest file-order index).
    // Walk back via the first back-pointer (which always points to index-1) until
    // block 0, then reverse into file order.
    var indices = new List<uint>();
    var blockCountForFile = CtzBlockCount(size, (uint)blockSize);
    var cur = head;
    for (var i = (int)blockCountForFile - 1; i >= 0; --i) {
      indices.Add(cur);
      if (i == 0) break;
      var bStart = (long)cur * blockSize;
      cur = BinaryPrimitives.ReadUInt32LittleEndian(this._image.AsSpan((int)bStart, 4));
    }
    indices.Reverse(); // now in file order: index 0 .. n-1

    var written = 0;
    for (var i = 0; i < indices.Count; ++i) {
      var pointerCount = i == 0 ? 0 : (TrailingZeros((uint)i) + 1);
      var pointerBytes = pointerCount * 4;
      var bStart = (long)indices[i] * blockSize;
      var dataCap = blockSize - pointerBytes;
      var take = Math.Min(dataCap, (int)size - written);
      this._image.AsSpan((int)bStart + pointerBytes, take).CopyTo(result.AsSpan(written));
      written += take;
    }

    return result;
  }

  /// <summary>Number of CTZ data blocks needed for <paramref name="size"/> bytes.</summary>
  private static uint CtzBlockCount(uint size, uint blockSize) {
    uint blocks = 0, written = 0;
    var i = 0;
    while (written < size) {
      var pointerCount = i == 0 ? 0 : (TrailingZeros((uint)i) + 1);
      var cap = blockSize - (uint)(pointerCount * 4);
      written += Math.Min(cap, size - written);
      ++blocks;
      ++i;
    }
    return blocks;
  }

  private static int TrailingZeros(uint x) {
    var n = 0;
    while ((x & 1) == 0) { x >>= 1; ++n; }
    return n;
  }

  private readonly record struct CommitEntry(uint Type, uint Id, byte[] Data);

  /// <summary>Collects the name and struct tags belonging to one directory-entry id.</summary>
  private sealed class EntryAccumulator {
    public string? Name { get; private set; }
    public bool IsSuperblock { get; private set; }
    public bool IsDirectory { get; private set; }
    public bool IsRegular { get; private set; }
    public (uint, uint)? DirPair { get; private set; }
    public byte[]? Inline { get; private set; }
    public (uint Head, uint Size)? Ctz { get; private set; }

    public void Apply(CommitEntry e) {
      switch (e.Type) {
        case TypeSuperblock:
          this.IsSuperblock = true;
          this.Name = Encoding.ASCII.GetString(e.Data);
          break;
        case TypeDir:
          this.IsDirectory = true;
          this.Name = Encoding.ASCII.GetString(e.Data);
          break;
        case TypeReg:
          this.IsRegular = true;
          this.Name = Encoding.ASCII.GetString(e.Data);
          break;
        case TypeDirStruct when e.Data.Length >= 8:
          this.DirPair = (
            BinaryPrimitives.ReadUInt32LittleEndian(e.Data.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(e.Data.AsSpan(4, 4)));
          break;
        case TypeInlineStruct:
          this.Inline = e.Data;
          break;
        case TypeCtzStruct when e.Data.Length >= 8:
          this.Ctz = (
            BinaryPrimitives.ReadUInt32LittleEndian(e.Data.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(e.Data.AsSpan(4, 4)));
          break;
        default:
          break;
      }
    }
  }
}
