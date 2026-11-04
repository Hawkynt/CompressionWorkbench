#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UnityPackage;

/// <summary>
/// Unity asset package (<c>.unitypackage</c>), shown the way Unity imports it: every asset at its project
/// path (<c>Assets/…</c>), its import settings beside it as <c>&lt;path&gt;.meta</c>, folders as directories.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.unity3d.com/Manual/AssetPackages.html</c> — Unity Manual, asset packages</description></item>
///   <item><description><c>https://github.com/Cobertos/unitypackage_extractor</c> — an independent extractor (MIT), the oracle</description></item>
/// </list>
/// </summary>
public sealed class UnityPackageFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  private const string MetaSuffix = ".meta";

  public string Id => "UnityPackage";
  public string DisplayName => "Unity package";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: an edit keeps every untouched asset's GUID, .meta and preview; tar timestamps are rewritten.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".unitypackage";
  public IReadOnlyList<string> Extensions => [".unitypackage"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // A gzip stream; the gzip descriptor owns 1F 8B, so this one is chosen by extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gzip", "tar.gz")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Unity asset package (tar.gz of GUID directories): assets at their project paths with their .meta beside " +
    "them. Create gives inputs outside Assets/ or Packages/ an Assets/ prefix and a fresh GUID unless a " +
    "matching <path>.meta input supplies one.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var list = new List<ArchiveEntryInfo>();
    foreach (var a in UnityPackage.Read(stream)) {
      list.Add(new ArchiveEntryInfo(list.Count, a.Path, a.Asset?.Length ?? 0, -1, "gzip", a.Asset == null, false, null));
      if (a.Meta != null)
        list.Add(new ArchiveEntryInfo(list.Count, a.Path + MetaSuffix, a.Meta.Length, -1, "gzip", false, false, null));
    }
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var a in UnityPackage.Read(stream)) {
      if (files == null || MatchesFilter(a.Path, files)) {
        if (a.Asset == null) Directory.CreateDirectory(Path.Combine(outputDir, a.Path));
        else WriteFile(outputDir, a.Path, a.Asset);
      }
      if (a.Meta != null && (files == null || MatchesFilter(a.Path + MetaSuffix, files)))
        WriteFile(outputDir, a.Path + MetaSuffix, a.Meta);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    var assets = new List<UnityPackageAsset>();
    Apply(assets, inputs);
    UnityPackage.Write(output, assets);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, assets => Apply(assets, inputs));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = entryNames.Select(n => ProjectPath(n.TrimEnd('/'))).ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, assets => {
      for (var i = assets.Count - 1; i >= 0; --i) {
        var a = assets[i];
        if (drop.Contains(a.Path)) assets.RemoveAt(i);                                   // the asset and its .meta
        else if (drop.Contains(a.Path + MetaSuffix)) assets[i] = a with { Meta = null }; // only the .meta
      }
    });
  }

  /// <summary>
  /// Adds or replaces assets: a file or directory input sets an asset's content (new assets get a fresh
  /// GUID and a minimal .meta), then a <c>&lt;path&gt;.meta</c> input sets that asset's settings and GUID;
  /// a .meta whose asset is neither given nor packed describes a folder.
  /// </summary>
  private static void Apply(List<UnityPackageAsset> assets, IReadOnlyList<ArchiveInputInfo> inputs) {
    var given = inputs.Select(i => (Path: ProjectPath(i.ArchiveName.TrimEnd('/')), Input: i)).ToList();
    bool IsMeta((string Path, ArchiveInputInfo Input) g) => !g.Input.IsDirectory && g.Path.EndsWith(MetaSuffix, StringComparison.Ordinal);
    foreach (var (path, input) in given.Where(g => !IsMeta(g))) {
      var content = input.IsDirectory ? null : input.ReadContent();
      var index = assets.FindIndex(a => a.Path == path);
      if (index >= 0) {
        assets[index] = assets[index] with { Asset = content };
        continue;
      }
      var guid = NewGuid();
      assets.Add(new UnityPackageAsset(guid, path, content, UnityPackage.MinimalMeta(guid, input.IsDirectory), null));
    }
    foreach (var (path, input) in given.Where(IsMeta)) {
      var meta = input.ReadContent();
      var target = path[..^MetaSuffix.Length];
      var guid = UnityPackage.GuidOf(meta);
      var index = assets.FindIndex(a => a.Path == target);
      if (index >= 0) assets[index] = assets[index] with { Meta = meta, Guid = guid ?? assets[index].Guid };
      else assets.Add(new UnityPackageAsset(guid ?? NewGuid(), target, null, meta, null));
    }
  }

  private static void Rewrite(Stream archive, Action<List<UnityPackageAsset>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    var assets = UnityPackage.Read(archive).ToList();
    edit(assets);
    using var rebuilt = new MemoryStream();
    UnityPackage.Write(rebuilt, assets);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  private static string ProjectPath(string name) {
    name = name.Replace('\\', '/');
    return name.StartsWith("Assets/", StringComparison.Ordinal) || name == "Assets"
      || name.StartsWith("Packages/", StringComparison.Ordinal) ? name : "Assets/" + name;
  }

  private static string NewGuid() => Guid.NewGuid().ToString("N");
}
