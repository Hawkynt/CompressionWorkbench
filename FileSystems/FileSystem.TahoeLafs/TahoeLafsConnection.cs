#pragma warning disable CS1591
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace FileSystem.TahoeLafs;

/// <summary>
/// Small local connection document binding a Tahoe gateway URL to a root
/// directory capability. The capability is deliberately kept in the document so
/// the normal archive <see cref="Stream"/> API can represent a live Tahoe
/// namespace without global configuration.
/// </summary>
public sealed class TahoeLafsConnection {
  public const string Magic = "CWB-TAHOE-LAFS-CAP/1";
  private const int MaximumDocumentSize = 64 * 1024;

  public TahoeLafsConnection(Uri nodeUri, TahoeLafsCapability rootCapability) {
    ArgumentNullException.ThrowIfNull(nodeUri);
    ArgumentNullException.ThrowIfNull(rootCapability);
    this.NodeUri = NormalizeNodeUri(nodeUri);
    if (!rootCapability.IsDirectory || !rootCapability.CanRead)
      throw new ArgumentException("Tahoe-LAFS connection root must be a readable directory capability.", nameof(rootCapability));
    this.RootCapability = rootCapability;
  }

  public Uri NodeUri { get; }

  /// <summary>Root bearer capability. Do not log its <see cref="TahoeLafsCapability.Value"/>.</summary>
  public TahoeLafsCapability RootCapability { get; }

  public bool CanWrite => this.RootCapability.CanWrite;

  public byte[] Serialize()
    => Encoding.UTF8.GetBytes($"{Magic}\nnode={this.NodeUri.AbsoluteUri}\ncap={this.RootCapability.Value}\n");

  public void Write(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanWrite)
      throw new ArgumentException("Tahoe-LAFS connection output must be writable.", nameof(stream));
    var bytes = this.Serialize();
    if (stream.CanSeek) {
      stream.Position = 0;
      stream.SetLength(0);
    }
    stream.Write(bytes);
    if (stream.CanSeek)
      stream.Position = 0;
  }

  public static TahoeLafsConnection Parse(Stream stream) {
    if (!TryRead(stream, out var connection))
      throw new InvalidDataException("Invalid Tahoe-LAFS capability connection document.");
    return connection;
  }

  public static bool TryRead(Stream stream, [NotNullWhen(true)] out TahoeLafsConnection? connection) {
    ArgumentNullException.ThrowIfNull(stream);
    connection = null;
    if (!stream.CanRead)
      return false;

    var original = stream.CanSeek ? stream.Position : 0;
    try {
      if (stream.CanSeek) {
        if (stream.Length > MaximumDocumentSize)
          return false;
        stream.Position = 0;
      }
      using var buffer = new MemoryStream();
      var scratch = new byte[4096];
      while (true) {
        var read = stream.Read(scratch);
        if (read == 0) break;
        if (buffer.Length + read > MaximumDocumentSize)
          return false;
        buffer.Write(scratch, 0, read);
      }
      return TryParse(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), out connection);
    } finally {
      if (stream.CanSeek)
        stream.Position = original;
    }
  }

  internal static bool TryParse(ReadOnlySpan<byte> data, out TahoeLafsConnection? connection) {
    connection = null;
    if (data.Length == 0 || data.Length > MaximumDocumentSize)
      return false;

    string text;
    try {
      text = new UTF8Encoding(false, true).GetString(data);
    } catch (DecoderFallbackException) {
      return false;
    }

    var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length != 3 || !lines[0].Equals(Magic, StringComparison.Ordinal))
      return false;
    if (!lines[1].StartsWith("node=", StringComparison.Ordinal)
        || !lines[2].StartsWith("cap=", StringComparison.Ordinal))
      return false;

    if (!Uri.TryCreate(lines[1][5..], UriKind.Absolute, out var nodeUri))
      return false;
    if (!TahoeLafsCapability.TryParse(lines[2][4..], out var capability)
        || !capability.IsDirectory || !capability.CanRead)
      return false;

    try {
      connection = new(nodeUri, capability);
      return true;
    } catch (ArgumentException) {
      return false;
    }
  }

  private static Uri NormalizeNodeUri(Uri uri) {
    if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
      throw new ArgumentException("Tahoe-LAFS gateway URL must use HTTP or HTTPS.", nameof(uri));
    if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
      throw new ArgumentException("Tahoe-LAFS gateway URL must not contain user-info, query, or fragment components.", nameof(uri));

    var builder = new UriBuilder(uri);
    if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
      builder.Path += "/";
    return builder.Uri;
  }

  /// <summary>Returns only the endpoint and authority class, never the capability value.</summary>
  public override string ToString()
    => $"Tahoe-LAFS connection {this.NodeUri} ({(this.CanWrite ? "read/write" : "read-only")})";
}
