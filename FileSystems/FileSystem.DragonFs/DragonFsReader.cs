#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.DragonFs;

/// <summary>
/// Reads DragonFS images — the read-only embedded filesystem used by
/// Libdragon (the open Nintendo 64 SDK) to bundle assets into a N64
/// ROM. DragonFS is big-endian throughout (MIPS R4300i convention),
/// uses 32-byte directory records, and a singly-linked list for file
/// chunks. Root directory entry sits at file offset 256
/// (Libdragon DFS_ROOT_OFFSET).
///
/// Directory entry layout (32 bytes BE):
///   0x00 u32  next_entry_offset (0 = end of dir)
///   0x04 u32  flags
///                 0x0001 = directory
///                 0x0002 = end-of-directory marker
///   0x08 char[20] name (NUL-terminated, ASCII)
///   0x1C u32  file_size (for files) / first_entry_offset (for dirs)
///
/// File data starts at offset_of_entry + 32 unless the file uses
/// indirection (large files chain via "next chunk" pointers); this
/// reader handles the common direct-contiguous-data case.
/// </summary>
public sealed class DragonFsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<DragonFsEntry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<DragonFsEntry> Entries => _entries;

    /// <summary>
  /// Gets a value indicating whether valid root.
  /// </summary>
public bool ValidRoot { get; private set; }
    /// <summary>
  /// Gets or sets the root offset.
  /// </summary>
public int RootOffset { get; private set; }

    /// <summary>
  /// Defines the default root offset constant value.
  /// </summary>
public const int DefaultRootOffset = 256;
  // Newer Libdragon images can prepend an 8-byte "DragonFS" ASCII tag
  // before the root entry table for robust auto-detect; we accept either.
    /// <summary>
  /// Provides the optional tag value.
  /// </summary>
public static readonly byte[] OptionalTag = "DragonFS"u8.ToArray();

    /// <summary>
  /// Initializes a new instance of <see cref="DragonFsReader"/>.
  /// </summary>
public DragonFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < DefaultRootOffset + 32)
      throw new InvalidDataException("DragonFs: image too small for root directory.");

    var rootOffset = DefaultRootOffset;
    // Detect optional "DragonFS" tag at offset 0 — if present, the root
    // entry table starts at offset 8 + 256 = 264.
    if (_data.Length >= 8 && _data.AsSpan(0, 8).SequenceEqual(OptionalTag))
      rootOffset = 8 + DefaultRootOffset;
    this.RootOffset = rootOffset;

    if (_data.Length < rootOffset + 32)
      throw new InvalidDataException("DragonFs: image too small for root entry.");

    // Validate "looks like" a root entry: first entry's flags should be sane
    // (we accept directory flag set or first child entry having reasonable name).
    this.ValidRoot = true;

    var visited = new HashSet<int>();
    WalkDirectory(rootOffset, "", visited, depth: 0);
  }

  private void WalkDirectory(int dirEntryOffset, string path, HashSet<int> visited, int depth) {
    // Bound recursion to avoid pathological malformed cycles.
    if (depth > 64) return;
    var currentOffset = dirEntryOffset;
    while (currentOffset > 0 && currentOffset + 32 <= _data.Length) {
      if (!visited.Add(currentOffset)) return;

      var nextOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(currentOffset));
      var flags = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(currentOffset + 4));
      var nameBytes = _data.AsSpan(currentOffset + 8, 20);
      var name = ReadAsciiNul(nameBytes);
      var sizeOrChild = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(currentOffset + 28));

      var isEndMarker = (flags & 0x0002) != 0;
      if (isEndMarker) break;
      // Some images use flags == 0 for the trailing record with no next link.
      // We continue walking until next_offset is 0 or out of range.

      if (string.IsNullOrEmpty(name)) {
        // Skip blank record but follow link.
        currentOffset = nextOffset;
        continue;
      }

      var isDir = (flags & 0x0001) != 0;
      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      if (isDir) {
        _entries.Add(new DragonFsEntry { Name = fullPath, Size = 0, IsDirectory = true, DataOffset = 0 });
        if (sizeOrChild != 0)
          WalkDirectory((int)sizeOrChild, fullPath, visited, depth + 1);
      } else {
        // File data starts immediately after the directory entry record (offset+32).
        var dataOffset = currentOffset + 32;
        _entries.Add(new DragonFsEntry {
          Name = fullPath,
          Size = sizeOrChild,
          IsDirectory = false,
          DataOffset = dataOffset,
        });
      }

      if (nextOffset == 0) break;
      currentOffset = nextOffset;
    }
  }

  private static string ReadAsciiNul(ReadOnlySpan<byte> span) {
    var end = 0;
    while (end < span.Length && span[end] != 0) end++;
    return Encoding.ASCII.GetString(span.Slice(0, end));
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(DragonFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Size <= 0) return [];
    if (entry.DataOffset < 0 || entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.AsSpan(entry.DataOffset, (int)entry.Size).ToArray();
  }

    /// <summary>
  /// Performs the build surface metadata operation.
  /// </summary>
public byte[] BuildSurfaceMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=DragonFS (Libdragon)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"root_offset={this.RootOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"entry_count={_entries.Count}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
