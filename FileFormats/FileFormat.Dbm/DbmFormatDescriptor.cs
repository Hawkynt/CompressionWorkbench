#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dbm;

/// <summary>
/// Exposes a DigiBooster Pro (DBM0) module as a read-only pseudo-archive. The
/// big-endian IFF-like container (NAME, INFO, SONG, PATT, INST, SMPL, VENV chunks)
/// is walked and surfaced under <c>chunks/&lt;TAG&gt;.bin</c>, with PATT patterns and
/// SMPL samples additionally decomposed. The layout was recovered through binary
/// inspection of the documented DigiBooster Pro file format and the OpenMPT loader.
/// Every chunk read is clamped; a malformed module surfaces
/// FULL + metadata(parse_status=partial) instead of throwing.
/// </summary>
public sealed class DbmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dbm";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DBM (DigiBooster Pro)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".dbm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dbm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DBM0"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "DigiBooster Pro (DBM0) module surfaced as a read-only pseudo-archive " +
    "(FULL + metadata + IFF-like chunks + PATT patterns + SMPL samples).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    Decompose(ReadAll(stream)).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in Decompose(ReadAll(stream))) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private readonly record struct Entry(string Name, byte[] Data, string Kind);

  private static List<Entry> Decompose(byte[] f) {
    var entries = new List<Entry> { new("FULL.dbm", f, "Track") };
    var meta = new StringBuilder().AppendLine("[dbm]");
    var ok = false;

    try {
      if (f.Length >= 8 && f[0] == 'D' && f[1] == 'B' && f[2] == 'M' && f[3] == '0') {
        meta.Append("magic = DBM0\n");
        // Bytes 4..7 hold the tracker version (BCD); chunks start at offset 8.
        meta.Append("tracker_version = 0x")
            .Append(BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4, 2)).ToString("X4"))
            .Append('\n');

        var pos = 8;
        var patt = 0;
        var smpl = 0;
        while (InRange(f, pos, 8)) {
          var tag = Encoding.ASCII.GetString(f, pos, 4);
          var len = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(pos + 4, 4)); // big-endian IFF
          var payOff = pos + 8;
          if (len < 0 || !InRange(f, payOff, len)) break;
          var payload = f.AsSpan(payOff, len).ToArray();

          switch (tag) {
            case "NAME":
              meta.Append("song_name = ").Append(ReadAscii(payload, 0, payload.Length)).Append('\n');
              break;
            case "INFO":
              // INFO: u16 instruments, samples, songs, patterns, channels.
              if (payload.Length >= 10) {
                meta.Append("num_instruments = ").Append(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2))).Append('\n');
                meta.Append("num_samples = ").Append(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2))).Append('\n');
                meta.Append("num_songs = ").Append(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2))).Append('\n');
                meta.Append("num_patterns = ").Append(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(6, 2))).Append('\n');
                meta.Append("num_channels = ").Append(BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(8, 2))).Append('\n');
              }
              entries.Add(new("chunks/INFO.bin", payload, "Tag"));
              break;
            case "PATT":
              entries.Add(new($"patterns/pattern_{++patt:D2}.bin", payload, "Pattern"));
              break;
            case "SMPL":
              entries.Add(new($"samples/{++smpl:D2}_sample.bin", payload, "Sample"));
              break;
            default:
              entries.Add(new($"chunks/{SanitizeTag(tag)}.bin", payload, "Tag"));
              break;
          }
          pos = payOff + len;
        }
        ok = true;
      }
    } catch { /* fall through to partial */ }

    if (!ok) meta.Append("parse_status = partial\n");
    entries.Insert(1, new("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"));
    return entries;
  }

  private static string SanitizeTag(string tag) {
    var sb = new StringBuilder(tag.Length);
    foreach (var c in tag) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
    return sb.ToString();
  }

  private static string ReadAscii(byte[] f, int off, int maxLen) {
    var end = Math.Min(off + maxLen, f.Length);
    var sb = new StringBuilder();
    for (var i = off; i < end; ++i) {
      var b = f[i];
      if (b == 0) break;
      if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static bool InRange(byte[] f, int off, int len) =>
    off >= 0 && len >= 0 && (long)off + len <= f.Length;
}
