#pragma warning disable CS1591
using System.Globalization;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ChromiumPak;

/// <summary>
/// Chromium resource pack (<c>.pak</c>) — the resource bundles of Chrome, Chromium, Electron and
/// CEF applications (<c>resources.pak</c>, <c>chrome_100_percent.pak</c>, <c>locales/*.pak</c>).
/// Each resource is an entry named by its decimal id.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://chromium.googlesource.com/chromium/src/+/main/tools/grit/grit/format/data_pack.py</c> — grit's pack reader/writer (BSD-3-Clause), the format's reference and the oracle</description></item>
///   <item><description><c>https://chromium.googlesource.com/chromium/src/+/main/ui/base/resource/data_pack.cc</c> — Chromium's runtime loader</description></item>
/// </list>
/// </summary>
public sealed class ChromiumPakFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "ChromiumPak";
  public string DisplayName => "Chromium resource pack";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: edits rebuild the pack in its own version and encoding, with grit's ordering and aliasing.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pak";
  public IReadOnlyList<string> Extensions => [".pak"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Version 5 with its zero padding; the encoding byte (0..2) is masked. Version 4 has nothing fixed
  // beyond its version word and is recognised structurally (FormatDetector's .pak disambiguation).
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], Confidence: 0.55, Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF]),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Chromium/Electron resource pack (versions 4 and 5): entries are the decimal resource ids, aliases " +
    "included. Create options: encoding (binary|utf8|utf16, default utf8, as grit writes), version (4|5, default 5).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (_, _, resources) = ChromiumPak.Read(ReadAll(stream));
    return resources.Select((r, i) => new ArchiveEntryInfo(i, r.Id.ToString(CultureInfo.InvariantCulture), r.Data.Length,
      r.AliasOf.HasValue ? 0 : r.Data.Length, r.AliasOf is { } of ? $"alias of {of}" : "store", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (_, _, resources) = ChromiumPak.Read(ReadAll(stream));
    foreach (var r in resources) {
      var name = r.Id.ToString(CultureInfo.InvariantCulture);
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, r.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var encoding = (options.GetString("encoding") ?? "utf8").ToLowerInvariant() switch {
      "binary" or "0" => ChromiumPakEncoding.Binary,
      "utf8" or "utf-8" or "1" => ChromiumPakEncoding.Utf8,
      "utf16" or "utf-16" or "2" => ChromiumPakEncoding.Utf16,
      var e => throw new NotSupportedException($"PAK: unknown encoding '{e}' (binary, utf8, utf16)."),
    };
    var version = options.GetString("version") switch {
      null or "" or "5" => 5u,
      "4" => 4u,
      var v => throw new NotSupportedException($"PAK: unknown version '{v}' (4, 5)."),
    };
    var resources = new Dictionary<ushort, byte[]>();
    foreach (var (name, data) in FilesOnly(inputs))
      if (!resources.TryAdd(ParseId(name), data))
        throw new ArgumentException($"PAK: resource id {ParseId(name)} is given twice.", nameof(inputs));
    ChromiumPak.Write(output, resources, encoding, version);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, resources => {
      foreach (var (name, data) in FilesOnly(inputs)) resources[ParseId(name)] = data;
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    Rewrite(archive, resources => {
      foreach (var name in entryNames)
        if (TryParseId(name, out var id)) resources.Remove(id);
    });
  }

  private static void Rewrite(Stream archive, Action<Dictionary<ushort, byte[]>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    var (version, encoding, list) = ChromiumPak.Read(ReadAll(archive));
    var resources = list.ToDictionary(r => r.Id, r => r.Data);
    edit(resources);
    using var rebuilt = new MemoryStream();
    ChromiumPak.Write(rebuilt, resources, encoding, version);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  /// <summary>An entry name is a decimal resource id, optionally followed by an extension (<c>101.png</c>).</summary>
  private static ushort ParseId(string name)
    => TryParseId(name, out var id) ? id : throw new ArgumentException($"PAK: '{name}' is not a resource id (0..65535).", nameof(name));

  private static bool TryParseId(string name, out ushort id) {
    var leaf = Path.GetFileName(name);
    var dot = leaf.IndexOf('.');
    return ushort.TryParse(dot < 0 ? leaf : leaf[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out id);
  }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
