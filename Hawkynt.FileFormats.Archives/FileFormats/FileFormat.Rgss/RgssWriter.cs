#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Rgss;

/// <summary>
/// Writes an RGSS3A (RPG Maker VX Ace) encrypted archive.
/// <para>
/// Format: magic "RGSSAD\0\3" + raw master key (u32 LE) + index entries + file data.
/// Master key is transformed as <c>masterKey = rawMaster * 9 + 3</c>.
/// Each index entry: offset (u32 xor), size (u32 xor), fileKey (u32 xor), nameLen (u32 xor),
/// name bytes (xor'd with masterKey cycling). File data is xor'd with the per-file key.
/// </para>
/// </summary>
internal sealed class RgssWriter {
  private readonly Stream _output;
  private readonly uint _masterKey;
  private readonly uint _rawMaster;

  /// <summary>
  /// Initializes a new <see cref="RgssWriter"/>.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="rawMaster">The raw master key value (before the *9+3 transform). Default 0xDEADCAFE.</param>
  public RgssWriter(Stream output, uint rawMaster = 0xDEADCAFE) {
    this._output = output;
    this._rawMaster = rawMaster;
    this._masterKey = rawMaster * 9u + 3u;
  }

  /// <summary>
  /// Writes a complete RGSS3A archive from a list of named data entries.
  /// </summary>
  /// <param name="entries">The entries to write, each as (name, data).</param>
  public void Write(IReadOnlyList<(string Name, byte[] Data)> entries) {
    // Write magic
    this._output.Write("RGSSAD\0\x3"u8);

    // Write raw master key
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, this._rawMaster);
    this._output.Write(buf);

    // Calculate data offsets: index comes first, then all data blocks.
    // Index size per entry: 4 (offset) + 4 (size) + 4 (fileKey) + 4 (nameLen) + nameBytes
    var indexSize = 0L;
    var nameBytesList = new List<byte[]>();
    foreach (var (name, _) in entries) {
      var nameBytes = Encoding.UTF8.GetBytes(name.Replace('/', '\\'));
      nameBytesList.Add(nameBytes);
      indexSize += 16 + nameBytes.Length; // 4*4 + nameLen
    }

    var dataStart = 12L + indexSize; // 8 (magic) + 4 (rawMaster) + index

    // Write index entries
    var currentDataOffset = dataStart;
    for (var i = 0; i < entries.Count; i++) {
      var (_, data) = entries[i];
      var nameBytes = nameBytesList[i];

      // Generate a per-file key from the entry index
      var fileKey = (uint)(0xA5A5A5A5 ^ (i * 0x12345678 + 0x9ABCDEF0));

      // Offset xor masterKey
      BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)currentDataOffset ^ this._masterKey);
      this._output.Write(buf);

      // Size xor masterKey
      BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)data.Length ^ this._masterKey);
      this._output.Write(buf);

      // FileKey xor masterKey
      BinaryPrimitives.WriteUInt32LittleEndian(buf, fileKey ^ this._masterKey);
      this._output.Write(buf);

      // NameLen xor masterKey
      BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)nameBytes.Length ^ this._masterKey);
      this._output.Write(buf);

      // Name bytes xor'd with masterKey cycling
      var encName = new byte[nameBytes.Length];
      for (var j = 0; j < nameBytes.Length; j++) {
        var kb = (byte)((this._masterKey >> ((j % 4) * 8)) & 0xFF);
        encName[j] = (byte)(nameBytes[j] ^ kb);
      }
      this._output.Write(encName);

      currentDataOffset += data.Length;
    }

    // Write data blocks (encrypted with per-file keys)
    for (var i = 0; i < entries.Count; i++) {
      var data = entries[i].Data;
      var fileKey = (uint)(0xA5A5A5A5 ^ (i * 0x12345678 + 0x9ABCDEF0));

      var enc = new byte[data.Length];
      for (var j = 0; j < data.Length; j++)
        enc[j] = (byte)(data[j] ^ ((fileKey >> ((j % 4) * 8)) & 0xFF));

      this._output.Write(enc);
    }
  }
}
