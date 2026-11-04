using System.Text;

namespace FileFormat.WebArchive;

/// <summary>One resource of a web archive and the archive name it maps to.</summary>
public sealed class WebArchiveResource {
  internal WebArchiveResource(OrderedDictionary<string, object?> dict, OrderedDictionary<string, object?> owner, bool isMain) {
    this.Dictionary = dict;
    this.Owner = owner;
    this.IsMain = isMain;
  }

  /// <summary>The resource dictionary (WebResourceURL, WebResourceData, …) — edited in place.</summary>
  internal OrderedDictionary<string, object?> Dictionary { get; }

  /// <summary>The web archive dictionary this resource belongs to (the root or a subframe).</summary>
  internal OrderedDictionary<string, object?> Owner { get; }

  /// <summary>Whether this is a (sub)frame's WebMainResource rather than one of its WebSubresources.</summary>
  public bool IsMain { get; }

  /// <summary>Archive name.</summary>
  public string Name { get; internal set; } = "";

  public string? Url => this.Dictionary.GetValueOrDefault(WebArchiveDocument.UrlKey) as string;
  public string? MimeType => this.Dictionary.GetValueOrDefault(WebArchiveDocument.MimeTypeKey) as string;
  public byte[] Data => this.Dictionary.GetValueOrDefault(WebArchiveDocument.DataKey) as byte[] ?? [];
}

/// <summary>
/// A Safari / WebKit web archive: a binary property list whose root dictionary holds
/// <c>WebMainResource</c> (the page), optional <c>WebSubresources</c> (images, stylesheets, scripts) and
/// optional <c>WebSubframeArchives</c> (nested web archives, one per frame). Each resource dictionary
/// carries <c>WebResourceURL</c>, <c>WebResourceMIMEType</c>, <c>WebResourceData</c> and optionally
/// <c>WebResourceTextEncodingName</c>, <c>WebResourceFrameName</c> and <c>WebResourceResponse</c> (an
/// archived NSURLResponse, kept opaque).
/// </summary>
public sealed class WebArchiveDocument {

  internal const string MainKey = "WebMainResource";
  internal const string SubresourcesKey = "WebSubresources";
  internal const string SubframesKey = "WebSubframeArchives";
  internal const string UrlKey = "WebResourceURL";
  internal const string MimeTypeKey = "WebResourceMIMEType";
  internal const string DataKey = "WebResourceData";
  internal const string EncodingKey = "WebResourceTextEncodingName";
  internal const string FrameNameKey = "WebResourceFrameName";

  public WebArchiveDocument(byte[] data) {
    if (BinaryPlist.Read(data) is not OrderedDictionary<string, object?> root)
      throw new InvalidDataException("Web archive root is not a dictionary.");
    this.Root = root;
    var resources = new List<WebArchiveResource>();
    Collect(root, resources, 0);
    this.Resources = resources;
    AssignNames(resources);
  }

  /// <summary>The root property-list dictionary.</summary>
  internal OrderedDictionary<string, object?> Root { get; }

  /// <summary>Every resource, page first, in document order (subframes after the root's subresources).</summary>
  public IReadOnlyList<WebArchiveResource> Resources { get; }

  /// <summary>The root page's origin (scheme + authority), when it has one.</summary>
  public string? RootOrigin => Origin(this.Resources[0].Url);

  /// <summary>Serializes the (possibly edited) document.</summary>
  public byte[] ToBytes() => BinaryPlist.Write(this.Root);

  private static void Collect(OrderedDictionary<string, object?> archive, List<WebArchiveResource> into, int depth) {
    if (depth > 64) throw new InvalidDataException("Web archive frames nest too deeply.");
    if (archive.GetValueOrDefault(MainKey) is not OrderedDictionary<string, object?> main)
      throw new InvalidDataException("Web archive has no WebMainResource dictionary.");
    into.Add(Validate(new WebArchiveResource(main, archive, isMain: true)));
    if (archive.GetValueOrDefault(SubresourcesKey) is { } subs)
      foreach (var s in subs as List<object?> ?? throw new InvalidDataException("WebSubresources is not an array."))
        into.Add(Validate(new WebArchiveResource(s as OrderedDictionary<string, object?> ?? throw new InvalidDataException("A web subresource is not a dictionary."), archive, isMain: false)));
    if (archive.GetValueOrDefault(SubframesKey) is { } frames)
      foreach (var f in frames as List<object?> ?? throw new InvalidDataException("WebSubframeArchives is not an array."))
        Collect(f as OrderedDictionary<string, object?> ?? throw new InvalidDataException("A web subframe archive is not a dictionary."), into, depth + 1);
  }

  private static WebArchiveResource Validate(WebArchiveResource r) {
    if (r.Dictionary.GetValueOrDefault(DataKey) is not (byte[] or null))
      throw new InvalidDataException("WebResourceData is not a data object.");
    if (r.Dictionary.GetValueOrDefault(UrlKey) is not (string or null))
      throw new InvalidDataException("WebResourceURL is not a string.");
    return r;
  }

  // ── naming (Content-Location style: root origin by path, others under host) ─────

  private static void AssignNames(List<WebArchiveResource> resources) {
    var origin = Origin(resources[0].Url);
    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < resources.Count; ++i) {
      var r = resources[i];
      var name = NameFor(r.Url, origin, r.MimeType) ?? $"resource_{i:D2}{ExtensionFor(r.MimeType)}";
      var unique = name;
      for (var n = 2; !used.Add(unique); ++n) {
        var ext = Path.GetExtension(name);
        unique = $"{name[..^ext.Length]}~{n}{ext}";
      }
      r.Name = unique;
    }
  }

  internal static string? Origin(string? url)
    => url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && !u.IsFile && u.Authority.Length > 0 && u.Scheme is "http" or "https"
      ? u.GetLeftPart(UriPartial.Authority)
      : null;

  internal static string? NameFor(string? url, string? rootOrigin, string? mimeType) {
    if (string.IsNullOrWhiteSpace(url)) return null;
    string path;
    if (HasScheme(url) && Uri.TryCreate(url, UriKind.Absolute, out var u)) {
      if (u.Scheme is "data" or "about" or "blob") return null;
      var local = Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/');
      if (u.Query.Length > 0) local += "_" + Uri.UnescapeDataString(u.Query[1..]);
      var origin = Origin(url);
      path = origin != null && string.Equals(origin, rootOrigin, StringComparison.OrdinalIgnoreCase) ? local
        : u.Authority.Length > 0 ? u.Authority + "/" + local
        : local;
    } else {
      path = Uri.UnescapeDataString(url).TrimStart('/');
    }
    if (path.Length == 0 || path.EndsWith('/'))
      path += "index" + ExtensionFor(mimeType);
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
      .Where(s => s is not ("." or ".."))
      .Select(Sanitize)
      .Where(s => s.Length > 0)
      .ToArray();
    return segments.Length == 0 ? null : string.Join('/', segments);
  }

  /// <summary>An RFC 3986 scheme of two or more characters (a single letter is a Windows drive).</summary>
  private static bool HasScheme(string url) {
    var colon = url.IndexOf(':');
    if (colon < 2 || !char.IsAsciiLetter(url[0])) return false;
    for (var i = 1; i < colon; ++i)
      if (!(char.IsAsciiLetterOrDigit(url[i]) || url[i] is '+' or '-' or '.')) return false;
    return true;
  }

  private static string Sanitize(string segment) {
    var sb = new StringBuilder(segment.Length);
    foreach (var c in segment)
      sb.Append(c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' || char.IsControl(c) ? '_' : c);
    return sb.ToString().TrimEnd('.', ' ');
  }

  // ── media types ──────────────────────────────────────────────────────

  private static readonly (string Extension, string MediaType)[] Types = [
    (".html", "text/html"), (".htm", "text/html"), (".xhtml", "application/xhtml+xml"),
    (".css", "text/css"), (".js", "text/javascript"), (".mjs", "text/javascript"),
    (".json", "application/json"), (".xml", "text/xml"), (".svg", "image/svg+xml"), (".txt", "text/plain"),
    (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"), (".gif", "image/gif"),
    (".webp", "image/webp"), (".avif", "image/avif"), (".bmp", "image/bmp"), (".ico", "image/x-icon"),
    (".woff", "font/woff"), (".woff2", "font/woff2"), (".ttf", "font/ttf"), (".otf", "font/otf"),
    (".pdf", "application/pdf"), (".mp4", "video/mp4"), (".webm", "video/webm"), (".mp3", "audio/mpeg"),
  ];

  internal static string MimeTypeFor(string name) {
    var ext = Path.GetExtension(name);
    foreach (var (e, m) in Types)
      if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return m;
    return "application/octet-stream";
  }

  internal static string ExtensionFor(string? mimeType) {
    foreach (var (e, m) in Types)
      if (string.Equals(m, mimeType, StringComparison.OrdinalIgnoreCase)) return e;
    return ".bin";
  }

  /// <summary>A new resource dictionary in the key order Safari writes.</summary>
  internal static OrderedDictionary<string, object?> NewResource(string url, string name, byte[] data, bool main) {
    var mime = MimeTypeFor(name);
    var dict = new OrderedDictionary<string, object?> { [DataKey] = data };
    if (main) dict[FrameNameKey] = "";
    dict[MimeTypeKey] = mime;
    if (mime.StartsWith("text/", StringComparison.Ordinal) || mime is "application/json" or "image/svg+xml" or "application/xhtml+xml")
      dict[EncodingKey] = "UTF-8";
    dict[UrlKey] = url;
    return dict;
  }
}
