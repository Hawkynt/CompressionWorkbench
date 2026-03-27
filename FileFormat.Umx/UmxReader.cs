#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Umx;

/// <summary>
/// Reads Unreal Engine UMX music packages. Extracts embedded tracker modules
/// (S3M, IT, XM, MOD) from the Unreal Package container.
/// </summary>
public sealed class UmxReader : IDisposable {
  public const uint UmxMagic = 0x9E2A83C1;
  private readonly byte[] _data;
  private readonly List<UmxEntry> _entries = [];

  public IReadOnlyList<UmxEntry> Entries => _entries;

  public UmxReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 36)
      throw new InvalidDataException("UMX: file too small.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(_data);
    if (magic != UmxMagic)
      throw new InvalidDataException("UMX: invalid magic.");

    var fileVersion = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(4));
    if (fileVersion < 60)
      throw new InvalidDataException("UMX: unsupported file version.");

    var nameCount = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x0C));
    var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x10));
    var exportCount = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x14));
    var exportOffset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x18));
    var importCount = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x1C));
    var importOffset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x20));

    if (nameCount <= 0 || exportCount <= 0 || importCount <= 0) return;

    // Read name table
    var names = new string[nameCount];
    var pos = nameOffset;
    for (var i = 0; i < nameCount && pos < _data.Length; i++) {
      var len = _data[pos++];
      if (pos + len > _data.Length) break;
      names[i] = Encoding.ASCII.GetString(_data, pos, Math.Max(0, len - 1)); // exclude null
      pos += len;
      pos += 4; // skip flags
    }

    // Read import table
    var importNames = new int[importCount];
    pos = importOffset;
    for (var i = 0; i < importCount && pos < _data.Length; i++) {
      ReadCompactIndex(ref pos); // class_package
      ReadCompactIndex(ref pos); // class_name
      pos += 4; // object_package
      importNames[i] = ReadCompactIndex(ref pos); // object_name
    }

    // Read export table and find Music objects
    pos = exportOffset;
    for (var i = 0; i < exportCount && pos < _data.Length; i++) {
      var classIndex = ReadCompactIndex(ref pos);
      ReadCompactIndex(ref pos); // superclass
      pos += 4; // package
      var objNameIdx = ReadCompactIndex(ref pos);
      pos += 4; // object_flags
      var serialSize = ReadCompactIndex(ref pos);
      var serialOffset = serialSize > 0 ? ReadCompactIndex(ref pos) : 0;

      if (serialSize <= 0 || classIndex >= 0) continue;

      // Check if class is "Music"
      var importIdx = -classIndex - 1;
      if (importIdx < 0 || importIdx >= importCount) continue;
      var classNameIdx = importNames[importIdx];
      if (classNameIdx < 0 || classNameIdx >= nameCount) continue;
      if (names[classNameIdx] != "Music") continue;

      // Found a music export
      var objName = objNameIdx >= 0 && objNameIdx < nameCount ? names[objNameIdx] : $"music_{i}";
      var ext = nameCount > 0 && names[0] != null ? names[0].ToLowerInvariant() : "mod";
      if (ext is not ("it" or "s3m" or "xm" or "mod")) ext = "mod";

      // Read the actual music data from serial offset
      var spos = serialOffset;
      if (spos + 8 > _data.Length) continue;
      spos += 2; // skip unknown
      spos += 4; // skip unknown
      var musicLen = ReadCompactIndex(ref spos);
      if (musicLen <= 0 || spos + musicLen > _data.Length) continue;

      _entries.Add(new UmxEntry {
        Name = $"{objName}.{ext}",
        Size = musicLen,
        Offset = spos,
      });
    }
  }

  private int ReadCompactIndex(ref int pos) {
    if (pos >= _data.Length) return 0;

    var b0 = _data[pos++];
    var negative = (b0 & 0x80) != 0;
    var more = (b0 & 0x40) != 0;
    var value = b0 & 0x3F;

    if (more) {
      for (var shift = 6; shift < 32 && pos < _data.Length; shift += 7) {
        var b = _data[pos++];
        value |= (b & 0x7F) << shift;
        if ((b & 0x80) == 0) break;
      }
    }

    return negative ? -value : value;
  }

  public byte[] Extract(UmxEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset + entry.Size > _data.Length)
      throw new InvalidDataException("UMX: data extends beyond file.");
    return _data.AsSpan(entry.Offset, (int)entry.Size).ToArray();
  }

  public void Dispose() { }
}
