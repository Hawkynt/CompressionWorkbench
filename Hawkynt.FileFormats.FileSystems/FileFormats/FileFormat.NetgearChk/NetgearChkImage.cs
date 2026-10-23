#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.NetgearChk;

/// <summary>
/// A NETGEAR <c>.chk</c> firmware image: a big-endian header (magic 0x2A23245E, header length, an
/// 8-byte reserved block whose first byte is the region, checksums and lengths of kernel and
/// rootfs, an image checksum and a header checksum) followed by the board-id string, the kernel
/// and the optional rootfs. The layout and checksum are specified in <c>docs/NETGEAR-CHK-ON-DISK.md</c>.
/// </summary>
public sealed class NetgearChkImage {

  public const uint Magic = 0x2A23245E;
  public const int FixedHeaderSize = 40;
  public const string DefaultBoardId = "U12H072T00_NETGEAR";

  /// <summary>The 8 reserved bytes, raw; byte 0 is the region (1 = worldwide, 2 = North America).</summary>
  public byte[] Reserved { get; set; } = [1, 99, 99, 99, 99, 99, 99, 99];
  public byte[] BoardId { get; set; } = Encoding.ASCII.GetBytes(DefaultBoardId);
  public byte[] Kernel { get; set; } = [];
  public byte[] Rootfs { get; set; } = [];
  public byte[] Trailer { get; set; } = [];

  public uint StoredKernelChecksum { get; private set; }
  public uint StoredRootfsChecksum { get; private set; }
  public uint StoredImageChecksum { get; private set; }
  public uint StoredHeaderChecksum { get; private set; }
  public uint ComputedHeaderChecksum { get; private set; }

  public bool ChecksumsValid =>
    this.StoredKernelChecksum == Checksum(this.Kernel)
    && this.StoredRootfsChecksum == (this.Rootfs.Length == 0 ? 0 : Checksum(this.Rootfs))
    && this.StoredImageChecksum == ImageChecksum(this.Kernel, this.Rootfs)
    && this.StoredHeaderChecksum == this.ComputedHeaderChecksum;

  public byte Region {
    get => this.Reserved[0];
    set => this.Reserved[0] = value;
  }

  /// <summary>
  /// NETGEAR's Fletcher-style checksum: over every byte, <c>c0 += b; c1 += c0</c> in 32 bits; each
  /// sum is then folded to 16 bits with end-around carry and the result is <c>c1 &lt;&lt; 16 | c0</c>.
  /// </summary>
  public static uint Checksum(ReadOnlySpan<byte> data) => Finish(Accumulate(0, 0, data));

  private static (uint C0, uint C1) Accumulate(uint c0, uint c1, ReadOnlySpan<byte> data) {
    foreach (var b in data) {
      c0 += b;
      c1 += c0;
    }
    return (c0, c1);
  }

  private static uint Finish((uint C0, uint C1) sums) {
    static uint Fold(uint c) {
      var b = (c & 0xFFFF) + (c >> 16);
      return ((b >> 16) + b) & 0xFFFF;
    }
    return Fold(sums.C1) << 16 | Fold(sums.C0);
  }

  private static uint ImageChecksum(byte[] kernel, byte[] rootfs) {
    var sums = Accumulate(0, 0, kernel);
    return Finish(Accumulate(sums.C0, sums.C1, rootfs));
  }

  public static NetgearChkImage Parse(ReadOnlySpan<byte> d) {
    if (d.Length < FixedHeaderSize || BinaryPrimitives.ReadUInt32BigEndian(d) != Magic)
      throw new InvalidDataException("CHK: no 0x2A23245E header.");
    var headerLength = BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
    if (headerLength < FixedHeaderSize || headerLength > d.Length || headerLength > 4096)
      throw new InvalidDataException($"CHK: header length {headerLength} is not plausible for a {d.Length}-byte file.");
    var kernelLength = BinaryPrimitives.ReadUInt32BigEndian(d[24..]);
    var rootfsLength = BinaryPrimitives.ReadUInt32BigEndian(d[28..]);
    if ((long)headerLength + kernelLength + rootfsLength > d.Length)
      throw new InvalidDataException($"CHK: kernel ({kernelLength}) and rootfs ({rootfsLength}) run past the end of the file.");
    var img = new NetgearChkImage {
      Reserved = d.Slice(8, 8).ToArray(),
      StoredKernelChecksum = BinaryPrimitives.ReadUInt32BigEndian(d[16..]),
      StoredRootfsChecksum = BinaryPrimitives.ReadUInt32BigEndian(d[20..]),
      StoredImageChecksum = BinaryPrimitives.ReadUInt32BigEndian(d[32..]),
      StoredHeaderChecksum = BinaryPrimitives.ReadUInt32BigEndian(d[36..]),
      BoardId = d[FixedHeaderSize..(int)headerLength].ToArray(),
    };
    var start = (int)headerLength;
    img.Kernel = d.Slice(start, (int)kernelLength).ToArray();
    img.Rootfs = d.Slice(start + (int)kernelLength, (int)rootfsLength).ToArray();
    img.Trailer = d[(start + (int)kernelLength + (int)rootfsLength)..].ToArray();
    var header = d[..start].ToArray();
    header.AsSpan(36, 4).Clear();
    img.ComputedHeaderChecksum = Checksum(header);
    return img;
  }

  public byte[] ToBytes() {
    if (this.BoardId.Length is 0 or > 64) throw new InvalidOperationException("CHK: the board id must be 1 to 64 bytes.");
    if (this.Reserved.Length != 8) throw new InvalidOperationException("CHK: the reserved block is 8 bytes.");
    var headerLength = FixedHeaderSize + this.BoardId.Length;
    var image = new byte[headerLength + this.Kernel.Length + this.Rootfs.Length + this.Trailer.Length];
    BinaryPrimitives.WriteUInt32BigEndian(image, Magic);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), (uint)headerLength);
    this.Reserved.CopyTo(image, 8);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16), Checksum(this.Kernel));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20), this.Rootfs.Length == 0 ? 0 : Checksum(this.Rootfs));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(24), (uint)this.Kernel.Length);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(28), (uint)this.Rootfs.Length);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(32), ImageChecksum(this.Kernel, this.Rootfs));
    this.BoardId.CopyTo(image, FixedHeaderSize);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(36), Checksum(image.AsSpan(0, headerLength)));
    this.Kernel.CopyTo(image, headerLength);
    this.Rootfs.CopyTo(image, headerLength + this.Kernel.Length);
    this.Trailer.CopyTo(image, headerLength + this.Kernel.Length + this.Rootfs.Length);
    return image;
  }
}
