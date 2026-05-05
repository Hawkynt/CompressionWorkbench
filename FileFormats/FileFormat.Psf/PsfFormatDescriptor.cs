#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Psf;

public sealed class PsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Psf";
  public string DisplayName => "Portable Sound Format";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".psf";
  public IReadOnlyList<string> Extensions => [
    ".psf", ".psf2", ".minipsf", ".minipsf2",
    ".ssf", ".dsf", ".gsf", ".usf", ".2sf", ".ncsf", ".snsf", ".qsf",
  ];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Confidence is intentionally moderate: "PSF" is a short and common ASCII trigram. The
  // platform-specific 4th byte (0x01..0x4x) further narrows detection but isn't part of
  // the magic since it varies across the dozen PSF variants.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PSF"u8.ToArray(), Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("psf-zlib", "PSF zlib")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Portable Sound Format (game music archival container)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new PsfReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "psf-zlib", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new PsfReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new PsfWriter(output, leaveOpen: true);

    var named = FlatFiles(inputs).ToList();

    // Convention-driven mapping by archive name. Any unnamed/unknown single file is
    // treated as the program payload — handy when the caller just hands us one blob.
    var matched = false;
    foreach (var (name, data) in named) {
      if (string.Equals(name, PsfConstants.EntryProgram, StringComparison.OrdinalIgnoreCase)) {
        w.ProgramData = data;
        matched = true;
      } else if (string.Equals(name, PsfConstants.EntryReserved, StringComparison.OrdinalIgnoreCase)) {
        w.ReservedData = data;
        matched = true;
      } else if (string.Equals(name, PsfConstants.EntryTags, StringComparison.OrdinalIgnoreCase)) {
        ParseIniInto(Encoding.UTF8.GetString(data), w.Tags);
        matched = true;
      } else if (string.Equals(name, PsfConstants.EntryHeader, StringComparison.OrdinalIgnoreCase)) {
        // header.bin is synthesized from the other fields; ignore caller-supplied header to
        // avoid CRC/length mismatches.
        matched = true;
      }
    }

    // Single unmatched input: assume it's the program. This handles "psf create song.exe".
    if (!matched && named.Count == 1)
      w.ProgramData = named[0].Data;
  }

  private static void ParseIniInto(string text, IDictionary<string, string> tags) {
    foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
      var eq = rawLine.IndexOf('=');
      if (eq <= 0)
        continue;
      var key = rawLine[..eq].Trim();
      var value = rawLine[(eq + 1)..].Trim();
      if (key.Length == 0)
        continue;
      tags[key] = tags.TryGetValue(key, out var existing) ? existing + "\n" + value : value;
    }
  }
}
