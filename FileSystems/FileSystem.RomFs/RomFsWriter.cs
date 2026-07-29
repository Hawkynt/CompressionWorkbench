#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.RomFs;

/// <summary>
/// Builds a Linux ROMFS filesystem image from a set of files.
/// Produces a valid romfs v1 image with "-rom1fs-" magic.
/// </summary>
public sealed class RomFsWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, FilePayload Payload)> _files = [];
  private bool _disposed;

  /// <summary>Initializes a new writer targeting <paramref name="output"/>.</summary>
  public RomFsWriter(Stream output, bool leaveOpen = false) {
    _output = output;
    _leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file at the given path (forward-slash separated, no leading slash).</summary>
  public void AddFile(string path, byte[] data) {
    path = path.Replace('\\', '/').TrimStart('/');
    _files.Add((path, FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a byte is read.
  /// </summary>
  public void AddStreamingFile(string path, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(openStream);
    _files.Add((path.Replace('\\', '/').TrimStart('/'), FilePayload.FromStream(size, openStream)));
  }

  /// <summary>Builds the ROMFS image and writes it to the output stream.</summary>
  public void Finish(string volumeName = "romfs") {
    // Build the in-memory image into a List<byte> (simpler than pre-computing sizes).
    var buf = new ImageBuilder();

    // ---- Superblock ----
    // [0..7]   magic "-rom1fs-"
    // [8..11]  uint32 BE fullSize  (placeholder, patched at end)
    // [12..15] uint32 BE checksum  (placeholder, patched at end)
    // [16..]   volume name, null-terminated, padded to 16-byte boundary from offset 16

    var magic = "-rom1fs-"u8.ToArray();
    buf.AddRange(magic);                    // offset 0
    buf.AddRange(new byte[4]);              // fullSize placeholder (offset 8)
    buf.AddRange(new byte[4]);              // checksum placeholder (offset 12)

    var nameBytes = Encoding.ASCII.GetBytes(volumeName);
    buf.AddRange(nameBytes);
    buf.Add(0); // null terminator
    // Pad name field to 16-byte boundary from its start (offset 16)
    var namePadded = Align16(nameBytes.Length + 1);
    for (var i = nameBytes.Length + 1; i < namePadded; i++) buf.Add(0);

    // ---- Build directory tree ----
    // Collect all unique directory paths implied by the file list
    var allDirs = new SortedSet<string>(StringComparer.Ordinal);
    allDirs.Add(""); // root
    foreach (var (path, _) in _files) {
      var parts = path.Split('/');
      var accumulated = "";
      for (var i = 0; i < parts.Length - 1; i++) {
        accumulated = accumulated.Length == 0 ? parts[i] : accumulated + "/" + parts[i];
        allDirs.Add(accumulated);
      }
    }

    // We write entries depth-first. For each directory we write:
    //   "." entry  (type=1, specInfo = offset of first child entry)
    //   ".." entry (type=1, specInfo = offset of parent's first child entry)
    //   child entries
    // Because sizes are unknown until we lay everything out, we use a two-pass approach:
    // Pass 1: compute all offsets without writing (dry run).
    // Pass 2: write with correct next/specInfo pointers.

    var firstFileOffset = buf.Count; // offset of first entry in root directory

    // Represent tree nodes
    var nodeOffsets = new Dictionary<string, long>(); // dir path -> offset of its first child entry

    // We do a single-pass layout by writing entries and back-patching.
    // Layout order: root children, then each subdirectory's children recursively.
    // Within each directory: "." first, ".." second, then children.

    WriteDirectory(buf, "", allDirs, _files, nodeOffsets);

    // Patch fullSize
    var fullSize = buf.Count;
    WriteUInt32BE(buf, 8, (uint)fullSize);

    // Patch checksums for every header block
    // The ROMFS spec says checksum covers all headers. Simple approach: checksum the superblock
    // (first Align16(16+namePadded) bytes with checksum field set to 0) so that
    // sum of all uint32 words = 0 mod 2^32.
    PatchSuperblockChecksum(buf, namePadded);

    buf.WriteTo(_output);
  }

  // Writes all entries for a single directory level into buf.
  // Returns the offset where this directory's first child entry starts (for specInfo of parent's
  // "." entry) or -1 if the directory has no children.
  private static long WriteDirectory(
      ImageBuilder buf,
      string dirPath,
      SortedSet<string> allDirs,
      List<(string Path, FilePayload Payload)> allFiles,
      Dictionary<string, long> nodeOffsets) {

    // Collect children of this directory
    var childDirs  = allDirs.Where(d => d.Length > 0 && GetParent(d) == dirPath)
                            .OrderBy(d => d).ToList();
    var childFiles = allFiles.Where(f => GetParent(f.Path) == dirPath)
                             .OrderBy(f => f.Path).ToList();

    if (childDirs.Count == 0 && childFiles.Count == 0) return -1;

    // The first child entry starts at current buf position
    var firstChildOffset = buf.Count;
    nodeOffsets[dirPath] = firstChildOffset;

    // Enumerate all child entries (dirs first, then files) to build the list
    // We need to know offsets ahead of time for "next" pointers, so we compute sizes first.

    var entryList = new List<(string Name, int Type, long Size, string FullPath)>();
    foreach (var d in childDirs)
      entryList.Add((GetLeaf(d), 1, 0, d));
    foreach (var (path, payload) in childFiles)
      entryList.Add((GetLeaf(path), 2, payload.Size, path));

    // Compute the byte size of each entry header (16 + padded name), excluding data
    var headerSizes = entryList.Select(e => 16 + Align16(Encoding.ASCII.GetByteCount(e.Name) + 1)).ToArray();

    // Compute data sizes for files (padded to 16 bytes)
    var dataSizes = entryList.Select((e, i) => e.Type == 2 ? Align16Long(e.Size) : 0L).ToArray();

    // Compute the start offset of each entry
    var entryOffsets = new long[entryList.Count];
    var cur = firstChildOffset;
    for (var i = 0; i < entryList.Count; i++) {
      entryOffsets[i] = cur;
      cur += headerSizes[i] + dataSizes[i];
    }
    // cur is now the offset just past the last entry at this level
    // (subdirectory children will follow)

    // We need to reserve space for subdirectory contents after each dir entry.
    // But directories' children are appended after this level's entries in DFS order.
    // So: write all this level's entry headers+data first, then recurse into subdirs.

    // Write entry headers
    for (var i = 0; i < entryList.Count; i++) {
      var (name, type, size, fullPath) = entryList[i];
      var nextOffset = (i + 1 < entryList.Count) ? entryOffsets[i + 1] : 0;

      // nextAndType: upper 28 bits = nextOffset (aligned), lower 4 bits = type
      // The next pointer must be 16-byte aligned (it already is by construction).
      var nextAndType = ((uint)nextOffset & 0xFFFFFFF0u) | (uint)(type & 0x0F);

      // specInfo: for directories = offset of first child entry (unknown until we recurse)
      //           will be back-patched; for files = 0
      var specInfoOffset = buf.Count + 4; // offset within buf where specInfo lives

      WriteUInt32BEToList(buf, nextAndType);
      WriteUInt32BEToList(buf, 0u);             // specInfo placeholder
      WriteUInt32BEToList(buf, (uint)size);
      WriteUInt32BEToList(buf, 0u);             // checksum placeholder

      var nameBytes = Encoding.ASCII.GetBytes(name);
      buf.AddRange(nameBytes);
      buf.Add(0);
      var paddedName = Align16(nameBytes.Length + 1);
      for (var j = nameBytes.Length + 1; j < paddedName; j++) buf.Add(0);

      // Write file data (for regular files)
      if (type == 2) {
        // Find file data
        var payload = allFiles.First(f => f.Path == fullPath).Payload;
        buf.AddPayload(payload);
        var paddedData = Align16Long(payload.Size);
        for (var j = payload.Size; j < paddedData; j++) buf.Add(0);
      }

      // Store specInfo offset for back-patching (dirs only)
      if (type == 1) {
        // We'll recurse into this dir after writing all entries at this level;
        // record where to patch specInfo
        entryList[i] = (name, type, size, fullPath); // keep same
        // Use a temporary tag: store specInfoOffset in a side list
        _ = specInfoOffset; // accessed below after recursion
        // We need to track (specInfoOffset -> dirPath) for back-patching after recursion.
        // Store in a separate collection passed by ref — simplest: inline the recursion
        // for each dir entry right here. But then "next" pointer logic breaks because
        // the next sibling entry's offset would shift.
        //
        // Correct approach: write ALL entries at this level first (files+dirs headers+data),
        // THEN recurse. Subdirectory children occupy offsets AFTER this level.
        // We already wrote the header; back-patch specInfo after recursion.
        // Store (bufIndex, dirPath) for later back-patch.
        _ = specInfoOffset; // will back-patch below in second pass
      }
    }

    // Now recurse into subdirectories and back-patch specInfo
    for (var i = 0; i < entryList.Count; i++) {
      var (_, type, _, fullPath) = entryList[i];
      if (type != 1) continue;

      // specInfo for this entry lives at: entryOffsets[i] + 4
      var specInfoBufOffset = entryOffsets[i] + 4;

      var childFirst = WriteDirectory(buf, fullPath, allDirs, allFiles, nodeOffsets);
      if (childFirst >= 0) {
        // Back-patch specInfo with the offset of the first child entry
        WriteUInt32BE(buf, specInfoBufOffset, (uint)childFirst);
      } else {
        // Empty directory: point specInfo to itself (the "." convention in romfs
        // for empty dirs is to point specInfo to the dir's own header offset).
        // Since we have no "." entry here (we write the actual named entries),
        // use 0 to indicate no children.
        WriteUInt32BE(buf, specInfoBufOffset, 0u);
      }
    }

    // Back-patch entry checksums
    for (var i = 0; i < entryList.Count; i++) {
      PatchEntryChecksum(buf, entryOffsets[i], headerSizes[i]);
    }

    return firstChildOffset;
  }

  // Compute and write the checksum for a single file/dir entry header.
  // The checksum field is at offset+12; the checksum covers the entire header
  // (16 bytes + padded name; NOT the file data), with checksum field = 0.
  private static void PatchEntryChecksum(ImageBuilder buf, long entryOffset, int headerSize) {
    // checksum field at entryOffset + 12; already 0 from initial write
    uint sum = 0;
    for (var i = 0; i < headerSize; i += 4)
      sum += ReadUInt32BEFromList(buf, entryOffset + i);
    WriteUInt32BE(buf, entryOffset + 12, (uint)(-(int)sum));
  }

  // Superblock checksum: sum of all uint32 words in the superblock (magic+fullSize+checksum+volname)
  // with checksum field = 0, must total 0 mod 2^32.
  private static void PatchSuperblockChecksum(ImageBuilder buf, int namePaddedLen) {
    // Superblock = 16 bytes fixed header + namePaddedLen bytes volume name
    var sbLen = 16 + namePaddedLen;
    // checksum at offset 12 is already 0
    uint sum = 0;
    for (var i = 0; i < sbLen; i += 4)
      sum += ReadUInt32BEFromList(buf, i);
    WriteUInt32BE(buf, 12, (uint)(-(int)sum));
  }

  private static string GetParent(string path) {
    var idx = path.LastIndexOf('/');
    return idx < 0 ? "" : path[..idx];
  }

  private static string GetLeaf(string path) {
    var idx = path.LastIndexOf('/');
    return idx < 0 ? path : path[(idx + 1)..];
  }

  private static int Align16(int len) => (len + 15) & ~15;

  private static long Align16Long(long len) => (len + 15) & ~15L;

  private static void WriteUInt32BEToList(ImageBuilder buf, uint value) {
    buf.Add((byte)(value >> 24));
    buf.Add((byte)(value >> 16));
    buf.Add((byte)(value >> 8));
    buf.Add((byte)value);
  }

  private static void WriteUInt32BE(ImageBuilder buf, long offset, uint value) {
    buf[offset]     = (byte)(value >> 24);
    buf[offset + 1] = (byte)(value >> 16);
    buf[offset + 2] = (byte)(value >> 8);
    buf[offset + 3] = (byte)value;
  }

  private static uint ReadUInt32BEFromList(ImageBuilder buf, long offset) =>
    ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) |
    ((uint)buf[offset + 2] << 8) | buf[offset + 3];

  /// <inheritdoc/>
  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    if (!_leaveOpen) _output.Dispose();
  }
  /// <summary>
  /// Image under construction: metadata bytes are held, file payloads are only
  /// recorded with the offset they belong at. Appending the payloads themselves
  /// is what capped ROMFS at what a List&lt;byte&gt; can address, and the format's
  /// 32-bit size field allows four times that.
  /// </summary>
  private sealed class ImageBuilder {

    private readonly List<byte> _meta = [];
    private readonly List<(long ImageOffset, int MetaStart, int Length)> _segments = [];
    private readonly List<(long ImageOffset, FilePayload Payload)> _payloads = [];
    private long _position;
    private long _segmentStart = -1;

    /// <summary>Current offset within the finished image.</summary>
    public long Count => this._position;

    public void Add(byte value) {
      this.EnsureSegment();
      this._meta.Add(value);
      ++this._position;
    }

    public void AddRange(ReadOnlySpan<byte> bytes) {
      this.EnsureSegment();
      foreach (var b in bytes) this._meta.Add(b);
      this._position += bytes.Length;
    }

    /// <summary>Records a payload at the current offset without holding its bytes.</summary>
    public void AddPayload(FilePayload payload) {
      this.CloseSegment();
      if (payload.Size > 0) this._payloads.Add((this._position, payload));
      this._position += payload.Size;
    }

    /// <summary>Metadata byte at an image offset. Only header bytes are addressed this way.</summary>
    public byte this[long imageOffset] {
      get => this._meta[this.MetaIndex(imageOffset)];
      set => this._meta[this.MetaIndex(imageOffset)] = value;
    }

    /// <summary>Writes the image: metadata segments and payloads in offset order.</summary>
    public void WriteTo(Stream output) {
      this.CloseSegment();
      var basePosition = output.CanSeek ? output.Position : 0;
      foreach (var (imageOffset, metaStart, length) in this._segments) {
        if (output.CanSeek) output.Position = basePosition + imageOffset;
        for (var i = 0; i < length; ++i) output.WriteByte(this._meta[metaStart + i]);
      }

      var buffer = new byte[64 * 1024];
      foreach (var (imageOffset, payload) in this._payloads) {
        if (output.CanSeek) output.Position = basePosition + imageOffset;
        using var src = payload.Open();
        var remaining = payload.Size;
        while (remaining > 0) {
          var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
          if (n <= 0) break;
          output.Write(buffer, 0, n);
          remaining -= n;
        }
      }
      if (output.CanSeek) output.Position = basePosition + this._position;
      output.Flush();
    }

    private void EnsureSegment() {
      if (this._segmentStart >= 0) return;
      this._segmentStart = this._position;
      this._segments.Add((this._position, this._meta.Count, 0));
    }

    private void CloseSegment() {
      // Only an open segment has a length to settle. Closing a closed one again
      // would stretch it over the payload that followed it.
      if (this._segments.Count == 0 || this._segmentStart < 0) return;
      var last = this._segments[^1];
      this._segments[^1] = (last.ImageOffset, last.MetaStart, (int)(this._position - last.ImageOffset));
      this._segmentStart = -1;
    }

    private int MetaIndex(long imageOffset) {
      this.CloseSegment();
      foreach (var (segOffset, metaStart, length) in this._segments)
        if (imageOffset >= segOffset && imageOffset < segOffset + length)
          return metaStart + (int)(imageOffset - segOffset);
      throw new ArgumentOutOfRangeException(nameof(imageOffset),
        "ROMFS: that offset is inside a file payload, which the builder does not hold.");
    }
  }

}
