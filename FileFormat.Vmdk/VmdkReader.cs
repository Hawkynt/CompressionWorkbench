#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vmdk;

public sealed class VmdkReader : IDisposable {
  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56]; // "KDMV" LE
  private readonly byte[] _data;
  private readonly List<VmdkEntry> _entries = [];
  private long _diskSize;

  // Sparse grain directory fields
  private bool _isSparse;
  private long _grainSizeBytes;
  private int _grainTableEntries; // grain table entries = grainSize * gtCoverage / grainSize
  private uint[] _grainDirectory = [];
  private int _numGdEntries;

  // Non-sparse fields (flat/descriptor)
  private long _flatDataOffset;

  public IReadOnlyList<VmdkEntry> Entries => _entries;

  public VmdkReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("VMDK: file too small.");

    // Check for sparse VMDK magic
    if (_data.AsSpan(0, 4).SequenceEqual(SparseMagic)) {
      ParseSparse();
    } else {
      // Try text descriptor
      var text = Encoding.ASCII.GetString(_data, 0, Math.Min(1024, _data.Length));
      if (text.Contains("createType") || text.Contains("VMDK"))
        ParseDescriptor(text);
      else
        throw new InvalidDataException("VMDK: unrecognized format.");
    }
  }

  private void ParseSparse() {
    _isSparse = true;

    // Sparse VMDK header (all offsets in sectors, little-endian)
    // offset  0: magic "KDMV" (4 bytes)
    // offset  4: version (4 bytes)
    // offset  8: flags (4 bytes)
    // offset 16: capacity in sectors (8 bytes)
    // offset 24: grainSize in sectors (8 bytes)
    // offset 32: descriptorOffset in sectors (8 bytes)
    // offset 40: descriptorSize in sectors (8 bytes)
    // offset 48: numGTEsPerGT (4 bytes) — grain table entries per grain table
    // offset 56: rgdOffset in sectors (8 bytes) — redundant grain directory
    // offset 64: gdOffset in sectors (8 bytes) — primary grain directory
    // offset 72: overHead in sectors (8 bytes)

    var capacity = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(16));
    var grainSizeSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(24));
    _grainTableEntries = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(48));
    var gdOffsetSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(64));

    _diskSize = capacity * 512;
    _grainSizeBytes = grainSizeSectors * 512;

    if (_grainTableEntries <= 0)
      _grainTableEntries = 512; // default per spec

    // Number of GD entries = ceil(capacity / (grainSize * numGTEsPerGT))
    var grainsPerGt = (long)_grainTableEntries;
    var sectorsPerGt = grainsPerGt * grainSizeSectors;
    _numGdEntries = (int)((capacity + sectorsPerGt - 1) / sectorsPerGt);

    // Read grain directory
    var gdByteOffset = gdOffsetSectors * 512;
    if (gdByteOffset > 0 && gdByteOffset + _numGdEntries * 4L <= _data.Length) {
      _grainDirectory = new uint[_numGdEntries];
      for (var i = 0; i < _numGdEntries; i++)
        _grainDirectory[i] = BinaryPrimitives.ReadUInt32LittleEndian(
          _data.AsSpan((int)(gdByteOffset + i * 4L)));
    } else {
      _grainDirectory = [];
    }

    _entries.Add(new VmdkEntry {
      Name = "disk.img",
      Size = _diskSize,
    });
  }

  private void ParseDescriptor(string text) {
    // Text descriptor: extract extent size
    long totalSectors = 0;
    foreach (var line in text.Split('\n')) {
      var trimmed = line.Trim();
      if (trimmed.StartsWith("RW ") || trimmed.StartsWith("RDONLY ")) {
        var parts = trimmed.Split(' ');
        if (parts.Length >= 2 && long.TryParse(parts[1], out var sectors))
          totalSectors += sectors;
      }
    }

    _diskSize = totalSectors > 0 ? totalSectors * 512 : _data.Length;
    _flatDataOffset = 0;
    _isSparse = false;

    _entries.Add(new VmdkEntry {
      Name = "disk.img",
      Size = _diskSize,
    });
  }

  public byte[] Extract(VmdkEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (!_isSparse) {
      var len = (int)Math.Min(entry.Size, _data.Length - _flatDataOffset);
      if (len <= 0) return [];
      return _data.AsSpan((int)_flatDataOffset, len).ToArray();
    }

    // Sparse: resolve grain directory -> grain table -> grain data
    var result = new byte[_diskSize];
    if (_grainSizeBytes <= 0 || _grainDirectory.Length == 0)
      return result;

    var totalGrains = (_diskSize + _grainSizeBytes - 1) / _grainSizeBytes;

    for (long grainIdx = 0; grainIdx < totalGrains; grainIdx++) {
      var gdIndex = (int)(grainIdx / _grainTableEntries);
      var gtIndex = (int)(grainIdx % _grainTableEntries);

      if (gdIndex >= _grainDirectory.Length)
        break;

      var gtSectorOffset = _grainDirectory[gdIndex];
      if (gtSectorOffset == 0)
        continue; // no grain table allocated — zeros

      // Read grain table entry
      var gtByteOffset = (long)gtSectorOffset * 512 + gtIndex * 4L;
      if (gtByteOffset + 4 > _data.Length)
        continue;

      var grainSectorOffset = BinaryPrimitives.ReadUInt32LittleEndian(
        _data.AsSpan((int)gtByteOffset));

      if (grainSectorOffset == 0)
        continue; // grain not allocated — zeros

      var grainByteOffset = (long)grainSectorOffset * 512;
      var destOffset = grainIdx * _grainSizeBytes;
      var copyLen = (int)Math.Min(_grainSizeBytes, _diskSize - destOffset);

      if (copyLen <= 0)
        break;

      if (grainByteOffset + copyLen > _data.Length)
        continue; // truncated file

      _data.AsSpan((int)grainByteOffset, copyLen).CopyTo(result.AsSpan((int)destOffset));
    }

    return result;
  }

  public void Dispose() { }
}
