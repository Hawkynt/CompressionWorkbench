#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WordPerfect;

/// <summary>
/// WordPerfect documents (.wpd and friends). All versions share the 4-byte
/// prefix <c>FF 57 50 43</c> ("\xFFWPC"). The document is retained as an
/// opaque single-file payload; creation accepts exactly one existing WordPerfect
/// document and copies it byte-for-byte, since this is not an archive codec.
/// The descriptor surfaces the
/// header plus the prefix and document areas carved out by the header's
/// document-area pointer. Prefix packet structure and document text are not
/// parsed.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://sourceforge.net/projects/libwpd/</c> — libwpd — open WordPerfect implementation; its documentation is the de-facto format reference</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/WordPerfect</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class WordPerfectFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "WordPerfect";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "WordPerfect";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".wpd";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".wpd", ".wp", ".wp5", ".wp6", ".wp7"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFF, 0x57, 0x50, 0x43], Confidence: 0.95),
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
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Corel/Novell WordPerfect document";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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

  /// <summary>
  /// Writes one existing WordPerfect document unchanged. WordPerfect's native
  /// document format has no generic multi-file archive or compression method.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite)
      throw new ArgumentException("Output stream must be writable.", nameof(output));
    if (options.MethodName is not (null or "stored"))
      throw new NotSupportedException($"WordPerfect creation method '{options.MethodName}' is not supported. The only method is 'stored'.");
    if (options.Password is not null)
      throw new NotSupportedException("WordPerfect encryption is not supported for creation.");
    var files = inputs.Where(static input => !input.IsDirectory).ToArray();
    if (files.Length != 1 || inputs.Any(static input => input.IsDirectory))
      throw new ArgumentException("A WordPerfect document is one file; creation requires exactly one document input.", nameof(inputs));

    var bytes = files[0].ReadContent();
    ValidateDocument(bytes);
    output.Write(bytes);
  }

  /// <summary>Streaming creation preserves the input document without buffering the whole file.</summary>
  public void CreateFromStreams(Stream target, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!target.CanWrite)
      throw new ArgumentException("Output stream must be writable.", nameof(target));
    if (options.MethodName is not (null or "stored"))
      throw new NotSupportedException($"WordPerfect creation method '{options.MethodName}' is not supported. The only method is 'stored'.");
    if (options.Password is not null)
      throw new NotSupportedException("WordPerfect encryption is not supported for creation.");
    using var iterator = inputs.GetEnumerator();
    if (!iterator.MoveNext() || iterator.Current.IsDirectory)
      throw new ArgumentException("A WordPerfect document is one file; creation requires exactly one document input.", nameof(inputs));
    var documentInput = iterator.Current;
    if (iterator.MoveNext())
      throw new ArgumentException("A WordPerfect document is one file; creation requires exactly one document input.", nameof(inputs));
    using var input = documentInput.OpenStream();
    Span<byte> header = stackalloc byte[16];
    input.ReadExactly(header);
    ValidateHeader(header);
    target.Write(header);
    input.CopyTo(target);
  }

  private static void ValidateDocument(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < 16)
      throw new InvalidDataException("WordPerfect input is shorter than its 16-byte header.");
    ValidateHeader(bytes[..16]);
  }

  private static void ValidateHeader(ReadOnlySpan<byte> header) {
    if (header.Length < 16 || !header[..4].SequenceEqual([0xFF, 0x57, 0x50, 0x43]))
      throw new InvalidDataException("Input is not a WordPerfect document (missing \\xFFWPC magic).");
  }

  private static WpView BuildView(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var full = ms.ToArray();

    // Magic + 16-byte header required.
    ValidateDocument(full);

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
