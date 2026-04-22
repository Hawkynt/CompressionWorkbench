#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Rpa;

/// <summary>
/// Reads Ren'Py RPA archives (RPA-2.0, RPA-3.0, RPA-3.2). The first line is an ASCII
/// header giving the offset of a zlib-compressed Python pickle index. The pickle maps
/// filename to a list of (offset, length, prefix) tuples; for v3 the offset/length are
/// XOR-scrambled with a 32-bit key embedded in the header.
/// </summary>
public sealed class RpaReader {

  private readonly Stream _stream;
  private readonly List<RpaEntry> _entries = [];

  public IReadOnlyList<RpaEntry> Entries => _entries;
  public string Version { get; }
  public long IndexOffset { get; }
  public uint XorKey { get; }
  public bool PickleParsed { get; private set; }

  public RpaReader(Stream stream) {
    this._stream = stream;
    stream.Position = 0;

    // Read first line (header)
    var header = ReadHeaderLine(stream);
    if (header.StartsWith("RPA-3.0 ", StringComparison.Ordinal) ||
        header.StartsWith("RPA-3.2 ", StringComparison.Ordinal)) {
      this.Version = header[..7];
      // RPA-3.2 has a 4-byte extra value before offset — but practically most tools treat it
      // the same as 3.0: "RPA-3.0 <16hex> <8hex>\n" / "RPA-3.2 <16hex> <8hex>\n"
      var parts = header[8..].TrimEnd('\n', '\r').Split(' ');
      if (parts.Length < 2)
        throw new InvalidDataException("RPA-3.x header missing fields.");
      this.IndexOffset = long.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      this.XorKey = uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    } else if (header.StartsWith("RPA-2.0 ", StringComparison.Ordinal)) {
      this.Version = "RPA-2.0";
      var rest = header[8..].TrimEnd('\n', '\r');
      this.IndexOffset = long.Parse(rest, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      this.XorKey = 0;
    } else {
      throw new InvalidDataException($"Not an RPA archive (header: {header.TrimEnd()}).");
    }

    // Read zlib-compressed index
    stream.Position = this.IndexOffset;
    byte[] rawIndex;
    try {
      using var zlib = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
      using var ms = new MemoryStream();
      zlib.CopyTo(ms);
      rawIndex = ms.ToArray();
    } catch (Exception ex) {
      throw new InvalidDataException("Failed to decompress RPA index (zlib).", ex);
    }

    // Parse pickle — fragile but strict RPA producers output a predictable format.
    try {
      this._entries.AddRange(RpaPickleParser.ParseIndex(rawIndex, this.XorKey));
      this.PickleParsed = true;
    } catch {
      // Fallback: pickle parse failed — leave entries empty; descriptor will still list FULL + metadata.
      this.PickleParsed = false;
    }
  }

  /// <summary>Extracts the raw bytes for the given entry (prefix + data slice).</summary>
  public byte[] Extract(RpaEntry entry) {
    var buf = new byte[entry.Length];
    Array.Copy(entry.Prefix, 0, buf, 0, Math.Min(entry.Prefix.Length, buf.Length));
    var bodyLen = (int)(entry.Length - entry.Prefix.Length);
    if (bodyLen > 0) {
      this._stream.Position = entry.Offset;
      var read = this._stream.Read(buf, entry.Prefix.Length, bodyLen);
      if (read < bodyLen)
        throw new InvalidDataException($"Unexpected end of RPA data for entry \"{entry.Path}\".");
    }
    return buf;
  }

  private static string ReadHeaderLine(Stream s) {
    // Read bytes until \n or 128 bytes limit
    var sb = new StringBuilder(64);
    for (var i = 0; i < 128; i++) {
      var b = s.ReadByte();
      if (b < 0) break;
      sb.Append((char)b);
      if (b == '\n') break;
    }
    return sb.ToString();
  }
}
