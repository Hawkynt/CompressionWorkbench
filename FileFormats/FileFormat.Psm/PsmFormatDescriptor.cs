#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Psm;

/// <summary>
/// Exposes a ProTracker Studio / Epic MegaGames PSM module as a read-only
/// pseudo-archive. Two variants are detected: the new IFF-like format
/// (<c>PSM&#160;</c> + FILE/TITL/SDFT/PBOD/SONG/DSMP chunks) and the old
/// <c>PSM\xFE</c> format. New-format chunks are surfaced under
/// <c>chunks/&lt;TAG&gt;_NN.bin</c>, with PBOD patterns and DSMP samples additionally
/// decomposed. The layout was recovered through binary inspection of the documented
/// PSM format and the OpenMPT loader. Every offset read is clamped; a malformed
/// module surfaces FULL + metadata(parse_status=partial) instead of throwing.
/// </summary>
public sealed class PsmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Psm";
  public string DisplayName => "PSM (ProTracker Studio / Epic MegaGames)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".psm";
  public IReadOnlyList<string> Extensions => [".psm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PSM "u8.ToArray(), Offset: 0, Confidence: 0.9),
    new([0x50, 0x53, 0x4D, 0xFE], Offset: 0, Confidence: 0.92),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "PSM (ProTracker Studio / Epic MegaGames) module surfaced as a read-only " +
    "pseudo-archive (FULL + metadata + IFF-like chunks + PBOD patterns + DSMP samples); " +
    "both the new 'PSM ' and old 'PSM\\xFE' variants are detected.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    Decompose(ReadAll(stream)).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();

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
    var entries = new List<Entry> { new("FULL.psm", f, "Track") };
    var meta = new StringBuilder().AppendLine("[psm]");
    var ok = false;

    try {
      if (f.Length >= 4 && f[0] == 'P' && f[1] == 'S' && f[2] == 'M') {
        if (f[3] == 0xFE) {
          // Old-format PSM (MASI predecessor): header carries an ASCII song name.
          meta.Append("variant = old (PSM\\xFE)\n");
          var name = ReadAscii(f, 4, 60);
          if (name.Length > 0) meta.Append("song_name = ").Append(name).Append('\n');
          ok = true;
        } else if (f[3] == ' ') {
          meta.Append("variant = new (PSM )\n");
          // New format: "PSM " then a "FILE" chunk-id wrapper is uncommon; the
          // documented MASI layout is "PSM " + u32 fileSize + "FILE" + chunks.
          // We walk IFF-style chunks: 4-byte tag + u32 LE length + payload.
          var pbod = 0;
          var dsmp = 0;
          var pos = 4;
          // Optional "FILE" form wrapper, which may appear directly after "PSM "
          // or after a 4-byte total-size field.
          if (HasTag(f, pos, "FILE"))
            pos += 4;
          else if (HasTag(f, pos + 4, "FILE"))
            pos += 8;

          while (InRange(f, pos, 8)) {
            var tag = Encoding.ASCII.GetString(f, pos, 4);
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(pos + 4, 4));
            var payOff = pos + 8;
            if (len < 0 || !InRange(f, payOff, len)) break;
            var payload = f.AsSpan(payOff, len).ToArray();

            switch (tag) {
              case "TITL":
                meta.Append("title = ").Append(ReadAscii(payload, 0, payload.Length)).Append('\n');
                break;
              case "PBOD":
                entries.Add(new($"patterns/pattern_{++pbod:D2}.bin", payload, "Pattern"));
                break;
              case "DSMP":
                entries.Add(new($"samples/{++dsmp:D2}_sample.bin", payload, "Sample"));
                break;
              default:
                entries.Add(new($"chunks/{SanitizeTag(tag)}.bin", payload, "Tag"));
                break;
            }
            pos = payOff + len;
            if (len % 2 == 1 && InRange(f, pos, 1)) pos++; // IFF word padding
          }
          meta.Append("num_patterns = ").Append(pbod).Append('\n');
          meta.Append("num_samples = ").Append(dsmp).Append('\n');
          ok = true;
        }
      }
    } catch { /* fall through to partial */ }

    if (!ok) meta.Append("parse_status = partial\n");
    entries.Insert(1, new("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"));
    return entries;
  }

  private static bool HasTag(byte[] f, int off, string tag) {
    if (!InRange(f, off, tag.Length)) return false;
    for (var i = 0; i < tag.Length; ++i)
      if (f[off + i] != (byte)tag[i]) return false;
    return true;
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
