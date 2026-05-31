#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;

namespace FileFormat.Vmdk;

/// <summary>
/// Reader for VMware VMDK images (sparse and flat/descriptor).
/// Streams reads via <see cref="SectorCache"/> so opening a multi-TB image
/// does not load the whole file into RAM — only the header, grain directory
/// and (during <see cref="Extract"/>) the requested grain bytes are fetched
/// on demand.
/// </summary>
public sealed class VmdkReader : IDisposable {
  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56]; // "KDMV" LE
  private readonly SectorCache _cache;
  private readonly long _streamLength;
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
    ArgumentNullException.ThrowIfNull(stream);
    _streamLength = stream.Length;
    _cache = new SectorCache(stream);
    Parse();
  }

  private void Parse() {
    if (_streamLength < 512)
      throw new InvalidDataException("VMDK: file too small.");

    // Peek the first 512 bytes — enough to disambiguate sparse vs text descriptor.
    var head = _cache.Read(0, (int)Math.Min(_streamLength, 1024));
    if (head.AsSpan(0, 4).SequenceEqual(SparseMagic)) {
      ParseSparse(head);
    } else {
      // Try text descriptor (read up to 1024 bytes which we already have).
      var text = Encoding.ASCII.GetString(head);
      if (text.Contains("createType") || text.Contains("VMDK"))
        ParseDescriptor(text);
      else
        throw new InvalidDataException("VMDK: unrecognized format.");
    }
  }

  private void ParseSparse(byte[] head) {
    _isSparse = true;

    // SparseExtentHeader is byte-packed (no natural alignment); all sector
    // offsets are little-endian.
    // offset  0: magic "KDMV" (4 bytes)
    // offset  4: version (4 bytes)
    // offset  8: flags (4 bytes)
    // offset 12: capacity in sectors (8 bytes)
    // offset 20: grainSize in sectors (8 bytes)
    // offset 28: descriptorOffset in sectors (8 bytes)
    // offset 36: descriptorSize in sectors (8 bytes)
    // offset 44: numGTEsPerGT (4 bytes) — grain table entries per grain table
    // offset 48: rgdOffset in sectors (8 bytes) — redundant grain directory
    // offset 56: gdOffset in sectors (8 bytes) — primary grain directory
    // offset 64: overHead in sectors (8 bytes)

    var capacity = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(12));
    var grainSizeSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20));
    _grainTableEntries = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(44));
    var gdOffsetSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(56));

    _diskSize = capacity * 512;
    _grainSizeBytes = grainSizeSectors * 512;

    if (_grainTableEntries <= 0)
      _grainTableEntries = 512; // default per spec

    // Number of GD entries = ceil(capacity / (grainSize * numGTEsPerGT))
    var grainsPerGt = (long)_grainTableEntries;
    var sectorsPerGt = grainsPerGt * grainSizeSectors;
    _numGdEntries = sectorsPerGt > 0 ? (int)((capacity + sectorsPerGt - 1) / sectorsPerGt) : 0;

    // Read grain directory via cache.
    var gdByteOffset = gdOffsetSectors * 512;
    if (gdByteOffset > 0 && _numGdEntries > 0 && gdByteOffset + _numGdEntries * 4L <= _streamLength) {
      _grainDirectory = new uint[_numGdEntries];
      var gdBytes = new byte[_numGdEntries * 4];
      _cache.Read(gdByteOffset, gdBytes);
      for (var i = 0; i < _numGdEntries; i++)
        _grainDirectory[i] = BinaryPrimitives.ReadUInt32LittleEndian(gdBytes.AsSpan(i * 4, 4));
    } else {
      _grainDirectory = [];
    }

    _entries.Add(new VmdkEntry {
      Name = "disk.img",
      Size = _diskSize,
    });
  }

  private void ParseDescriptor(string text) {
    // Text descriptor: extract extent size.
    long totalSectors = 0;
    foreach (var line in text.Split('\n')) {
      var trimmed = line.Trim();
      if (trimmed.StartsWith("RW ") || trimmed.StartsWith("RDONLY ")) {
        var parts = trimmed.Split(' ');
        if (parts.Length >= 2 && long.TryParse(parts[1], out var sectors))
          totalSectors += sectors;
      }
    }

    _diskSize = totalSectors > 0 ? totalSectors * 512 : _streamLength;
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
      var len = (int)Math.Min(entry.Size, _streamLength - _flatDataOffset);
      if (len <= 0) return [];
      var buf = new byte[len];
      _cache.Read(_flatDataOffset, buf);
      return buf;
    }

    // Sparse: resolve grain directory -> grain table -> grain data via the cache.
    var result = new byte[_diskSize];
    if (_grainSizeBytes <= 0 || _grainDirectory.Length == 0)
      return result;

    var totalGrains = (_diskSize + _grainSizeBytes - 1) / _grainSizeBytes;

    // Single reusable 4-byte buffer for grain table entry reads.
    Span<byte> gteBuf = stackalloc byte[4];

    for (long grainIdx = 0; grainIdx < totalGrains; grainIdx++) {
      var gdIndex = (int)(grainIdx / _grainTableEntries);
      var gtIndex = (int)(grainIdx % _grainTableEntries);

      if (gdIndex >= _grainDirectory.Length)
        break;

      var gtSectorOffset = _grainDirectory[gdIndex];
      if (gtSectorOffset == 0)
        continue; // no grain table allocated — zeros

      // Read grain table entry via the cache.
      var gtByteOffset = (long)gtSectorOffset * 512 + gtIndex * 4L;
      if (gtByteOffset + 4 > _streamLength)
        continue;

      _cache.Read(gtByteOffset, gteBuf);
      var grainSectorOffset = BinaryPrimitives.ReadUInt32LittleEndian(gteBuf);

      if (grainSectorOffset == 0)
        continue; // grain not allocated — zeros

      var grainByteOffset = (long)grainSectorOffset * 512;
      var destOffset = grainIdx * _grainSizeBytes;
      var copyLen = (int)Math.Min(_grainSizeBytes, _diskSize - destOffset);

      if (copyLen <= 0)
        break;

      if (grainByteOffset + copyLen > _streamLength)
        continue; // truncated file

      _cache.Read(grainByteOffset, result.AsSpan((int)destOffset, copyLen));
    }

    return result;
  }

  public void Dispose() => _cache.Dispose();
}
