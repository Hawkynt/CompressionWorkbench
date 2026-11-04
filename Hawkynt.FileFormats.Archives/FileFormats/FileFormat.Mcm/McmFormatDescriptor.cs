#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mcm;

/// <summary>
/// MCM 0.84 — Mathieu Chartier's context-mixing archiver (<c>MCMARCHIVE</c>): solid blocks per
/// detected content type (text with a word dictionary, binary with an x86 branch filter, stereo
/// PCM), a compressed file list, and five CM levels.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/mathieuchartier/mcm</c> — the reference program (GPL-3.0; used to derive the specification and as the oracle, no code taken)</description></item>
///   <item><description><c>docs/MCM-ON-DISK.md</c> — the specification this reader and writer were written from</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Creation reproduces <c>mcm -{t|f|m|h|s}N</c> byte for byte, choose the level with the method
/// (<c>turbo</c>, <c>fast</c>, <c>mid</c>, <c>high</c>, <c>simple</c>, <c>store</c>) and the memory
/// level 0–9 with <see cref="FormatCreateOptions.Level"/> (default 6, as mcm). Two departures keep
/// every archive readable by mcm itself: binary blocks that mcm's decoder cannot restore (its
/// fast path) are coded with the text model set or stored, and stereo PCM is not split into a
/// Wav16 block (mcm's Wav16 decoder does not invert its encoder). The reader decodes both of those
/// as mcm wrote them. <c>-x</c> archives cannot exist — mcm 0.84 crashes writing them.
/// </remarks>
public sealed class McmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  private const int DefaultMemoryLevel = 6;

  public string Id => "Mcm";
  public string DisplayName => "MCM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".mcm";
  public IReadOnlyList<string> Extensions => [".mcm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("MCMARCHIVE"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("mid", "MCM mid (-m)"), new("turbo", "MCM turbo (-t)"), new("fast", "MCM fast (-f)"),
    new("high", "MCM high (-h)"), new("simple", "MCM simple (-s)"), new("store", "MCM store (-store)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  public string Description => "MCM 0.84 context-mixing archive (Mathieu Chartier): solid text/binary blocks, word dictionary and x86 filters";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    try {
      var index = McmArchive.ReadIndex(data);
      var sizes = McmArchive.FileSizes(index);
      return index.Files.Select((f, i) => new ArchiveEntryInfo(i, f.Name, sizes[i], 0, MethodOf(index, i), f.IsDirectory, false, null)).ToList();
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException) {
      var meta = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
        $"[mcm]\nsize = {data.Length}\nparse_status = partial\nreason = {e.Message}\n"));
      return [
        new ArchiveEntryInfo(0, "FULL.mcm", data.Length, -1, "Stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
      ];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    var index = McmArchive.ReadIndex(data);
    var contents = McmArchive.Extract(data, index);
    for (var i = 0; i < index.Files.Count; ++i) {
      var file = index.Files[i];
      if (files != null && !MatchesFilter(file.Name, files))
        continue;
      if (file.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, file.Name));
        continue;
      }
      WriteFile(outputDir, file.Name, contents[i]);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var compressor = (options.MethodName ?? "mid").ToLowerInvariant() switch {
      "mid" or "mcm" => McmCompressor.Mid,
      "turbo" => McmCompressor.Turbo,
      "fast" => McmCompressor.Fast,
      "high" => McmCompressor.High,
      "simple" => McmCompressor.Simple,
      "store" => McmCompressor.Store,
      var other => throw new NotSupportedException($"MCM has no method '{other}'."),
    };
    var entries = inputs.Select(i => new McmInput(i.ArchiveName, i.IsDirectory ? [] : i.ReadContent(), i.IsDirectory)).ToList();
    var bytes = McmArchiveWriter.Write(entries, compressor, options.Level ?? DefaultMemoryLevel);
    output.Write(bytes);
  }

  private static string MethodOf(McmArchive.Index index, int file) {
    foreach (var block in index.Blocks)
      foreach (var segment in block.Segments)
        if (segment.FileIndex == file)
          return block.Compressor.ToString().ToLowerInvariant();
    return "stored";
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek)
      stream.Position = 0;
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }
}
