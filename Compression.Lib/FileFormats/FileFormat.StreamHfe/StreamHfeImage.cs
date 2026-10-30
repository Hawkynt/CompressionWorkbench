#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Dictionary.Lz4;

namespace FileFormat.StreamHfe;

/// <summary>
/// An HxC Stream HFE flux image: a 512-byte header, a track list of 32-byte entries and per track an
/// LZ4 block holding one revolution of variable-length pulse deltas. Layout in
/// <c>docs/STREAMHFE-ON-DISK.md</c>.
/// </summary>
public sealed class StreamHfeImage {
  public const int HeaderSize = 512;
  public const int EntrySize = 32;
  public const uint PackedFlag = 1;
  public const uint DefaultBitPeriod = 40_000;
  public static ReadOnlySpan<byte> Signature => "HxC_Stream_Image"u8;

  /// <summary>One track: its pulse stream (unpacked), revolution length in ticks and list flags.</summary>
  public sealed record Track(byte[] Pulses, uint Length, uint Flags = PackedFlag) {
    public int PulseCount => CountPulses(this.Pulses);
  }

  public uint Revision { get; set; }
  public uint Flags { get; set; }
  public int Cylinders { get; set; }
  public int Sides { get; set; } = 2;
  public uint BitPeriod { get; set; } = DefaultBitPeriod;
  public uint InterfaceMode { get; set; }
  public string Name { get; set; } = "";
  public string Date { get; set; } = "";

  /// <summary>Tracks by (cylinder, side); a missing one is written as an unformatted (empty) track.</summary>
  public SortedDictionary<(int Cylinder, int Side), Track> Tracks { get; } = new();

  public static StreamHfeImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize || !data[..16].SequenceEqual(Signature))
      throw new InvalidDataException("Stream HFE: missing 'HxC_Stream_Image' signature.");
    var image = new StreamHfeImage {
      Revision = U32(data, 0x10),
      Flags = U32(data, 0x14),
      Cylinders = (int)Math.Min(U32(data, 0x24), int.MaxValue),
      Sides = (int)Math.Min(U32(data, 0x28), int.MaxValue),
      BitPeriod = U32(data, 0x2C),
      InterfaceMode = U32(data, 0x30),
      Name = CString(data.Slice(0x44, 128)),
      Date = CString(data.Slice(0xC4, 16)),
    };
    if (image.Sides is not (1 or 2)) throw new InvalidDataException($"Stream HFE: {image.Sides} sides.");
    if (image.Cylinders is < 1 or > 255) throw new InvalidDataException($"Stream HFE: {image.Cylinders} tracks.");
    var list = (long)U32(data, 0x1C);
    var count = image.Cylinders * image.Sides;
    if (list + (long)count * EntrySize > data.Length) throw new InvalidDataException("Stream HFE: the track list runs past the end of the file.");
    for (var c = 0; c < image.Cylinders; ++c)
      for (var s = 0; s < image.Sides; ++s) {
        var e = data.Slice((int)list + (image.Sides == 2 ? (c << 1) | s : c) * EntrySize, EntrySize);
        var flags = U32(e, 0);
        var offset = (long)U32(e, 4);
        var packedSize = (long)U32(e, 8);
        var unpackedSize = U32(e, 12);
        if (packedSize == 0) continue;
        if (offset + packedSize > data.Length) throw new InvalidDataException($"Stream HFE: track {c}.{s} runs past the end of the file.");
        if (unpackedSize > 64 << 20) throw new InvalidDataException($"Stream HFE: track {c}.{s} claims {unpackedSize} bytes.");
        var stored = data.Slice((int)offset, (int)packedSize);
        byte[] pulses;
        if (unpackedSize == 0) {
          pulses = [];
        } else if ((flags & PackedFlag) != 0) {
          pulses = new byte[unpackedSize];
          int got;
          try {
            got = Lz4BlockDecompressor.Decompress(stored, pulses);
          } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) {
            throw new InvalidDataException($"Stream HFE: track {c}.{s} is not a valid LZ4 block.", ex);
          }
          if (got != unpackedSize) throw new InvalidDataException($"Stream HFE: track {c}.{s} unpacks to {got} bytes, not {unpackedSize}.");
        } else {
          if (unpackedSize != packedSize) throw new InvalidDataException($"Stream HFE: unpacked track {c}.{s} sizes disagree.");
          pulses = stored.ToArray();
        }
        var track = new Track(pulses, U32(e, 16), flags);
        if (track.PulseCount != U32(e, 20)) throw new InvalidDataException($"Stream HFE: track {c}.{s} holds {track.PulseCount} pulses, the list says {U32(e, 20)}.");
        image.Tracks[(c, s)] = track;
      }
    return image;
  }

  public void Write(Stream output) {
    if (this.Sides is not (1 or 2)) throw new InvalidDataException($"Stream HFE: {this.Sides} sides.");
    var cylinders = Math.Max(this.Cylinders, this.Tracks.Count == 0 ? 1 : this.Tracks.Keys.Max(k => k.Cylinder) + 1);
    if (cylinders > 255) throw new InvalidDataException("Stream HFE: more than 255 tracks.");
    if (this.Tracks.Keys.Any(k => k.Side >= this.Sides)) throw new InvalidDataException("Stream HFE: a side-1 track on a single-sided image.");
    var slots = cylinders * this.Sides;
    // hxcfe's layout: header, the list padded to whole blocks with room for one spare entry, a zeroed index block, the data.
    var listBlocks = (slots + 1) * EntrySize / HeaderSize + 1;
    var listOffset = HeaderSize;
    var indexOffset = listOffset + listBlocks * HeaderSize;
    var dataOffset = indexOffset + HeaderSize;

    var header = new byte[HeaderSize];
    Signature.CopyTo(header);
    Put(header, 0x10, this.Revision);
    Put(header, 0x14, this.Flags);
    Put(header, 0x18, (uint)indexOffset);
    Put(header, 0x1C, (uint)listOffset);
    Put(header, 0x20, (uint)dataOffset);
    Put(header, 0x24, (uint)cylinders);
    Put(header, 0x28, (uint)this.Sides);
    Put(header, 0x2C, this.BitPeriod);
    Put(header, 0x30, this.InterfaceMode);
    PutString(header.AsSpan(0x44, 128), this.Name);
    PutString(header.AsSpan(0xC4, 16), this.Date);

    var list = new byte[listBlocks * HeaderSize];
    using var body = new MemoryStream();
    // Every slot is written: hxcfe's loader expects each cylinder to be present, so a missing track
    // becomes an unformatted one (no pulses).
    for (var slot = 0; slot < slots; ++slot) {
      var (c, s) = this.Sides == 2 ? (slot >> 1, slot & 1) : (slot, 0);
      var track = this.Tracks.GetValueOrDefault((c, s)) ?? new Track([], 0);
      var count = track.PulseCount;
      // An empty track still gets data, as hxcfe writes it (a one-token LZ4 block): a zero size means "no track",
      // and hxcfe's loader needs every cylinder present.
      var stored = (track.Flags & PackedFlag) == 0 ? track.Pulses
        : track.Pulses.Length == 0 ? [0x00] : Lz4BlockCompressor.Compress(track.Pulses);
      var e = list.AsSpan(slot * EntrySize, EntrySize);
      Put(e, 0, track.Flags);
      Put(e, 4, (uint)(dataOffset + body.Length));
      Put(e, 8, (uint)stored.Length);
      Put(e, 12, (uint)track.Pulses.Length);
      Put(e, 16, track.Length);
      Put(e, 20, (uint)count);
      body.Write(stored);
    }
    output.Write(header);
    output.Write(list);
    output.Write(new byte[HeaderSize]);
    body.Position = 0;
    body.CopyTo(output);
  }

  /// <summary>Pulse deltas (ticks) of a stream; throws when a code is cut short.</summary>
  public static List<uint> Decode(ReadOnlySpan<byte> pulses) {
    var deltas = new List<uint>();
    var k = 0;
    while (k < pulses.Length) {
      var c = pulses[k];
      var n = c < 0x80 ? 1 : c < 0xC0 ? 2 : c < 0xE0 ? 3 : c < 0xF0 ? 4 : 0;
      if (n == 0) throw new InvalidDataException($"Stream HFE: reserved pulse code 0x{c:X2}.");
      if (k + n > pulses.Length) throw new InvalidDataException("Stream HFE: a pulse code is cut short.");
      var v = (uint)(c & (0xFF >> n));
      for (var i = 1; i < n; ++i) v = (v << 8) | pulses[k + i];
      deltas.Add(v);
      k += n;
    }
    return deltas;
  }

  public static int CountPulses(ReadOnlySpan<byte> pulses) => Decode(pulses).Count;

  /// <summary>Encodes pulse deltas (ticks), clamping as hxcfe does at 28 bits.</summary>
  public static byte[] Encode(IEnumerable<uint> deltas) {
    var o = new List<byte>();
    foreach (var raw in deltas) {
      var d = Math.Min(raw, 0x0FFF_FFFFu);
      if (d < 0x80) o.Add((byte)d);
      else if (d < 0x4000) { o.Add((byte)(0x80 | (d >> 8))); o.Add((byte)d); }
      else if (d < 0x20_0000) { o.Add((byte)(0xC0 | (d >> 16))); o.Add((byte)(d >> 8)); o.Add((byte)d); }
      else { o.Add((byte)(0xE0 | (d >> 24))); o.Add((byte)(d >> 16)); o.Add((byte)(d >> 8)); o.Add((byte)d); }
    }
    return [.. o];
  }

  /// <summary>The revolution length hxcfe would give a stream: its total ticks rounded up to 32.</summary>
  public static uint DefaultLength(ReadOnlySpan<byte> pulses) {
    var total = Decode(pulses).Aggregate(0UL, (a, d) => a + d);
    return (uint)Math.Min((total + 31) & ~31UL, uint.MaxValue & ~31u);
  }

  public string RenderMetadata() {
    var sb = new StringBuilder("[StreamHfe]\n");
    sb.Append(CultureInfo.InvariantCulture, $"revision={this.Revision}\nflags={this.Flags}\ntracks={this.Cylinders}\nsides={this.Sides}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bit_period_ps={this.BitPeriod}\ninterface_mode={this.InterfaceMode}\nname={this.Name}\ndate={this.Date}\n");
    foreach (var ((c, s), t) in this.Tracks)
      sb.Append(CultureInfo.InvariantCulture, $"track_{c:D3}_{s}_length={t.Length}\ntrack_{c:D3}_{s}_pulses={t.PulseCount}\ntrack_{c:D3}_{s}_flags={t.Flags}\n");
    return sb.ToString();
  }

  private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);
  private static void Put(Span<byte> d, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d[at..], v);

  private static string CString(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
  }

  private static void PutString(Span<byte> field, string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    if (bytes.Length >= field.Length) throw new InvalidDataException($"Stream HFE: '{value}' does not fit {field.Length - 1} bytes.");
    bytes.CopyTo(field);
  }
}
