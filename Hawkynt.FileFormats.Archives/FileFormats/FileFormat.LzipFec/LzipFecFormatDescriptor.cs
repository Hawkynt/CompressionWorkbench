#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace FileFormat.LzipFec;

/// <summary>One fec packet: its block number and its FEC bytes.</summary>
public sealed record LzipFecBlock(int Number, byte[] Data);

/// <summary>A parsed lziprecover fec file.</summary>
public sealed record LzipFecFile(
  bool Gf16, long FecBlockSize, long ProtectedSize, byte[] ProtectedMd5, uint[] Crc32s, uint[]? Crc32Cs, IReadOnlyList<LzipFecBlock> Blocks) {

  /// <summary>Number of data blocks the protected file is divided into.</summary>
  public long DataBlocks => (this.ProtectedSize + this.FecBlockSize - 1) / this.FecBlockSize;
}

/// <summary>
/// lziprecover forward-error-correction file (<c>.fec</c>), read-only: the parity lziprecover repairs a file
/// from. It holds no files, so the view is <c>metadata.ini</c>, the CRC arrays of the chksum packets and one
/// entry per FEC block (<c>fec_NNNNN.bin</c>); every packet's header and payload CRC is checked.
///
/// References:
/// <list type="bullet">
///   <item><description>lziprecover manual, "Fec file format" (<c>https://www.nongnu.org/lzip/manual/lziprecover_manual.html</c>)</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Packets (little-endian except the MD5): chksum <c>B3 A5 B6 AF</c>, Byte version 0, Byte flags (bit 0 CRC32-C
/// array, bit 1 GF(2^16)), UInt16 fbs, UInt64 protected size, Byte[16] MD5, UInt32 header CRC32, N × UInt32 CRC,
/// UInt32 CRC32 of the array; fec <c>B3 46 45 43</c>, UInt16 block number, UInt16 fbs, UInt32 header CRC32,
/// fbs bytes, UInt32 CRC32 of them. <c>fbs</c> is an 11-bit mantissa and 5-bit exponent: mantissa × 2^(exponent+9).
/// </remarks>
public sealed class LzipFecFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  private static ReadOnlySpan<byte> ChksumMagic => [0xB3, 0xA5, 0xB6, 0xAF];
  private static ReadOnlySpan<byte> FecMagic => [0xB3, 0x46, 0x45, 0x43];

  public string Id => "LzipFec";
  public string DisplayName => "lziprecover fec";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".fec";
  public IReadOnlyList<string> Extensions => [".fec"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xB3, 0xA5, 0xB6, 0xAF, 0x00], Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gf8", "GF(2^8)"), new("gf16", "GF(2^16)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "lziprecover fec file: metadata.ini, CRC arrays and FEC blocks, every packet CRC checked (read-only)";

  /// <summary>Decodes the 16-bit floating-point block size.</summary>
  public static long DecodeFbs(ushort coded) => (long)(coded & 0x7FF) << ((coded >> 11) + 9);

  /// <summary>Parses and checks a fec file.</summary>
  public static LzipFecFile Read(ReadOnlySpan<byte> file) {
    if (file.Length < 40 || !file.StartsWith(ChksumMagic))
      throw new InvalidDataException("fec: missing the chksum packet magic.");
    var (first, length) = ReadChksum(file, 0);
    var pos = length;
    uint[]? crc32c = null;
    var blocks = new List<LzipFecBlock>();
    while (pos < file.Length) {
      var rest = file[pos..];
      if (rest.StartsWith(ChksumMagic)) {
        if (crc32c != null) throw new InvalidDataException($"fec: a third chksum packet at {pos}.");
        var (second, secondLength) = ReadChksum(file, pos);
        if (!second.Crc32C || second.Fbs != first.Fbs || second.Size != first.Size || !second.Md5.AsSpan().SequenceEqual(first.Md5))
          throw new InvalidDataException("fec: the second chksum packet does not describe the same file with CRC32-C.");
        crc32c = second.Crcs;
        pos += secondLength;
        continue;
      }
      if (rest.Length < 16 || !rest.StartsWith(FecMagic))
        throw new InvalidDataException($"fec: no packet at offset {pos}.");
      if (Crc32.Compute(rest[..8]) != BinaryPrimitives.ReadUInt32LittleEndian(rest[8..]))
        throw new InvalidDataException($"fec: fec packet at {pos} has a bad header CRC.");
      var number = BinaryPrimitives.ReadUInt16LittleEndian(rest[4..]);
      var fbs = DecodeFbs(BinaryPrimitives.ReadUInt16LittleEndian(rest[6..]));
      if (fbs != first.Fbs || 16 + fbs > rest.Length)
        throw new InvalidDataException($"fec: fec packet {number} of {fbs} bytes does not fit.");
      var block = rest.Slice(12, (int)fbs);
      if (Crc32.Compute(block) != BinaryPrimitives.ReadUInt32LittleEndian(rest[(12 + (int)fbs)..]))
        throw new InvalidDataException($"fec: fec block {number} has a bad payload CRC.");
      blocks.Add(new LzipFecBlock(number, block.ToArray()));
      pos += 16 + (int)fbs;
    }
    if (blocks.Count == 0)
      throw new InvalidDataException("fec: no fec packets.");
    return new LzipFecFile(first.Gf16, first.Fbs, first.Size, first.Md5, first.Crcs, crc32c, blocks);
  }

  private readonly record struct Chksum(bool Crc32C, bool Gf16, long Fbs, long Size, byte[] Md5, uint[] Crcs);

  private static (Chksum Packet, int Length) ReadChksum(ReadOnlySpan<byte> file, int offset) {
    var p = file[offset..];
    if (p.Length < 40 || p[4] != 0)
      throw new InvalidDataException($"fec: chksum packet at {offset} is truncated or of an unknown version.");
    if (Crc32.Compute(p[..32]) != BinaryPrimitives.ReadUInt32LittleEndian(p[32..]))
      throw new InvalidDataException($"fec: chksum packet at {offset} has a bad header CRC.");
    var flags = p[5];
    if ((flags & 0xFC) != 0)
      throw new InvalidDataException($"fec: chksum packet flags 0x{flags:X2}.");
    var fbs = DecodeFbs(BinaryPrimitives.ReadUInt16LittleEndian(p[6..]));
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(p[8..]);
    if (fbs < 512 || size < 1)
      throw new InvalidDataException($"fec: chksum packet with block size {fbs} for {size} bytes.");
    var n = (size + fbs - 1) / fbs;
    if (40 + 4 * n > p.Length)
      throw new InvalidDataException($"fec: chksum packet lists {n} CRCs past the end of the file.");
    var crcs = new uint[n];
    for (var i = 0; i < n; ++i) crcs[i] = BinaryPrimitives.ReadUInt32LittleEndian(p[(36 + 4 * i)..]);
    if (Crc32.Compute(p.Slice(36, (int)(4 * n))) != BinaryPrimitives.ReadUInt32LittleEndian(p[(36 + 4 * (int)n)..]))
      throw new InvalidDataException($"fec: chksum packet at {offset} has a bad CRC-array CRC.");
    return (new Chksum((flags & 1) != 0, (flags & 2) != 0, fbs, size, p.Slice(16, 16).ToArray(), crcs), 40 + 4 * (int)n);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => [.. Entries(Read(ReadAll(stream))).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.Length, e.Data.Length, "stored", false, false, null))];

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(ReadAll(stream))))
      if (files == null || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  private static List<(string Name, byte[] Data)> Entries(LzipFecFile f) {
    var ini = new StringBuilder()
      .Append("[fec]\n")
      .Append(CultureInfo.InvariantCulture, $"protected_size={f.ProtectedSize}\n")
      .Append(CultureInfo.InvariantCulture, $"protected_md5={Convert.ToHexStringLower(f.ProtectedMd5)}\n")
      .Append(CultureInfo.InvariantCulture, $"block_size={f.FecBlockSize}\n")
      .Append(CultureInfo.InvariantCulture, $"data_blocks={f.DataBlocks}\n")
      .Append(CultureInfo.InvariantCulture, $"fec_blocks={f.Blocks.Count}\n")
      .Append(CultureInfo.InvariantCulture, $"field={(f.Gf16 ? "GF(2^16)" : "GF(2^8)")}\n")
      .Append(CultureInfo.InvariantCulture, $"crc32c={(f.Crc32Cs != null ? "yes" : "no")}\n");
    var entries = new List<(string, byte[])> { ("metadata.ini", Encoding.UTF8.GetBytes(ini.ToString())), ("crc32.bin", Bytes(f.Crc32s)) };
    if (f.Crc32Cs != null) entries.Add(("crc32c.bin", Bytes(f.Crc32Cs)));
    entries.AddRange(f.Blocks.Select(b => ($"fec_{b.Number:D5}.bin", b.Data)));
    return entries;
  }

  private static byte[] Bytes(uint[] values) {
    var b = new byte[values.Length * 4];
    for (var i = 0; i < values.Length; ++i) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), values[i]);
    return b;
  }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
