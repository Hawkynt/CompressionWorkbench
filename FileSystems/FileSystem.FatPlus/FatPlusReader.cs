#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.FatPlus;

/// <summary>
/// Read-only reader for FAT+ filesystem images. FAT+ is a backward-compatible
/// extension to FAT32 (and FAT16) that lifts the 4 GiB per-file size limit by
/// repurposing previously-reserved bytes in the 32-byte directory entry to hold
/// the upper bits of an extended file-size value.
/// </summary>
/// <remarks>
/// <para><b>Specification source.</b> The FAT+ draft specification (FATPLUS.TXT,
/// revisions 2 and 3, 2007) was authored by Udo Kuhnt, Luchezar Georgiev and
/// Jeremy Davis, and is historically hosted under fdos.org. It is referenced
/// from the Wikipedia "File Allocation Table" / "Large-file support" articles.
/// The draft documents a file-size extension that pushes the cap to 256 GiB - 1
/// byte (2^38 - 1) on otherwise spec-compliant FAT32 (and FAT16) volumes.</para>
///
/// <para><b>Volume identification.</b> A FAT+ volume is marked by an OEM-name
/// signature in the BPB: bytes 3..10 (the 8-byte <c>BS_OEMName</c> field) read
/// <c>"FAT+    "</c> (4 ASCII chars + 4 spaces). Standard FAT32 readers ignore
/// the OEM string, so non-aware readers still see the underlying FAT32 layout
/// and can list files whose sizes fit in 32 bits — they only mis-read files
/// &gt; 4 GiB (the size field appears truncated and the cluster chain looks
/// over-long).</para>
///
/// <para><b>Directory-entry layout.</b> The standard 32-byte FAT directory
/// entry is unchanged in placement; only previously-reserved bytes are used
/// for the extended size field. This implementation follows the most widely
/// documented FAT+ rev 2/3 variant:
/// <list type="bullet">
///   <item><description>Offset 28..31 (<c>DIR_FileSize</c>): low 32 bits of file size — unchanged.</description></item>
///   <item><description>Offset 12 (<c>DIR_NTRes</c>): high 6 bits of file size (bits 32..37). The top 2 bits of <c>DIR_NTRes</c> remain reserved (matching Windows NT's use of <c>0x08</c> / <c>0x10</c>).</description></item>
/// </list>
/// The resulting 38-bit size field caps file size at 2^38 − 1 = 256 GiB − 1
/// byte, matching the documented FAT+ limit.</para>
///
/// <para><b>Compatibility caveats.</b>
/// <list type="bullet">
///   <item><description>This reader transparently honours the extended size and reads the cluster chain to that length. Where the OEM string is <i>not</i> <c>"FAT+    "</c> but the underlying image is a normal FAT32 volume, you should use <see cref="FileSystem.Fat.FatReader"/> instead — this descriptor will not detect such images.</description></item>
///   <item><description>FAT+ may conflict with HPFS/OS2 extended attributes (which use <c>DIR_NTRes</c> high bits). The FAT+ draft rev 3 addresses this; this implementation uses 6 bits of <c>NTRes</c> for size, leaving the top 2 bits for compatibility with the NT case-flag convention.</description></item>
/// </list></para>
/// </remarks>
public sealed class FatPlusReader : IDisposable {

  /// <summary>OEM-name signature that identifies a FAT+ volume. 8 ASCII bytes at offset 3 of the BPB.</summary>
  public static readonly byte[] OemSignature = "FAT+    "u8.ToArray();

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<FatPlusEntry> _entries = [];

  public IReadOnlyList<FatPlusEntry> Entries => this._entries;

  /// <summary>FAT type (12, 16, or 32). FAT+ is most commonly applied to FAT32 but the spec also covers FAT16.</summary>
  public int FatType { get; private set; }

  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount;
  private long _totalSectors;
  private int _fatSize;
  private int _rootDirSectors;
  private long _firstDataSector;
  private long _totalDataClusters;
  private int _rootCluster;
  private int _clusterBytes;

  public FatPlusReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    if (!stream.CanSeek)
      throw new ArgumentException("FAT+: stream must be seekable.", nameof(stream));
    this.Parse();
  }

  /// <summary>
  /// Tests whether the BPB at the start of <paramref name="bpb"/> carries the
  /// FAT+ OEM signature.
  /// </summary>
  public static bool HasFatPlusSignature(ReadOnlySpan<byte> bpb)
    => bpb.Length >= 11 && bpb.Slice(3, 8).SequenceEqual(OemSignature);

  private void Parse() {
    if (this._stream.Length < 512)
      throw new InvalidDataException("FAT+: image too small.");

    Span<byte> boot = stackalloc byte[512];
    this._stream.Position = 0;
    if (this._stream.Read(boot) != 512)
      throw new InvalidDataException("FAT+: cannot read boot sector.");

    if (boot[0] != 0xEB && boot[0] != 0xE9 && boot[0] != 0x00)
      throw new InvalidDataException("FAT+: invalid boot jump.");

    if (!HasFatPlusSignature(boot))
      throw new InvalidDataException("FAT+: missing 'FAT+    ' OEM signature in BPB.");

    this._bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
    if (this._bytesPerSector is 0 or > 4096) this._bytesPerSector = 512;
    this._sectorsPerCluster = boot[13];
    if (this._sectorsPerCluster == 0) this._sectorsPerCluster = 1;
    this._reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
    this._fatCount = boot[16];
    if (this._fatCount == 0) this._fatCount = 2;
    this._rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);

    this._totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..]);
    if (this._totalSectors == 0)
      this._totalSectors = (uint)BinaryPrimitives.ReadInt32LittleEndian(boot[32..]);

    this._fatSize = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
    if (this._fatSize == 0)
      this._fatSize = BinaryPrimitives.ReadInt32LittleEndian(boot[36..]);

    this._rootDirSectors = (this._rootEntryCount * 32 + this._bytesPerSector - 1) / this._bytesPerSector;
    this._firstDataSector = this._reservedSectors + (long)this._fatCount * this._fatSize + this._rootDirSectors;
    this._totalDataClusters = (this._totalSectors - this._firstDataSector) / this._sectorsPerCluster;

    this.FatType = this._totalDataClusters < 4085 ? 12 : this._totalDataClusters < 65525 ? 16 : 32;
    this._clusterBytes = this._sectorsPerCluster * this._bytesPerSector;

    if (this.FatType == 32)
      this._rootCluster = BinaryPrimitives.ReadInt32LittleEndian(boot[44..]);

    if (this.FatType == 32) {
      var rootData = this.ReadClusterChain(this._rootCluster, sizeLimit: long.MaxValue);
      this.ReadDirectoryEntries(rootData, rootData.Length / 32, "");
    } else {
      var rootOffset = (long)(this._reservedSectors + this._fatCount * this._fatSize) * this._bytesPerSector;
      var rootSize = this._rootEntryCount * 32;
      var buf = new byte[rootSize];
      this._stream.Position = rootOffset;
      this._stream.ReadExactly(buf);
      this.ReadDirectoryEntries(buf, this._rootEntryCount, "");
    }
  }

  private void ReadDirectoryEntries(byte[] dirData, int maxEntries, string path) {
    var lfnParts = new SortedDictionary<int, string>();

    for (var i = 0; i < maxEntries; i++) {
      var off = i * 32;
      if (off + 32 > dirData.Length) break;

      var firstByte = dirData[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) { lfnParts.Clear(); continue; }

      var attr = dirData[off + 11];

      // LFN slot
      if ((attr & 0x3F) == 0x0F) {
        var seq = dirData[off] & 0x3F;
        var part = new StringBuilder();
        ReadLfnChars(dirData, off + 1, 5, part);
        ReadLfnChars(dirData, off + 14, 6, part);
        ReadLfnChars(dirData, off + 28, 2, part);
        lfnParts[seq] = part.ToString();
        continue;
      }

      if ((attr & 0x08) != 0) { lfnParts.Clear(); continue; } // volume label

      var shortName = GetShortName(dirData, off);
      string name;
      if (lfnParts.Count > 0) {
        var sb = new StringBuilder();
        foreach (var p in lfnParts.Values)
          sb.Append(p);
        name = sb.ToString().TrimEnd('\0', '\xFFFF');
        lfnParts.Clear();
      } else {
        name = shortName;
      }

      var isDir = (attr & 0x10) != 0;

      // ── FAT+ extended file size ──────────────────────────────────────────
      // Low 32 bits at offset 28 (DIR_FileSize), high 6 bits at offset 12
      // (DIR_NTRes lower 6 bits — top 2 reserved for the NT case-flag
      // compatibility). Yields a 38-bit size field (max 256 GiB - 1).
      var sizeLo = (uint)BinaryPrimitives.ReadInt32LittleEndian(dirData.AsSpan(off + 28));
      var ntRes = dirData[off + 12];
      var sizeHi = (long)(ntRes & 0x3F); // low 6 bits
      var fileSize = (sizeHi << 32) | sizeLo;

      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 26));
      if (this.FatType == 32)
        startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 20)) << 16;

      if (name is "." or "..") continue;

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      var date = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 24));
      var time = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 22));
      DateTime? lastMod = null;
      if (date != 0) {
        try {
          lastMod = new DateTime(1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F,
            time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2);
        } catch { /* tolerate invalid */ }
      }

      this._entries.Add(new FatPlusEntry {
        Name = fullPath,
        Size = isDir ? 0 : fileSize,
        IsDirectory = isDir,
        StartCluster = startCluster,
        LastModified = lastMod,
      });

      if (isDir && startCluster >= 2) {
        var childData = this.ReadClusterChain(startCluster, sizeLimit: long.MaxValue);
        this.ReadDirectoryEntries(childData, childData.Length / 32, fullPath);
      }
    }
  }

  private static void ReadLfnChars(byte[] data, int offset, int count, StringBuilder sb) {
    for (var j = 0; j < count; j++) {
      var charOff = offset + j * 2;
      if (charOff + 2 > data.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  /// <summary>
  /// Walks the cluster chain starting at <paramref name="startCluster"/>, reading
  /// directly from the underlying stream. Stops at end-of-chain, cycle, or once
  /// <paramref name="sizeLimit"/> bytes have been emitted.
  /// </summary>
  /// <remarks>
  /// Returns the in-memory cluster data. For files &gt; 2 GiB the caller should
  /// use <see cref="ExtractTo"/> (streaming) rather than this byte array path.
  /// </remarks>
  private byte[] ReadClusterChain(int startCluster, long sizeLimit) {
    using var ms = new MemoryStream();
    this.WalkClusterChain(startCluster, sizeLimit, (data, len) => ms.Write(data, 0, len));
    return ms.ToArray();
  }

  /// <summary>
  /// Streams the cluster chain for <paramref name="entry"/> into
  /// <paramref name="output"/>. This is the only safe path for files &gt; 2 GiB
  /// because a byte[] of that size cannot be allocated on .NET.
  /// </summary>
  public void ExtractTo(FatPlusEntry entry, Stream output) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(output);
    if (entry.IsDirectory || entry.StartCluster < 2 || entry.Size == 0) return;
    this.WalkClusterChain(entry.StartCluster, entry.Size, (data, len) => output.Write(data, 0, len));
  }

  /// <summary>
  /// In-memory extract — only safe for files that fit in a <c>byte[]</c>.
  /// Throws for files &gt; <see cref="int.MaxValue"/>.
  /// </summary>
  public byte[] Extract(FatPlusEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.StartCluster < 2) return [];
    if (entry.Size > int.MaxValue)
      throw new InvalidOperationException(
        $"FAT+: entry '{entry.Name}' is {entry.Size} bytes — exceeds in-memory byte[] limit. Use ExtractTo(Stream).");
    using var ms = new MemoryStream((int)entry.Size);
    this.ExtractTo(entry, ms);
    return ms.ToArray();
  }

  private void WalkClusterChain(int startCluster, long sizeLimit, Action<byte[], int> emit) {
    var buffer = new byte[this._clusterBytes];
    var cluster = startCluster;
    var seen = new HashSet<int>();
    long emitted = 0;

    while (cluster >= 2 && !this.IsEndOfChain(cluster) && seen.Add(cluster) && emitted < sizeLimit) {
      var clusterOffset = (this._firstDataSector + (long)(cluster - 2) * this._sectorsPerCluster) * this._bytesPerSector;
      if (clusterOffset + this._clusterBytes > this._stream.Length) break;
      this._stream.Position = clusterOffset;
      this._stream.ReadExactly(buffer, 0, this._clusterBytes);

      var remaining = sizeLimit - emitted;
      var toEmit = remaining < this._clusterBytes ? (int)remaining : this._clusterBytes;
      emit(buffer, toEmit);
      emitted += toEmit;

      cluster = this.GetNextCluster(cluster);
    }
  }

  private int GetNextCluster(int cluster) {
    var fatBaseOffset = (long)this._reservedSectors * this._bytesPerSector;
    switch (this.FatType) {
      case 12: {
        var bytePos = fatBaseOffset + cluster * 3 / 2;
        if (bytePos + 2 > this._stream.Length) return 0xFFF;
        Span<byte> b = stackalloc byte[2];
        this._stream.Position = bytePos;
        this._stream.ReadExactly(b);
        var val = BinaryPrimitives.ReadUInt16LittleEndian(b);
        return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
      }
      case 16: {
        var pos = fatBaseOffset + cluster * 2L;
        if (pos + 2 > this._stream.Length) return 0xFFFF;
        Span<byte> b = stackalloc byte[2];
        this._stream.Position = pos;
        this._stream.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
      }
      case 32: {
        var pos = fatBaseOffset + cluster * 4L;
        if (pos + 4 > this._stream.Length) return 0x0FFFFFF8;
        Span<byte> b = stackalloc byte[4];
        this._stream.Position = pos;
        this._stream.ReadExactly(b);
        return BinaryPrimitives.ReadInt32LittleEndian(b) & 0x0FFFFFFF;
      }
      default: return 0;
    }
  }

  private bool IsEndOfChain(int cluster) => this.FatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    32 => cluster >= 0x0FFFFFF8,
    _ => true
  };

  public void Dispose() {
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
