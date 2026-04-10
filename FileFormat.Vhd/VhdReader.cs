#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vhd;

public sealed class VhdReader : IDisposable {
  private static readonly byte[] Magic = "conectix"u8.ToArray();
  private readonly byte[] _data;
  private readonly List<VhdEntry> _entries = [];
  private long _diskDataOffset;
  private long _diskDataLength;

  public IReadOnlyList<VhdEntry> Entries => _entries;

  public VhdReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("VHD: file too small.");

    // Footer at end of file
    var footerOff = _data.Length - 512;
    if (!_data.AsSpan(footerOff, 8).SequenceEqual(Magic)) {
      // Try at offset 0 (some VHDs have copy at start)
      if (_data.AsSpan(0, 8).SequenceEqual(Magic))
        footerOff = 0;
      else
        throw new InvalidDataException("VHD: invalid footer magic.");
    }

    var diskType = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(footerOff + 60));
    var currentSize = (long)BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(footerOff + 56));

    if (diskType == 2) {
      // Fixed VHD: raw data is everything before the footer
      _diskDataOffset = 0;
      _diskDataLength = _data.Length - 512;
    } else {
      // Dynamic/differencing: simplified - just expose available data
      _diskDataOffset = 0;
      _diskDataLength = Math.Min(currentSize, _data.Length - 512);
    }

    _entries.Add(new VhdEntry {
      Name = "disk.img",
      Size = _diskDataLength,
    });
  }

  public byte[] Extract(VhdEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var len = (int)Math.Min(entry.Size, _data.Length - _diskDataOffset);
    if (len <= 0) return [];
    return _data.AsSpan((int)_diskDataOffset, len).ToArray();
  }

  public void Dispose() { }
}
