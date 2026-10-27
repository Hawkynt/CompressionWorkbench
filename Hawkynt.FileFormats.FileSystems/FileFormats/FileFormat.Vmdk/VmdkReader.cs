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

  private readonly Stream _stream;
  private readonly SectorCache _cache;
  private readonly long _streamLength;
  private readonly List<VmdkEntry> _entries = [];
  private long _diskSize;

  private bool _zeroedGrainEntryEnabled;
  private long _grainSizeBytes;
  private int _grainTableEntries;
  private uint[] _grainDirectory = [];
  private int _numGdEntries;


  public IReadOnlyList<VmdkEntry> Entries => this._entries;

  public VmdkReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._streamLength = stream.Length;
    this._stream = stream;
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
    this._entries.Add(new VmdkEntry { Name = "disk.img", Size = this._diskSize });
  }

  /// <summary>
  /// Reads the whole guest disk through <see cref="VmdkVirtualDisk" />: compressed and
  /// stream-optimized grains, zeroed grain-table entries, multi-extent descriptors and
  /// delta disks over their parent.
  /// </summary>
  /// <exception cref="NotSupportedException">The disk needs a feature or a file this reader cannot reach.</exception>
  /// <exception cref="InvalidDataException">The disk or a file it references is damaged or missing.</exception>
  public byte[] Extract(VmdkEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > int.MaxValue)
      throw new NotSupportedException("VMDK buffered extraction currently supports guest disks up to 2 GiB.");
    using var disk = VmdkVirtualDisk.Open(this._stream)
      ?? throw new InvalidDataException("VMDK: unrecognized format.");
    if (disk.Length > int.MaxValue)
      throw new NotSupportedException("VMDK buffered extraction currently supports guest disks up to 2 GiB.");
    var result = new byte[checked((int)disk.Length)];
    disk.Position = 0;
    disk.ReadExactly(result);
    return result;
  }

  public void Dispose() => this._cache.Dispose();
}
