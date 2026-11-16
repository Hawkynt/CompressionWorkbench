#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.ExePackers;

/// <summary>
/// The fields of a DOS MZ header that the DOS executable unpackers work from, plus the
/// writer that lays out an unpacked program the way the unpackers emit it: a 28-byte
/// header, the relocation table at offset 28, then the load image on a 16-byte boundary,
/// followed by any overlay copied unchanged.
/// </summary>
internal sealed record DosExe(
  int NumRelocs,
  int HeaderParagraphs,
  int MinAlloc,
  int MaxAlloc,
  short RegSS,
  ushort RegSP,
  ushort RegIP,
  short RegCS,
  int RelocTablePos,
  long EndOfCode,
  int FileLength) {

  /// <summary>Byte offset of the load image (the end of the header).</summary>
  public long StartOfCode => HeaderParagraphs * 16L;

  /// <summary>Byte offset of CS:IP in the file.</summary>
  public long EntryPoint => (HeaderParagraphs + RegCS) * 16L + RegIP;

  /// <summary>Bytes after the load image.</summary>
  public long OverlayLength => Math.Max(0, FileLength - EndOfCode);

  /// <summary>Reads the header; null when the bytes are not an MZ executable.</summary>
  public static DosExe? TryRead(ReadOnlySpan<byte> file) {
    if (file.Length < 28 || file[0] != 'M' || file[1] != 'Z')
      return null;
    var lastPageBytes = BinaryPrimitives.ReadUInt16LittleEndian(file[2..]);
    var pages = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]) & 0x7FF;
    var end = pages * 512L;
    if (lastPageBytes is >= 1 and <= 511)
      end = end - 512 + lastPageBytes;
    return new(
      BinaryPrimitives.ReadUInt16LittleEndian(file[6..]),
      BinaryPrimitives.ReadUInt16LittleEndian(file[8..]),
      BinaryPrimitives.ReadUInt16LittleEndian(file[10..]),
      BinaryPrimitives.ReadUInt16LittleEndian(file[12..]),
      BinaryPrimitives.ReadInt16LittleEndian(file[14..]),
      BinaryPrimitives.ReadUInt16LittleEndian(file[16..]),
      BinaryPrimitives.ReadUInt16LittleEndian(file[20..]),
      BinaryPrimitives.ReadInt16LittleEndian(file[22..]),
      BinaryPrimitives.ReadUInt16LittleEndian(file[24..]),
      end,
      file.Length);
  }

  /// <summary>
  /// Copies <paramref name="count"/> bytes from <paramref name="pos"/>, zero-filling what lies
  /// outside the file (the unpackers' probes read past EOF as zeros).
  /// </summary>
  public static byte[] ReadPadded(ReadOnlySpan<byte> file, long pos, int count) {
    var result = new byte[count];
    if (pos >= file.Length || pos + count <= 0)
      return result;
    var from = (int)Math.Max(0, pos);
    var to = (int)Math.Min(file.Length, pos + count);
    file[from..to].CopyTo(result.AsSpan((int)(from - pos)));
    return result;
  }

  /// <summary>
  /// The two CRC-32 values of the 32-byte halves of the 64 bytes at the entry point, as one
  /// 64-bit value (first half in the high word): the fingerprint by which the stubs are told apart.
  /// </summary>
  public ulong EntryPointCrcs(ReadOnlySpan<byte> file) {
    var bytes = ReadPadded(file, this.EntryPoint, 64);
    return ((ulong)Crc32.Compute(bytes.AsSpan(0, 32)) << 32) | Crc32.Compute(bytes.AsSpan(32, 32));
  }

  /// <summary>
  /// Lays out an unpacked program: 28-byte header, the relocation table (4 bytes per entry,
  /// offset then segment) at offset 28, the code image on a 16-byte boundary, then the overlay.
  /// </summary>
  public static byte[] Build(
      ReadOnlySpan<byte> relocTable, ReadOnlySpan<byte> code, int minAlloc, int maxAlloc,
      short regSS, ushort regSP, ushort regIP, short regCS, ReadOnlySpan<byte> overlay = default) {
    const int RelocPos = 28;
    var startOfCode = (RelocPos + relocTable.Length + 15) & ~15;
    var fileSize = (long)startOfCode + code.Length;
    var output = new byte[fileSize + overlay.Length];
    var h = output.AsSpan();
    BinaryPrimitives.WriteUInt16LittleEndian(h, 0x5A4D);
    BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)(fileSize % 512));
    BinaryPrimitives.WriteUInt16LittleEndian(h[4..], (ushort)((fileSize + 511) / 512));
    BinaryPrimitives.WriteUInt16LittleEndian(h[6..], (ushort)(relocTable.Length / 4));
    BinaryPrimitives.WriteUInt16LittleEndian(h[8..], (ushort)(startOfCode / 16));
    BinaryPrimitives.WriteUInt16LittleEndian(h[10..], (ushort)minAlloc);
    BinaryPrimitives.WriteUInt16LittleEndian(h[12..], (ushort)maxAlloc);
    BinaryPrimitives.WriteInt16LittleEndian(h[14..], regSS);
    BinaryPrimitives.WriteUInt16LittleEndian(h[16..], regSP);
    BinaryPrimitives.WriteUInt16LittleEndian(h[20..], regIP);
    BinaryPrimitives.WriteInt16LittleEndian(h[22..], regCS);
    BinaryPrimitives.WriteUInt16LittleEndian(h[24..], RelocPos);
    relocTable.CopyTo(h[RelocPos..]);
    code.CopyTo(h[startOfCode..]);
    overlay.CopyTo(h[(int)fileSize..]);
    return output;
  }
}

/// <summary>A DOS executable restored from its packed form.</summary>
/// <param name="Variant">The packer version or layout the stub was recognised as.</param>
/// <param name="Executable">The restored MZ (or COM) file.</param>
/// <param name="FileName">Suggested entry name, <c>unpacked.exe</c> or <c>unpacked.com</c>.</param>
public sealed record DosUnpacked(string Variant, byte[] Executable, string FileName = "unpacked.exe");

/// <summary>An unpacker for one DOS executable packer family.</summary>
public interface IDosExeUnpacker {
  /// <summary>Short id, also the <see cref="Compression.Core.ExecutableUnpacking.IExecutablePackerHandler.Id"/>.</summary>
  string Id { get; }

  /// <summary>Human-readable packer name.</summary>
  string DisplayName { get; }

  /// <summary>The packer variant the stub was recognised as, or null when it is not this packer.</summary>
  string? Detect(ReadOnlySpan<byte> file);

  /// <summary>Restores the original program; throws <see cref="InvalidDataException"/> when it cannot.</summary>
  DosUnpacked Unpack(ReadOnlySpan<byte> file);
}
