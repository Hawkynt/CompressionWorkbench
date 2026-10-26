namespace FileFormat.Mht;

/// <summary>Maps between file extensions and the media types web resources are served with.</summary>
internal static class MhtMediaTypes {

  private static readonly (string Extension, string MediaType)[] Table = [
    (".html", "text/html"), (".htm", "text/html"), (".xhtml", "application/xhtml+xml"),
    (".css", "text/css"), (".js", "text/javascript"), (".mjs", "text/javascript"),
    (".json", "application/json"), (".xml", "text/xml"), (".svg", "image/svg+xml"), (".txt", "text/plain"),
    (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"), (".gif", "image/gif"),
    (".webp", "image/webp"), (".avif", "image/avif"), (".bmp", "image/bmp"), (".ico", "image/x-icon"),
    (".woff", "font/woff"), (".woff2", "font/woff2"), (".ttf", "font/ttf"), (".otf", "font/otf"),
    (".pdf", "application/pdf"), (".mp4", "video/mp4"), (".webm", "video/webm"), (".mp3", "audio/mpeg"),
  ];

  public static string MediaTypeFor(string name) {
    var ext = Path.GetExtension(name);
    foreach (var (e, m) in Table)
      if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return m;
    return "application/octet-stream";
  }

  public static string ExtensionFor(string mediaType) {
    foreach (var (e, m) in Table)
      if (string.Equals(m, mediaType, StringComparison.OrdinalIgnoreCase)) return e;
    return ".bin";
  }
}
