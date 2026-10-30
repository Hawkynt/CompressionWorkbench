#pragma warning disable CS1591
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Zip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BagIt;

/// <summary>A payload file of a bag.</summary>
/// <param name="Path">Path below <c>data/</c>, '/'-separated.</param>
/// <param name="Size">Length in bytes.</param>
public sealed record BagItPayload(string Path, long Size);

/// <summary>
/// A BagIt bag (RFC 8493) serialized as a ZIP archive with one top-level directory: <c>bagit.txt</c>,
/// payload under <c>data/</c>, <c>manifest-&lt;algorithm&gt;.txt</c> files and optional tag files.
/// Opening validates the bag: every payload file must be in each payload manifest with a matching
/// checksum, every manifest entry must exist, and tag manifests must match.
/// </summary>
public sealed class BagItBag {
  private static readonly string[] Algorithms = ["sha512", "sha256", "sha1", "md5"];

  private readonly Dictionary<string, byte[]> _files;
  private readonly string _root;
  private readonly bool _percentEncoded;

  /// <summary>Gets the payload files, in manifest order.</summary>
  public IReadOnlyList<BagItPayload> Payload { get; }

  /// <summary>Gets the BagIt version from bagit.txt.</summary>
  public string Version { get; }

  /// <summary>Gets the payload manifest algorithms present.</summary>
  public IReadOnlyList<string> ManifestAlgorithms { get; }

  /// <summary>Gets the name of the bag's top-level directory.</summary>
  public string RootName => this._root.TrimEnd('/');

  /// <summary>Gets bag-info.txt (null when the bag has none).</summary>
  public string? BagInfo => this._files.TryGetValue(this._root + "bag-info.txt", out var info) ? Encoding.UTF8.GetString(info) : null;

  /// <summary>Gets every tag file other than bagit.txt, bag-info.txt and the (tag) manifests, named below the bag root.</summary>
  public IReadOnlyList<(string Name, byte[] Data)> OtherTagFiles => [.. this._files
    .Where(kv => kv.Key.StartsWith(this._root, StringComparison.Ordinal))
    .Select(kv => (Name: kv.Key[this._root.Length..], Data: kv.Value))
    .Where(f => !f.Name.StartsWith("data/", StringComparison.Ordinal) && f.Name is not ("bagit.txt" or "bag-info.txt")
      && !f.Name.StartsWith("manifest-", StringComparison.Ordinal) && !f.Name.StartsWith("tagmanifest-", StringComparison.Ordinal))
    .OrderBy(f => f.Name, StringComparer.Ordinal)];

  /// <summary>Opens and validates a ZIP-serialized bag.</summary>
  /// <exception cref="InvalidDataException">The archive holds no bag, or the bag is not valid.</exception>
  /// <exception cref="NotSupportedException">The bag uses only manifest algorithms or tag encodings this reader cannot check.</exception>
  public BagItBag(Stream zip) {
    zip.Position = 0;
    using var reader = new ZipReader(zip, leaveOpen: true);
    this._files = new(StringComparer.Ordinal);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      this._files[entry.FileName.Replace('\\', '/')] = reader.ExtractEntry(entry);

    var declarations = this._files.Keys.Where(k => k.EndsWith("bagit.txt", StringComparison.Ordinal)
      && (k == "bagit.txt" || (k.Count(c => c == '/') == 1 && k.EndsWith("/bagit.txt", StringComparison.Ordinal)))).ToList();
    if (declarations.Count != 1)
      throw new InvalidDataException("The archive does not hold exactly one bag (bagit.txt at its top level).");
    this._root = declarations[0][..^"bagit.txt".Length];

    var declaration = Encoding.UTF8.GetString(this._files[declarations[0]]);
    this.Version = Field(declaration, "BagIt-Version") ?? throw new InvalidDataException("bagit.txt has no BagIt-Version.");
    this._percentEncoded = !this.Version.StartsWith("0.", StringComparison.Ordinal);
    var encoding = Field(declaration, "Tag-File-Character-Encoding");
    if (encoding != null && !encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"BagIt tag files in {encoding} are not supported.");

    var algorithms = Algorithms.Where(a => this._files.ContainsKey($"{this._root}manifest-{a}.txt")).ToList();
    if (algorithms.Count == 0)
      throw this._files.Keys.Any(k => k.StartsWith(this._root + "manifest-", StringComparison.Ordinal))
        ? new NotSupportedException("The bag only has payload manifests this reader cannot verify.")
        : new InvalidDataException("The bag has no payload manifest.");
    this.ManifestAlgorithms = algorithms;

    var payloadFiles = this._files.Keys.Where(k => k.StartsWith(this._root + "data/", StringComparison.Ordinal))
      .Select(k => k[this._root.Length..]).ToHashSet(StringComparer.Ordinal);
    List<string>? order = null;
    foreach (var algorithm in algorithms) {
      var listed = ReadManifest(this._files[$"{this._root}manifest-{algorithm}.txt"], this._percentEncoded);
      foreach (var (path, digest) in listed) {
        if (!this._files.TryGetValue(this._root + path, out var data))
          throw new InvalidDataException($"manifest-{algorithm}.txt lists '{path}', which the bag does not contain.");
        if (!digest.Equals(Hash(algorithm, data), StringComparison.OrdinalIgnoreCase))
          throw new InvalidDataException($"'{path}' does not match its {algorithm} checksum.");
      }

      var missing = payloadFiles.Except(listed.Select(l => l.Path)).ToList();
      if (missing.Count > 0)
        throw new InvalidDataException($"Payload file '{missing[0]}' is not in manifest-{algorithm}.txt.");
      order ??= [.. listed.Select(l => l.Path)];
    }

    foreach (var algorithm in Algorithms) {
      if (!this._files.TryGetValue($"{this._root}tagmanifest-{algorithm}.txt", out var tags))
        continue;
      foreach (var (path, digest) in ReadManifest(tags, this._percentEncoded))
        if (!this._files.TryGetValue(this._root + path, out var data) || !digest.Equals(Hash(algorithm, data), StringComparison.OrdinalIgnoreCase))
          throw new InvalidDataException($"Tag file '{path}' does not match tagmanifest-{algorithm}.txt.");
    }

    this.Payload = [.. order!.Select(p => new BagItPayload(p["data/".Length..], this._files[this._root + p].LongLength))];
  }

  /// <summary>Returns a payload file's bytes (already verified).</summary>
  public byte[] Read(BagItPayload payload) => this._files[this._root + "data/" + payload.Path];

  private static string? Field(string tagFile, string name) {
    foreach (var line in tagFile.Split('\n')) {
      var colon = line.IndexOf(':');
      if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
        return line[(colon + 1)..].Trim();
    }

    return null;
  }

  // "checksum<whitespace>path" per line. %0D and %0A in paths are decoded; %25 too in a 1.0 bag
  // (RFC 8493 §2.1.3 added it), but not in a 0.97 bag. bagit-python 1.8.1 still applies the 0.97
  // rule to every bag; where the two disagree, the RFC text wins.
  private static List<(string Path, string Digest)> ReadManifest(byte[] manifest, bool percent) {
    var result = new List<(string, string)>();
    foreach (var raw in Encoding.UTF8.GetString(manifest).Split('\n')) {
      var line = raw.TrimEnd('\r');
      if (line.Length == 0)
        continue;
      var space = line.IndexOfAny([' ', '\t']);
      if (space <= 0)
        throw new InvalidDataException($"Malformed manifest line '{line}'.");
      var path = line[space..].TrimStart(' ', '\t').Replace("%0D", "\r").Replace("%0A", "\n");
      if (percent)
        path = path.Replace("%25", "%");
      if (path.StartsWith("./", StringComparison.Ordinal))
        path = path[2..];
      result.Add((path, line[..space]));
    }

    return result;
  }

  internal static string Hash(string algorithm, byte[] data) => Convert.ToHexStringLower(algorithm switch {
    "sha512" => SHA512.HashData(data),
    "sha256" => SHA256.HashData(data),
    "sha1" => SHA1.HashData(data),
    _ => MD5.HashData(data),
  });

  // The writer always writes BagIt 1.0, whose rule encodes '%' as well as CR and LF.
  private static string EncodePath(string path) => path.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

  /// <summary>
  /// Serializes a bag as ZIP: root/bagit.txt, bag-info.txt, any further tag files, a payload manifest
  /// and a tag manifest per algorithm, and data/. <paramref name="bagInfo"/> replaces the default
  /// bag-info.txt, with its Payload-Oxum rewritten to match the payload.
  /// </summary>
  public static void Write(Stream output, IReadOnlyList<(string Path, byte[] Data)> payload, DateTime baggingDate,
    string root = "bag", string? bagInfo = null, IReadOnlyList<(string Name, byte[] Data)>? otherTagFiles = null,
    IReadOnlyList<string>? algorithms = null) {
    algorithms ??= ["sha512"];
    otherTagFiles ??= [];
    var oxum = $"Payload-Oxum: {payload.Sum(p => (long)p.Data.Length)}.{payload.Count}";
    string infoText;
    if (bagInfo == null)
      infoText = $"Bagging-Date: {baggingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n{oxum}\n";
    else {
      var lines = bagInfo.Split('\n');
      for (var i = 0; i < lines.Length; ++i)
        if (lines[i].StartsWith("Payload-Oxum:", StringComparison.OrdinalIgnoreCase))
          lines[i] = oxum + (lines[i].EndsWith('\r') ? "\r" : "");
      infoText = string.Join('\n', lines);
    }

    var files = new List<(string Name, byte[] Data)> {
      ("bagit.txt", "BagIt-Version: 1.0\nTag-File-Character-Encoding: UTF-8\n"u8.ToArray()),
      ("bag-info.txt", Encoding.UTF8.GetBytes(infoText)),
    };
    files.AddRange(otherTagFiles);
    foreach (var algorithm in algorithms) {
      var manifest = new StringBuilder();
      foreach (var (path, data) in payload)
        manifest.Append(Hash(algorithm, data)).Append("  data/").Append(EncodePath(path)).Append('\n');
      files.Add(($"manifest-{algorithm}.txt", Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    var tagged = files.ToList();
    foreach (var algorithm in algorithms) {
      var tags = new StringBuilder();
      foreach (var (name, data) in tagged)
        tags.Append(Hash(algorithm, data)).Append("  ").Append(EncodePath(name)).Append('\n');
      files.Add(($"tagmanifest-{algorithm}.txt", Encoding.UTF8.GetBytes(tags.ToString())));
    }

    foreach (var (path, data) in payload)
      files.Add(("data/" + path, data));

    using var writer = new ZipWriter(output, leaveOpen: true);
    foreach (var (name, data) in files)
      writer.AddEntry($"{root}/{name}", data, ZipCompressionMethod.Deflate, baggingDate);
    writer.Finish();
  }
}

/// <summary>
/// BagIt (RFC 8493), the Library of Congress packaging convention, serialized as a ZIP archive with
/// one top-level bag directory. Listing and extraction validate the bag's manifests. A serialized
/// bag is an ordinary ZIP, so this format is chosen explicitly rather than detected.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc8493</c> — The BagIt File Packaging Format (V1.0)</description></item>
///   <item><description><c>https://github.com/LibraryOfCongress/bagit-python</c> — the Library of Congress reference implementation, used as the oracle</description></item>
/// </list>
/// </summary>
public sealed class BagItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "BagIt";
  public string DisplayName => "BagIt";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: bags we write validate with bagit-python; edits keep the bag's root name, bag-info.txt
  // (Payload-Oxum updated), other tag files and manifest algorithms, and recompute the manifests.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".zip";
  // A serialized bag is an ordinary ZIP; it owns no extension or signature of its own.
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sha512", "SHA-512 manifests")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BagIt (RFC 8493) bag serialized as ZIP; payload validated against its manifests";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var bag = new BagItBag(stream);
    return bag.Payload.Select((p, i) => new ArchiveEntryInfo(i, p.Path, p.Size, p.Size,
      string.Join("+", bag.ManifestAlgorithms), false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var bag = new BagItBag(stream);
    foreach (var p in bag.Payload) {
      if (files != null && !MatchesFilter(p.Path, files)) continue;
      WriteFile(outputDir, p.Path, bag.Read(p));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => BagItBag.Write(output, [.. FilesOnly(inputs).Select(f => (f.Name.Replace('\\', '/'), f.Data))], DateTime.UtcNow);

  /// <summary>Adds (or replaces by path) payload files; tag files are kept and manifests recomputed.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var added = FilesOnly(inputs).ToList();
    var names = added.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, p => !names.Contains(p.Path), added.Select(f => (f.Name, f.Data)));
  }

  /// <summary>Removes payload files; tag files are kept and manifests recomputed.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var names = entryNames.ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, p => !names.Contains(p.Path), []);
  }

  private static void Rewrite(Stream archive, Func<BagItPayload, bool> keep, IEnumerable<(string, byte[])> appended) {
    var bag = new BagItBag(archive);
    var payload = bag.Payload.Where(keep).Select(p => (p.Path, bag.Read(p))).Concat(appended).ToList();
    using var rebuilt = new MemoryStream();
    BagItBag.Write(rebuilt, payload, DateTime.UtcNow, bag.RootName.Length > 0 ? bag.RootName : "bag", bag.BagInfo,
      bag.OtherTagFiles, bag.ManifestAlgorithms);
    archive.Position = 0;
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.SetLength(rebuilt.Length);
    archive.Flush();
  }
}
