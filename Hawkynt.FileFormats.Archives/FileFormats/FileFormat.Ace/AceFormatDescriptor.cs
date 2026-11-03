#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;
using CoreAce = Compression.Core.Dictionary.Ace.AceConstants;

namespace FileFormat.Ace;

/// <summary>
/// ACE archive (Marcel Lemke / WinAce): reads ACE 1.0 and 2.0 archives — stored, LZ77 and the 2.0
/// blocked modes (delta, exe, sound, picture), solid and Blowfish-encrypted members, comments, SFX
/// stubs — and writes ACE 1.0 archives (stored or LZ77, optionally solid and encrypted).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/droe/acefile</c> — acefile (BSD-2-Clause), converted for the decoder and header layout, and the oracle for the writer</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ACE_(compressed_file_format)</c> — format overview and history</description></item>
/// </list>
/// </summary>
public sealed class AceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the ACE archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the ACE archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new AceReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e))).ToList();
      },
      buildImage: files => {
        var w = new AceWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new AceReader(archive, leaveOpen: true);
    foreach (var e in r.Entries)
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
  }

  public string Id => "Ace";
  public string DisplayName => "ACE";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: create writes ACE 1.0 archives acefile accepts; edits go through the verified
  // extract -> edit -> re-create rebuild (default IArchiveModifiable).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ace";
  public IReadOnlyList<string> Extensions => [".ace"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("**ACE**"u8.ToArray(), Offset: 7, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ace1", "ACE 1.0 LZ77"), new("store", "Store")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ACE archive. Reads ACE 1.0/2.0 (all compression modes, solid, encrypted, SFX); writes ACE 1.0 " +
    "(stored or LZ77, solid, encrypted). Multi-volume members are listed but not extracted.";

  private static string MethodName(AceEntry e) => e.IsDirectory ? "dir" : e.CompressionType switch {
    CoreAce.CompStore => "store",
    CoreAce.CompAce10 => "ACE 1.0",
    CoreAce.CompAce20 => "ACE 2.0",
    _ => $"ACE type {e.CompressionType}",
  };

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AceReader(stream, leaveOpen: true, password: password);
    return r.Entries.Select(e => new ArchiveEntryInfo(e.Index, e.FileName, e.OriginalSize, e.CompressedSize,
      MethodName(e), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AceReader(stream, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
        continue;
      }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new AceReader(archive, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var dictBits = options.DictSize > 0
      ? Math.Clamp((int)Math.Ceiling(Math.Log2(options.DictSize)), CoreAce.MinDictBits, CoreAce.MaxDictBits)
      : CoreAce.DefaultDictBits;
    var w = new AceWriter(dictionaryBits: dictBits, password: options.Password,
      solid: options.GetOptionBool("solid", false),
      store: options.MethodName == "store");
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      var modified = input.Metadata?.LastWriteTimeUtc?.LocalDateTime
                     ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTime(input.FullPath) : null);
      if (input.IsDirectory) w.AddDirectory(input.ArchiveName, modified);
      else w.AddFile(input.ArchiveName, input.ReadContent(), modified);
    }
    w.WriteTo(output);
  }
}
