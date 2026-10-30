using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Aff;

/// <summary>One segment of an AFF file: name, 32-bit argument and data.</summary>
public sealed record AffSegment(string Name, uint Argument, byte[] Data);

/// <summary>
/// Reader/writer for single-file AFF (Advanced Forensic Format, AFFLIB v3 <c>.aff</c>).
/// Implemented from <c>docs/AFF-ON-DISK.md</c>; AFFLIB's tools are the oracle only.
/// </summary>
public sealed class AffImage {
  public static ReadOnlySpan<byte> FileMagic => "AFF10\r\n\0"u8;
  private static ReadOnlySpan<byte> SegmentHead => "AFF\0"u8;
  private static ReadOnlySpan<byte> SegmentTail => "ATT\0"u8;
  private const int HeadSize = 16;
  private const int TailSize = 8;

  public const uint PageCompressed = 0x0001;
  public const uint PageCompressedMax = 0x0002;
  public const uint PageAlgorithmMask = 0x00F0;
  public const uint PageAlgorithmZlib = 0x0000;
  public const uint PageAlgorithmLzma = 0x0020;
  public const uint PageAlgorithmZero = 0x0030;
  public const uint QuadwordFlag = 0x0002;
  public const int DefaultPageSize = 16 * 1024 * 1024;
  private const int MaxPageSize = 16 * 1024 * 1024;

  public IReadOnlyList<AffSegment> Segments { get; }
  public uint PageSize { get; }
  public uint SectorSize { get; }
  public long ImageSize { get; }

  private readonly Dictionary<long, AffSegment> _pages = [];

  private AffImage(List<AffSegment> segments) {
    this.Segments = segments;
    foreach (var s in segments)
      if (TryPageNumber(s.Name, out var page))
        this._pages[page] = s;
    this.PageSize = Find("pagesize")?.Argument ?? Find("segsize")?.Argument ?? 0;
    this.SectorSize = Find("sectorsize")?.Argument ?? 512;
    if (this.PageSize == 0 && this._pages.Count > 0)
      throw new InvalidDataException("AFF: no pagesize segment.");
    if (this.PageSize > MaxPageSize)
      throw new InvalidDataException($"AFF: pagesize {this.PageSize} exceeds the 16 MiB limit.");

    var imageSize = Find("imagesize") is { Data.Length: 8 } q ? DecodeQuad(q.Data) : -1;
    if (imageSize < 0) {
      // No imagesize segment: the last page's decoded length determines the size.
      if (this._pages.Count == 0) imageSize = 0;
      else {
        var last = this._pages.Keys.Max();
        imageSize = last * this.PageSize + this.DecodePage(this._pages[last]).Length;
      }
    }
    this.ImageSize = imageSize;
  }

  /// <summary>True when any segment is encrypted (AFFLIB's <c>/aes256</c> suffix or a stored key).</summary>
  public bool IsEncrypted => this.Segments.Any(s => s.Name.EndsWith("/aes256", StringComparison.Ordinal) || s.Name.StartsWith("affkey", StringComparison.Ordinal));

  /// <summary>True when the image carries signature segments (<c>/sha256</c>) or a signing certificate.</summary>
  public bool IsSigned => this.Segments.Any(s => s.Name.EndsWith("/sha256", StringComparison.Ordinal) || s.Name == "cert-sha256");

  public AffSegment? Find(string name) => this.Segments.LastOrDefault(s => s.Name == name);

  public int PageCount => this._pages.Count;

  /// <summary>Parses every segment; empty-name (ignored / free-space) segments are skipped.</summary>
  public static AffImage Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    Span<byte> magic = stackalloc byte[8];
    if (stream.ReadAtLeast(magic, 8, throwOnEndOfStream: false) != 8 || !magic.SequenceEqual(FileMagic))
      throw new InvalidDataException("Not an AFF file: the \"AFF10\\r\\n\\0\" header is missing.");

    var segments = new List<AffSegment>();
    Span<byte> head = stackalloc byte[HeadSize];
    Span<byte> tail = stackalloc byte[TailSize];
    for (;;) {
      var got = stream.ReadAtLeast(head, HeadSize, throwOnEndOfStream: false);
      if (got == 0) break;
      if (got != HeadSize || !head[..4].SequenceEqual(SegmentHead))
        throw new InvalidDataException($"AFF: segment {segments.Count} has no \"AFF\\0\" head.");
      var nameLength = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
      var dataLength = BinaryPrimitives.ReadUInt32BigEndian(head[8..]);
      var argument = BinaryPrimitives.ReadUInt32BigEndian(head[12..]);
      if (nameLength > 1024 || dataLength > MaxPageSize + 1024 * 1024)
        throw new InvalidDataException("AFF: segment name or data length is implausible.");
      if (stream.CanSeek && stream.Length - stream.Position < nameLength + dataLength + TailSize)
        throw new InvalidDataException("AFF: segment runs past the end of the file.");
      var nameBytes = ReadExact(stream, (int)nameLength);
      var data = ReadExact(stream, (int)dataLength);
      if (stream.ReadAtLeast(tail, TailSize, throwOnEndOfStream: false) != TailSize || !tail[..4].SequenceEqual(SegmentTail))
        throw new InvalidDataException("AFF: segment has no \"ATT\\0\" tail.");
      if (BinaryPrimitives.ReadUInt32BigEndian(tail[4..]) != HeadSize + nameLength + dataLength + TailSize)
        throw new InvalidDataException("AFF: segment tail length disagrees with its head.");
      if (nameLength == 0) continue;
      segments.Add(new(Encoding.ASCII.GetString(nameBytes), argument, data));
    }
    return new AffImage(segments);
  }

  /// <summary>Reconstructs the whole image; absent pages read as zeros (as AFFLIB does without bad-block fill).</summary>
  public void CopyImageTo(Stream output) {
    if (this.IsEncrypted)
      throw new NotSupportedException("AFF: encrypted images need AFFLIB's key handling, which is not implemented.");
    var remaining = this.ImageSize;
    var zero = Array.Empty<byte>();
    for (long page = 0; remaining > 0; ++page) {
      var want = (int)Math.Min(remaining, this.PageSize);
      if (this._pages.TryGetValue(page, out var seg)) {
        var data = this.DecodePage(seg);
        if (data.Length < want) {
          output.Write(data);
          WriteZeros(output, want - data.Length, ref zero);
        } else
          output.Write(data, 0, want);
      } else
        WriteZeros(output, want, ref zero);
      remaining -= want;
    }
  }

  /// <summary>The reconstructed image, verified against the stored md5/sha1/sha256 segments when present.</summary>
  public byte[] ReadVerifiedImage() {
    using var ms = new MemoryStream();
    this.CopyImageTo(ms);
    var image = ms.ToArray();
    Verify("md5", MD5.HashData);
    Verify("sha1", SHA1.HashData);
    Verify("sha256", SHA256.HashData);
    return image;

    void Verify(string name, Func<byte[], byte[]> hash) {
      if (this.Find(name) is { } seg && !seg.Data.AsSpan().SequenceEqual(hash(image)))
        throw new InvalidDataException($"AFF: the stored {name} does not match the reconstructed image.");
    }
  }

  private byte[] DecodePage(AffSegment seg) {
    if ((seg.Argument & PageCompressed) == 0)
      return seg.Data;
    switch (seg.Argument & PageAlgorithmMask) {
      case PageAlgorithmZero:
        if (seg.Data.Length != 4)
          throw new InvalidDataException($"AFF: zero page {seg.Name} payload is {seg.Data.Length} bytes, expected 4.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(seg.Data);
        if (count > this.PageSize)
          throw new InvalidDataException($"AFF: zero page {seg.Name} claims {count} bytes, more than a page.");
        return new byte[count];
      case PageAlgorithmZlib:
        var inflated = FileFormat.Zlib.ZlibStream.Decompress(seg.Data);
        if (inflated.Length > this.PageSize)
          throw new InvalidDataException($"AFF: page {seg.Name} inflates beyond the page size.");
        return inflated;
      case PageAlgorithmLzma: {
        if (seg.Data.Length < 13)
          throw new InvalidDataException($"AFF: LZMA page {seg.Name} is shorter than its 13-byte header.");
        var size = BinaryPrimitives.ReadInt64LittleEndian(seg.Data.AsSpan(5));
        if (size < 0 || size > this.PageSize)
          throw new InvalidDataException($"AFF: LZMA page {seg.Name} declares {size} bytes.");
        using var input = new MemoryStream(seg.Data, 13, seg.Data.Length - 13);
        var decoded = new LzmaDecoder(input, seg.Data[..5], size).Decode();
        if (decoded.Length != size)
          throw new InvalidDataException($"AFF: LZMA page {seg.Name} decoded to {decoded.Length} bytes, expected {size}.");
        return decoded;
      }
      default:
        throw new NotSupportedException($"AFF: page {seg.Name} uses unsupported compression 0x{seg.Argument & PageAlgorithmMask:X2}.");
    }
  }

  /// <summary><c>metadata.ini</c>: the geometry plus every non-page segment rendered as text, hex or a number.</summary>
  public byte[] RenderMetadata() {
    var sb = new StringBuilder("[aff]\n");
    sb.Append(CultureInfo.InvariantCulture, $"pagesize={this.PageSize}\nsectorsize={this.SectorSize}\nimagesize={this.ImageSize}\npages={this.PageCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"encrypted={(this.IsEncrypted ? "yes" : "no")}\nsigned={(this.IsSigned ? "yes" : "no")}\n");
    sb.Append("[segments]\n");
    foreach (var s in this.Segments) {
      if (TryPageNumber(s.Name, out _) || s.Name.Contains('=') || s.Name.Contains('\n')) continue;
      sb.Append(s.Name).Append('=').Append(RenderValue(s)).Append('\n');
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string RenderValue(AffSegment s) {
    if (s.Data.Length == 0) return s.Argument.ToString(CultureInfo.InvariantCulture);
    if (s.Data.Length == 8 && (s.Argument & QuadwordFlag) != 0) return DecodeQuad(s.Data).ToString(CultureInfo.InvariantCulture);
    var text = s.Data.AsSpan().TrimEnd((byte)0);
    if (text.Length > 0 && !text.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E) && s.Name is not ("md5" or "sha1" or "sha256" or "badflag"))
      return Encoding.ASCII.GetString(text);
    return Convert.ToHexStringLower(s.Data.Length > 64 ? s.Data[..64] : s.Data) + (s.Data.Length > 64 ? "..." : "");
  }

  /// <summary>Page number of a <c>page%d</c> (or legacy <c>seg%d</c>) segment name.</summary>
  public static bool TryPageNumber(string name, out long page) {
    page = -1;
    var digits = name.StartsWith("page", StringComparison.Ordinal) ? name.AsSpan(4)
      : name.StartsWith("seg", StringComparison.Ordinal) && name != "segsize" ? name.AsSpan(3) : default;
    return digits.Length > 0 && !digits.ContainsAnyExceptInRange('0', '9') &&
           long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out page);
  }

  /// <summary>AFFLIB's 64-bit "quad": low 32 bits then high 32 bits, each big-endian.</summary>
  public static long DecodeQuad(ReadOnlySpan<byte> data)
    => (long)((ulong)BinaryPrimitives.ReadUInt32BigEndian(data[4..]) << 32 | BinaryPrimitives.ReadUInt32BigEndian(data));

  public static byte[] EncodeQuad(long value) {
    var data = new byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(data, (uint)value);
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)((ulong)value >> 32));
    return data;
  }

  /// <summary>
  /// Writes an AFF for <paramref name="image"/>: the given leading metadata segments, then
  /// pagesize/sectorsize/imagesize/devicesectors, the pages (all-zero pages as ZERO, others
  /// zlib-compressed when that is smaller), and md5/sha1 of the image.
  /// </summary>
  public static void Write(Stream output, byte[] image, IEnumerable<AffSegment>? metadata = null,
      int pageSize = DefaultPageSize, uint sectorSize = 512) {
    if (pageSize <= 0 || pageSize > MaxPageSize || pageSize % sectorSize != 0)
      throw new ArgumentOutOfRangeException(nameof(pageSize), "AFF page size must be a positive multiple of the sector size, at most 16 MiB.");
    output.Write(FileMagic);
    WriteSegment(output, "pagesize", (uint)pageSize, []);
    WriteSegment(output, "sectorsize", sectorSize, []);
    foreach (var m in metadata ?? [])
      WriteSegment(output, m.Name, m.Argument, m.Data);
    WriteSegment(output, "imagesize", QuadwordFlag, EncodeQuad(image.Length));
    WriteSegment(output, "devicesectors", QuadwordFlag, EncodeQuad(image.Length / sectorSize));
    for (long page = 0, offset = 0; offset < image.Length; ++page, offset += pageSize) {
      var slice = image.AsSpan((int)offset, (int)Math.Min(pageSize, image.Length - offset));
      var name = "page" + page.ToString(CultureInfo.InvariantCulture);
      if (!slice.ContainsAnyExcept((byte)0)) {
        var count = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, (uint)slice.Length);
        WriteSegment(output, name, PageCompressed | PageAlgorithmZero | PageCompressedMax, count);
        continue;
      }
      var packed = FileFormat.Zlib.ZlibStream.Compress(slice);
      if (packed.Length < slice.Length) WriteSegment(output, name, PageCompressed | PageAlgorithmZlib, packed);
      else WriteSegment(output, name, 0, slice);
    }
    WriteSegment(output, "md5", 0, MD5.HashData(image));
    WriteSegment(output, "sha1", 0, SHA1.HashData(image));
  }

  /// <summary>Segments that describe the image's content or geometry and must be regenerated, not copied, on rewrite.</summary>
  public static bool IsDerived(string name)
    => TryPageNumber(name.Split('_')[0], out _) || name is "pagesize" or "segsize" or "sectorsize" or "imagesize" or "devicesectors"
      or "md5" or "sha1" or "sha256" or "badsectors" or "blanksectors" or "dir";

  private static void WriteSegment(Stream output, string name, uint argument, ReadOnlySpan<byte> data) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    Span<byte> head = stackalloc byte[HeadSize];
    SegmentHead.CopyTo(head);
    BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)nameBytes.Length);
    BinaryPrimitives.WriteUInt32BigEndian(head[8..], (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(head[12..], argument);
    output.Write(head);
    output.Write(nameBytes);
    output.Write(data);
    Span<byte> tail = stackalloc byte[TailSize];
    SegmentTail.CopyTo(tail);
    BinaryPrimitives.WriteUInt32BigEndian(tail[4..], (uint)(HeadSize + nameBytes.Length + data.Length + TailSize));
    output.Write(tail);
  }

  private static void WriteZeros(Stream output, int count, ref byte[] zero) {
    if (zero.Length < count) zero = new byte[Math.Max(count, 65536)];
    output.Write(zero, 0, count);
  }

  private static byte[] ReadExact(Stream stream, int count) {
    var buffer = new byte[count];
    if (stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false) != count)
      throw new InvalidDataException("AFF: file truncated inside a segment.");
    return buffer;
  }
}
