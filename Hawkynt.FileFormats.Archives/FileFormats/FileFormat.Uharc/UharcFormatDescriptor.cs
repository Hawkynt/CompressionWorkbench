#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.6 archive (Uwe Herklotz, 1997–2005). Lists and extracts every 0.6/0.6a/0.6b archive: the store, RLE (<c>-mr</c>), LZ78 (<c>-mw</c>), LZP (<c>-mz</c>), PPM (<c>-mx</c>) and ALZ
/// (<c>-m1</c>..<c>-m3</c>, including the multimedia predictors) modes, password-protected archives
/// and self-extractors, and UHARC 0.2 archives. Archives of 0.3 and 0.4 are refused. Creates 0.6
/// archives, stored or LZP-packed, that <c>uharc.exe</c> extracts; it cannot modify them.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/UHARC-ON-DISK.md</c> — the layout and codecs as implemented, with the measurements behind them</description></item>
///   <item><description><c>http://fileformats.archiveteam.org/wiki/UHARC</c> — format overview and sample archives</description></item>
///   <item><description>UHARC 0.6b documentation (<c>UHARC.DOC</c>, 01 Oct 2005) — modes, switches and dictionary sizes</description></item>
/// </list>
/// </summary>
public sealed class UharcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable {

  public string Id => "Uharc";
  public string DisplayName => "UHARC";
  public FormatCategory Category => FormatCategory.Archive;
  // Write-once: uharc.exe itself only creates archives, and every archive the writer makes is
  // extracted byte for byte by uharc.exe and unuharc.exe.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".uha";
  public IReadOnlyList<string> Extensions => [".uha"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("UHA"u8.ToArray(), Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("store", "Store"), new("rle", "RLE"), new("lz78", "LZ78"), new("lzp", "LZP"), new("ppm", "PPM"), new("alz", "ALZ"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "UHARC 0.6 archive. Lists and extracts the store, RLE, LZ78, LZP, PPM and ALZ modes (with multimedia " +
    "and executable filters, passwords and SFX stubs), and UHARC 0.2 archives. 0.3 and 0.4 archives are refused. " +
    "Creates 0.6 archives, stored or packed with LZP.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (TryOpen(stream, password, out var reader, out var image) is { } failure)
      return [
        new(0, "FULL.uha", image.LongLength, image.LongLength, "stored", false, false, null),
        new(1, "metadata.ini", Metadata(failure).Length, Metadata(failure).Length, "stored", false, false, null),
      ];

    var method = reader!.MethodName;
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, -1,
      e.IsDirectory ? "dir" : method, e.IsDirectory, reader.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (TryOpen(stream, password, out var reader, out var image) is { } failure) {
      WriteIfMatch(outputDir, "FULL.uha", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Metadata(failure), files);
      return;
    }

    foreach (var e in reader!.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files))
        continue;
      if (e.IsDirectory)
        Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
      else
        WriteFile(outputDir, e.FileName, reader.Extract(e));
    }
  }

  /// <summary>Writes a UHARC 0.6 archive: method <c>lzp</c> (the default) or <c>store</c>.</summary>
  /// <exception cref="NotSupportedException">Another method, a password, a name longer than 255 bytes or more than 4 GiB of data.</exception>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!string.IsNullOrEmpty(options?.Password))
      throw new NotSupportedException("UHARC: the writer does not encipher archives.");

    var method = options?.MethodName?.Trim().ToLowerInvariant();
    var compress = method switch {
      null or "" or "lzp" => true,
      "store" or "stored" => false,
      _ => throw new NotSupportedException($"UHARC: the writer packs with 'lzp' or 'store', not '{options!.MethodName}'."),
    };

    var items = inputs.Select(i => new UharcWriter.Item(
      i.ArchiveName,
      i.IsDirectory ? null : i.ReadContent(),
      i.SourceLastModifiedUtc)).ToList();
    UharcWriter.Write(output, items, compress, DateTime.Now);
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    ArgumentNullException.ThrowIfNull(entryName);
    ArgumentNullException.ThrowIfNull(output);
    if (input.CanSeek)
      input.Position = 0;
    var reader = new UharcReader(input, password);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"UHARC: no entry named '{entryName}'.");
    output.Write(reader.Extract(entry));
  }

  /// <summary>
  /// Opens the archive, or reports why it cannot be read: unsupported versions and damaged
  /// headers fall back to a raw listing instead of throwing. A missing or wrong password still
  /// throws, so the caller can ask for one.
  /// </summary>
  private static string? TryOpen(Stream stream, string? password, out UharcReader? reader, out byte[] image) {
    using (var copy = new MemoryStream()) {
      if (stream.CanSeek)
        stream.Position = 0;
      stream.CopyTo(copy);
      image = copy.ToArray();
    }

    try {
      reader = new UharcReader(new MemoryStream(image, writable: false), password);
      return null;
    } catch (InvalidDataException e) when (!e.Data.Contains(UharcReader.WrongPasswordKey)) {
      reader = null;
      return e.Message;
    }
  }

  private static byte[] Metadata(string reason)
    => Encoding.UTF8.GetBytes($"parse_status=partial\nreason={reason.Replace('\n', ' ')}\n");

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? files) {
    if (files != null && files.Length > 0 && !MatchesFilter(name, files))
      return;
    WriteFile(outputDir, name, data);
  }
}
