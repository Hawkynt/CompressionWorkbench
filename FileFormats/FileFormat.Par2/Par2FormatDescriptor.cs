#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Par2;

/// <summary>
/// PAR2 (Parchive v2) recovery archive. A PAR2 file is a flat sequence of packets,
/// each laid out as: 8-byte magic <c>"PAR2\0PKT"</c>, u64 little-endian packet length
/// (header + body, total), 16-byte MD5 of the packet (covering everything after the
/// length field), 16-byte recovery-set ID, 16-byte packet type, then the body.
///
/// <para>Well-known packet types: <c>"PAR 2.0\0Main\0\0\0\0"</c> (block size + protected
/// file IDs), <c>"PAR 2.0\0FileDesc"</c> (per-file 16-byte ID + MD5 + 16-byte MD5 of
/// first 16 KiB + u64 length + UTF-8 name), <c>"PAR 2.0\0IFSC\0\0\0\0"</c> (input file
/// slice checksums) and <c>"PAR 2.0\0RecvSlic"</c> (Reed-Solomon recovery slices).</para>
///
/// <para>This descriptor surfaces a verbatim <c>FULL.par2</c>, a <c>metadata.ini</c>
/// (recovery-set id, packet count, block size, protected-file count), a
/// <c>files.ini</c> listing each protected file's name and length parsed from the
/// FileDesc packets, and one raw entry per packet under <c>packets/NNNN_&lt;type&gt;.bin</c>.
/// Read-only; malformed input degrades to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://parchive.sourceforge.net</c> — Parchive project — hosts the PAR 2.0 specification</description></item>
///   <item><description><c>https://github.com/Parchive/par2cmdline</c> — par2cmdline — maintained reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Parchive</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class Par2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Par2";
  public string DisplayName => "Parchive v2 (PAR2)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".par2";
  public IReadOnlyList<string> Extensions => [".par2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PAR2\0PKT"u8.ToArray(), Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Parchive v2 (PAR2) recovery set: a packet stream. Surfaces FULL.par2, metadata.ini, " +
    "files.ini (protected file names + sizes from FileDesc packets) and a raw entry per packet.";

  private static ReadOnlySpan<byte> PacketMagic => "PAR2\0PKT"u8;

  private sealed record PacketInfo(long Offset, long Length, byte[] RecoverySetId, byte[] Type, string TypeName);

  private sealed record ProtectedFile(string Name, ulong Length);

  private sealed record Par2Model(
    bool Valid,
    bool Partial,
    byte[]? RecoverySetId,
    ulong BlockSize,
    IReadOnlyList<PacketInfo> Packets,
    IReadOnlyList<ProtectedFile> Files);

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    var model = Parse(data);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.par2", data.Length, data.Length, "Stored", false, false, null, Kind: "Track"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
    };
    var idx = 2;
    if (model.Files.Count > 0)
      entries.Add(new ArchiveEntryInfo(idx++, "files.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"));
    for (var i = 0; i < model.Packets.Count; ++i) {
      var p = model.Packets[i];
      entries.Add(new ArchiveEntryInfo(idx++, PacketEntryName(i, p), p.Length, p.Length, "Stored", false, false, null, Kind: "Chunk"));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.par2"))
      WriteFile(outputDir, "FULL.par2", data);

    var model = Parse(data);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(model)));

    if (model.Files.Count > 0 && Wants(files, "files.ini"))
      WriteFile(outputDir, "files.ini", Encoding.UTF8.GetBytes(BuildFilesIni(model)));

    for (var i = 0; i < model.Packets.Count; ++i) {
      var p = model.Packets[i];
      var name = PacketEntryName(i, p);
      if (!Wants(files, name)) continue;
      if (p.Offset < 0 || p.Length <= 0 || p.Offset + p.Length > data.Length) continue;
      var slab = new byte[p.Length];
      Array.Copy(data, p.Offset, slab, 0, p.Length);
      WriteFile(outputDir, name, slab);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static string PacketEntryName(int index, PacketInfo p) {
    var sb = new StringBuilder();
    foreach (var b in p.TypeName)
      sb.Append(char.IsLetterOrDigit(b) || b == '.' || b == '_' ? b : '_');
    var safeType = sb.ToString().Trim('_');
    if (safeType.Length == 0) safeType = "unknown";
    return $"packets/{index:D4}_{safeType}.bin";
  }

  private static Par2Model Parse(byte[] data) {
    var packets = new List<PacketInfo>();
    var files = new List<ProtectedFile>();
    byte[]? setId = null;
    ulong blockSize = 0;
    var partial = false;

    try {
      long pos = 0;
      var guard = 0;
      while (pos + 64 <= data.Length) {
        if (++guard > 1_000_000) { partial = true; break; }
        // Resync to the next magic if the current position is not a packet boundary.
        if (!HasMagic(data, pos)) {
          var next = IndexOfMagic(data, pos + 1);
          if (next < 0) break;
          partial = true;
          pos = next;
          continue;
        }

        var length = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(pos + 8), 8));
        // Length must cover header (64 bytes) and be a multiple of 4; reject implausible values.
        if (length < 64 || (length % 4) != 0 || pos + (long)length > data.Length) {
          partial = true;
          var next = IndexOfMagic(data, pos + 1);
          if (next < 0) break;
          pos = next;
          continue;
        }

        var recoverySetId = data.AsSpan((int)(pos + 32), 16).ToArray();
        var type = data.AsSpan((int)(pos + 48), 16).ToArray();
        var typeName = DecodeType(type);
        setId ??= recoverySetId;

        packets.Add(new PacketInfo(pos, (long)length, recoverySetId, type, typeName));

        var bodyOffset = pos + 64;
        var bodyLen = (long)length - 64;
        if (typeName.StartsWith("Main", StringComparison.Ordinal) && bodyLen >= 8)
          blockSize = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)bodyOffset, 8));
        else if (typeName.StartsWith("FileDesc", StringComparison.Ordinal))
          TryParseFileDesc(data, bodyOffset, bodyLen, files);

        pos += (long)length;
      }
    } catch {
      partial = true;
    }

    var valid = packets.Count > 0;
    return new Par2Model(valid, partial, setId, blockSize, packets, files);
  }

  // FileDesc body: 16-byte file id, 16-byte MD5 (full), 16-byte MD5 (first 16 KiB),
  // u64 length, then the UTF-8 file name padded with NUL to a 4-byte boundary.
  private static void TryParseFileDesc(byte[] data, long bodyOffset, long bodyLen, List<ProtectedFile> files) {
    if (bodyLen < 56) return;
    var p = (int)bodyOffset;
    var length = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p + 48, 8));
    var nameOffset = p + 56;
    var nameLen = (int)(bodyOffset + bodyLen) - nameOffset;
    if (nameLen <= 0) return;
    var nameBytes = data.AsSpan(nameOffset, nameLen);
    var nul = nameBytes.IndexOf((byte)0);
    if (nul >= 0) nameBytes = nameBytes[..nul];
    string name;
    try { name = Encoding.UTF8.GetString(nameBytes); }
    catch { name = string.Empty; }
    if (name.Length > 0)
      files.Add(new ProtectedFile(name, length));
  }

  private static string DecodeType(byte[] type) {
    // Type field is ASCII like "PAR 2.0\0FileDesc"; strip the "PAR 2.0\0" prefix.
    var s = Encoding.ASCII.GetString(type);
    const string prefix = "PAR 2.0\0";
    if (s.StartsWith(prefix, StringComparison.Ordinal))
      s = s[prefix.Length..];
    return s.Replace("\0", string.Empty).Trim();
  }

  private static bool HasMagic(byte[] data, long pos)
    => pos >= 0 && pos + 8 <= data.Length && data.AsSpan((int)pos, 8).SequenceEqual(PacketMagic);

  private static int IndexOfMagic(byte[] data, long from) {
    var span = data.AsSpan();
    for (var i = (int)Math.Max(0, from); i + 8 <= data.Length; ++i)
      if (span.Slice(i, 8).SequenceEqual(PacketMagic))
        return i;
    return -1;
  }

  private static string BuildMetadataIni(Par2Model m) {
    var sb = new StringBuilder();
    sb.Append("[Par2]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(m.Valid ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"packet_count={m.Packets.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"protected_file_count={m.Files.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"block_size={m.BlockSize}\n");
    if (m.RecoverySetId != null)
      sb.Append(CultureInfo.InvariantCulture, $"recovery_set_id={Convert.ToHexString(m.RecoverySetId)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(m.Partial || !m.Valid ? "partial" : "ok")}\n");
    return sb.ToString();
  }

  private static string BuildFilesIni(Par2Model m) {
    var sb = new StringBuilder();
    sb.Append("[Files]\n");
    sb.Append(CultureInfo.InvariantCulture, $"count={m.Files.Count}\n");
    for (var i = 0; i < m.Files.Count; ++i) {
      var f = m.Files[i];
      sb.Append(CultureInfo.InvariantCulture, $"\n[File{i}]\n");
      sb.Append(CultureInfo.InvariantCulture, $"name={f.Name.Replace('\n', ' ').Replace('\r', ' ')}\n");
      sb.Append(CultureInfo.InvariantCulture, $"length={f.Length}\n");
    }
    return sb.ToString();
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
