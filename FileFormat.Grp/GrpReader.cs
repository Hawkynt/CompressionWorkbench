namespace FileFormat.Grp;

/// <summary>
/// Reads BUILD Engine GRP archives as used by Duke Nukem 3D, Blood, and Shadow Warrior.
/// Format: 12-byte ASCII magic "KenSilverman", uint32 LE file count, then per-file directory
/// entries (12-byte null-padded name + uint32 LE size), followed immediately by concatenated
/// file data.
/// </summary>
public sealed class GrpReader {
  /// <summary>ASCII magic string at offset 0.</summary>
  public const string Magic = "KenSilverman";

  private readonly Stream _stream;
  private readonly List<GrpEntry> _entries = [];

  /// <summary>All entries read from the archive directory.</summary>
  public IReadOnlyList<GrpEntry> Entries => _entries;

  /// <summary>Opens and parses a GRP stream.</summary>
  /// <exception cref="InvalidDataException">Thrown when the magic bytes are wrong or the stream is too short.</exception>
  public GrpReader(Stream stream) {
    _stream = stream;
    using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

    // Validate magic (12 bytes)
    var magic = br.ReadBytes(12);
    if (magic.Length < 12)
      throw new InvalidDataException("Stream too small to be a GRP file.");
    var magicStr = System.Text.Encoding.ASCII.GetString(magic);
    if (magicStr != Magic)
      throw new InvalidDataException($"Not a GRP file (bad magic: \"{magicStr}\").");

    var fileCount = br.ReadUInt32();

    // Read directory: each entry is 12-byte name + 4-byte size = 16 bytes
    var rawEntries = new (string Name, int Size)[fileCount];
    for (var i = 0; i < fileCount; i++) {
      var nameBytes = br.ReadBytes(12);
      var name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
      var size = (int)br.ReadUInt32();
      rawEntries[i] = (name, size);
    }

    // Data starts immediately after the directory
    var dataStart = stream.Position; // = 12 + 4 + fileCount * 16
    var offset = dataStart;
    foreach (var (name, size) in rawEntries) {
      _entries.Add(new GrpEntry { Name = name, Size = size, DataOffset = offset });
      offset += size;
    }
  }

  /// <summary>Extracts the raw bytes for the given entry.</summary>
  public byte[] Extract(GrpEntry entry) {
    _stream.Position = entry.DataOffset;
    var buf = new byte[entry.Size];
    var read = _stream.Read(buf, 0, entry.Size);
    if (read < entry.Size)
      throw new InvalidDataException($"Unexpected end of GRP data for entry \"{entry.Name}\".");
    return buf;
  }
}
