using Compression.Core.Streams;
using FileFormat.Ar;
using FileFormat.Gzip;
using FileFormat.Tar;
using FileFormat.Xz;

namespace FileFormat.Vib;

/// <summary>
/// Reads a VMware vSphere Installation Bundle (<c>.vib</c>). A VIB is a Unix
/// <c>ar</c> archive holding three members:
/// <list type="bullet">
///   <item><c>descriptor.xml</c> — bundle metadata (name, version, payloads).</item>
///   <item><c>sig.pkcs7</c> — the detached PKCS#7 signature.</item>
///   <item>a payload member — a <c>.vgz</c> (gzip-compressed tar), an
///   xz-compressed tar, or a bare tar — whose name matches the payload id.</item>
/// </list>
/// The reader surfaces the descriptor XML, the raw signature and the fully
/// decompressed payload tree (tar entries).
/// </summary>
public sealed class VibReader : IDisposable {
  private readonly ArReader _ar;
  private bool _disposed;

  /// <summary>Raw <c>ar</c> members exactly as stored in the bundle.</summary>
  public IReadOnlyList<ArEntry> RawMembers => this._ar.Entries;

  /// <summary>Opens a VIB from a seekable stream.</summary>
  public VibReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._ar = new ArReader(stream);
  }

  /// <summary>The <c>descriptor.xml</c> bytes, or null when absent.</summary>
  public byte[]? DescriptorXml
    => this._ar.Entries.FirstOrDefault(e => Leaf(e.Name) == VibConstants.DescriptorName)?.Data;

  /// <summary>The <c>sig.pkcs7</c> bytes, or null when absent.</summary>
  public byte[]? Signature
    => this._ar.Entries.FirstOrDefault(e => Leaf(e.Name) == VibConstants.SignatureName)?.Data;

  /// <summary>Name of the payload member (anything that is not descriptor/signature).</summary>
  public string? PayloadMemberName
    => this._ar.Entries.FirstOrDefault(IsPayload)?.Name;

  /// <summary>The compressed payload member bytes, or null when absent.</summary>
  public byte[]? PayloadRaw
    => this._ar.Entries.FirstOrDefault(IsPayload)?.Data;

  /// <summary>
  /// Decompresses the payload member (gzip/xz/stored) and returns its bytes.
  /// Returns an empty array when there is no payload.
  /// </summary>
  public byte[] DecompressPayload() {
    var payload = this._ar.Entries.FirstOrDefault(IsPayload);
    return payload is null ? [] : Decompress(payload.Data);
  }

  /// <summary>
  /// Reads the payload's tar tree. Returns an empty list when the payload is
  /// absent or is not a tar (best-effort; never throws for a non-tar payload).
  /// </summary>
  public IReadOnlyList<VibEntry> ReadPayloadEntries() {
    var decompressed = this.DecompressPayload();
    if (decompressed.Length == 0)
      return [];

    var result = new List<VibEntry>();
    try {
      using var ms = new MemoryStream(decompressed, writable: false);
      using var tar = new TarReader(ms);
      while (true) {
        var entry = tar.GetNextEntry();
        if (entry is null)
          break;
        if (entry.IsDirectory) {
          result.Add(new VibEntry(entry.Name, [], true));
          continue;
        }
        using var es = tar.GetEntryStream();
        using var buf = new MemoryStream();
        es.CopyTo(buf);
        result.Add(new VibEntry(entry.Name, buf.ToArray(), false));
      }
    } catch (InvalidDataException) {
      // Payload was not a tar (or was truncated) — surface nothing rather than throw.
    } catch (EndOfStreamException) {
    }
    return result;
  }

  private static bool IsPayload(ArEntry e) {
    var leaf = Leaf(e.Name);
    return leaf != VibConstants.DescriptorName && leaf != VibConstants.SignatureName;
  }

  private static string Leaf(string name) {
    var slash = name.LastIndexOf('/');
    return slash >= 0 ? name[(slash + 1)..] : name;
  }

  private static byte[] Decompress(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    if (HasGzipMagic(data)) {
      using var gz = new GzipStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
      gz.CopyTo(output);
    } else if (HasXzMagic(data)) {
      using var xz = new XzStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
      xz.CopyTo(output);
    } else {
      input.CopyTo(output); // already a bare tar
    }
    return output.ToArray();
  }

  private static bool HasGzipMagic(byte[] d) => d.Length >= 2 && d[0] == 0x1F && d[1] == 0x8B;

  private static bool HasXzMagic(byte[] d)
    => d.Length >= 6 && d[0] == 0xFD && d[1] == 0x37 && d[2] == 0x7A &&
       d[3] == 0x58 && d[4] == 0x5A && d[5] == 0x00;

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    this._ar.Dispose();
  }
}
