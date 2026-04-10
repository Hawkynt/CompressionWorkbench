#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vmdk;

public sealed class VmdkReader : IDisposable {
  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56]; // "KDMV" LE
  private readonly byte[] _data;
  private readonly List<VmdkEntry> _entries = [];
  private long _diskSize;
  private long _dataOffset;

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
    // Sparse header
    var capacity = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(16)); // in sectors
    var grainSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(24)); // in sectors
    var overHead = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(64)); // in sectors

    _diskSize = capacity * 512;
    _dataOffset = overHead * 512;

    // For sparse VMDK, the actual data is scattered in grains.
    // For simple extraction, report the overhead-based data or reconstructed flat
    _entries.Add(new VmdkEntry {
      Name = "disk.img",
      Size = Math.Min(_diskSize, _data.Length - _dataOffset),
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
    _dataOffset = 0;

    _entries.Add(new VmdkEntry {
      Name = "disk.img",
      Size = _diskSize,
    });
  }

  public byte[] Extract(VmdkEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var len = (int)Math.Min(entry.Size, _data.Length - _dataOffset);
    if (len <= 0) return [];
    return _data.AsSpan((int)_dataOffset, len).ToArray();
  }

  public void Dispose() { }
}
