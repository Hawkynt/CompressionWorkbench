#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.ZxScl;

/// <summary>
/// Builds a fresh ZX Spectrum <c>.scl</c> TR-DOS archive from scratch (WORM).
/// </summary>
/// <remarks>
/// <para>
/// Layout:
/// <list type="number">
///   <item>8 bytes: ASCII magic "SINCLAIR".</item>
///   <item>1 byte: number of file headers (0-255).</item>
///   <item>N * 14 bytes: TR-DOS header per file (8-char name + type char + 2-byte param1 +
///   2-byte param2 + 1-byte length-in-sectors).</item>
///   <item>Concatenated raw file data (sum of LengthSectors * 256 bytes per file).</item>
///   <item>4 bytes: trailing checksum (little-endian 32-bit sum of all preceding bytes).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ZxSclWriter {

  private const int HeaderSize = ZxSclReader.HeaderSize;  // 14
  private const int SectorSize = ZxSclReader.SectorSize;  // 256
  /// <summary>TR-DOS hard cap: headers are stored in a single 256-entry directory-like table.</summary>
  public const int MaxEntries = 128;

  private readonly List<(string Name, char Type, ushort Param1, ushort Param2, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data, char fileType = 'C', ushort param1 = 0x8000, ushort param2 = 0)
    => this._files.Add((name, fileType, param1, param2 == 0 ? (ushort)data.Length : param2, data));

  public byte[] Build() {
    if (this._files.Count > MaxEntries)
      throw new InvalidOperationException(
        $"SCL: {this._files.Count} files exceeds limit of {MaxEntries}.");

    // Pre-compute sector-padded length per file (max 255 sectors per TR-DOS entry = 65 280 bytes).
    var padded = new byte[this._files.Count][];
    var lengthSectors = new byte[this._files.Count];
    for (var i = 0; i < this._files.Count; i++) {
      var data = this._files[i].Data;
      var sectors = (data.Length + SectorSize - 1) / SectorSize;
      if (sectors > 255)
        throw new InvalidOperationException(
          $"SCL: file '{this._files[i].Name}' requires {sectors} sectors; TR-DOS max is 255.");
      if (sectors == 0) sectors = 1;  // TR-DOS stores at least one sector even for empty files
      var buf = new byte[sectors * SectorSize];
      if (data.Length > 0) Buffer.BlockCopy(data, 0, buf, 0, data.Length);
      padded[i] = buf;
      lengthSectors[i] = (byte)sectors;
    }

    var totalDataBytes = 0L;
    foreach (var p in padded) totalDataBytes += p.Length;

    var fileSize = 8 + 1 + this._files.Count * HeaderSize + (int)totalDataBytes + 4;
    var output = new byte[fileSize];

    // --- Magic ---
    Buffer.BlockCopy(ZxSclReader.Magic, 0, output, 0, ZxSclReader.Magic.Length);
    // --- File count ---
    output[8] = (byte)this._files.Count;

    // --- Headers ---
    for (var i = 0; i < this._files.Count; i++) {
      var ho = 9 + i * HeaderSize;
      var (rawName, type, p1, p2, _) = this._files[i];
      var (baseName, fileType) = SanitizeName(rawName, type);

      for (var j = 0; j < 8; j++)
        output[ho + j] = (byte)(j < baseName.Length ? baseName[j] : ' ');
      output[ho + 8] = (byte)fileType;
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(ho + 9), p1);
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(ho + 11), p2);
      output[ho + 13] = lengthSectors[i];
    }

    // --- File data ---
    var cursor = 9 + this._files.Count * HeaderSize;
    for (var i = 0; i < this._files.Count; i++) {
      Buffer.BlockCopy(padded[i], 0, output, cursor, padded[i].Length);
      cursor += padded[i].Length;
    }

    // --- Trailing 32-bit little-endian checksum: sum of all preceding bytes ---
    var sum = 0u;
    for (var i = 0; i < cursor; i++) sum += output[i];
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor), sum);

    return output;
  }

  /// <summary>Normalise a name to a TR-DOS 8-char base + single-char type. Strips dotted extensions.</summary>
  private static (string BaseName, char Type) SanitizeName(string raw, char defaultType) {
    if (string.IsNullOrEmpty(raw)) return ("UNNAMED", defaultType);
    var file = Path.GetFileName(raw);
    var dot = file.LastIndexOf('.');
    string baseName;
    var type = defaultType;
    if (dot > 0) {
      baseName = file[..dot];
      var ext = file[(dot + 1)..].ToUpperInvariant();
      // Conventional mappings from the reader: bas -> B, cod -> C, dat -> D, seq -> #.
      type = ext switch {
        "BAS" => 'B',
        "COD" => 'C',
        "DAT" => 'D',
        "SEQ" => '#',
        _ => defaultType,
      };
    } else {
      baseName = file;
    }

    // TR-DOS names are 8 chars of printable ASCII.
    var chars = new char[baseName.Length];
    for (var i = 0; i < baseName.Length; i++) {
      var c = baseName[i];
      chars[i] = (c >= 0x20 && c < 0x7F) ? c : '_';
    }
    var clean = new string(chars);
    // Preserve TAIL to match project-wide convention.
    if (clean.Length > 8) clean = clean[^8..];
    if (clean.Length == 0) clean = "UNNAMED";
    return (clean, type);
  }
}
