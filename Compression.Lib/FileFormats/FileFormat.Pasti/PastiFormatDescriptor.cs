#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pasti;

/// <summary>
/// Pasti (<c>.stx</c>) Atari ST copy-protection-preserving floppy image, read-only. Lists
/// <c>metadata.ini</c> (every track and sector ID with FDC status, timing and position), each
/// sector's data as <c>tracks/cCC_hH/sNNN.bin</c> (fuzzy masks beside them), recorded track
/// images, and — when every track is a clean standard one — the plain sector image
/// <c>disk.st</c>. Pasti is an observational format and no free tool writes it, so it is not
/// written here.
/// </summary>
public sealed class PastiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Pasti";
  public string DisplayName => "Pasti (Atari ST STX)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".stx";
  public IReadOnlyList<string> Extensions => [".stx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("RSY\0"u8.ToArray(), Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Observed sectors")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Pasti STX Atari ST floppy image (sector IDs, FDC status, timing, fuzzy bits, track images)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Entries(Read(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null)).ToList();
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.stx", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(stream)))
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  private static (int Version, int Revision, List<PastiTrack> Tracks) Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return PastiImage.Read(ms.ToArray());
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries((int Version, int Revision, List<PastiTrack> Tracks) image) {
    yield return ("metadata.ini", Metadata(image));
    if (PastiImage.StandardImage(image.Tracks) is { } st) yield return ("disk.st", st);
    foreach (var t in image.Tracks) {
      var folder = string.Create(CultureInfo.InvariantCulture, $"tracks/c{t.Cylinder:D2}_h{t.Head}");
      if (t.TrackImage is not null) yield return ($"{folder}/track_image.bin", t.TrackImage);
      var seen = new Dictionary<int, int>();
      foreach (var s in t.Sectors) {
        var n = seen.TryGetValue(s.IdNumber, out var c) ? c + 1 : 0;
        seen[s.IdNumber] = n;
        var stem = n == 0 ? $"s{s.IdNumber:D3}" : $"s{s.IdNumber:D3}_{n}";
        yield return ($"{folder}/{stem}.bin", s.Data);
        if (s.FuzzyMask is not null) yield return ($"{folder}/{stem}.fuzzy", s.FuzzyMask);
      }
    }
  }

  private static byte[] Metadata((int Version, int Revision, List<PastiTrack> Tracks) image) {
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder("[pasti]\n");
    sb.Append(inv, $"version={image.Version}\nrevision={image.Revision}\ntracks={image.Tracks.Count}\n");
    sb.Append(inv, $"standard_image={(PastiImage.StandardImage(image.Tracks) is null ? "no" : "yes")}\n");
    foreach (var t in image.Tracks) {
      sb.Append(inv, $"[track c{t.Cylinder:D2} h{t.Head}]\nflags=0x{t.Flags:X4}\ntrack_length={t.TrackLength}\ntrack_image={(t.TrackImage is null ? 0 : t.TrackImage.Length)}\n");
      foreach (var s in t.Sectors)
        sb.Append(inv, $"sector={s.IdTrack}.{s.IdSide}.{s.IdNumber}.{s.IdSize} crc=0x{s.IdCrc:X4} fdc=0x{s.FdcStatus:X2} pos={s.BitPosition} time={s.ReadTime}{(s.FuzzyMask is null ? "" : " fuzzy")}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
