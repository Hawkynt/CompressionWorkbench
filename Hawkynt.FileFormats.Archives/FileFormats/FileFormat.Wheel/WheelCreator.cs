using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Zip;

namespace FileFormat.Wheel;

/// <summary>Creates standards-compliant Python wheel ZIP containers.</summary>
internal static class WheelCreator {
  // The directory name carries the escaped form of the distribution name, the
  // metadata the dashed one; PEP 427 asks for exactly that pairing.
  private const string SynthesizedDistribution = "compression_workbench_archive";
  private const string SynthesizedDistributionName = "compression-workbench-archive";
  private const string SynthesizedVersion = "0";

  private const string SynthesizedMetadata =
    "Metadata-Version: 2.1\n"
    + "Name: " + SynthesizedDistributionName + "\n"
    + "Version: " + SynthesizedVersion + "\n";

  private const string SynthesizedWheel =
    "Wheel-Version: 1.0\n"
    + "Generator: CompressionWorkbench\n"
    + "Root-Is-Purelib: true\n"
    + "Tag: py3-none-any\n";

  /// <summary>
  /// Writes a wheel from already-named package files. A caller-supplied root
  /// <c>*.dist-info/METADATA</c> and <c>WHEEL</c> are kept as they are; a tree that
  /// has neither gets a minimal deterministic pair so an ordinary set of files can
  /// still become a wheel a Python tool will accept. RECORD is generated from the
  /// actual bytes written so hashes and sizes cannot drift from the contents.
  /// </summary>
  public static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var files = new List<(string Name, byte[] Data)>();
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var name = NormalizeName(input.ArchiveName);
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase))
        continue; // synthetic reader-only entry
      files.Add((name, input.ReadContent()));
    }

    if (files.Count == 0)
      throw new InvalidDataException("A Python wheel must contain package files and a .dist-info directory.");

    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (name, _) in files)
      if (!names.Add(name))
        throw new InvalidDataException($"Wheel input contains duplicate archive path '{name}'.");

    string? distInfo = null;
    foreach (var (name, _) in files) {
      if (!name.EndsWith("/METADATA", StringComparison.Ordinal))
        continue;
      var candidate = name[..^"/METADATA".Length];
      if (candidate.Contains('/') || !candidate.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase))
        continue;
      if (distInfo != null)
        throw new InvalidDataException("A wheel must contain exactly one root-level *.dist-info/METADATA file.");
      distInfo = candidate;
    }

    // An arbitrary file tree carries no packaging metadata, so a conversion into a
    // wheel has to supply it. The synthesized names and contents are fixed, so the
    // same tree always produces the same wheel.
    if (distInfo == null) {
      distInfo = SynthesizedDistribution + "-" + SynthesizedVersion + ".dist-info";
      files.Add((distInfo + "/METADATA", Encoding.UTF8.GetBytes(SynthesizedMetadata)));
      names.Add(distInfo + "/METADATA");
    }

    if (!names.Contains(distInfo + "/WHEEL"))
      files.Add((distInfo + "/WHEEL", Encoding.UTF8.GetBytes(SynthesizedWheel)));

    var recordName = distInfo + "/RECORD";
    files.RemoveAll(file => string.Equals(file.Name, recordName, StringComparison.Ordinal));

    // PEP 427 recommends placing .dist-info physically at the end of the archive.
    // Stable ordering also makes identical input produce identical wheel bytes.
    files.Sort((a, b) => {
      var aMeta = a.Name.StartsWith(distInfo + "/", StringComparison.Ordinal);
      var bMeta = b.Name.StartsWith(distInfo + "/", StringComparison.Ordinal);
      if (aMeta != bMeta)
        return aMeta ? 1 : -1;
      return StringComparer.Ordinal.Compare(a.Name, b.Name);
    });

    var record = new StringBuilder();
    foreach (var (name, data) in files)
      AppendRecordRow(record, name, data);
    record.Append(EscapeCsv(recordName)).Append(",,\n");
    var recordData = Encoding.UTF8.GetBytes(record.ToString());

    using var zip = new ZipWriter(output, leaveOpen: true);
    foreach (var (name, data) in files)
      zip.AddEntry(name, data, ZipCompressionMethod.Deflate);
    zip.AddEntry(recordName, recordData, ZipCompressionMethod.Deflate);
  }

  private static string NormalizeName(string name) {
    if (string.IsNullOrWhiteSpace(name))
      throw new InvalidDataException("Wheel entries require a non-empty archive path.");
    var normalized = name.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new InvalidDataException($"Wheel file path '{name}' is invalid.");
    foreach (var component in normalized.Split('/'))
      if (component is "" or "." or "..")
        throw new InvalidDataException($"Wheel file path '{name}' contains an unsafe path component.");
    return normalized;
  }

  private static void AppendRecordRow(StringBuilder record, string name, byte[] data) {
    var hash = SHA256.HashData(data);
    var encodedHash = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    record.Append(EscapeCsv(name))
      .Append(",sha256=").Append(encodedHash)
      .Append(',').Append(data.Length)
      .Append('\n');
  }

  private static string EscapeCsv(string value) {
    if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
      return value;
    return "\"" + value.Replace("\"", "\"\"") + "\"";
  }
}
