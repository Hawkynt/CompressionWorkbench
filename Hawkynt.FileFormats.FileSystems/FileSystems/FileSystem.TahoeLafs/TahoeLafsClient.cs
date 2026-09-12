#pragma warning disable CS1591
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FileSystem.TahoeLafs;

public enum TahoeLafsUploadFormat {
  Chk,
  Sdmf,
  Mdmf,
}

/// <summary>One namespace entry returned by a Tahoe-LAFS gateway.</summary>
public sealed class TahoeLafsRemoteEntry {
  public required string Path { get; init; }
  public required bool IsDirectory { get; init; }
  public required long Size { get; init; }
  public required bool Mutable { get; init; }
  public string? Format { get; init; }
  public TahoeLafsCapability? ReadCapability { get; init; }
  public TahoeLafsCapability? WriteCapability { get; init; }
}

/// <summary>
/// Capability-aware client for Tahoe-LAFS's documented HTTP gateway API. Tahoe
/// itself remains responsible for share discovery, erasure coding, encryption,
/// validation, mutable signatures and publication; this class exercises only the
/// authority conveyed by the supplied capability.
/// </summary>
public sealed class TahoeLafsClient : IDisposable {
  private const int MaximumTraversalDepth = 256;
  private readonly HttpClient _http;
  private readonly bool _ownsHttpClient;
  private readonly Uri _nodeUri;

  public TahoeLafsClient(Uri nodeUri)
    : this(nodeUri, new HttpClient(), ownsHttpClient: true) { }

  public TahoeLafsClient(Uri nodeUri, HttpClient httpClient)
    : this(nodeUri, httpClient, ownsHttpClient: false) { }

  private TahoeLafsClient(Uri nodeUri, HttpClient httpClient, bool ownsHttpClient) {
    ArgumentNullException.ThrowIfNull(nodeUri);
    ArgumentNullException.ThrowIfNull(httpClient);
    this._nodeUri = NormalizeNodeUri(nodeUri);
    this._http = httpClient;
    this._ownsHttpClient = ownsHttpClient;
  }

  /// <summary>Lists one directory, optionally walking descendant directory capabilities.</summary>
  public IReadOnlyList<TahoeLafsRemoteEntry> ListDirectory(TahoeLafsCapability directory, bool recursive = true) {
    RequireReadableDirectory(directory);
    var result = new List<TahoeLafsRemoteEntry>();
    var ancestors = new HashSet<string>(StringComparer.Ordinal);
    this.ListDirectoryCore(directory, "", recursive, 0, ancestors, result);
    return result;
  }

  /// <summary>Reads plaintext through the authority of a file read-capability.</summary>
  public byte[] ReadFile(TahoeLafsCapability file) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.CanRead || file.IsDirectory || file.Kind == TahoeLafsCapabilityKind.Verifier)
      throw new UnauthorizedAccessException("Tahoe-LAFS capability does not grant readable file authority.");

    using var request = new HttpRequestMessage(HttpMethod.Get, this.BuildCapabilityUri(file));
    using var response = this.Send(request, "read file");
    return ReadBytes(response);
  }

  /// <summary>Uploads a standalone Tahoe file and returns the newly issued capability.</summary>
  public TahoeLafsCapability Upload(ReadOnlyMemory<byte> data, TahoeLafsUploadFormat format = TahoeLafsUploadFormat.Chk) {
    using var request = new HttpRequestMessage(HttpMethod.Put, this.BuildRootUri($"format={ProtocolFormat(format)}")) {
      Content = BinaryContent(data),
    };
    using var response = this.Send(request, "upload file");
    return ParseCapabilityResponse(response);
  }

  /// <summary>Creates an unattached mutable directory and returns its write-capability.</summary>
  public TahoeLafsCapability CreateDirectory(TahoeLafsUploadFormat format = TahoeLafsUploadFormat.Mdmf) {
    if (format == TahoeLafsUploadFormat.Chk)
      throw new ArgumentOutOfRangeException(nameof(format), "Tahoe mutable directories use SDMF or MDMF.");
    using var request = new HttpRequestMessage(HttpMethod.Post, this.BuildRootUri($"t=mkdir&format={ProtocolFormat(format)}")) {
      Content = new ByteArrayContent([]),
    };
    using var response = this.Send(request, "create directory");
    var capability = ParseCapabilityResponse(response);
    if (!capability.IsDirectory || !capability.CanWrite)
      throw new InvalidDataException("Tahoe-LAFS gateway returned a non-writable directory capability for mkdir.");
    return capability;
  }

  /// <summary>Creates a mutable directory below a writable root directory.</summary>
  public void CreateDirectory(TahoeLafsCapability directory, string path, TahoeLafsUploadFormat format = TahoeLafsUploadFormat.Mdmf) {
    RequireWritableDirectory(directory);
    if (format == TahoeLafsUploadFormat.Chk)
      throw new ArgumentOutOfRangeException(nameof(format), "Tahoe mutable directories use SDMF or MDMF.");
    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      this.BuildCapabilityUri(directory, NormalizeRelativePath(path), $"t=mkdir&format={ProtocolFormat(format)}")) {
      Content = new ByteArrayContent([]),
    };
    using var response = this.Send(request, "create child directory");
  }

  /// <summary>
  /// Uploads/replaces a child under a writable directory. The directory write-cap
  /// supplies the authority; CHK is the default storage profile for regular files.
  /// </summary>
  public TahoeLafsCapability? UploadFile(
      TahoeLafsCapability directory,
      string path,
      ReadOnlyMemory<byte> data,
      TahoeLafsUploadFormat format = TahoeLafsUploadFormat.Chk) {
    RequireWritableDirectory(directory);
    using var request = new HttpRequestMessage(
      HttpMethod.Put,
      this.BuildCapabilityUri(directory, NormalizeRelativePath(path), $"format={ProtocolFormat(format)}")) {
      Content = BinaryContent(data),
    };
    using var response = this.Send(request, "upload child");
    return TryParseCapabilityResponse(response);
  }

  /// <summary>Overwrites an existing mutable SDMF/MDMF file through its write-capability.</summary>
  public void WriteMutableFile(TahoeLafsCapability file, ReadOnlyMemory<byte> data) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.CanWrite || file.IsDirectory || !file.IsMutable)
      throw new UnauthorizedAccessException("Tahoe-LAFS capability does not grant mutable-file write authority.");

    using var request = new HttpRequestMessage(HttpMethod.Put, this.BuildCapabilityUri(file)) {
      Content = BinaryContent(data),
    };
    using var response = this.Send(request, "write mutable file");
  }

  /// <summary>Unlinks one child path from a writable directory.</summary>
  public void Remove(TahoeLafsCapability directory, string path) {
    RequireWritableDirectory(directory);
    using var request = new HttpRequestMessage(HttpMethod.Delete, this.BuildCapabilityUri(directory, NormalizeRelativePath(path)));
    using var response = this.Send(request, "unlink child");
  }

  /// <summary>Unlinks every immediate child while preserving the root directory itself.</summary>
  public int PurgeDirectory(TahoeLafsCapability directory) {
    RequireWritableDirectory(directory);
    var children = this.ListDirectory(directory, recursive: false);
    foreach (var child in children)
      this.Remove(directory, child.Path);
    return children.Count;
  }

  private void ListDirectoryCore(
      TahoeLafsCapability directory,
      string prefix,
      bool recursive,
      int depth,
      HashSet<string> ancestors,
      List<TahoeLafsRemoteEntry> result) {
    if (depth > MaximumTraversalDepth)
      throw new InvalidDataException("Tahoe-LAFS directory traversal exceeded the safety depth limit.");
    if (!ancestors.Add(directory.Value))
      return;

    try {
      using var request = new HttpRequestMessage(HttpMethod.Get, this.BuildCapabilityUri(directory, query: "t=json"));
      using var response = this.Send(request, "list directory");
      using var document = JsonDocument.Parse(response.Content.ReadAsStream());
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2
          || root[0].ValueKind != JsonValueKind.String || root[0].GetString() != "dirnode"
          || root[1].ValueKind != JsonValueKind.Object
          || !root[1].TryGetProperty("children", out var children)
          || children.ValueKind != JsonValueKind.Object)
        throw new InvalidDataException("Tahoe-LAFS gateway returned malformed directory JSON.");

      foreach (var child in children.EnumerateObject()) {
        ValidateChildName(child.Name);
        if (child.Value.ValueKind != JsonValueKind.Array || child.Value.GetArrayLength() != 2
            || child.Value[0].ValueKind != JsonValueKind.String
            || child.Value[1].ValueKind != JsonValueKind.Object)
          throw new InvalidDataException("Tahoe-LAFS gateway returned malformed child metadata.");

        var nodeKind = child.Value[0].GetString();
        var metadata = child.Value[1];
        var isDirectory = nodeKind == "dirnode";
        if (!isDirectory && nodeKind != "filenode")
          continue;

        var readCapability = ReadCapability(metadata, "ro_uri") ?? ReadCapability(metadata, "rw_uri");
        var writeCapability = ReadCapability(metadata, "rw_uri");
        var path = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
        var size = metadata.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsedSize)
          ? Math.Max(0, parsedSize)
          : 0;
        var mutable = metadata.TryGetProperty("mutable", out var mutableNode)
                      && mutableNode.ValueKind is JsonValueKind.True or JsonValueKind.False
                      && mutableNode.GetBoolean();
        var format = metadata.TryGetProperty("format", out var formatNode) && formatNode.ValueKind == JsonValueKind.String
          ? formatNode.GetString()
          : null;

        result.Add(new TahoeLafsRemoteEntry {
          Path = path,
          IsDirectory = isDirectory,
          Size = size,
          Mutable = mutable,
          Format = format,
          ReadCapability = readCapability,
          WriteCapability = writeCapability,
        });

        if (recursive && isDirectory && readCapability is { CanRead: true })
          this.ListDirectoryCore(readCapability, path, true, depth + 1, ancestors, result);
      }
    } finally {
      ancestors.Remove(directory.Value);
    }
  }

  private static TahoeLafsCapability? ReadCapability(JsonElement metadata, string propertyName) {
    if (!metadata.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
      return null;
    return TahoeLafsCapability.TryParse(node.GetString(), out var capability) ? capability : null;
  }

  private HttpResponseMessage Send(HttpRequestMessage request, string operation) {
    try {
      var response = this._http.Send(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
      if (response.IsSuccessStatusCode)
        return response;
      var status = response.StatusCode;
      response.Dispose();
      throw new IOException($"Tahoe-LAFS {operation} failed with HTTP {(int)status}.");
    } catch (HttpRequestException ex) {
      var status = ex.StatusCode is { } value ? $" HTTP {(int)value}." : ".";
      throw new IOException($"Tahoe-LAFS {operation} transport failure{status}");
    }
  }

  private Uri BuildRootUri(string? query = null) {
    var relative = "uri" + (string.IsNullOrEmpty(query) ? "" : "?" + query);
    return new Uri(this._nodeUri, relative);
  }

  private Uri BuildCapabilityUri(TahoeLafsCapability capability, string? path = null, string? query = null) {
    var relative = new StringBuilder("uri/").Append(Uri.EscapeDataString(capability.Value));
    if (!string.IsNullOrEmpty(path))
      foreach (var segment in path.Split('/'))
        relative.Append('/').Append(Uri.EscapeDataString(segment));
    if (!string.IsNullOrEmpty(query))
      relative.Append('?').Append(query);
    return new Uri(this._nodeUri, relative.ToString());
  }

  private static ByteArrayContent BinaryContent(ReadOnlyMemory<byte> data) {
    var content = new ByteArrayContent(data.ToArray());
    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    return content;
  }

  private static byte[] ReadBytes(HttpResponseMessage response) {
    using var source = response.Content.ReadAsStream();
    using var target = new MemoryStream();
    source.CopyTo(target);
    return target.ToArray();
  }

  private static TahoeLafsCapability ParseCapabilityResponse(HttpResponseMessage response)
    => TryParseCapabilityResponse(response)
       ?? throw new InvalidDataException("Tahoe-LAFS gateway returned no valid capability.");

  private static TahoeLafsCapability? TryParseCapabilityResponse(HttpResponseMessage response) {
    using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8, true, 1024, leaveOpen: false);
    var text = reader.ReadToEnd().Trim();
    return TahoeLafsCapability.TryParse(text, out var capability) ? capability : null;
  }

  private static string ProtocolFormat(TahoeLafsUploadFormat format)
    => format switch {
      TahoeLafsUploadFormat.Chk => "CHK",
      TahoeLafsUploadFormat.Sdmf => "SDMF",
      TahoeLafsUploadFormat.Mdmf => "MDMF",
      _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

  private static void RequireReadableDirectory(TahoeLafsCapability directory) {
    ArgumentNullException.ThrowIfNull(directory);
    if (!directory.IsDirectory || !directory.CanRead)
      throw new UnauthorizedAccessException("Tahoe-LAFS capability does not grant readable directory authority.");
  }

  private static void RequireWritableDirectory(TahoeLafsCapability directory) {
    ArgumentNullException.ThrowIfNull(directory);
    if (!directory.IsDirectory || !directory.CanWrite)
      throw new UnauthorizedAccessException("Tahoe-LAFS capability does not grant directory write authority.");
  }

  private static string NormalizeRelativePath(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    if (path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\'))
      throw new ArgumentException("Tahoe-LAFS archive paths must be relative '/'-separated paths.", nameof(path));
    var segments = path.Split('/');
    if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl)))
      throw new ArgumentException("Tahoe-LAFS archive path contains an unsafe segment.", nameof(path));
    return string.Join('/', segments);
  }

  private static void ValidateChildName(string name) {
    if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
      throw new InvalidDataException("Tahoe-LAFS directory contains a child name that cannot be represented safely as an archive path.");
  }

  private static Uri NormalizeNodeUri(Uri uri) {
    if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
      throw new ArgumentException("Tahoe-LAFS gateway URL must use HTTP or HTTPS.", nameof(uri));
    var builder = new UriBuilder(uri);
    if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
      builder.Path += "/";
    return builder.Uri;
  }

  public void Dispose() {
    if (this._ownsHttpClient)
      this._http.Dispose();
  }
}
