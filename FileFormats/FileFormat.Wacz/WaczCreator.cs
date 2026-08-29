using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Compression.Registry;
using FileFormat.Warc;
using FileFormat.Zip;

namespace FileFormat.Wacz;

/// <summary>Creates WACZ 1.x ZIP containers from caller-supplied web-archive resources.</summary>
internal static class WaczCreator {
  private const string SyntheticDate = "1970-01-01T00:00:00Z";
  private const string SyntheticTimestamp = "19700101000000";
  private const string SyntheticWarcName = "archive/data.warc";
  private const string SyntheticIndexName = "indexes/index.cdxj";
  private const string SyntheticPagesName = "pages/pages.jsonl";

  public static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var files = new List<(string Name, byte[] Data)>();
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var name = NormalizeName(input.ArchiveName);
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase))
        continue;
      if (!names.Add(name))
        throw new InvalidDataException($"WACZ input contains duplicate archive path '{name}'.");
      files.Add((name, input.ReadContent()));
    }

    var hasWarc = files.Any(file => IsWarcPath(file.Name));
    if (!hasWarc) {
      // Archive conversion supplies an arbitrary file tree, not WARC-specific inputs.
      // Preserve those files verbatim as legal root/custom WACZ resources and also
      // wrap them into a deterministic WARC so the resulting package remains a web
      // archive rather than merely a ZIP with a .wacz extension.
      var payloads = files
        .Where(file => !string.Equals(file.Name, "datapackage.json", StringComparison.Ordinal))
        .ToArray();

      RemoveFile(files, names, "datapackage.json");
      AddGeneratedFile(files, names, SyntheticWarcName, BuildSyntheticWarc(payloads, out var captures));
      AddGeneratedFile(files, names, SyntheticIndexName, BuildSyntheticIndex(captures));
      AddGeneratedFile(files, names, SyntheticPagesName, BuildSyntheticPages(captures));
      AddGeneratedFile(files, names, "datapackage.json", BuildManifest(files));
    } else if (!names.Contains("datapackage.json")) {
      // A caller that already supplied WARC/index/page resources can omit the manifest;
      // generate its mandatory fixity inventory from the exact bytes we will store.
      AddGeneratedFile(files, names, "datapackage.json", BuildManifest(files));
    }

    files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

    using var zip = new ZipWriter(output, leaveOpen: true);
    foreach (var (name, data) in files) {
      // WACZ 1.1.1 says WARC files SHOULD be ZIP-stored for range access and
      // already-compressed files MUST NOT be compressed a second time.
      var method = name.StartsWith("archive/", StringComparison.Ordinal)
                   || name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
        ? ZipCompressionMethod.Store
        : ZipCompressionMethod.Deflate;
      zip.AddEntry(name, data, method);
    }
  }

  private static byte[] BuildSyntheticWarc(
      IReadOnlyList<(string Name, byte[] Data)> payloads,
      out List<SyntheticCapture> captures) {
    captures = [];
    using var combined = new MemoryStream();

    if (payloads.Count == 0) {
      payloads = [("empty", Array.Empty<byte>())];
    }

    foreach (var (name, data) in payloads) {
      var targetUrl = ToSyntheticUrl(name);
      var digest = Convert.ToHexStringLower(SHA256.HashData(data));
      var entry = new WarcEntry {
        Type = "resource",
        TargetUri = targetUrl,
        RecordId = $"<urn:sha256:{digest}>",
        Date = SyntheticDate,
        ContentType = "application/octet-stream",
        ContentLength = data.Length,
      };

      using var recordStream = new MemoryStream();
      var writer = new WarcWriter();
      writer.AddRecord(entry, data);
      writer.WriteTo(recordStream);
      var record = recordStream.ToArray();
      var offset = combined.Position;
      combined.Write(record);
      captures.Add(new SyntheticCapture(name, targetUrl, digest, offset, record.Length, data.Length));
    }

    return combined.ToArray();
  }

  private static byte[] BuildSyntheticIndex(IEnumerable<SyntheticCapture> captures) {
    var lines = new StringBuilder();
    foreach (var capture in captures.OrderBy(capture => capture.Url, StringComparer.Ordinal)) {
      var fields = JsonSerializer.Serialize(new {
        offset = capture.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
        length = capture.RecordLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        mime = "application/octet-stream",
        status = "-",
        filename = "data.warc",
        url = capture.Url,
        digest = "sha256:" + capture.Digest,
      });
      lines.Append(ToSurtKey(capture.Url)).Append(' ')
        .Append(SyntheticTimestamp).Append(' ')
        .Append(fields).Append('\n');
    }
    return Encoding.UTF8.GetBytes(lines.ToString());
  }

  private static byte[] BuildSyntheticPages(IEnumerable<SyntheticCapture> captures) {
    var lines = new StringBuilder();
    lines.AppendLine(JsonSerializer.Serialize(new { format = "json-pages-1.0", id = "pages", title = "Converted files" }));
    foreach (var capture in captures.OrderBy(capture => capture.Url, StringComparer.Ordinal))
      lines.AppendLine(JsonSerializer.Serialize(new {
        id = capture.Digest[..Math.Min(12, capture.Digest.Length)],
        url = capture.Url,
        ts = SyntheticDate,
        title = capture.Name,
        size = capture.PayloadLength,
      }));
    return Encoding.UTF8.GetBytes(lines.ToString());
  }

  private static byte[] BuildManifest(IEnumerable<(string Name, byte[] Data)> files) {
    var resources = files
      .Where(file => !string.Equals(file.Name, "datapackage.json", StringComparison.Ordinal))
      .OrderBy(file => file.Name, StringComparer.Ordinal)
      .Select(file => new {
        name = Path.GetFileName(file.Name),
        path = file.Name,
        hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(file.Data)),
        bytes = file.Data.Length,
      })
      .ToArray();

    var json = JsonSerializer.Serialize(new {
      profile = "data-package",
      wacz_version = "1.1.1",
      title = "CompressionWorkbench converted archive",
      created = SyntheticDate,
      software = "CompressionWorkbench",
      resources,
    }, new JsonSerializerOptions { WriteIndented = true });
    return Encoding.UTF8.GetBytes(json + "\n");
  }

  private static bool IsWarcPath(string name)
    => name.StartsWith("archive/", StringComparison.Ordinal)
       && (name.EndsWith(".warc", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".warc.gz", StringComparison.OrdinalIgnoreCase));

  private static void AddGeneratedFile(
      List<(string Name, byte[] Data)> files,
      HashSet<string> names,
      string name,
      byte[] data) {
    if (!names.Add(name))
      throw new InvalidDataException($"WACZ input conflicts with generated required path '{name}'.");
    files.Add((name, data));
  }

  private static void RemoveFile(List<(string Name, byte[] Data)> files, HashSet<string> names, string name) {
    files.RemoveAll(file => string.Equals(file.Name, name, StringComparison.Ordinal));
    names.Remove(name);
  }

  private static string ToSyntheticUrl(string name)
    => "https://compression-workbench.invalid/" + string.Join('/',
      name.Split('/').Select(Uri.EscapeDataString));

  private static string ToSurtKey(string url) {
    var uri = new Uri(url, UriKind.Absolute);
    var hostParts = uri.Host.Split('.');
    Array.Reverse(hostParts);
    return string.Join(',', hostParts) + ")" + uri.AbsolutePath;
  }

  private static string NormalizeName(string name) {
    if (string.IsNullOrWhiteSpace(name))
      throw new InvalidDataException("WACZ entries require a non-empty archive path.");
    var normalized = name.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new InvalidDataException($"WACZ file path '{name}' is invalid.");
    foreach (var component in normalized.Split('/'))
      if (component is "" or "." or "..")
        throw new InvalidDataException($"WACZ file path '{name}' contains an unsafe path component.");
    return normalized;
  }

  private sealed record SyntheticCapture(
    string Name,
    string Url,
    string Digest,
    long Offset,
    int RecordLength,
    int PayloadLength);
}
