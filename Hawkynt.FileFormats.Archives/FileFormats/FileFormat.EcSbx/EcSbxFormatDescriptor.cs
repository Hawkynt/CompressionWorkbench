#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.EcSbx;

/// <summary>The decoded content of an error-correcting SeqBox container.</summary>
public sealed record EcSbxContent(int Version, string FileName, DateTime? Modified, int DataShards, int ParityShards, byte[] Data);

/// <summary>
/// Error-correcting SeqBox (<c>.ecsbx</c>, blkar's SBX versions 17/18/19), read-only. Every block carries its
/// sequence number, so the protected file is reassembled from the data blocks wherever the burst
/// interleaving put them, and checked against the stored hash. A container whose data blocks are damaged or
/// missing needs a Reed-Solomon repair, which is refused rather than guessed.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/darrenldl/blockyarchive/blob/master/SBX_FORMAT.md</c> — blkar's SBX/EC-SBX specification (MIT)</description></item>
///   <item><description><c>https://github.com/MarcoPon/SeqBox</c> — the original SeqBox format</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Block (big-endian): <c>"SBx"</c>, version (17 → 512, 18 → 128, 19 → 4096 bytes), CRC-16-CCITT of bytes 6..end
/// seeded with the version, 6-byte file UID, UInt32 sequence number, payload. Sequence 0 is the metadata block
/// (FNM, SNM, FSZ, FDT, SDT, HSH multihash, RSD/RSP shard counts; 1 + parity copies). Sequence s ≥ 1 belongs to
/// RS set (s - 1) / (M + N); its first M positions are data, the remaining N parity.
/// </remarks>
public sealed class EcSbxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  private const int HeaderSize = 16;

  public string Id => "EcSbx";
  public string DisplayName => "Error-correcting SeqBox";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities => FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".ecsbx";
  public IReadOnlyList<string> Extensions => [".ecsbx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SBx\x11"u8.ToArray(), Confidence: 0.9), new("SBx\x12"u8.ToArray(), Confidence: 0.9), new("SBx\x13"u8.ToArray(), Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rs", "Reed-Solomon parity")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "blkar error-correcting SeqBox (versions 17/18/19): the protected file, hash-checked (read-only, no repair)";

  public static int BlockSize(int version) => version switch {
    17 => 512, 18 => 128, 19 => 4096,
    _ => throw new InvalidDataException($"EC-SBX: version {version} is not an error-correcting SeqBox (17, 18, 19)."),
  };

  /// <summary>CRC-16-CCITT (polynomial 0x1021, MSB first) seeded with the block's version byte.</summary>
  public static ushort BlockCrc(ReadOnlySpan<byte> block) {
    var crc = (ushort)block[3];
    foreach (var b in block[6..]) {
      crc ^= (ushort)(b << 8);
      for (var i = 0; i < 8; ++i)
        crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
    }
    return crc;
  }

  public static EcSbxContent Read(ReadOnlySpan<byte> file) {
    if (file.Length < HeaderSize || !file.StartsWith("SBx"u8))
      throw new InvalidDataException("EC-SBX: missing the SBx signature.");
    var version = file[3];
    var size = BlockSize(version);
    if (file.Length % size != 0)
      throw new InvalidDataException($"EC-SBX: {file.Length} bytes is not a whole number of {size}-byte blocks.");
    var uid = file.Slice(6, 6).ToArray();
    var blocks = new Dictionary<uint, byte[]>();
    for (var off = 0; off < file.Length; off += size) {
      var b = file.Slice(off, size);
      // A block that is damaged or belongs to another container is an erasure: skip it.
      if (!b.StartsWith("SBx"u8) || b[3] != version || !b.Slice(6, 6).SequenceEqual(uid)
          || BlockCrc(b) != BinaryPrimitives.ReadUInt16BigEndian(b[4..]))
        continue;
      blocks.TryAdd(BinaryPrimitives.ReadUInt32BigEndian(b[12..]), b[HeaderSize..].ToArray());
    }
    if (!blocks.TryGetValue(0, out var meta))
      throw new InvalidDataException("EC-SBX: no intact metadata block.");

    string? name = null;
    long fileSize = -1;
    DateTime? modified = null;
    byte[]? hash = null;
    int rsd = 0, rsp = 0;
    for (var p = 0; p + 4 <= meta.Length && meta[p] != 0x1A;) {
      var id = Encoding.ASCII.GetString(meta, p, 3);
      var len = meta[p + 3];
      if (p + 4 + len > meta.Length) throw new InvalidDataException($"EC-SBX: metadata field {id} runs past the block.");
      var v = meta.AsSpan(p + 4, len);
      switch (id) {
        case "FNM": name = Encoding.UTF8.GetString(v); break;
        case "FSZ" when len == 8: fileSize = (long)BinaryPrimitives.ReadUInt64BigEndian(v); break;
        case "FDT" when len == 8: modified = DateTime.UnixEpoch.AddSeconds(BinaryPrimitives.ReadInt64BigEndian(v)); break;
        case "HSH": hash = v.ToArray(); break;
        case "RSD" when len == 1: rsd = v[0]; break;
        case "RSP" when len == 1: rsp = v[0]; break;
      }
      p += 4 + len;
    }
    if (fileSize < 0 || rsd < 1 || rsp < 1)
      throw new InvalidDataException("EC-SBX: metadata lacks the file size or the Reed-Solomon shard counts.");

    var payload = size - HeaderSize;
    var dataBlocks = (fileSize + payload - 1) / payload;
    var data = new byte[fileSize];
    for (long i = 0; i < dataBlocks; ++i) {
      var seq = (uint)(1 + i / rsd * (rsd + rsp) + i % rsd);
      if (!blocks.TryGetValue(seq, out var d))
        throw new NotSupportedException($"EC-SBX: data block {i} (sequence {seq}) is damaged or missing; repairing it from the Reed-Solomon parity is not supported.");
      var start = i * payload;
      d.AsSpan(0, (int)Math.Min(payload, fileSize - start)).CopyTo(data.AsSpan((int)start));
    }
    VerifyHash(hash, data);
    return new EcSbxContent(version, name ?? "data.bin", modified, rsd, rsp, data);
  }

  /// <summary>Checks a multihash (SHA-1 0x11, SHA-256 0x12, SHA-512 0x13); other functions are not checked.</summary>
  private static void VerifyHash(byte[]? multihash, byte[] data) {
    if (multihash == null || multihash.Length < 2) return;
    byte[]? actual = multihash[0] switch {
      0x11 => SHA1.HashData(data),
      0x12 => SHA256.HashData(data),
      0x13 => SHA512.HashData(data),
      _ => null,
    };
    if (actual != null && !multihash.AsSpan(2).SequenceEqual(actual))
      throw new InvalidDataException("EC-SBX: the reassembled file does not match the stored hash.");
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var c = Read(ReadAll(stream));
    return [new ArchiveEntryInfo(0, SafeName(c.FileName), c.Data.Length, stream.Length, "rs", false, false, c.Modified)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var c = Read(ReadAll(stream));
    var name = SafeName(c.FileName);
    if (files == null || MatchesFilter(name, files))
      WriteFile(outputDir, name, c.Data);
  }

  private static string SafeName(string name) {
    var leaf = Path.GetFileName(name.Replace('\\', '/'));
    return string.IsNullOrWhiteSpace(leaf) ? "data.bin" : leaf;
  }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
