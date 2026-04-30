using System.IO.Compression;
using System.Text;

namespace FileFormat.Hpi;

/// <summary>
/// Writes the unencrypted, zlib-only subset of Total Annihilation HPI archives.
/// All file payloads are split into 64 KB SQSH chunks and compressed with zlib;
/// chunks that fail to shrink fall back to stored to avoid bloat.
/// </summary>
public sealed class HpiWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, byte[] Data)> _files = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>Initializes a new <see cref="HpiWriter"/>.</summary>
  /// <param name="stream">The output stream. Must be writable and seekable.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public HpiWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanWrite || !stream.CanSeek)
      throw new ArgumentException("HpiWriter requires a writable, seekable stream.", nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file to the archive at the given forward-slash path.</summary>
  /// <param name="path">Forward-slash path (e.g. <c>"units/armcom.fbi"</c>); leading slashes and backslashes are normalized.</param>
  /// <param name="data">The raw file bytes.</param>
  public void AddFile(string path, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((NormalizePath(path), data));
  }

  /// <summary>Flushes everything to the stream and finalizes the archive.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // Pass 1: reserve the 20-byte header. We backpatch DirectoryStart and DirectorySize at the end.
    this._stream.Position = 0;
    Span<byte> headerScratch = stackalloc byte[HpiConstants.HeaderSize];
    headerScratch.Clear();
    BitConverter.TryWriteBytes(headerScratch[0..4], HpiConstants.Magic);
    BitConverter.TryWriteBytes(headerScratch[4..8], HpiConstants.VersionTaClassic);
    // headerKey = 0 (unencrypted), dirSize and dirStart filled in later.
    this._stream.Write(headerScratch);

    // Pass 2: build the directory tree from the flat path list. Sorting keeps output deterministic
    // and makes recursion straightforward (siblings appear contiguously).
    var sortedFiles = this._files
      .OrderBy(f => f.Path, StringComparer.Ordinal)
      .ToList();
    var root = BuildTree(sortedFiles);

    // Pass 3: write all file data blocks first; record offsets keyed by path so the directory entries
    // (written later) can point at them.
    this._fileDataOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var (path, data) in sortedFiles) {
      this._fileDataOffsets[path] = this._stream.Position;
      WriteFileDataBlock(data);
    }

    // Pass 4: figure out where the string table will land. The tree serializer writes absolute
    // name offsets, so we compute the directory section's exact byte size up front, then place the
    // string table immediately after it. This breaks the chicken-and-egg: we know name offsets before
    // we ever write a directory record.
    var dirSectionSize = ComputeDirectorySize(root);
    var dirSectionStart = this._stream.Position;
    var stringsStart = dirSectionStart + dirSectionSize;

    var stringTable = new MemoryStream();
    var nameOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
    CollectStrings(root, stringsStart, stringTable, nameOffsets);

    // Pass 5: serialize the directory tree at dirSectionStart. The recursive serializer leaves the
    // stream cursor in the middle of the section, so we sanity-check the advancing cursor and then
    // explicitly seek to stringsStart before writing the string table.
    this._stream.Position = dirSectionStart;
    var dirCursor = dirSectionStart;
    SerializeDirectory(root, ref dirCursor, nameOffsets);

    if (dirCursor != stringsStart)
      throw new InvalidOperationException($"HPI writer directory layout drift: expected directory end at 0x{stringsStart:X} but cursor reached 0x{dirCursor:X}.");

    // Pass 6: append the string table.
    this._stream.Position = stringsStart;
    var stringBytes = stringTable.ToArray();
    this._stream.Write(stringBytes);

    // Pass 7: backpatch the header. DirectorySize covers the directory tree portion only,
    // matching what readers verify against the entry-walk; the string table sits after it.
    var dirSize = (uint)dirSectionSize;
    var dirStart = (uint)dirSectionStart;
    this._stream.Position = 8;
    Span<byte> patch = stackalloc byte[12];
    BitConverter.TryWriteBytes(patch[0..4], dirSize);
    BitConverter.TryWriteBytes(patch[4..8], 0u); // headerKey
    BitConverter.TryWriteBytes(patch[8..12], dirStart);
    this._stream.Write(patch);

    this._stream.Position = this._stream.Length;
  }

  // -------- tree types --------

  private sealed class DirNode {
    public string Name = "";
    public List<DirNode> SubDirs = [];
    public List<FileNode> Files = [];
  }

  private sealed class FileNode {
    public string Name = "";
    public string FullPath = "";
  }

  // -------- tree build --------

  private static DirNode BuildTree(IEnumerable<(string Path, byte[] Data)> files) {
    var root = new DirNode { Name = "" };
    foreach (var (path, _) in files) {
      var parts = path.Split('/');
      var node = root;
      for (var i = 0; i < parts.Length - 1; ++i) {
        var seg = parts[i];
        var existing = node.SubDirs.FirstOrDefault(d => d.Name == seg);
        if (existing == null) {
          existing = new DirNode { Name = seg };
          node.SubDirs.Add(existing);
        }
        node = existing;
      }
      node.Files.Add(new FileNode { Name = parts[^1], FullPath = path });
    }
    return root;
  }

  // -------- size + string collection passes --------

  private static long ComputeDirectorySize(DirNode root) {
    long total = 0;
    Visit(root);
    return total;

    void Visit(DirNode dir) {
      total += HpiConstants.DirectoryHeaderSize;
      total += (long)(dir.SubDirs.Count + dir.Files.Count) * HpiConstants.EntryRecordSize;
      foreach (var sub in dir.SubDirs)
        Visit(sub);
    }
  }

  private static void CollectStrings(DirNode root, long stringsBaseOffset, MemoryStream table, Dictionary<string, long> nameOffsets) {
    Visit(root);

    void Visit(DirNode dir) {
      foreach (var sub in dir.SubDirs) {
        AddName(sub.Name);
        Visit(sub);
      }
      foreach (var file in dir.Files)
        AddName(file.Name);
    }

    void AddName(string name) {
      if (nameOffsets.ContainsKey(NameKey(name)))
        return;
      var off = stringsBaseOffset + table.Position;
      var bytes = Encoding.ASCII.GetBytes(name);
      table.Write(bytes);
      table.WriteByte(0);
      nameOffsets[NameKey(name)] = off;
    }
  }

  // Each directory may contain multiple files/dirs that happen to share a leaf name with peers in other directories;
  // dedup by raw name string is fine because HPI entry records reference the cstring by absolute offset.
  private static string NameKey(string name) => name;

  // -------- directory + file serialization --------

  private void SerializeDirectory(DirNode dir, ref long cursor, Dictionary<string, long> nameOffsets) {
    var entryCount = dir.SubDirs.Count + dir.Files.Count;
    var headerOffset = cursor;
    var entryListOffset = headerOffset + HpiConstants.DirectoryHeaderSize;

    // Write this directory's 8-byte header.
    this._stream.Position = headerOffset;
    Span<byte> hdr = stackalloc byte[HpiConstants.DirectoryHeaderSize];
    BitConverter.TryWriteBytes(hdr[0..4], (uint)entryCount);
    BitConverter.TryWriteBytes(hdr[4..8], (uint)entryListOffset);
    this._stream.Write(hdr);

    // Reserve the contiguous entry block, then fill in. Sub-directory headers go *after* the entry block,
    // so we advance the cursor past it before recursing.
    var afterEntries = entryListOffset + (long)entryCount * HpiConstants.EntryRecordSize;
    cursor = afterEntries;

    // First, pre-place each child's header offset so we can write the parent's entry records correctly,
    // then recurse to actually serialize each child.
    var subDirHeaderOffsets = new long[dir.SubDirs.Count];
    for (var i = 0; i < dir.SubDirs.Count; ++i) {
      subDirHeaderOffsets[i] = cursor;
      SerializeDirectory(dir.SubDirs[i], ref cursor, nameOffsets);
    }

    // Now write the entry records. Order: subdirs first, then files. Stable within each group.
    this._stream.Position = entryListOffset;
    Span<byte> rec = stackalloc byte[HpiConstants.EntryRecordSize];
    for (var i = 0; i < dir.SubDirs.Count; ++i) {
      var sub = dir.SubDirs[i];
      BitConverter.TryWriteBytes(rec[0..4], (uint)nameOffsets[NameKey(sub.Name)]);
      BitConverter.TryWriteBytes(rec[4..8], (uint)subDirHeaderOffsets[i]);
      rec[8] = 1; // directory
      this._stream.Write(rec);
    }
    foreach (var file in dir.Files) {
      var dataOff = this._fileDataOffsets[file.FullPath];
      BitConverter.TryWriteBytes(rec[0..4], (uint)nameOffsets[NameKey(file.Name)]);
      BitConverter.TryWriteBytes(rec[4..8], (uint)dataOff);
      rec[8] = 0; // file
      this._stream.Write(rec);
    }
  }

  // The file-data offsets dictionary is referenced from within SerializeDirectory; declared as a field so the
  // recursive method can see it without threading it through. Populated in Finish() before serialization.
  private Dictionary<string, long> _fileDataOffsets = new();

  private void WriteFileDataBlock(byte[] data) {
    // Stored files (small payloads) skip the SQSH chunk overhead entirely — TA's loader handles either form.
    if (data.Length == 0 || data.Length < 64) {
      WriteUInt32((uint)data.Length);
      WriteUInt32(HpiConstants.CompressionStored);
      if (data.Length > 0)
        this._stream.Write(data);
      return;
    }

    WriteUInt32((uint)data.Length);
    WriteUInt32(HpiConstants.CompressionZlib);

    var offset = 0;
    while (offset < data.Length) {
      var thisChunkSize = Math.Min(HpiConstants.MaxChunkSize, data.Length - offset);
      WriteChunk(data, offset, thisChunkSize);
      offset += thisChunkSize;
    }
  }

  private void WriteChunk(byte[] data, int offset, int length) {
    var compressed = ZlibCompress(data, offset, length);
    byte compressionFlag;
    byte[] payload;

    // Fall back to stored if zlib didn't actually shrink the chunk — common for already-compressed assets
    // and avoids paying the framing overhead twice.
    if (compressed.Length >= length) {
      compressionFlag = HpiConstants.CompressionStored;
      payload = new byte[length];
      Buffer.BlockCopy(data, offset, payload, 0, length);
    } else {
      compressionFlag = HpiConstants.CompressionZlib;
      payload = compressed;
    }

    var checksum = ComputeChecksum(payload);

    Span<byte> hdr = stackalloc byte[HpiConstants.ChunkHeaderSize];
    BitConverter.TryWriteBytes(hdr[0..4], HpiConstants.ChunkMagic);
    hdr[4] = HpiConstants.ChunkMarkerDefault;
    hdr[5] = compressionFlag;
    hdr[6] = HpiConstants.EncryptPlain;
    BitConverter.TryWriteBytes(hdr[7..11], (uint)payload.Length);
    BitConverter.TryWriteBytes(hdr[11..15], (uint)length);
    BitConverter.TryWriteBytes(hdr[15..19], checksum);
    this._stream.Write(hdr);
    this._stream.Write(payload);
  }

  private static byte[] ZlibCompress(byte[] data, int offset, int length) {
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      z.Write(data, offset, length);
    return ms.ToArray();
  }

  private static uint ComputeChecksum(ReadOnlySpan<byte> payload) {
    // HAPI's documented "checksum" is the byte-sum of the compressed payload, taken mod 2^32.
    var sum = 0u;
    foreach (var b in payload)
      sum += b;
    return sum;
  }

  private void WriteUInt32(uint value) {
    Span<byte> buf = stackalloc byte[4];
    BitConverter.TryWriteBytes(buf, value);
    this._stream.Write(buf);
  }

  private static string NormalizePath(string path) {
    var s = path.Replace('\\', '/').TrimStart('/');
    return s;
  }

  // -------- IDisposable --------

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
