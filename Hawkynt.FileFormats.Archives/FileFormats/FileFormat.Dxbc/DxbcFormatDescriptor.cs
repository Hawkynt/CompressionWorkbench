#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dxbc;

/// <summary>
/// DirectX shader bytecode container (DXBC): the chunked file <c>fxc</c> writes for shader models
/// 4 and 5 — RDEF, ISGN/OSGN, SHEX/SHDR, STAT and friends. Each chunk is an entry named by its
/// four-character code; creating and editing rebuild the container with a fresh checksum.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/microsoft/DirectXShaderCompiler/blob/main/include/dxc/DxilContainer/DxilContainer.h</c> — container header and part layout</description></item>
///   <item><description><c>https://github.com/microsoft/DirectXShaderCompiler/blob/main/lib/DxilHash/DxilHash.cpp</c> — the checksum (NCSA licence)</description></item>
/// </list>
/// </summary>
public sealed class DxbcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Dxbc";
  public string DisplayName => "DXBC (DirectX shader bytecode)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dxbc";
  public IReadOnlyList<string> Extensions => [".dxbc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("DXBC"u8.ToArray(), Confidence: 0.9)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Compiled D3D shader container; one entry per chunk (RDEF, ISGN, SHEX, STAT, ...), named by its code. " +
    "Create takes entries whose names are four-character codes, in order; edits rebuild the checksum fxc verifies.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => Named(DxbcContainer.Read(ReadAll(stream)))
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Chunk.Data.Length, e.Chunk.Data.Length, "stored", false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, chunk) in Named(DxbcContainer.Read(ReadAll(stream)))) {
      if (files is { Length: > 0 } && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, chunk.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var chunks = FilesOnly(inputs).Select(f => new DxbcChunk(FourCcOf(f.Name), f.Data)).ToList();
    output.Write(DxbcContainer.Write(chunks));
  }

  /// <summary>Appends chunks (or replaces the first chunk of the same code).</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, chunks => {
      foreach (var (name, data) in FilesOnly(inputs)) {
        var code = FourCcOf(name);
        var existing = chunks.FindIndex(c => c.FourCc == code);
        if (existing >= 0) chunks[existing] = new DxbcChunk(code, data);
        else chunks.Add(new DxbcChunk(code, data));
      }
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    Rewrite(archive, chunks => {
      var named = Named(chunks);
      var drop = named.Where(n => entryNames.Contains(n.Name, StringComparer.Ordinal)).Select(n => n.Chunk).ToHashSet();
      chunks.RemoveAll(drop.Contains);
    });
  }

  private static void Rewrite(Stream archive, Action<List<DxbcChunk>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    var chunks = DxbcContainer.Read(ReadAll(archive)).ToList();
    edit(chunks);
    var image = DxbcContainer.Write(chunks);
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(image);
    archive.Flush();
  }

  /// <summary>Entry names: the chunk code, with <c>~N</c> on repeats.</summary>
  public static List<(string Name, DxbcChunk Chunk)> Named(IEnumerable<DxbcChunk> chunks) {
    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
    var result = new List<(string, DxbcChunk)>();
    foreach (var c in chunks) {
      var safe = new string(c.FourCc.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
      if (seen.TryGetValue(safe, out var n)) {
        seen[safe] = n + 1;
        safe = $"{safe}~{n + 1}";
      } else
        seen[safe] = 1;
      result.Add((safe, c));
    }
    return result;
  }

  /// <summary>The chunk code an entry name stands for: its first four characters before any <c>~N</c> or extension.</summary>
  private static string FourCcOf(string name) {
    var file = Path.GetFileName(name.Replace('\\', '/'));
    var tilde = file.IndexOf('~');
    if (tilde >= 0) file = file[..tilde];
    var dot = file.IndexOf('.');
    if (dot >= 0) file = file[..dot];
    if (file.Length != 4 || file.Any(c => c > 0x7E || c < 0x20))
      throw new ArgumentException($"DXBC entry '{name}' must be named by a four-character chunk code (e.g. SHEX, RDEF).");
    return file;
  }

  private static byte[] ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
