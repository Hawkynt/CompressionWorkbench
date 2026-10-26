#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WebArchive;

/// <summary>
/// Safari / WebKit web archive (<c>.webarchive</c>) — a saved page with its subresources and frames,
/// stored as a binary property list.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.apple.com/documentation/webkit/webarchive</c> — WebKit's WebArchive / WebResource classes (the dictionary keys)</description></item>
///   <item><description><c>https://opensource.apple.com/source/CF/CF-1153.18/CFBinaryPList.c</c> — the bplist00 layout description</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Every resource is an entry named after its URL (the page's origin by path, other origins under their
/// host). Created archives put every file under <see cref="BaseUrl"/> with the first HTML file as the
/// main resource. Add and remove rewrite the property list with every other key — frame names,
/// encodings, archived responses, unknown keys — carried over unchanged.
/// </remarks>
public sealed class WebArchiveFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  /// <summary>The URL created resources are located under (RFC 2606 reserved TLD).</summary>
  public const string BaseUrl = "https://webarchive.invalid/";

  public string Id => "WebArchive";
  public string DisplayName => "Safari web archive";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".webarchive";
  public IReadOnlyList<string> Extensions => [".webarchive"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // "bplist00" opens every binary property list; only the extension says web archive.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Safari/WebKit saved page (binary plist). Resources are named by URL; create puts the first HTML file " +
    "first as the main resource. Create option: root (the page to use as main resource).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var doc = Read(stream);
    return [.. doc.Resources.Select((r, i) => new ArchiveEntryInfo(i, r.Name, r.Data.Length, r.Data.Length, "stored", false, false, null))];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var r in Read(stream).Resources) {
      if (files != null && files.Length > 0 && !MatchesFilter(r.Name, files)) continue;
      WriteFile(outputDir, r.Name, r.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = FilesOnly(inputs).Select(f => (Name: f.Name.Replace('\\', '/'), f.Data)).ToList();
    if (files.Count == 0)
      throw new ArgumentException("A web archive needs at least one file: its main resource.");
    var root = options?.GetString("root");
    var rootIndex = root != null
      ? files.FindIndex(f => string.Equals(f.Name, root, StringComparison.OrdinalIgnoreCase))
      : Math.Max(0, files.FindIndex(f => WebArchiveDocument.MimeTypeFor(f.Name) == "text/html"));
    if (rootIndex < 0)
      throw new ArgumentException($"Web archive: the root page '{root}' is not among the inputs.");
    var plist = new OrderedDictionary<string, object?> {
      [WebArchiveDocument.MainKey] = WebArchiveDocument.NewResource(BaseUrl + Escape(files[rootIndex].Name), files[rootIndex].Name, files[rootIndex].Data, main: true),
    };
    var subs = files.Where((_, i) => i != rootIndex)
      .Select(f => (object?)WebArchiveDocument.NewResource(BaseUrl + Escape(f.Name), f.Name, f.Data, main: false))
      .ToList();
    if (subs.Count > 0) plist[WebArchiveDocument.SubresourcesKey] = subs;
    output.Write(BinaryPlist.Write(plist));
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var doc = Read(archive);
    var origin = doc.RootOrigin;
    foreach (var (rawName, data) in FilesOnly(inputs)) {
      var name = rawName.Replace('\\', '/');
      if (doc.Resources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing) {
        existing.Dictionary[WebArchiveDocument.DataKey] = data;
        continue;
      }
      var url = (origin != null ? origin + "/" : BaseUrl) + Escape(name);
      if (doc.Root.GetValueOrDefault(WebArchiveDocument.SubresourcesKey) is not List<object?> subs)
        doc.Root[WebArchiveDocument.SubresourcesKey] = subs = [];
      subs.Add(WebArchiveDocument.NewResource(url, name, data, main: false));
      doc = new WebArchiveDocument(doc.ToBytes());
      origin = doc.RootOrigin;
    }
    Replace(archive, doc.ToBytes());
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var doc = Read(archive);
    var targets = doc.Resources.Where(r => entryNames.Any(n => string.Equals(r.Name, n.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))).ToList();
    if (targets.Count == 0) return;
    if (targets.FirstOrDefault(t => t.IsMain) is { } main)
      throw new NotSupportedException($"Web archive: '{main.Name}' is a frame's main resource; a web archive cannot exist without it.");
    foreach (var t in targets)
      ((List<object?>)t.Owner[WebArchiveDocument.SubresourcesKey]!).Remove(t.Dictionary);
    Replace(archive, doc.ToBytes());
  }

  private static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

  private static WebArchiveDocument Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new WebArchiveDocument(ms.ToArray());
  }

  private static void Replace(Stream archive, byte[] data) {
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(data);
    archive.Flush();
  }
}
