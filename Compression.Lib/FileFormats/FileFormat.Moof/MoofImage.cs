using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Moof;

/// <summary>
/// Applesauce MOOF (Macintosh 3.5" bitstream/flux) image, from the MOOF reference
/// (applesaucefdc.com/moof-reference): a 12-byte header with a CRC-32 of the rest, then chunks —
/// INFO (60 bytes), TMAP (160 entries, index = track × 2 + side), TRKS (160 TRK entries pointing at
/// 512-byte blocks from block 3), an optional FLUX map in its own block and optional META.
/// </summary>
public sealed class MoofImage {
  public const int InfoSize = 60, MapSize = 160;
  private const int TrkTableSize = 160 * 8;

  public byte[] Info { get; set; } = DefaultInfo(2);
  public byte[] Tmap { get; set; } = EmptyMap();
  public byte[]? FluxMap { get; set; }
  public string? Meta { get; set; }
  public SortedDictionary<int, (byte[] Data, uint BitCount)> Tracks { get; } = [];
  public bool CrcValid { get; private set; } = true;
  public List<(string Id, byte[] Data)> UnknownChunks { get; } = [];

  public static byte[] EmptyMap() {
    var m = new byte[MapSize];
    Array.Fill(m, (byte)0xFF);
    return m;
  }

  /// <param name="diskType">1 SSDD GCR 400K, 2 DSDD GCR 800K, 3 DSHD MFM 1.44M, 4 Twiggy.</param>
  public static byte[] DefaultInfo(byte diskType) {
    var info = new byte[InfoSize];
    info[0] = 1;
    info[1] = diskType;
    info[4] = (byte)(diskType == 3 ? 8 : 16);
    Encoding.ASCII.GetBytes("CompressionWorkbench".PadRight(32)).CopyTo(info, 5);
    return info;
  }

  /// <exception cref="InvalidDataException">Not MOOF, or a chunk or track runs out of the file.</exception>
  public static MoofImage Read(ReadOnlySpan<byte> d) {
    if (d.Length < 12 || !d[..4].SequenceEqual("MOOF"u8) || d[4] != 0xFF || d[5] != 0x0A || d[6] != 0x0D || d[7] != 0x0A)
      throw new InvalidDataException("MOOF: header signature missing.");
    var image = new MoofImage { CrcValid = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]) == Crc32(d[12..]) };
    byte[]? trks = null;
    var hasInfo = false;
    for (var pos = 12; pos + 8 <= d.Length;) {
      var id = Encoding.ASCII.GetString(d.Slice(pos, 4));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]);
      if (size > d.Length - pos - 8) throw new InvalidDataException($"MOOF: chunk {id} runs past the end of the file.");
      var payload = d.Slice(pos + 8, (int)size).ToArray();
      switch (id) {
        case "INFO" when payload.Length >= InfoSize: image.Info = payload[..InfoSize]; hasInfo = true; break;
        case "TMAP" when payload.Length >= MapSize: image.Tmap = payload[..MapSize]; break;
        case "FLUX" when payload.Length >= MapSize: image.FluxMap = payload[..MapSize]; break;
        case "TRKS": trks = payload; break;
        case "META": image.Meta = Encoding.UTF8.GetString(payload); break;
        default: image.UnknownChunks.Add((id, payload)); break;
      }
      pos += 8 + (int)size;
    }
    if (!hasInfo) throw new InvalidDataException("MOOF: no INFO chunk.");
    if (trks is not null) {
      if (trks.Length < TrkTableSize) throw new InvalidDataException("MOOF: TRKS chunk shorter than its TRK table.");
      for (var t = 0; t < 160; ++t) {
        var start = BinaryPrimitives.ReadUInt16LittleEndian(trks.AsSpan(8 * t));
        var blocks = BinaryPrimitives.ReadUInt16LittleEndian(trks.AsSpan(8 * t + 2));
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(trks.AsSpan(8 * t + 4));
        if (start == 0 || blocks == 0 || bits == 0) continue;
        var isFlux = image.FluxMap is not null && Array.IndexOf(image.FluxMap, (byte)t) >= 0;
        var length = isFlux ? (long)bits : (bits + 7) / 8;
        if (length > blocks * 512L || start * 512L + length > d.Length)
          throw new InvalidDataException($"MOOF: track {t} runs past its blocks or the file.");
        image.Tracks[t] = (d.Slice(start * 512, (int)length).ToArray(), bits);
      }
    }
    return image;
  }

  /// <summary>Writes the image; track blocks follow the TRK table from block 3, a FLUX map takes its own block after them.</summary>
  public void Write(Stream output) {
    if (this.Info.Length != InfoSize || this.Tmap.Length != MapSize || this.FluxMap is { Length: not MapSize })
      throw new ArgumentException("MOOF INFO is 60 bytes, TMAP and FLUX maps 160.");
    var info = (byte[])this.Info.Clone();
    var trkTable = new byte[TrkTableSize];
    var data = new MemoryStream();
    var block = 3;
    var largest = 0;
    var largestFlux = 0;
    foreach (var (t, (bytes, bits)) in this.Tracks) {
      if (t is < 0 or >= 160) throw new ArgumentException($"MOOF holds TRK entries 0–159, not {t}.");
      var isFlux = this.FluxMap is not null && Array.IndexOf(this.FluxMap, (byte)t) >= 0;
      var length = isFlux ? (int)bits : (int)((bits + 7) / 8);
      if (bits == 0 || length > bytes.Length) throw new ArgumentException($"MOOF track {t}: bit count {bits} does not fit {bytes.Length} bytes.");
      var blocks = (length + 511) / 512;
      BinaryPrimitives.WriteUInt16LittleEndian(trkTable.AsSpan(8 * t), (ushort)block);
      BinaryPrimitives.WriteUInt16LittleEndian(trkTable.AsSpan(8 * t + 2), (ushort)blocks);
      BinaryPrimitives.WriteUInt32LittleEndian(trkTable.AsSpan(8 * t + 4), bits);
      data.Write(bytes, 0, length);
      data.Write(new byte[blocks * 512 - length]);
      block += blocks;
      if (isFlux) largestFlux = Math.Max(largestFlux, blocks); else largest = Math.Max(largest, blocks);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(38), (ushort)largest);
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(40), (ushort)(this.FluxMap is null ? 0 : block));
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(42), (ushort)(this.FluxMap is null ? 0 : largestFlux));

    var body = new MemoryStream();
    Chunk(body, "INFO", info);
    Chunk(body, "TMAP", this.Tmap);
    var trks = new byte[TrkTableSize + data.Length];
    trkTable.CopyTo(trks, 0);
    data.ToArray().CopyTo(trks, TrkTableSize);
    Chunk(body, "TRKS", trks);
    if (this.FluxMap is not null) {
      // The FLUX chunk occupies its own block: TRKS ends block-aligned, so it starts at 'block'.
      // Its payload is the 160-byte map padded to fill the block, so the next chunk starts on a block boundary.
      var fluxPayload = new byte[512 - 8];
      this.FluxMap.CopyTo(fluxPayload, 0);
      Chunk(body, "FLUX", fluxPayload);
    }
    if (!string.IsNullOrEmpty(this.Meta)) Chunk(body, "META", Encoding.UTF8.GetBytes(this.Meta));
    foreach (var (id, payload) in this.UnknownChunks) Chunk(body, id, payload);

    var bodyBytes = body.ToArray();
    var header = new byte[12];
    "MOOF"u8.CopyTo(header);
    header[4] = 0xFF; header[5] = 0x0A; header[6] = 0x0D; header[7] = 0x0A;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Crc32(bodyBytes));
    output.Write(header);
    output.Write(bodyBytes);
  }

  public string RenderMetadata() {
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder("[moof]\n");
    var type = this.Info[1] switch { 1 => "SSDD GCR 400K", 2 => "DSDD GCR 800K", 3 => "DSHD MFM 1.44M", 4 => "Twiggy", var x => $"0x{x:X2}" };
    sb.Append(inv, $"info_version={this.Info[0]}\ndisk_type={type}\nwrite_protected={this.Info[2]}\nsynchronized={this.Info[3]}\n");
    sb.Append(inv, $"optimal_bit_timing={this.Info[4]}\ncreator={Encoding.UTF8.GetString(this.Info, 5, 32).TrimEnd()}\n");
    sb.Append(inv, $"tracks={this.Tracks.Count}\nflux={(this.FluxMap is null ? "no" : "yes")}\ncrc={(this.CrcValid ? "valid" : "INVALID")}\n");
    foreach (var (t, (_, bits)) in this.Tracks) sb.Append(inv, $"track_{t:D3}_bits={bits}\n");
    return sb.ToString();
  }

  private static void Chunk(Stream s, string id, ReadOnlySpan<byte> payload) {
    Span<byte> h = stackalloc byte[8];
    Encoding.ASCII.GetBytes(id, h);
    BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)payload.Length);
    s.Write(h);
    s.Write(payload);
  }

  internal static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
    }
    return ~crc;
  }
}
