using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Tar;

namespace FileFormat.Gem;

/// <summary>Builds canonical modern Ruby Gem packages from arbitrary archive inputs.</summary>
internal static class GemCreator {
  private const string PackageName = "compression_workbench_archive";
  private const string PackageVersion = "0";

  internal static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var rawFiles = inputs
      .Where(input => !input.IsDirectory)
      .Select(input => (Name: NormalizeName(input.ArchiveName), Data: input.ReadContent()))
      .ToList();

    // Re-creating a Gem from this descriptor's own extracted view should feed only
    // the data/ subtree back into data.tar.gz; metadata.ini/yaml and checksums.yaml
    // are derived views that are regenerated below.
    var extractedGemView = rawFiles.Any(file => file.Name.StartsWith("data/", StringComparison.Ordinal))
                           && rawFiles.Any(file => file.Name == "metadata.yaml" || file.Name == "checksums.yaml");

    var payloads = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in rawFiles) {
      if (extractedGemView) {
        if (name is "metadata.ini" or "metadata.yaml" or "checksums.yaml")
          continue;
        if (!name.StartsWith("data/", StringComparison.Ordinal))
          continue;
        payloads.Add((NormalizeName(name["data/".Length..]), data));
      } else {
        payloads.Add((name, data));
      }
    }

    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (name, _) in payloads)
      if (!seen.Add(name))
        throw new InvalidDataException($"Gem input contains duplicate archive path '{name}'.");
    payloads.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

    var metadata = BuildMetadata(payloads);
    var dataTar = BuildDataTar(payloads);
    var metadataGz = Gzip(metadata);
    var dataTarGz = Gzip(dataTar);
    var checksumsGz = Gzip(BuildChecksums(metadataGz, dataTarGz));

    using var outer = new TarWriter(output, leaveOpen: true);
    AddOuterEntry(outer, "metadata.gz", metadataGz);
    AddOuterEntry(outer, "data.tar.gz", dataTarGz);
    AddOuterEntry(outer, "checksums.yaml.gz", checksumsGz);
    outer.Finish();
  }

  private static byte[] BuildDataTar(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var stream = new MemoryStream();
    using var writer = new TarWriter(stream, leaveOpen: true);
    foreach (var (name, data) in files) {
      writer.AddEntry(new TarEntry {
        Name = name,
        Mode = 420, // 0644
        ModifiedTime = DateTimeOffset.UnixEpoch,
      }, data);
    }
    writer.Finish();
    return stream.ToArray();
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, byte[] Data)> files) {
    var yaml = new StringBuilder();
    yaml.AppendLine("--- !ruby/object:Gem::Specification");
    yaml.Append("name: ").AppendLine(PackageName);
    yaml.AppendLine("version: !ruby/object:Gem::Version");
    yaml.Append("  version: '").Append(PackageVersion).AppendLine("'");
    yaml.AppendLine("platform: ruby");
    yaml.AppendLine("authors:");
    yaml.AppendLine("- CompressionWorkbench");
    yaml.AppendLine("bindir: bin");
    yaml.AppendLine("cert_chain: []");
    yaml.AppendLine("date: 1970-01-01 00:00:00.000000000 Z");
    yaml.AppendLine("dependencies: []");
    yaml.AppendLine("description: Archive converted by CompressionWorkbench");
    yaml.AppendLine("email: []");
    yaml.AppendLine("executables: []");
    yaml.AppendLine("extensions: []");
    yaml.AppendLine("extra_rdoc_files: []");
    yaml.AppendLine("files:");
    foreach (var (name, _) in files)
      yaml.Append("- '").Append(EscapeYamlSingleQuoted(name)).AppendLine("'");
    yaml.AppendLine("homepage:");
    yaml.AppendLine("licenses: []");
    yaml.AppendLine("metadata: {}");
    yaml.AppendLine("post_install_message:");
    yaml.AppendLine("rdoc_options: []");
    yaml.AppendLine("require_paths:");
    yaml.AppendLine("- lib");
    yaml.AppendLine("required_ruby_version: !ruby/object:Gem::Requirement");
    yaml.AppendLine("  requirements:");
    yaml.AppendLine("  - - '>='");
    yaml.AppendLine("    - !ruby/object:Gem::Version");
    yaml.AppendLine("      version: '0'");
    yaml.AppendLine("required_rubygems_version: !ruby/object:Gem::Requirement");
    yaml.AppendLine("  requirements:");
    yaml.AppendLine("  - - '>='");
    yaml.AppendLine("    - !ruby/object:Gem::Version");
    yaml.AppendLine("      version: '0'");
    yaml.AppendLine("requirements: []");
    yaml.AppendLine("rubygems_version: 3.0.0");
    yaml.AppendLine("signing_key:");
    yaml.AppendLine("specification_version: 4");
    yaml.AppendLine("summary: Archive converted by CompressionWorkbench");
    yaml.AppendLine("test_files: []");
    return Encoding.UTF8.GetBytes(yaml.ToString());
  }

  private static byte[] BuildChecksums(byte[] metadataGz, byte[] dataTarGz) {
    var yaml = new StringBuilder();
    yaml.AppendLine("---");
    yaml.AppendLine("SHA256:");
    AppendChecksum(yaml, "metadata.gz", SHA256.HashData(metadataGz));
    AppendChecksum(yaml, "data.tar.gz", SHA256.HashData(dataTarGz));
    yaml.AppendLine("SHA512:");
    AppendChecksum(yaml, "metadata.gz", SHA512.HashData(metadataGz));
    AppendChecksum(yaml, "data.tar.gz", SHA512.HashData(dataTarGz));
    return Encoding.UTF8.GetBytes(yaml.ToString());
  }

  private static void AppendChecksum(StringBuilder yaml, string name, byte[] digest)
    => yaml.Append("  ").Append(name).Append(": ").AppendLine(Convert.ToHexStringLower(digest));

  private static byte[] Gzip(byte[] data) {
    using var stream = new MemoryStream();
    using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
      gzip.Write(data);
    return stream.ToArray();
  }

  private static void AddOuterEntry(TarWriter writer, string name, byte[] data)
    => writer.AddEntry(new TarEntry {
      Name = name,
      Mode = 292, // 0444, matching RubyGems package members
      ModifiedTime = DateTimeOffset.UnixEpoch,
    }, data);

  private static string NormalizeName(string name) {
    if (string.IsNullOrWhiteSpace(name))
      throw new InvalidDataException("Gem entries require a non-empty archive path.");
    var normalized = name.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new InvalidDataException($"Gem file path '{name}' is invalid.");
    foreach (var component in normalized.Split('/'))
      if (component is "" or "." or "..")
        throw new InvalidDataException($"Gem file path '{name}' contains an unsafe path component.");
    return normalized;
  }

  private static string EscapeYamlSingleQuoted(string value) => value.Replace("'", "''");
}
