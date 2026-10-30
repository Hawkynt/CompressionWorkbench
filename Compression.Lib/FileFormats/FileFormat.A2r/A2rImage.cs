#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.A2r;

/// <summary>
/// An Applesauce A2R flux image (version 2 or 3) as an ordered list of chunks. Capture-bearing chunks
/// (A2R3 <c>RWCP</c>/<c>SLVD</c>, A2R2 <c>STRM</c>) are split into their entries; every other chunk
/// (<c>INFO</c>, <c>META</c>, anything unknown) is kept as raw bytes, so a rebuild reproduces the file.
/// </summary>
/// <remarks>
/// Layouts after the published references: <c>https://applesaucefdc.com/a2r/</c> (3.x) and
/// <c>https://applesaucefdc.com/a2r2-reference/</c> (2.x). All integers are little-endian.
/// </remarks>
public sealed class A2rImage {
  /// <summary>One chunk: raw <see cref="Data"/>, or for a capture chunk its 16-byte header and its entries (marks stripped).</summary>
  public sealed record Chunk(string Id, byte[] Data, byte[]? Header = null, List<byte[]>? Entries = null) {
    public bool IsCaptureChunk => this.Entries is not null;
  }

  public const int HeaderSize = 8;
  public const int CaptureHeaderSize = 16;
  public const int InfoSize3 = 37;
  public const int InfoSize2 = 36;

  /// <summary>Major version: 2 (<c>A2R2</c>) or 3 (<c>A2R3</c>).</summary>
  public int Version { get; set; } = 3;

  public List<Chunk> Chunks { get; } = [];

  public static bool IsCaptureId(int version, string id)
    => version == 3 ? id is "RWCP" or "SLVD" : id == "STRM";

  public static A2rImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize || data[0] != 'A' || data[1] != '2' || data[2] != 'R' || data[4] != 0xFF || data[5] != 0x0A || data[6] != 0x0D || data[7] != 0x0A)
      throw new InvalidDataException("A2R: missing 'A2Rn' FF 0A 0D 0A signature.");
    var version = data[3] - '0';
    if (version is not (2 or 3)) throw new InvalidDataException($"A2R: unsupported major version '{(char)data[3]}'.");
    var image = new A2rImage { Version = version };
    var pos = HeaderSize;
    while (data.Length - pos >= 8) {
      var id = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      pos += 8;
      if (size > (uint)(data.Length - pos)) throw new InvalidDataException($"A2R: chunk '{id}' runs past the end of the file.");
      var body = data.Slice(pos, (int)size);
      pos += (int)size;
      image.Chunks.Add(IsCaptureId(version, id) ? ParseCaptureChunk(version, id, body) : new Chunk(id, body.ToArray()));
    }
    if (pos != data.Length) throw new InvalidDataException("A2R: trailing bytes after the last chunk.");
    return image;
  }

  /// <summary>Splits a capture chunk body into its header and entries.</summary>
  public static Chunk ParseCaptureChunk(int version, string id, ReadOnlySpan<byte> body) {
    var headerSize = version == 3 ? CaptureHeaderSize : 0;
    if (body.Length < headerSize + 1) throw new InvalidDataException($"A2R: {id} chunk is too short.");
    var entries = new List<byte[]>();
    var end = version == 3 ? (byte)'X' : (byte)0xFF;
    var mark = id == "SLVD" ? (byte)'T' : (byte)'C';
    var pos = headerSize;
    while (true) {
      if (pos >= body.Length) throw new InvalidDataException($"A2R: {id} chunk lacks its end mark.");
      if (body[pos] == end) {
        if (pos + 1 != body.Length) throw new InvalidDataException($"A2R: bytes after the end mark of a {id} chunk.");
        break;
      }
      if (version == 3) {
        if (body[pos] != mark) throw new InvalidDataException($"A2R: bad entry mark 0x{body[pos]:X2} in {id}.");
        ++pos;
      }
      var length = EntryLength(id, body[pos..]);
      entries.Add(body.Slice(pos, length).ToArray());
      pos += length;
    }
    return new Chunk(id, [], body[..headerSize].ToArray(), entries);
  }

  /// <summary>The length of the entry at the start of <paramref name="entry"/> (mark excluded), validated against the data available.</summary>
  public static int EntryLength(string id, ReadOnlySpan<byte> entry) {
    // RWCP: type, u16 location, n, n × u32 index, u32 size, data.
    // SLVD: u16 location, mirror out, mirror in, 6 reserved, n, n × u32 index, u32 size, data.
    // STRM: u8 location, type, u32 size, u32 loop ticks, data.
    var (indexCountAt, fixedHead) = id switch {
      "RWCP" => (3, 4),
      "SLVD" => (10, 11),
      "STRM" => (-1, 10),
      _ => throw new InvalidDataException($"A2R: '{id}' has no entries."),
    };
    if (entry.Length < fixedHead) throw new InvalidDataException($"A2R: truncated {id} entry.");
    long length;
    if (indexCountAt < 0) {
      length = fixedHead + (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]);
    } else {
      var sizeAt = fixedHead + 4 * entry[indexCountAt];
      if (entry.Length < sizeAt + 4) throw new InvalidDataException($"A2R: truncated {id} entry.");
      length = sizeAt + 4 + (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[sizeAt..]);
    }
    if (length > entry.Length) throw new InvalidDataException($"A2R: {id} entry data runs past its chunk.");
    if (id == "RWCP" && entry[0] is not (1 or 2 or 3)) throw new InvalidDataException($"A2R: unknown capture type {entry[0]}.");
    if (id == "STRM" && entry[1] is not (1 or 2 or 3)) throw new InvalidDataException($"A2R: unknown capture type {entry[1]}.");
    return (int)length;
  }

  public void Write(Stream output) {
    var head = new byte[HeaderSize];
    head[0] = (byte)'A'; head[1] = (byte)'2'; head[2] = (byte)'R'; head[3] = (byte)('0' + this.Version);
    head[4] = 0xFF; head[5] = 0x0A; head[6] = 0x0D; head[7] = 0x0A;
    output.Write(head);
    Span<byte> chunkHead = stackalloc byte[8];
    foreach (var chunk in this.Chunks) {
      var body = this.Body(chunk);
      Encoding.ASCII.GetBytes(chunk.Id.AsSpan(0, 4), chunkHead);
      BinaryPrimitives.WriteUInt32LittleEndian(chunkHead[4..], (uint)body.Length);
      output.Write(chunkHead);
      output.Write(body);
    }
  }

  private byte[] Body(Chunk chunk) {
    if (chunk.Id.Length != 4) throw new InvalidDataException($"A2R: chunk id '{chunk.Id}' is not four characters.");
    if (!chunk.IsCaptureChunk) return chunk.Data;
    using var ms = new MemoryStream();
    if (this.Version == 3) {
      if (chunk.Header is not { Length: CaptureHeaderSize }) throw new InvalidDataException($"A2R: {chunk.Id} header must be {CaptureHeaderSize} bytes.");
      ms.Write(chunk.Header);
    }
    foreach (var entry in chunk.Entries!) {
      if (EntryLength(chunk.Id, entry) != entry.Length) throw new InvalidDataException($"A2R: a {chunk.Id} entry carries trailing bytes.");
      if (this.Version == 3) ms.WriteByte(chunk.Id == "SLVD" ? (byte)'T' : (byte)'C');
      else if (entry[0] == 0xFF) throw new InvalidDataException("A2R: STRM location 255 is the end mark.");
      ms.Write(entry);
    }
    ms.WriteByte(this.Version == 3 ? (byte)'X' : (byte)0xFF);
    return ms.ToArray();
  }

  /// <summary>The location of an entry (track &lt;&lt; 1 | side, or quarter tracks for 5.25" drives).</summary>
  public static int Location(string id, byte[] entry) => id switch {
    "RWCP" => BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(1)),
    "SLVD" => BinaryPrimitives.ReadUInt16LittleEndian(entry),
    _ => entry[0],
  };

  /// <summary>The capture type name of an entry, or <c>solved</c> for an SLVD track.</summary>
  public static string KindName(string id, byte[] entry) => id == "SLVD" ? "solved" : (id == "RWCP" ? entry[0] : entry[1]) switch {
    1 => "timing",
    2 => "bits",
    _ => "xtiming",
  };

  /// <summary>Flux intervals in ticks from the packed byte stream of a timing capture (255 continues).</summary>
  public static List<uint> DecodeFlux(ReadOnlySpan<byte> packed) {
    var flux = new List<uint>(packed.Length);
    var acc = 0u;
    foreach (var b in packed) {
      acc += b;
      if (b == 255) continue;
      flux.Add(acc);
      acc = 0;
    }
    if (acc != 0) flux.Add(acc);
    return flux;
  }

  /// <summary>Packs flux intervals in ticks: runs of 255 then the remainder.</summary>
  public static byte[] EncodeFlux(IEnumerable<uint> flux) {
    var packed = new List<byte>();
    foreach (var f in flux) {
      var t = f;
      while (t >= 255) { packed.Add(255); t -= 255; }
      packed.Add((byte)t);
    }
    return [.. packed];
  }

  /// <summary>An A2R3 RWCP capture entry (without its mark).</summary>
  public static byte[] RwcpEntry(int type, int location, IReadOnlyList<uint> indexTicks, ReadOnlySpan<byte> data) {
    var entry = new byte[4 + 4 * indexTicks.Count + 4 + data.Length];
    entry[0] = (byte)type;
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(1), (ushort)location);
    entry[3] = (byte)indexTicks.Count;
    for (var i = 0; i < indexTicks.Count; ++i) BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4 + 4 * i), indexTicks[i]);
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4 + 4 * indexTicks.Count), (uint)data.Length);
    data.CopyTo(entry.AsSpan(8 + 4 * indexTicks.Count));
    return entry;
  }

  /// <summary>An A2R2 STRM capture entry.</summary>
  public static byte[] StrmEntry(int type, int location, uint loopTicks, ReadOnlySpan<byte> data) {
    var entry = new byte[10 + data.Length];
    entry[0] = (byte)location;
    entry[1] = (byte)type;
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(2), (uint)data.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(6), loopTicks);
    data.CopyTo(entry.AsSpan(10));
    return entry;
  }

  /// <summary>A 16-byte RWCP/SLVD header: chunk version and picoseconds per tick.</summary>
  public static byte[] CaptureHeader(int chunkVersion, uint picosecondsPerTick) {
    var h = new byte[CaptureHeaderSize];
    h[0] = (byte)chunkVersion;
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(1), picosecondsPerTick);
    return h;
  }

  /// <summary>An INFO chunk body for this version.</summary>
  public static byte[] Info(int version, string creator, int driveType, bool writeProtected, bool synchronized, int hardSectors = 0) {
    var info = new byte[version == 3 ? InfoSize3 : InfoSize2];
    info[0] = 1;
    var name = Encoding.UTF8.GetBytes(creator);
    info.AsSpan(1, 32).Fill(0x20);
    name.AsSpan(0, Math.Min(32, name.Length)).CopyTo(info.AsSpan(1));
    info[33] = (byte)driveType;
    info[34] = writeProtected ? (byte)1 : (byte)0;
    info[35] = synchronized ? (byte)1 : (byte)0;
    if (version == 3) info[36] = (byte)hardSectors;
    return info;
  }

  public string RenderMetadata() {
    var sb = new StringBuilder("[A2R]\n");
    sb.Append("version=").Append(this.Version).Append('\n');
    var info = this.Chunks.FirstOrDefault(c => c.Id == "INFO");
    if (info is { Data.Length: >= InfoSize2 }) {
      var d = info.Data;
      sb.Append("info_version=").Append(d[0]).Append('\n');
      sb.Append("creator=").Append(Encoding.UTF8.GetString(d, 1, 32).TrimEnd(' ', '\0')).Append('\n');
      sb.Append("drive_type=").Append(d[33]).Append('\n');
      sb.Append("write_protected=").Append(d[34]).Append('\n');
      sb.Append("synchronized=").Append(d[35]).Append('\n');
      if (this.Version == 3 && d.Length >= InfoSize3) sb.Append("hard_sectors=").Append(d[36]).Append('\n');
    }
    for (var i = 0; i < this.Chunks.Count; ++i) {
      var c = this.Chunks[i];
      if (!c.IsCaptureChunk) continue;
      sb.Append(CultureInfo.InvariantCulture, $"chunk_{i:D2}_{c.Id}_entries={c.Entries!.Count}\n");
      if (this.Version == 3)
        sb.Append(CultureInfo.InvariantCulture, $"chunk_{i:D2}_{c.Id}_ps_per_tick={BinaryPrimitives.ReadUInt32LittleEndian(c.Header.AsSpan(1))}\n");
    }
    return sb.ToString();
  }
}
