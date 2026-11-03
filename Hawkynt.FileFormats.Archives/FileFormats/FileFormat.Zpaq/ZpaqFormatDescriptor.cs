#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zpaq;

/// <summary>
/// ZPAQ archive (journaling and streaming layouts) with deduplication and ZPAQL-described
/// context-mixing compression.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://mattmahoney.net/dc/zpaq206.pdf</c> — ZPAQ Level 2 specification (Matt Mahoney)</description></item>
///   <item><description><c>https://mattmahoney.net/dc/zpaq.html</c> — zpaq / libzpaq reference implementation (public domain)</description></item>
/// </list>
/// </summary>
public sealed class ZpaqFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable {

  /// <inheritdoc />
  /// <remarks>zpaq truncates an update that adds nothing, so there is no empty archive to purge to.</remarks>
  public bool CanPurgeToEmpty => false;

  /// <summary>Rebuild-based defrag: extracts then re-creates the archive as a single version.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the archive as a single version.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new ZpaqReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractBytes(e))).ToList();
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new ZpaqWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files)
            w.AddFile(n, d);
        return ms.ToArray();
      });
  }

  public string Id => "Zpaq";
  public string DisplayName => "ZPAQ";
  public FormatCategory Category => FormatCategory.Archive;

  // R/W: zpaq 7.15 lists and extracts what ZpaqWriter produces (ZpaqExternalOracleTests), and the
  // reader decodes zpaq's own archives at every method. Modify goes through the default
  // extract -> edit -> re-create rebuild, which writes a fresh single-version archive.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".zpaq";
  public IReadOnlyList<string> Extensions => [".zpaq"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Journaling archives and libzpaq streams start with the 13-byte locator tag "7kSt\xA0\x31\x83\xD3\x8C\xB2\x28\xB0\xD3";
  // tagless streams start directly with the "zPQ" block header.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x37, 0x6B, 0x53, 0x74, 0xA0, 0x31, 0x83, 0xD3, 0x8C, 0xB2, 0x28, 0xB0, 0xD3], Confidence: 0.95),
    new([(byte)'z', (byte)'P', (byte)'Q'], Confidence: 0.80),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("1", "ZPAQ method 1 (LZ77, default)"),
    new("0", "ZPAQ method 0 (dedup only)"),
    new("2", "ZPAQ method 2 (LZ77, slower)"),
    new("3", "ZPAQ method 3 (BWT / LZ77+CM)"),
    new("4", "ZPAQ method 4 (CM)"),
    new("5", "ZPAQ method 5 (CM, slowest)"),
  ];

  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZPAQ journaling archive with deduplication and ZPAQL context-mixing compression";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var seekable = Seekable(stream);
    try {
      var (reader, _) = TryOpen(seekable);
      if (reader == null) {
        var length = seekable.Length;
        return [
          new(0, "FULL.zpaq", length, length, "Stored", false, false, null, Kind: "Track"),
          new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
        ];
      }
      using (reader)
        return reader.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, Math.Max(e.Size, 0), e.CompressedSize,
          "zpaq", e.IsDirectory, false, e.LastModified)).ToList();
    } finally {
      if (!ReferenceEquals(seekable, stream))
        seekable.Dispose();
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var seekable = Seekable(stream);
    try {
      var (reader, problem) = TryOpen(seekable);
      if (reader == null) {
        if (Wants(files, "FULL.zpaq")) {
          seekable.Position = 0;
          using var ms = new MemoryStream();
          seekable.CopyTo(ms);
          WriteFile(outputDir, "FULL.zpaq", ms.ToArray());
        }
        if (Wants(files, "metadata.ini"))
          WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(
            "[Zpaq]\n" + string.Create(CultureInfo.InvariantCulture, $"problem={problem?.Replace('\n', ' ')}\n") +
            "member_enumeration=unavailable\nparse_status=partial\n"));
        return;
      }
      using (reader) {
        // Visit files in fragment order so consecutive files reuse the decoded block.
        var ordered = reader.Entries
          .Where(e => !e.IsDirectory && (files == null || MatchesFilter(e.FileName, files)))
          .OrderBy(e => e.Fragments.Count > 0 ? e.Fragments[0] : 0u);
        foreach (var e in ordered)
          WriteFile(outputDir, e.FileName, reader.ExtractBytes(e));
      }
    } finally {
      if (!ReferenceEquals(seekable, stream))
        seekable.Dispose();
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    using var w = new ZpaqWriter(output, leaveOpen: true, MethodFor(options));
    foreach (var input in inputs) {
      if (input.IsDirectory)
        w.AddDirectory(input.ArchiveName);
      else
        w.AddFile(input.ArchiveName, input.ReadContent());
    }
  }

  /// <summary>Maps the create options to a zpaq method: an explicit method string, else the level 0..5, else 1.</summary>
  internal static string MethodFor(FormatCreateOptions options) {
    var m = options.MethodName;
    if (!string.IsNullOrEmpty(m) && (m[0] is >= '0' and <= '5' || m[0] == 'x'))
      return m;
    if (options.Level is { } level)
      return Math.Clamp(level, 0, 5).ToString(CultureInfo.InvariantCulture);
    return "1";
  }

  private static (ZpaqReader? Reader, string? Problem) TryOpen(Stream stream) {
    try {
      stream.Position = 0;
      return (new ZpaqReader(stream, leaveOpen: true), null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException or IOException
                                  or OverflowException or ArgumentException or IndexOutOfRangeException) {
      return (null, e.Message);
    }
  }

  private static Stream Seekable(Stream stream) {
    if (stream.CanSeek)
      return stream;
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);
}
