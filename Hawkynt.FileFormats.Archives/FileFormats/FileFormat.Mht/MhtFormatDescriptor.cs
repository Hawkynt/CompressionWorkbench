#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mht;

/// <summary>
/// MHTML web archive (<c>.mht</c> / <c>.mhtml</c>) — a saved web page with its resources, as written by
/// Chromium ("Save as single file"), Internet Explorer and Word.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc2557</c> — RFC 2557, MIME Encapsulation of Aggregate Documents (MHTML)</description></item>
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc2045</c> / <c>rfc2046</c> — MIME transfer encodings and multipart bodies</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Each resource is an entry named after its <c>Content-Location</c>: resources on the root page's
/// origin by their path, others under their host, <c>cid:</c> parts under <c>cid/</c>. Created archives
/// place every file under <see cref="MhtWriter.DefaultBaseUrl"/> with the first HTML file as root, so a
/// page's relative links keep working in a browser. Add and remove splice whole parts between boundary
/// delimiters; every other byte of the document is kept.
/// </remarks>
public sealed class MhtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "Mht";
  public string DisplayName => "MHTML (web archive)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".mht";
  public IReadOnlyList<string> Extensions => [".mht", ".mhtml"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // A MIME message starts with whatever header its writer put first; detection is by extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("base64", "Base64"), new("quoted-printable", "Quoted-printable")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Saved web page (RFC 2557 MHTML). Resources are named by their Content-Location; created archives open in " +
    "Chromium with their relative links intact. Create options: subject, root (the page to put first).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = Read(stream);
    var list = new List<ArchiveEntryInfo>(r.Parts.Count);
    for (var i = 0; i < r.Parts.Count; ++i) {
      var p = r.Parts[i];
      long size;
      try { size = r.Extract(p).Length; } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) { size = p.BodyLength; }
      list.Add(new ArchiveEntryInfo(i, p.Name, size, p.BodyLength, p.TransferEncoding, false, false, null));
    }
    return list;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = Read(stream);
    foreach (var p in r.Parts) {
      if (files != null && files.Length > 0 && !MatchesFilter(p.Name, files)) continue;
      WriteFile(outputDir, p.Name, r.Extract(p));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = FilesOnly(inputs).Select(f => (Name: f.Name.Replace('\\', '/'), f.Data)).ToList();
    var root = options?.GetString("root");
    var rootIndex = root != null
      ? files.FindIndex(f => string.Equals(f.Name, root, StringComparison.OrdinalIgnoreCase))
      : files.FindIndex(f => MhtMediaTypes.MediaTypeFor(f.Name) == "text/html");
    if (root != null && rootIndex < 0)
      throw new ArgumentException($"MHTML: the root page '{root}' is not among the inputs.");
    if (rootIndex > 0) {
      var page = files[rootIndex];
      files.RemoveAt(rootIndex);
      files.Insert(0, page);
    }
    MhtWriter.Write(output, files, options?.GetString("subject"));
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var data = ReadAll(archive);
    foreach (var (rawName, content) in FilesOnly(inputs)) {
      var name = rawName.Replace('\\', '/');
      var r = new MhtReader(data);
      RequireMultipart(r);
      if (r.Parts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing) {
        RequireTopLevel(existing);
        data = Splice(data, existing.SegmentStart, existing.SegmentEnd, []);
        r = new MhtReader(data);
      }
      var location = r.RootOrigin is { } origin
        ? origin + "/" + MhtWriter.EscapePath(name)
        : r.Parts.Any(p => p.Location?.StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true)
          ? "file:///" + MhtWriter.EscapePath(name)
          : MhtWriter.DefaultBaseUrl + MhtWriter.EscapePath(name);
      var segment = MhtWriter.PartSegment(r.Boundary!, r.LineBreak, location, MhtMediaTypes.MediaTypeFor(name), content);
      data = Splice(data, r.CloseSegmentStart, r.CloseSegmentStart, segment);
    }
    Replace(archive, data);
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var data = ReadAll(archive);
    var r = new MhtReader(data);
    var targets = r.Parts.Where(p => entryNames.Any(n => string.Equals(p.Name, n.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))).ToList();
    if (targets.Count == 0) return;
    RequireMultipart(r);
    foreach (var t in targets) RequireTopLevel(t);
    foreach (var t in targets.OrderByDescending(t => t.SegmentStart))
      data = Splice(data, t.SegmentStart, t.SegmentEnd, []);
    Replace(archive, data);
  }

  private static void RequireMultipart(MhtReader r) {
    if (r.Boundary == null)
      throw new NotSupportedException("MHTML: a single-resource document has no multipart body to edit.");
  }

  private static void RequireTopLevel(MhtPart p) {
    if (!p.IsTopLevel)
      throw new NotSupportedException($"MHTML: '{p.Name}' sits inside a nested multipart; only top-level parts can be edited.");
  }

  private static byte[] Splice(byte[] data, int start, int end, byte[] insert) {
    var result = new byte[data.Length - (end - start) + insert.Length];
    data.AsSpan(0, start).CopyTo(result);
    insert.CopyTo(result, start);
    data.AsSpan(end).CopyTo(result.AsSpan(start + insert.Length));
    return result;
  }

  private static MhtReader Read(Stream stream) => new(ReadAll(stream));

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static void Replace(Stream archive, byte[] data) {
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(data);
    archive.Flush();
  }
}
