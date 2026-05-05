#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WordPerfect;

/// <summary>
/// WordPerfect documents (.wpd and friends). All versions share the 4-byte
/// prefix <c>FF 57 50 43</c> ("\xFFWPC"). Read-only descriptor surfacing the
/// header plus the prefix and document areas carved out by the header's
/// document-area pointer. Prefix packet structure and document text are not
/// parsed.
/// </summary>
public sealed class WordPerfectFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "WordPerfect";
  public string DisplayName => "WordPerfect";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".wpd";
  public IReadOnlyList<string> Extensions => [".wpd", ".wp", ".wp5", ".wp6", ".wp7"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFF, 0x57, 0x50, 0x43], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Corel/Novell WordPerfect document";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var result = new List<ArchiveEntryInfo>();
    try {
      var view = BuildView(stream);
      var idx = 0;
      result.Add(new ArchiveEntryInfo(idx++, "FULL.wpd", view.FullBytes.LongLength, view.FullBytes.LongLength, "Stored", false, view.Encrypted, null, Kind: "Passthrough"));
      result.Add(new ArchiveEntryInfo(idx++, "metadata.ini", view.MetadataIni.LongLength, view.MetadataIni.LongLength, "Stored", false, false, null, Kind: "Metadata"));
      result.Add(new ArchiveEntryInfo(idx++, "header.bin", view.Header.LongLength, view.Header.LongLength, "Stored", false, false, null, Kind: "Header"));
      if (view.PrefixArea.Length > 0)
        result.Add(new ArchiveEntryInfo(idx++, "prefix_area.bin", view.PrefixArea.LongLength, view.PrefixArea.LongLength, "Stored", false, false, null, Kind: "PrefixArea"));
      if (view.DocumentArea.Length > 0)
        result.Add(new ArchiveEntryInfo(idx++, "document_area.bin", view.DocumentArea.LongLength, view.DocumentArea.LongLength, "Stored", false, view.Encrypted, null, Kind: "DocumentArea"));
    } catch {
      // Robust: never throw.
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var view = BuildView(stream);
    if (files == null || MatchesFilter("FULL.wpd", files))
      WriteFile(outputDir, "FULL.wpd", view.FullBytes);
    if (files == null || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", view.MetadataIni);
    if (files == null || MatchesFilter("header.bin", files))
      WriteFile(outputDir, "header.bin", view.Header);
    if (view.PrefixArea.Length > 0 && (files == null || MatchesFilter("prefix_area.bin", files)))
      WriteFile(outputDir, "prefix_area.bin", view.PrefixArea);
    if (view.DocumentArea.Length > 0 && (files == null || MatchesFilter("document_area.bin", files)))
      WriteFile(outputDir, "document_area.bin", view.DocumentArea);
  }

  private static WpView BuildView(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var full = ms.ToArray();

    // Magic + 16-byte header required.
    if (full.Length < 16
        || full[0] != 0xFF || full[1] != 0x57 || full[2] != 0x50 || full[3] != 0x43)
      throw new InvalidDataException("Not a WordPerfect file (missing \\xFFWPC magic).");

    var header = full.AsSpan(0, 16).ToArray();
    var documentAreaOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
    var productType = header[8];
    var fileType = header[9];
    var majorVersion = header[10];
    var minorVersion = header[11];
    // Encryption flag historically lives in one of the two bytes after version.
    // Treat non-zero as encrypted — good enough for surface metadata.
    var encryptionByte = header[12];
    var encrypted = encryptionByte != 0;

    byte[] prefix;
    byte[] document;
    if (documentAreaOffset >= 16 && documentAreaOffset <= (uint)full.Length) {
      prefix = full.AsSpan(16, (int)(documentAreaOffset - 16)).ToArray();
      document = full.AsSpan((int)documentAreaOffset).ToArray();
    } else {
      // Document pointer is bogus (truncated / non-WP file that happens to
      // match magic). Surface what we can without throwing.
      prefix = full.AsSpan(16).ToArray();
      document = [];
    }

    var sb = new StringBuilder();
    sb.AppendLine("[wordperfect]");
    sb.AppendLine($"product_type=0x{productType:X2}");
    sb.AppendLine($"product_type_name={ProductTypeName(productType)}");
    sb.AppendLine($"file_type=0x{fileType:X2}");
    sb.AppendLine($"file_type_name={FileTypeName(fileType)}");
    sb.AppendLine($"major_version={majorVersion}");
    sb.AppendLine($"minor_version={minorVersion}");
    sb.AppendLine($"encrypted={(encrypted ? "true" : "false")}");
    sb.AppendLine($"document_area_offset={documentAreaOffset}");
    var metadataIni = Encoding.UTF8.GetBytes(sb.ToString());

    return new WpView(full, metadataIni, header, prefix, document, encrypted);
  }

  private static string ProductTypeName(byte v) => v switch {
    0x01 => "WordPerfect",
    0x02 => "WordPerfect Macro",
    0x03 => "Printer",
    _ => "unknown",
  };

  private static string FileTypeName(byte v) => v switch {
    0x0A => "Macro",
    0x0B => "Shell Macro",
    0x10 => "Document",
    _ => "unknown",
  };

  private sealed record WpView(byte[] FullBytes, byte[] MetadataIni, byte[] Header, byte[] PrefixArea, byte[] DocumentArea, bool Encrypted);
}
