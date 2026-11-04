using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Scp;

/// <summary>
/// SuperCard Pro flux image (<c>.scp</c>): a 16-byte header, a 168-entry track table, optional
/// extension blocks, per-track "TRK" headers with one (index time, flux count, offset) triple per
/// revolution, big-endian 16-bit flux intervals (0 = carry 65 536) and an optional footer.
/// Read and rebuilt from Jim Drew's SCP image specification; layout choices follow Greaseweazle's
/// <c>image/scp.py</c> (public domain, see <c>THIRD-PARTY-NOTICE.greaseweazle.txt</c>).
/// </summary>
public sealed class ScpImage {
  public const int HeaderSize = 0x10;
  public const int TrackSlots = 168;
  public const int TableEnd = HeaderSize + TrackSlots * 4; // 0x2B0
  public const byte FlagIndexed = 1 << 0, FlagTpi96 = 1 << 1, FlagFooter = 1 << 5;

  public byte Version { get; init; }
  public byte DiskType { get; init; } = 0x80;
  public byte Revolutions { get; init; }
  public byte Flags { get; init; } = FlagTpi96 | FlagIndexed;
  public byte BitCellWidth { get; init; }
  public byte Heads { get; init; }
  public byte Resolution { get; init; }
  public bool ChecksumValid { get; init; } = true;
  /// <summary>Bytes between the track table and the first track (the EXTS extension area), kept verbatim.</summary>
  public byte[] Extension { get; init; } = [];
  /// <summary>Footer bytes after the last track (when the footer flag is set).</summary>
  public byte[] Footer { get; init; } = [];
  /// <summary>Track number → the track's own bytes: "TRK", number, revolution table, flux data (offsets relative to it).</summary>
  public SortedDictionary<int, byte[]> Tracks { get; init; } = [];

  /// <exception cref="InvalidDataException">Not an SCP image, or a track header is inconsistent.</exception>
  public static ScpImage Read(ReadOnlySpan<byte> d) {
    if (d.Length < TableEnd || !d[..3].SequenceEqual("SCP"u8))
      throw new InvalidDataException("SCP: \"SCP\" signature or track table missing.");
    var revs = d[5];
    var flags = d[8];
    var sum = 0u;
    foreach (var b in d[HeaderSize..]) sum += b;
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(d[12..]);

    var offsets = new uint[TrackSlots];
    for (var i = 0; i < TrackSlots; ++i) offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(d[(HeaderSize + 4 * i)..]);
    var first = offsets.Where(o => o != 0).DefaultIfEmpty((uint)d.Length).Min();
    if (first < TableEnd) throw new InvalidDataException("SCP: a track offset points into the header.");

    var tracks = new SortedDictionary<int, byte[]>();
    var end = (long)first;
    for (var t = 0; t < TrackSlots; ++t) {
      var off = offsets[t];
      if (off == 0) continue;
      var tdhLength = 4 + 12 * revs;
      if (off + (long)tdhLength > d.Length) throw new InvalidDataException($"SCP: track {t} header runs past the end of the file.");
      var tdh = d.Slice((int)off, tdhLength);
      if (!tdh[..3].SequenceEqual("TRK"u8)) throw new InvalidDataException($"SCP: track {t} has no \"TRK\" signature.");
      if (tdh[3] != t) throw new InvalidDataException($"SCP: track table slot {t} holds track {tdh[3]}.");
      var trackEnd = (long)tdhLength;
      for (var r = 0; r < revs; ++r) {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(tdh[(4 + 12 * r + 4)..]);
        var dataOff = BinaryPrimitives.ReadUInt32LittleEndian(tdh[(4 + 12 * r + 8)..]);
        if (count == 0) continue;
        if (dataOff < tdhLength) throw new InvalidDataException($"SCP: track {t} revolution {r} data overlaps its header.");
        trackEnd = Math.Max(trackEnd, dataOff + 2L * count);
      }
      if (off + trackEnd > d.Length) throw new InvalidDataException($"SCP: track {t} flux runs past the end of the file.");
      tracks[t] = d.Slice((int)off, (int)trackEnd).ToArray();
      end = Math.Max(end, off + trackEnd);
    }

    return new ScpImage {
      Version = d[3], DiskType = d[4], Revolutions = revs, Flags = flags, BitCellWidth = d[9], Heads = d[10], Resolution = d[11],
      ChecksumValid = stored == sum || stored == 0,
      Extension = d[TableEnd..(int)Math.Min(first, (uint)d.Length)].ToArray(),
      Footer = (flags & FlagFooter) != 0 && end < d.Length ? d[(int)end..].ToArray() : [],
      Tracks = tracks,
    };
  }

  /// <summary>Flux intervals (in ticks) of one revolution of a track blob.</summary>
  public static List<uint> Flux(byte[] track, int revolution) {
    var count = BinaryPrimitives.ReadUInt32LittleEndian(track.AsSpan(4 + 12 * revolution + 4));
    var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(track.AsSpan(4 + 12 * revolution + 8));
    var list = new List<uint>((int)count);
    var carry = 0u;
    for (var i = 0; i < count; ++i) {
      var v = (uint)(track[offset + 2 * i] << 8 | track[offset + 2 * i + 1]);
      if (v == 0) { carry += 65536; continue; }
      list.Add(carry + v);
      carry = 0;
    }
    return list;
  }

  /// <summary>
  /// Writes the image: header, track table, the extension area verbatim, the tracks in order
  /// (each renumbered to its slot), and the 32-bit checksum. A stored footer is not carried over
  /// — its fields are absolute offsets into the old layout — and the footer flag is cleared.
  /// </summary>
  public void Write(Stream output) {
    foreach (var (t, blob) in this.Tracks) {
      if (t is < 0 or >= TrackSlots) throw new ArgumentException($"SCP holds tracks 0–167, not {t}.");
      if (blob.Length < 4 + 12 * this.Revolutions || !blob.AsSpan(0, 3).SequenceEqual("TRK"u8))
        throw new ArgumentException($"SCP: track {t} is not a TRK block with {this.Revolutions} revolutions.");
    }
    var body = new MemoryStream();
    var table = new byte[TrackSlots * 4];
    body.Write(table);
    body.Write(this.Extension);
    foreach (var (t, blob) in this.Tracks) {
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 * t), (uint)(HeaderSize + body.Length));
      var copy = (byte[])blob.Clone();
      copy[3] = (byte)t;
      body.Write(copy);
    }
    var bytes = body.ToArray();
    table.CopyTo(bytes, 0);
    var header = new byte[HeaderSize];
    "SCP"u8.CopyTo(header);
    header[3] = this.Version;
    header[4] = this.DiskType;
    header[5] = this.Revolutions;
    header[6] = (byte)(this.Tracks.Count == 0 ? 0 : this.Tracks.Keys.Min());
    header[7] = (byte)(this.Tracks.Count == 0 ? 0 : this.Tracks.Keys.Max());
    header[8] = (byte)(this.Flags & ~FlagFooter);
    header[9] = this.BitCellWidth;
    header[10] = this.Heads;
    header[11] = this.Resolution;
    var sum = 0u;
    foreach (var b in bytes) sum += b;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), sum);
    output.Write(header);
    output.Write(bytes);
  }

  public byte[] RenderMetadata() {
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder("[scp]\n");
    sb.Append(inv, $"version=0x{this.Version:X2}\ndisk_type=0x{this.DiskType:X2}\nrevolutions={this.Revolutions}\nflags=0x{this.Flags:X2}\n");
    sb.Append(inv, $"bit_cell_width={this.BitCellWidth}\nheads={this.Heads}\nresolution={this.Resolution}\n");
    sb.Append(inv, $"tracks={this.Tracks.Count}\nchecksum={(this.ChecksumValid ? "valid" : "INVALID")}\nfooter_bytes={this.Footer.Length}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  /// <summary>Applies the editable header fields of a <c>metadata.ini</c> (hex or decimal).</summary>
  public ScpImage WithMetadata(byte[] ini) {
    byte Get(Dictionary<string, string> m, string key, byte fallback) {
      if (!m.TryGetValue(key, out var v)) return fallback;
      v = v.Trim();
      return v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? byte.Parse(v.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : byte.Parse(v, CultureInfo.InvariantCulture);
    }
    var map = Encoding.UTF8.GetString(ini).Split('\n').Select(l => l.TrimEnd('\r'))
      .Where(l => l.Contains('=') && !l.StartsWith('[')).Select(l => l.Split('=', 2))
      .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1], StringComparer.Ordinal);
    return new ScpImage {
      Version = Get(map, "version", this.Version), DiskType = Get(map, "disk_type", this.DiskType),
      Revolutions = Get(map, "revolutions", this.Revolutions), Flags = Get(map, "flags", this.Flags),
      BitCellWidth = Get(map, "bit_cell_width", this.BitCellWidth), Heads = Get(map, "heads", this.Heads),
      Resolution = Get(map, "resolution", this.Resolution), Extension = this.Extension, Tracks = this.Tracks,
    };
  }
}
