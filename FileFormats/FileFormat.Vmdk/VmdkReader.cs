#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;

namespace FileFormat.Vmdk;

/// <summary>
/// Reader for VMware VMDK images. Sparse extents honor the header's secondary
/// grain-directory selector and zeroed-grain-table-entry flag.
/// </summary>
public sealed class VmdkReader : IDisposable {
  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56];

  private readonly SectorCache _cache;
  private readonly long _streamLength;
  private readonly List<VmdkEntry> _entries = [];
  private long _diskSize;

  private bool _isSparse;
  private bool _zeroedGrainEntryEnabled;
  private long _grainSizeBytes;
  private int _grainTableEntries;
  private uint[] _grainDirectory = [];
  private int _numGdEntries;

  private long _flatDataOffset;

  public IReadOnlyList<VmdkEntry> Entries => this._entries;

  public VmdkReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._streamLength = stream.Length;
    this._cache = new SectorCache(stream);
    this.Parse();
  }

  private void Parse() {
    if (this._streamLength < 512)
      throw new InvalidDataException("VMDK: file too small.");

    var head = this._cache.Read(0, (int)Math.Min(this._streamLength, 1024));
    if (head.AsSpan(0, 4).SequenceEqual(SparseMagic)) {
      this.ParseSparse(head);
      return;
    }

    var text = Encoding.ASCII.GetString(head);
    if (text.Contains("createType", StringComparison.Ordinal) || text.Contains("VMDK", StringComparison.Ordinal)) {
      this.ParseDescriptor(text);
      return;
    }

    throw new InvalidDataException("VMDK: unrecognized format.");
  }

  private void ParseSparse(byte[] head) {
    this._isSparse = true;
    if (head.Length < 72)
      throw new InvalidDataException("VMDK: sparse header is truncated.");

    var flags = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
    var rawCapacity = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(12));
    var rawGrainSizeSectors = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20));
    var rawGtes = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(44));
    var rawRgdOffset = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(48));
    var rawGdOffset = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(56));

    if (rawCapacity > long.MaxValue / 512 || rawGrainSizeSectors > long.MaxValue / 512)
      throw new InvalidDataException("VMDK: capacity or grain size exceeds the supported range.");
    if (rawGtes > int.MaxValue)
      throw new NotSupportedException("VMDK: grain table exceeds the current managed-array limit.");

    var capacitySectors = (long)rawCapacity;
    var grainSizeSectors = (long)rawGrainSizeSectors;
    this._grainTableEntries = rawGtes == 0 ? 512 : (int)rawGtes;
    this._zeroedGrainEntryEnabled = (flags & 0x0000_0004u) != 0;

    // Flag bit 1 says the redundant/secondary grain directory is authoritative.
    var useSecondary = (flags & 0x0000_0002u) != 0 && rawRgdOffset != 0;
    var rawDirectoryOffset = useSecondary ? rawRgdOffset : rawGdOffset;
    if (rawDirectoryOffset > long.MaxValue / 512)
      throw new InvalidDataException("VMDK: grain-directory offset exceeds the supported range.");

    this._diskSize = checked(capacitySectors * 512);
    this._grainSizeBytes = checked(grainSizeSectors * 512);
    if (this._grainSizeBytes <= 0)
      throw new InvalidDataException("VMDK: grain size is zero.");

    var sectorsPerTable = checked((long)this._grainTableEntries * grainSizeSectors);
    this._numGdEntries = sectorsPerTable > 0
      ? checked((int)((capacitySectors + sectorsPerTable - 1) / sectorsPerTable))
      : 0;

    var directoryByteOffset = checked((long)rawDirectoryOffset * 512);
    var directoryByteLength = checked((long)this._numGdEntries * 4);
    if (directoryByteOffset <= 0 || this._numGdEntries <= 0 || directoryByteOffset + directoryByteLength > this._streamLength) {
      this._grainDirectory = [];
    } else {
      this._grainDirectory = new uint[this._numGdEntries];
      var bytes = new byte[checked((int)directoryByteLength)];
      this._cache.Read(directoryByteOffset, bytes);
      for (var i = 0; i < this._grainDirectory.Length; ++i)
        this._grainDirectory[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
    }

    this._entries.Add(new VmdkEntry { Name = "disk.img", Size = this._diskSize });
  }

  private void ParseDescriptor(string text) {
    long totalSectors = 0;
    foreach (var line in text.Split('\n')) {
      var trimmed = line.Trim();
      if (!trimmed.StartsWith("RW ", StringComparison.Ordinal) &&
          !trimmed.StartsWith("RDONLY ", StringComparison.Ordinal))
        continue;
      var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length >= 2 && long.TryParse(parts[1], out var sectors))
        totalSectors = checked(totalSectors + sectors);
    }

    this._diskSize = totalSectors > 0 ? checked(totalSectors * 512) : this._streamLength;
    this._flatDataOffset = 0;
    this._isSparse = false;
    this._entries.Add(new VmdkEntry { Name = "disk.img", Size = this._diskSize });
  }

  public byte[] Extract(VmdkEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > int.MaxValue)
      throw new NotSupportedException("VMDK buffered extraction currently supports guest disks up to 2 GiB.");

    if (!this._isSparse) {
      var length = checked((int)Math.Min(entry.Size, this._streamLength - this._flatDataOffset));
      if (length <= 0) return [];
      var result = new byte[checked((int)entry.Size)];
      this._cache.Read(this._flatDataOffset, result.AsSpan(0, length));
      return result;
    }

    var disk = new byte[checked((int)this._diskSize)];
    if (this._grainSizeBytes <= 0 || this._grainDirectory.Length == 0)
      return disk;

    var totalGrains = (this._diskSize + this._grainSizeBytes - 1) / this._grainSizeBytes;
    Span<byte> entryBytes = stackalloc byte[4];

    for (long grainIndex = 0; grainIndex < totalGrains; ++grainIndex) {
      var directoryIndex = checked((int)(grainIndex / this._grainTableEntries));
      var tableIndex = checked((int)(grainIndex % this._grainTableEntries));
      if (directoryIndex >= this._grainDirectory.Length) break;

      var tableSector = this._grainDirectory[directoryIndex];
      if (IsSparseEntry(tableSector)) continue;

      var tableEntryOffset = checked((long)tableSector * 512 + tableIndex * 4L);
      if (tableEntryOffset < 0 || tableEntryOffset + 4 > this._streamLength)
        throw new InvalidDataException($"VMDK: grain table {directoryIndex} extends beyond the file.");
      this._cache.Read(tableEntryOffset, entryBytes);
      var grainSector = BinaryPrimitives.ReadUInt32LittleEndian(entryBytes);
      if (IsSparseEntry(grainSector)) continue;

      var sourceOffset = checked((long)grainSector * 512);
      var destinationOffset = checked(grainIndex * this._grainSizeBytes);
      var length = checked((int)Math.Min(this._grainSizeBytes, this._diskSize - destinationOffset));
      if (sourceOffset < 0 || sourceOffset + length > this._streamLength)
        throw new InvalidDataException($"VMDK: grain {grainIndex} extends beyond the file.");
      this._cache.Read(sourceOffset, disk.AsSpan(checked((int)destinationOffset), length));
    }

    return disk;
  }

  private bool IsSparseEntry(uint sector)
    => sector == 0 || (this._zeroedGrainEntryEnabled && sector == 1);

  public void Dispose() => this._cache.Dispose();
}
