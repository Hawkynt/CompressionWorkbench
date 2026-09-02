#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CbmNibble;

/// <summary>
/// Shared implementation for the .nib + .g64 Commodore nibble-dump pseudo-archives.
/// Both variants share the same section walk: <c>metadata.ini</c> plus one
/// <c>track_{NN}.bin</c> entry per (half-)track containing raw GCR bytes.
/// Converting GCR back to a cleanly sectored D64 is a separate, non-trivial
/// undertaking (see nibtools); this descriptor is intentionally read-only.
/// </summary>
internal static class CbmNibbleEntries {
  public static List<(string Name, byte[] Data)> Build(Stream stream, string? fileName) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var img = CbmNibbleReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length), fileName);

    var result = new List<(string, byte[])> {
      ("metadata.ini", CbmNibbleReader.BuildMetadata(img)),
    };
    foreach (var t in img.Tracks) {
      if (t.Data.Length == 0) continue;  // skip empty half-tracks
      result.Add(($"track_{t.Index:D2}.bin", t.Data));
    }
    return result;
  }

  public static List<ArchiveEntryInfo> List(Stream stream, string? fileName) =>
    Build(stream, fileName).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null
    )).ToList();

  public static void Extract(Stream stream, string outputDir, string[]? files, string? fileName) {
    foreach (var e in Build(stream, fileName)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}

/// <summary>
/// Commodore G64 GCR track container (VICE emulator). Detected by the
/// 8-byte "GCR-1541" ASCII magic at offset 0.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://unusedino.de/ec64/technical/formats/g64.html</c> — Peter Schepers' G64 format specification</description></item>
///   <item><description><c>https://vice-emu.sourceforge.io</c> — VICE emulator, the origin and maintained implementation of G64</description></item>
/// </list>
/// </summary>
public sealed class G64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IArchiveCreatable {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "G64";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "G64 (Commodore GCR)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".g64";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".g64"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("GCR-1541"u8.ToArray(), Offset: 0, Confidence: 0.90)];
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
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Commodore 1541 GCR-encoded disk image (VICE G64)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    CbmNibbleEntries.List(stream, "image.g64");

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    CbmNibbleEntries.Extract(stream, outputDir, files, "image.g64");

  /// <summary>
  /// Builds a fresh G64 image from the inputs. The Commodore filesystem is flat,
  /// so names are reduced to their filename component and stored in the single
  /// track-18 directory by <see cref="CbmNibbleWriter"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var writer = new CbmNibbleWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      writer.AddFile(Path.GetFileName(input.ArchiveName), input.ReadContent());
    }
    writer.WriteTo(output);
  }
}

/// <summary>
/// Commodore NIB raw nibble dump (nibtools / ZoomFloppy). No magic header —
/// detected by file extension only; the typical dump is exactly 84 × 8192 bytes.
///
/// References:
/// <list type="bullet">
///   <item><description>nibtools (Pete Rittwage's C64 Disk Preservation Project) — the tool that defines and produces the de-facto NIB dump layout</description></item>
///   <item><description><c>http://unusedino.de/ec64/technical/formats/g64.html</c> — Peter Schepers' GCR track documentation (shared with G64)</description></item>
/// </list>
/// </summary>
public sealed class NibFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Nib";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "NIB (Commodore nibble dump)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".nib";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".nib"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // NIB has no leading magic — detection is purely extension-based.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Commodore 1541 raw nibble dump (nibtools / ZoomFloppy)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    CbmNibbleEntries.List(stream, "image.nib");

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    CbmNibbleEntries.Extract(stream, outputDir, files, "image.nib");
}
