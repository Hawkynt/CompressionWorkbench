#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.UImage;

/// <summary>
/// Reader for the legacy U-Boot uImage container (<c>mkimage</c> output). The
/// fixed 64-byte big-endian header is followed by a body whose length is declared
/// in the header and whose compression is selected by the <c>ih_comp</c> byte.
/// </summary>
/// <remarks>
/// This reader only splits the container — it does not decompress the body.
/// Decompression is performed by <see cref="UImageFormatDescriptor"/> when the
/// compression type is one it can delegate to (currently only <c>none</c> — all
/// other schemes leave the body as <c>payload.bin</c> and skip the decompressed
/// entry).
/// </remarks>
public sealed class UImageReader {

  /// <summary>Legacy uImage magic <c>0x27051956</c> (BE u32 at offset 0).</summary>
  public const uint Magic = 0x27051956u;

  /// <summary>Length of the fixed-size legacy header.</summary>
  public const int HeaderSize = 64;

  /// <summary>Length of the image-name field.</summary>
  public const int NameLength = 32;

  /// <summary>Parsed uImage container.</summary>
  public sealed record UImage(
    uint Magic,
    uint HeaderCrc,
    uint Timestamp,
    uint DataSize,
    uint LoadAddress,
    uint EntryPoint,
    uint DataCrc,
    byte Os,
    byte Architecture,
    byte Type,
    byte Compression,
    string Name,
    byte[] Header,
    byte[] Body,
    uint ComputedHeaderCrc,
    uint ComputedDataCrc
  );

  /// <summary>Parses a uImage from a full-file byte span.</summary>
  public static UImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize)
      throw new InvalidDataException($"uImage: file shorter than {HeaderSize}-byte header.");

    var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
    if (magic != Magic)
      throw new InvalidDataException($"uImage: bad magic 0x{magic:X8} (expected 0x{Magic:X8}).");

    var hcrc = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
    var time = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
    var size = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
    var load = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
    var ep = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
    var dcrc = BinaryPrimitives.ReadUInt32BigEndian(data[24..]);
    var os = data[28];
    var arch = data[29];
    var type = data[30];
    var comp = data[31];

    var nameSpan = data.Slice(32, NameLength);
    var nameEnd = nameSpan.IndexOf((byte)0);
    if (nameEnd < 0) nameEnd = NameLength;
    var name = Encoding.ASCII.GetString(nameSpan[..nameEnd]);

    var bodyEnd = HeaderSize + (int)Math.Min(size, (uint)(data.Length - HeaderSize));
    var body = data[HeaderSize..bodyEnd].ToArray();
    var header = data[..HeaderSize].ToArray();

    // Recompute header CRC: clear the hcrc field to 0 before hashing.
    var headerForCrc = header.ToArray();
    headerForCrc[4] = headerForCrc[5] = headerForCrc[6] = headerForCrc[7] = 0;
    var computedHeaderCrc = Crc32Ieee.Compute(headerForCrc);
    var computedDataCrc = Crc32Ieee.Compute(body);

    return new UImage(magic, hcrc, time, size, load, ep, dcrc, os, arch, type, comp,
      name, header, body, computedHeaderCrc, computedDataCrc);
  }

  /// <summary>Decodes the <c>ih_os</c> byte to a readable name.</summary>
  public static string OsName(byte os) => os switch {
    0 => "INVALID",
    1 => "OPENBSD",
    2 => "NETBSD",
    3 => "FREEBSD",
    4 => "4_4BSD",
    5 => "LINUX",
    6 => "SVR4",
    7 => "ESIX",
    8 => "SOLARIS",
    9 => "IRIX",
    10 => "SCO",
    11 => "DELL",
    12 => "NCR",
    13 => "LYNXOS",
    14 => "VXWORKS",
    15 => "PSOS",
    16 => "QNX",
    17 => "U_BOOT",
    18 => "RTEMS",
    19 => "ARTOS",
    20 => "UNITY",
    21 => "INTEGRITY",
    22 => "OSE",
    23 => "PLAN9",
    24 => "OPENRTOS",
    25 => "ARM_TRUSTED_FIRMWARE",
    26 => "TEE",
    27 => "OPENSBI",
    28 => "EFI",
    29 => "ELF",
    _ => $"UNKNOWN({os})",
  };

  /// <summary>Decodes the <c>ih_arch</c> byte to a readable name.</summary>
  public static string ArchName(byte arch) => arch switch {
    0 => "INVALID",
    1 => "ALPHA",
    2 => "ARM",
    3 => "I386",
    4 => "IA64",
    5 => "MIPS",
    6 => "MIPS64",
    7 => "PPC",
    8 => "S390",
    9 => "SH",
    10 => "SPARC",
    11 => "SPARC64",
    12 => "M68K",
    13 => "NIOS",
    14 => "MICROBLAZE",
    15 => "NIOS2",
    16 => "BLACKFIN",
    17 => "AVR32",
    18 => "ST200",
    19 => "SANDBOX",
    20 => "NDS32",
    21 => "OPENRISC",
    22 => "ARM64",
    23 => "ARC",
    24 => "X86_64",
    25 => "XTENSA",
    26 => "RISCV",
    _ => $"UNKNOWN({arch})",
  };

  /// <summary>Decodes the <c>ih_type</c> byte to a readable name.</summary>
  public static string TypeName(byte type) => type switch {
    0 => "INVALID",
    1 => "STANDALONE",
    2 => "KERNEL",
    3 => "RAMDISK",
    4 => "MULTI",
    5 => "FIRMWARE",
    6 => "SCRIPT",
    7 => "FILESYSTEM",
    8 => "FLATDT",
    9 => "KWBIMAGE",
    10 => "IMXIMAGE",
    14 => "KERNEL_NOLOAD",
    22 => "LOADABLE",
    _ => $"TYPE_{type}",
  };

  /// <summary>Decodes the <c>ih_comp</c> byte to a readable name.</summary>
  public static string CompressionName(byte comp) => comp switch {
    0 => "none",
    1 => "gzip",
    2 => "bzip2",
    3 => "lzma",
    4 => "lzo",
    5 => "lz4",
    6 => "zstd",
    _ => $"comp_{comp}",
  };
}
