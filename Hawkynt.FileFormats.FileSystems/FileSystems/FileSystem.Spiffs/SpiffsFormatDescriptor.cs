#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Spiffs;

/// <summary>
/// SPIFFS, the wear-levelled NOR-flash file system of ESP8266/ESP32 firmware: read, and
/// created fresh (WORM). The geometry is not stored in the image, so reading detects it
/// from the per-block magic and the index tables; writing takes it from a profile that
/// matches the tool the firmware was built with. SPIFFS has no directories: a name is a
/// path of at most 31 bytes.
/// <para>References:</para>
/// <list type="bullet">
///   <item><description><c>https://github.com/pellepl/spiffs</c> — SPIFFS (MIT), <c>spiffs_nucleus.h</c> page and index layout</description></item>
///   <item><description><c>https://github.com/igrr/mkspiffs</c> — mkspiffs (MIT), the image tool; its oracle</description></item>
///   <item><description>ESP-IDF <c>components/spiffs/spiffsgen.py</c> (Apache-2.0) — the second image tool</description></item>
/// </list>
/// </summary>
public sealed class SpiffsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IFormatOptionsSchema {

  /// <summary>Write profiles: the build configurations of the tools that make SPIFFS images.</summary>
  public static readonly IReadOnlyDictionary<string, SpiffsGeometry> Profiles = new Dictionary<string, SpiffsGeometry> {
    ["ESP-IDF / Arduino ESP32"] = new(256, 4096, 4, false, true),
    ["Arduino ESP8266"] = new(256, 8192, 0, true, false),
    ["mkspiffs generic"] = new(256, 4096, 0, false, true),
  };

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("Profile", "Profile", FormatOptionKind.Enum, "ESP-IDF / Arduino ESP32", [.. Profiles.Keys],
      "Build configuration of the firmware that will mount the image: page/block size, metadata length, index alignment and magic."),
    new("ImageSize", "Image size", FormatOptionKind.Integer, "0", null,
      "Partition size in bytes (a multiple of the block size); 0 sizes the image to its contents plus one spare block."),
  ];

  public string Id => "Spiffs";
  public string DisplayName => "SPIFFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".spiffs";
  public IReadOnlyList<string> Extensions => [".spiffs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SPIFFS flash file system image (ESP8266/ESP32)";

  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => null;
  public string AcceptedInputsDescription => "Files whose path, with a leading slash, is at most 31 bytes; directories are implied by the paths.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    reason = input.IsDirectory ? null : SpiffsWriter.Reject(input.ArchiveName);
    return reason == null;
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SpiffsReader(ReadAll(stream));
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SpiffsReader(ReadAll(stream));
    foreach (var e in r.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var profile = options?.GetOption("Profile", "ESP-IDF / Arduino ESP32") ?? "ESP-IDF / Arduino ESP32";
    if (!Profiles.TryGetValue(profile, out var g)) throw new ArgumentException($"SPIFFS: unknown profile '{profile}'.");
    var files = inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, Data: i.ReadContent())).ToList();
    long size = options?.GetOptionInt("ImageSize", 0) ?? 0;
    if (size <= 0) {
      long pages = files.Sum(f => 1 + (f.Data.Length + g.DataPerPage - 1) / g.DataPerPage
        + Math.Max(0, (f.Data.Length + g.DataPerPage - 1) / g.DataPerPage - g.HeaderIndexEntries + g.IndexEntries - 1) / g.IndexEntries);
      var blocks = (pages + g.LookupEntries - 1) / g.LookupEntries + 2;
      size = blocks * g.BlockSize;
    }
    var w = new SpiffsWriter(g, size);
    foreach (var (name, data) in files) w.AddFile(name, data);
    output.Write(w.Build());
  }
}
