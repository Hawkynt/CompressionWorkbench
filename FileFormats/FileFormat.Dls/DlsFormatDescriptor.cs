#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Dls;

/// <summary>
/// Exposes a Downloadable Sounds (DLS level-1/2, <c>RIFF/DLS </c>) collection as a
/// pseudo-archive: <c>FULL.dls</c> (byte-exact) plus one standalone WAV per wave pool
/// entry (<c>samples/NNN_&lt;name&gt;.wav</c>) and the INFO sub-chunks as
/// <c>metadata/&lt;id&gt;.txt</c> tags. Each wave pool entry is a <c>LIST wave</c> carrying
/// a <c>fmt </c> + <c>data</c> pair (and optional per-wave INFO); they are rewrapped into
/// independent RIFF/WAVE blobs. Read-only.
/// </summary>
public sealed class DlsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Dls";
  public string DisplayName => "DLS (Downloadable Sounds)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dls";
  public IReadOnlyList<string> Extensions => [".dls"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // "RIFF" at offset 0 is shared; the "DLS " form type at offset 8 discriminates.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DLS "u8.ToArray(), Offset: 8, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DLS downloadable sounds collection; full file + one WAV per wave-pool sample + INFO metadata.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.dls", "Container", blob),
    };

    if (blob.Length < 12 ||
        !blob.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
        !blob.AsSpan(8, 4).SequenceEqual("DLS "u8))
      return entries;

    var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4));
    var end = Math.Min(blob.Length, 8L + riffSize);

    var info = new List<(string Id, string Value)>();
    var waveCount = 0;

    var pos = 12L;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      if (id == "LIST" && bodyEnd - bodyStart >= 4) {
        var listType = Encoding.ASCII.GetString(blob, (int)bodyStart, 4);
        if (listType == "INFO")
          ParseInfo(blob, bodyStart + 4, bodyEnd, info);
        else if (listType == "wvpl")
          ParseWavePool(blob, bodyStart + 4, bodyEnd, entries, ref waveCount);
      }

      pos += 8 + size + (size & 1);
    }

    foreach (var (id, value) in info)
      entries.Add(new($"metadata/{id}.txt", "Tag", Encoding.ASCII.GetBytes(value)));

    var ini = new StringBuilder();
    var name = info.FirstOrDefault(t => t.Id == "INAM").Value;
    if (!string.IsNullOrEmpty(name)) ini.AppendLine($"name={name}");
    ini.AppendLine($"sample_count={waveCount}");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    return entries;
  }

  // Each child is a "LIST wave" holding fmt/data (+ optional INFO with dlid/name).
  private static void ParseWavePool(byte[] blob, long start, long end,
      List<AudioPseudoArchive.Entry> entries, ref int waveCount) {
    var pos = start;
    var index = 0;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      if (id == "LIST" && bodyEnd - bodyStart >= 4 &&
          Encoding.ASCII.GetString(blob, (int)bodyStart, 4) == "wave") {
        var wav = ExtractWave(blob, bodyStart + 4, bodyEnd, out var waveName);
        if (wav != null) {
          var safe = SanitizeFileName(waveName);
          var fileName = safe.Length == 0 ? $"samples/{index:D3}.wav" : $"samples/{index:D3}_{safe}.wav";
          entries.Add(new(fileName, "Sample", wav));
          ++waveCount;
        }
        ++index;
      }

      pos += 8 + size + (size & 1);
    }
  }

  // Rewrap a DLS wave's fmt + data into a standalone RIFF/WAVE blob.
  private static byte[]? ExtractWave(byte[] blob, long start, long end, out string waveName) {
    waveName = "";
    byte[]? fmt = null;
    byte[]? data = null;

    var pos = start;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      switch (id) {
        case "fmt ":
          fmt = blob.AsSpan((int)bodyStart, (int)(bodyEnd - bodyStart)).ToArray();
          break;
        case "data":
          data = blob.AsSpan((int)bodyStart, (int)(bodyEnd - bodyStart)).ToArray();
          break;
        case "LIST" when bodyEnd - bodyStart >= 4 &&
                         Encoding.ASCII.GetString(blob, (int)bodyStart, 4) == "INFO": {
          var info = new List<(string, string)>();
          ParseInfo(blob, bodyStart + 4, bodyEnd, info);
          waveName = info.FirstOrDefault(t => t.Item1 == "INAM").Item2 ?? "";
          break;
        }
      }

      pos += 8 + size + (size & 1);
    }

    if (fmt == null || data == null) return null;
    return BuildWav(fmt, data);
  }

  private static byte[] BuildWav(byte[] fmt, byte[] data) {
    var fmtPad = fmt.Length & 1;
    var dataPad = data.Length & 1;
    var riffSize = 4 + (8 + fmt.Length + fmtPad) + (8 + data.Length + dataPad);

    var wav = new byte[8 + riffSize];
    var s = wav.AsSpan();
    "RIFF"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)riffSize);
    "WAVE"u8.CopyTo(s[8..]);

    var o = 12;
    "fmt "u8.CopyTo(s[o..]); o += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(s[o..], (uint)fmt.Length); o += 4;
    fmt.CopyTo(s[o..]); o += fmt.Length + fmtPad;
    "data"u8.CopyTo(s[o..]); o += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(s[o..], (uint)data.Length); o += 4;
    data.CopyTo(s[o..]);
    return wav;
  }

  private static void ParseInfo(byte[] blob, long start, long end, List<(string, string)> info) {
    var pos = start;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      // INFO ids are uppercase 4CC strings; ignore binary chunks like dlid.
      if (id.Length == 4 && id.All(c => c is >= 'A' and <= 'Z')) {
        var sb = new StringBuilder();
        for (var i = bodyStart; i < bodyEnd; ++i) {
          var b = blob[(int)i];
          if (b == 0) break;
          sb.Append((char)b);
        }
        info.Add((id, sb.ToString()));
      }

      pos += 8 + size + (size & 1);
    }
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    return sb.ToString().Trim('.', '_', ' ');
  }
}
