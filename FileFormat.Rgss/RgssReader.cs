#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Rgss;

/// <summary>
/// Reads RPG Maker RGSSAD / RGSS2A / RGSS3A encrypted archives.
/// <para>
/// v1 ("RGSSAD\0\1"): per-byte XOR with low byte of a running 32-bit key (init 0xDEADCAFE,
/// advance <c>key = key * 7 + 3</c>). Entries: name_length (u32 xor), name (per-byte xor),
/// size (u32 xor), then inline data (xor'd with running key restarted at 0xDEADCAFE advanced per byte).
/// </para>
/// <para>
/// v2 ("RGSSAD\0\2"): u32-at-a-time XOR with running key (init 0xDEADCAFE, advance per word).
/// Entries: data_offset, size, name_length, name — name xor'd per 32-bit word.
/// </para>
/// <para>
/// v3 ("RGSSAD\0\3"): master key stored at offset 8 (u32 LE), transformed via <c>masterKey * 9 + 3</c>.
/// Entries: offset, size, per-file-key, name_length, name — all XOR'd with the transformed master key.
/// Data is XOR'd with the per-file key, byte-by-byte cycling through key bytes.
/// </para>
/// </summary>
public sealed class RgssReader {

  private readonly Stream _stream;
  private readonly List<RgssEntry> _entries = [];

  public IReadOnlyList<RgssEntry> Entries => _entries;
  public int Version { get; }
  public uint MasterKeyV3 { get; private set; }

  public RgssReader(Stream stream) {
    this._stream = stream;
    stream.Position = 0;

    var magic = new byte[8];
    if (stream.Read(magic, 0, 8) < 8)
      throw new InvalidDataException("Stream too small to be RGSSAD.");
    if (magic[0] != 'R' || magic[1] != 'G' || magic[2] != 'S' || magic[3] != 'S' ||
        magic[4] != 'A' || magic[5] != 'D' || magic[6] != 0)
      throw new InvalidDataException("Not an RGSSAD archive (bad magic).");

    this.Version = magic[7];
    switch (this.Version) {
      case 1: ParseV1(); break;
      case 2: ParseV2(); break;
      case 3: ParseV3(); break;
      default: throw new InvalidDataException($"Unsupported RGSSAD version {this.Version}.");
    }
  }

  private void ParseV1() {
    uint key = 0xDEADCAFE;
    while (this._stream.Position < this._stream.Length) {
      if (!TryReadU32(out var nameLen)) break;
      nameLen ^= NextByteKeyWord(ref key);
      if (nameLen == 0 || nameLen > 4096) break;

      var nameBytes = new byte[nameLen];
      if (this._stream.Read(nameBytes, 0, (int)nameLen) < nameLen) break;
      for (int i = 0; i < nameLen; i++) {
        nameBytes[i] ^= (byte)(key & 0xFF);
        key = key * 7u + 3u;
      }
      // Forward slashes
      for (int i = 0; i < nameBytes.Length; i++) if (nameBytes[i] == (byte)'\\') nameBytes[i] = (byte)'/';
      var name = Encoding.UTF8.GetString(nameBytes);

      if (!TryReadU32(out var size)) break;
      size ^= NextByteKeyWord(ref key);
      if (size > int.MaxValue) break;

      this._entries.Add(new RgssEntry {
        Name = name, Offset = this._stream.Position, Size = size, FileKey = key
      });
      // advance past inline data
      this._stream.Position += size;
    }
  }

  /// <summary>Advance <paramref name="key"/> by 4 byte-steps and return the composed XOR mask.</summary>
  private static uint NextByteKeyWord(ref uint key) {
    // Each byte of the 32-bit value is xor'd with (key & 0xFF), advancing key per byte
    uint b0 = key & 0xFF; key = key * 7u + 3u;
    uint b1 = key & 0xFF; key = key * 7u + 3u;
    uint b2 = key & 0xFF; key = key * 7u + 3u;
    uint b3 = key & 0xFF; key = key * 7u + 3u;
    return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
  }

  private void ParseV2() {
    uint key = 0xDEADCAFE;
    while (this._stream.Position < this._stream.Length) {
      if (!TryReadU32(out var dataOffset)) break;
      dataOffset ^= key; key = key * 7u + 3u;
      if (dataOffset == 0) break;

      if (!TryReadU32(out var size)) break;
      size ^= key; key = key * 7u + 3u;

      if (!TryReadU32(out var nameLen)) break;
      nameLen ^= key; key = key * 7u + 3u;
      if (nameLen == 0 || nameLen > 4096) break;

      var nameBytes = new byte[nameLen];
      if (this._stream.Read(nameBytes, 0, (int)nameLen) < nameLen) break;
      for (int i = 0; i < nameLen; i++) {
        // v2 uses a running byte-key model: each byte xor'd with low byte of key, advance per byte
        nameBytes[i] ^= (byte)(key & 0xFF);
        key = key * 7u + 3u;
      }
      for (int i = 0; i < nameBytes.Length; i++) if (nameBytes[i] == (byte)'\\') nameBytes[i] = (byte)'/';
      var name = Encoding.UTF8.GetString(nameBytes);

      this._entries.Add(new RgssEntry {
        Name = name, Offset = dataOffset, Size = size, FileKey = 0
      });
    }
  }

  private void ParseV3() {
    // Read master key at offset 8 (already positioned there)
    if (!TryReadU32(out var rawMaster))
      throw new InvalidDataException("RGSS3A truncated master key.");
    uint masterKey = rawMaster * 9u + 3u;
    this.MasterKeyV3 = masterKey;

    while (this._stream.Position < this._stream.Length) {
      if (!TryReadU32(out var offset)) break;
      offset ^= masterKey;
      if (offset == 0) break;

      if (!TryReadU32(out var size)) break;
      size ^= masterKey;

      if (!TryReadU32(out var fileKey)) break;
      fileKey ^= masterKey;

      if (!TryReadU32(out var nameLen)) break;
      nameLen ^= masterKey;
      if (nameLen == 0 || nameLen > 4096) break;

      var nameBytes = new byte[nameLen];
      if (this._stream.Read(nameBytes, 0, (int)nameLen) < nameLen) break;
      // v3: name xor'd per byte by cycling through masterKey bytes
      for (int i = 0; i < nameLen; i++) {
        uint kb = (masterKey >> ((i % 4) * 8)) & 0xFF;
        nameBytes[i] ^= (byte)kb;
      }
      for (int i = 0; i < nameBytes.Length; i++) if (nameBytes[i] == (byte)'\\') nameBytes[i] = (byte)'/';
      var name = Encoding.UTF8.GetString(nameBytes);

      this._entries.Add(new RgssEntry {
        Name = name, Offset = offset, Size = size, FileKey = fileKey
      });
    }
  }

  public byte[] Extract(RgssEntry entry) {
    this._stream.Position = entry.Offset;
    var buf = new byte[entry.Size];
    var read = this._stream.Read(buf, 0, (int)entry.Size);
    if (read < entry.Size)
      throw new InvalidDataException($"Unexpected end of RGSS data for entry \"{entry.Name}\".");

    switch (this.Version) {
      case 1:
      case 2: {
        // Decrypt using a running byte key starting at entry.FileKey (v1) or reset to 0xDEADCAFE (v2 common)
        uint key = this.Version == 1 ? entry.FileKey : 0xDEADCAFE;
        for (int i = 0; i < buf.Length; i++) {
          buf[i] ^= (byte)(key & 0xFF);
          key = key * 7u + 3u;
        }
        break;
      }
      case 3: {
        // Block XOR using entry.FileKey's 4 bytes in repeating pattern
        uint k = entry.FileKey;
        for (int i = 0; i < buf.Length; i++) {
          buf[i] ^= (byte)((k >> ((i % 4) * 8)) & 0xFF);
        }
        break;
      }
    }
    return buf;
  }

  private bool TryReadU32(out uint value) {
    Span<byte> b = stackalloc byte[4];
    int n = this._stream.Read(b);
    if (n < 4) { value = 0; return false; }
    value = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    return true;
  }
}
