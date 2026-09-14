#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nss;

/// <summary>
/// Conservative scan-based reader for the quiescent NSS profile documented in
/// docs/NSS-ON-DISK.md. It never guesses tree roots: only structurally valid
/// DirH entries and LEAF object records are accepted, and file names/ZIDs must
/// agree before payload extents are exposed.
/// </summary>
internal sealed class NssNativeScanner {
  private const int BlockSize = 4096;
  private const ulong RootParentZid = 0x7f;
  private readonly Stream _image;
  private readonly List<NssEntry> _entries = [];

  private sealed record DirectoryCandidate(ulong Zid, ulong ParentZid, string Name, ushort Counter, long BlockOffset);
  private sealed record ObjectCandidate(ulong Zid, string Name, long Size, uint StoredStartBlock, uint BlockCount, ushort Counter, long BlockOffset);

  public NssNativeScanner(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek) return;
    this._image = image;
    this.Scan();
  }

  public IReadOnlyList<NssEntry> Entries => this._entries;

  public byte[] Extract(NssEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!entry.IsNativeNssEntry || entry.IsDirectory) return [];
    if (entry.NativeBlockCount == 0)
      return entry.Size == 0 ? [] : throw new InvalidDataException("NSS file has no data extent.");
    if (entry.Size is < 0 or > int.MaxValue)
      throw new NotSupportedException("NSS extraction currently materializes a single file in memory and is limited to Int32.MaxValue bytes.");

    var offset = checked((long)entry.NativeAbsoluteStartBlock * BlockSize);
    var capacity = checked((long)entry.NativeBlockCount * BlockSize);
    if (entry.Size > capacity || offset < 0 || offset > this._image.Length - capacity)
      throw new InvalidDataException("NSS extent points outside the image or is shorter than the logical file size.");

    var result = new byte[checked((int)entry.Size)];
    var old = this._image.Position;
    try {
      this._image.Position = offset;
      ReadExactly(this._image, result);
      return result;
    } finally {
      this._image.Position = old;
    }
  }

  private void Scan() {
    var directories = new Dictionary<ulong, DirectoryCandidate>();
    var objects = new Dictionary<ulong, ObjectCandidate>();
    var block = new byte[BlockSize];
    var old = this._image.Position;
    try {
      for (long offset = 0; offset <= this._image.Length - BlockSize; offset += BlockSize) {
        this._image.Position = offset;
        if (!TryReadExactly(this._image, block)) break;
        var tag = block.AsSpan(0, 4);
        if (tag.SequenceEqual("DirH"u8))
          ParseDirectoryBlock(block, offset, directories);
        else if (tag.SequenceEqual("LEAF"u8))
          ParseLeafBlock(block, offset, objects);
      }
    } finally {
      this._image.Position = old;
    }

    var parentIds = directories.Values.Select(candidate => candidate.ParentZid).ToHashSet();
    var paths = new Dictionary<ulong, string>();
    foreach (var candidate in directories.Values.OrderBy(candidate => candidate.Zid)) {
      var path = ResolvePath(candidate.Zid, directories, paths, []);
      if (path is null) continue;
      var isDirectory = parentIds.Contains(candidate.Zid);
      if (isDirectory) {
        this._entries.Add(new NssEntry {
          Name = path,
          Size = 0,
          IsDirectory = true,
          IsNativeNssEntry = true,
          NativeZid = candidate.Zid,
          NativeParentZid = candidate.ParentZid,
        });
        continue;
      }

      if (!objects.TryGetValue(candidate.Zid, out var obj)) continue;
      if (!string.Equals(candidate.Name, obj.Name, StringComparison.Ordinal)) continue;
      var absoluteStart = checked((ulong)obj.StoredStartBlock + 8UL);
      var extentBytes = checked((ulong)obj.BlockCount * BlockSize);
      if (obj.Size < 0 || (ulong)obj.Size > extentBytes) continue;
      var startOffset = checked(absoluteStart * BlockSize);
      if (startOffset > (ulong)this._image.Length || extentBytes > (ulong)this._image.Length - startOffset) continue;

      this._entries.Add(new NssEntry {
        Name = path,
        Size = obj.Size,
        IsDirectory = false,
        IsNativeNssEntry = true,
        NativeZid = candidate.Zid,
        NativeParentZid = candidate.ParentZid,
        NativeAbsoluteStartBlock = absoluteStart,
        NativeBlockCount = obj.BlockCount,
      });
    }

    this._entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
  }

  private static void ParseDirectoryBlock(
      ReadOnlySpan<byte> block,
      long blockOffset,
      Dictionary<ulong, DirectoryCandidate> output) {
    var count = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(6, 2));
    if (count == 0 || count > 59) return;
    var counter = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
    if (BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(10, 2)) != 0xC000) return;

    var seenOffsets = new HashSet<uint>();
    for (var i = 0; i < count; ++i) {
      var indexOffset = BlockSize - 4 * (i + 1);
      var relative = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(indexOffset, 4));
      if (!seenOffsets.Add(relative) || relative > BlockSize - 48 - 64) continue;
      var entryOffset = checked(48 + (int)relative);
      var nameChars = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(entryOffset + 42, 2));
      if (nameChars == 0 || nameChars > 10) continue;
      var nameBytes = checked(nameChars * 2);
      if (entryOffset + 44 + nameBytes > entryOffset + 64) continue;
      var name = DecodeName(block.Slice(entryOffset + 44, nameBytes));
      if (!IsUsableName(name)) continue;

      var zid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(entryOffset + 8, 8));
      var parent = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(entryOffset + 24, 8));
      if (zid == 0) continue;
      var candidate = new DirectoryCandidate(zid, parent, name, counter, blockOffset);
      if (!output.TryGetValue(zid, out var current)
          || candidate.Counter > current.Counter
          || candidate.Counter == current.Counter && candidate.BlockOffset > current.BlockOffset)
        output[zid] = candidate;
    }
  }

  private static void ParseLeafBlock(
      ReadOnlySpan<byte> block,
      long blockOffset,
      Dictionary<ulong, ObjectCandidate> output) {
    if (BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(4, 2)) != 0x0003) return;
    var counter = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
    if (BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(10, 2)) != 0xC000) return;

    var seenOffsets = new HashSet<ushort>();
    for (var indexOffset = BlockSize - 2; indexOffset >= BlockSize - 1024; indexOffset -= 2) {
      var relative = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(indexOffset, 2));
      if (relative > 0x0FD8 || !seenOffsets.Add(relative)) break;
      var recordOffset = 0x28 + relative;
      if (recordOffset < 0x28 || recordOffset + 272 > indexOffset) break;
      var marker = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(recordOffset + 2, 2));
      if (marker == 0x3030) continue;
      if (marker != 0x444E) break;

      var zid = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(recordOffset + 8, 8));
      var sizeRaw = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(recordOffset + 24, 8));
      if (zid == 0 || sizeRaw > long.MaxValue) continue;
      var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(recordOffset + 88, 4));
      var storedStart = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(recordOffset + 92, 4));
      var nameChars = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(recordOffset + 140, 2));
      if (nameChars == 0 || nameChars > 65) continue;
      var nameBytes = checked(nameChars * 2);
      if (recordOffset + 142 + nameBytes > recordOffset + 272) continue;
      var name = DecodeName(block.Slice(recordOffset + 142, nameBytes));
      if (!IsUsableName(name)) continue;

      var candidate = new ObjectCandidate(zid, name, checked((long)sizeRaw), storedStart, blockCount, counter, blockOffset);
      if (!output.TryGetValue(zid, out var current)
          || candidate.Counter > current.Counter
          || candidate.Counter == current.Counter && candidate.BlockOffset > current.BlockOffset)
        output[zid] = candidate;
    }
  }

  private static string? ResolvePath(
      ulong zid,
      IReadOnlyDictionary<ulong, DirectoryCandidate> directories,
      Dictionary<ulong, string> cache,
      HashSet<ulong> active) {
    if (cache.TryGetValue(zid, out var cached)) return cached;
    if (!active.Add(zid) || !directories.TryGetValue(zid, out var current)) return null;
    try {
      string path;
      if (current.ParentZid == RootParentZid) {
        path = current.Name;
      } else {
        var parent = ResolvePath(current.ParentZid, directories, cache, active);
        if (parent is null) return null;
        path = parent + "/" + current.Name;
      }
      cache[zid] = path;
      return path;
    } finally {
      active.Remove(zid);
    }
  }

  private static bool IsUsableName(string name)
    => name.Length > 0
       && name is not "." and not ".."
       && name.IndexOfAny(['/', '\\', '\0']) < 0;

  private static string DecodeName(ReadOnlySpan<byte> bytes)
    => Encoding.Unicode.GetString(bytes).TrimEnd('\0');

  private static bool TryReadExactly(Stream source, Span<byte> destination) {
    var done = 0;
    while (done < destination.Length) {
      var read = source.Read(destination[done..]);
      if (read == 0) return false;
      done += read;
    }
    return true;
  }

  private static void ReadExactly(Stream source, Span<byte> destination) {
    if (!TryReadExactly(source, destination)) throw new EndOfStreamException();
  }
}
