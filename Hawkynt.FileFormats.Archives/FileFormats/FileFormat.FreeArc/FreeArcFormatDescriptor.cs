#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FreeArc;

/// <summary>
/// Format descriptor for FreeArc archives (.arc, signature <c>ArC\x01</c>).
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/FREEARC-ON-DISK.md</c> — the layout this implementation follows, derived from FreeArc's sources and checked against FreeArc 0.67's output</description></item>
///   <item><description><c>https://github.com/Bulat-Ziganshin/FA</c> — FreeArc'Next by FreeArc's author, Bulat Ziganshin (the original freearc.org site is defunct)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/FreeArc</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Reads real FreeArc archives; extracts solid blocks compressed with <c>storing</c> or <c>lzma</c>
/// and refuses every other method (tornado, ppmd, lzp, grzip, rep, delta, exe, ...) and encryption.
/// Writes <c>storing</c>/<c>lzma</c> archives that FreeArc 0.67's <c>unarc</c> and <c>arc</c>
/// test and extract byte-identically.
/// </remarks>
public sealed class FreeArcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the FreeArc archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the FreeArc archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new FreeArcReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new FreeArcWriter("lzma");
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new FreeArcReader(archive, leaveOpen: true);
    foreach (var e in r.Entries)
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.Block.Position, e.CompressedSize, DefragBlockKind.Used, FileName: e.Name);
  }

  /// <inheritdoc/>
  public string Id => "FreeArc";

  /// <inheritdoc/>
  public string DisplayName => "FreeArc";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  // R/W: Add/Replace/Remove go through the verified extract -> edit -> re-create rebuild
  // (default IArchiveModifiable). The writer's output is accepted by FreeArc 0.67 itself.
  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <inheritdoc/>
  public string DefaultExtension => ".arc";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".arc"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  // "ArC\x01" opens every FreeArc archive; the legacy SEA ARC format starts with 0x1A instead.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([(byte)'A', (byte)'r', (byte)'C', 0x01], Confidence: 0.95)];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA"), new(FreeArcMethods.Storing, "Storing")];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description => "FreeArc archive";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      using var r = new FreeArcReader(stream, leaveOpen: true);
      return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize, e.Method,
        e.IsDirectory, FreeArcMethods.IsEncrypted(e.Method), e.LastModified)).ToList();
    } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) {
      // Encrypted headers or a directory compressed with a method we lack: expose the raw archive.
      return [
        new(0, "FULL.arc", stream.Length, stream.Length, FreeArcMethods.Storing, false, false, null),
        new(1, "metadata.ini", Metadata(ex).Length, Metadata(ex).Length, FreeArcMethods.Storing, false, false, null),
      ];
    }
  }

  /// <inheritdoc/>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    FreeArcReader r;
    try {
      r = new FreeArcReader(stream, leaveOpen: true);
    } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) {
      stream.Position = 0;
      using var raw = new MemoryStream();
      stream.CopyTo(raw);
      if (files == null || MatchesFilter("FULL.arc", files)) WriteFile(outputDir, "FULL.arc", raw.ToArray());
      if (files == null || MatchesFilter("metadata.ini", files)) WriteFile(outputDir, "metadata.ini", Metadata(ex));
      return;
    }

    using (r) {
      foreach (var e in r.Entries) {
        if (files != null && !MatchesFilter(e.Name, files)) continue;
        if (e.IsDirectory)
          Directory.CreateDirectory(Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar)));
        else
          WriteFile(outputDir, e.Name, r.Extract(e));
      }
    }
  }

  /// <inheritdoc/>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var method = options.MethodName?.Trim().ToLowerInvariant() switch {
      null or "" or "lzma" => options.DictSize > 0 ? $"lzma:{Math.Max(4096, options.DictSize)}b" : "lzma",
      var m => m,
    };
    var w = new FreeArcWriter(method);
    foreach (var input in inputs) {
      var modified = input.Metadata?.LastWriteTimeUtc?.UtcDateTime
        ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : (DateTime?)null);
      if (input.IsDirectory)
        w.AddDirectory(input.ArchiveName, modified);
      else
        w.AddFile(input.ArchiveName, input.ReadContent(), modified);
    }
    w.WriteTo(output);
  }

  private static byte[] Metadata(Exception ex) =>
    Encoding.UTF8.GetBytes($"format=FreeArc\nparse_status=partial\nreason={ex.Message.ReplaceLineEndings(" ")}\n");
}
