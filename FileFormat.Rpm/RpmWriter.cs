using System.Buffers.Binary;
using System.Text;
using Compression.Core.Streams;
using FileFormat.Cpio;

namespace FileFormat.Rpm;

/// <summary>
/// Creates basic unsigned RPM packages.
/// </summary>
/// <remarks>
/// Writes a Lead (96 bytes) + empty Signature header + main Header + compressed CPIO payload.
/// The resulting package is unsigned and suitable for testing or internal use.
/// </remarks>
public sealed class RpmWriter {
  private readonly List<(string Path, byte[] Data)> _files = [];
  private string _name = "package";
  private string _version = "1.0";
  private string _release = "1";
  private string _architecture = "noarch";
  private string _payloadCompressor = "gzip";

  /// <summary>Gets or sets the package name.</summary>
  public string Name {
    get => this._name;
    set => this._name = value ?? throw new ArgumentNullException(nameof(value));
  }

  /// <summary>Gets or sets the package version.</summary>
  public string Version {
    get => this._version;
    set => this._version = value ?? throw new ArgumentNullException(nameof(value));
  }

  /// <summary>Gets or sets the package release.</summary>
  public string Release {
    get => this._release;
    set => this._release = value ?? throw new ArgumentNullException(nameof(value));
  }

  /// <summary>Gets or sets the package architecture.</summary>
  public string Architecture {
    get => this._architecture;
    set => this._architecture = value ?? throw new ArgumentNullException(nameof(value));
  }

  /// <summary>
  /// Gets or sets the payload compressor name. Supported: "gzip", "xz", "bzip2", "zstd".
  /// Defaults to "gzip".
  /// </summary>
  public string PayloadCompressor {
    get => this._payloadCompressor;
    set => this._payloadCompressor = value ?? throw new ArgumentNullException(nameof(value));
  }

  /// <summary>
  /// Adds a file to the package payload.
  /// </summary>
  /// <param name="path">The file path within the package (e.g., "usr/bin/hello").</param>
  /// <param name="data">The file contents.</param>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((path, data));
  }

  /// <summary>
  /// Writes the RPM package to the specified stream.
  /// </summary>
  /// <param name="output">The stream to write the package to.</param>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // 1. Write Lead
    WriteLead(output);

    // 2. Write empty Signature header + alignment
    WriteEmptyHeader(output);
    AlignTo8(output);

    // 3. Write main Header with metadata tags
    WriteMainHeader(output);

    // 4. Write compressed CPIO payload
    WritePayload(output);

    output.Flush();
  }

  /// <summary>
  /// Creates the RPM package as a byte array.
  /// </summary>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    WriteTo(ms);
    return ms.ToArray();
  }

  private void WriteLead(Stream output) {
    Span<byte> lead = stackalloc byte[RpmConstants.LeadSize];
    lead.Clear();

    // Magic
    lead[0] = 0xED; lead[1] = 0xAB; lead[2] = 0xEE; lead[3] = 0xDB;
    // Major/minor version
    lead[4] = 3; lead[5] = 0;
    // Type = binary
    lead[6] = 0; lead[7] = 0;
    // Architecture = generic
    lead[8] = 0; lead[9] = 1;
    // Name (up to 65 bytes)
    var nameBytes = Encoding.ASCII.GetBytes(this._name);
    var copyLen = Math.Min(nameBytes.Length, 65);
    nameBytes.AsSpan(0, copyLen).CopyTo(lead[10..]);
    // OS = 1
    lead[76] = 0; lead[77] = 1;
    // Signature type = 5
    lead[78] = 0; lead[79] = 5;

    output.Write(lead);
  }

  private static void WriteEmptyHeader(Stream output) {
    WriteHeaderStructure(output, []);
  }

  private void WriteMainHeader(Stream output) {
    var tags = new List<(int Tag, string Value)> {
      (RpmConstants.TagName, this._name),
      (RpmConstants.TagVersion, this._version),
      (RpmConstants.TagRelease, this._release),
      (RpmConstants.TagArch, this._architecture),
      (RpmConstants.TagPayloadFormat, "cpio"),
      (RpmConstants.TagPayloadCompressor, this._payloadCompressor),
    };
    WriteHeaderStructure(output, tags);
  }

  private static void WriteHeaderStructure(Stream output, IList<(int Tag, string Value)> tags) {
    // Build store
    using var storeMs = new MemoryStream();
    var offsets = new int[tags.Count];
    for (var i = 0; i < tags.Count; ++i) {
      offsets[i] = (int)storeMs.Position;
      var b = Encoding.UTF8.GetBytes(tags[i].Value);
      storeMs.Write(b);
      storeMs.WriteByte(0);
    }
    var store = storeMs.ToArray();

    // Preamble
    Span<byte> preamble = stackalloc byte[RpmConstants.HeaderPreambleSize];
    preamble[0] = 0x8E; preamble[1] = 0xAD; preamble[2] = 0xE8;
    preamble[3] = RpmConstants.HeaderVersion;
    preamble[4] = 0; preamble[5] = 0; preamble[6] = 0; preamble[7] = 0;
    BinaryPrimitives.WriteInt32BigEndian(preamble[8..], tags.Count);
    BinaryPrimitives.WriteInt32BigEndian(preamble[12..], store.Length);
    output.Write(preamble);

    // Index entries
    Span<byte> entry = stackalloc byte[RpmConstants.IndexEntrySize];
    for (var i = 0; i < tags.Count; ++i) {
      BinaryPrimitives.WriteInt32BigEndian(entry, tags[i].Tag);
      BinaryPrimitives.WriteInt32BigEndian(entry[4..], RpmConstants.TypeString);
      BinaryPrimitives.WriteInt32BigEndian(entry[8..], offsets[i]);
      BinaryPrimitives.WriteInt32BigEndian(entry[12..], 1);
      output.Write(entry);
    }

    output.Write(store);
  }

  private void WritePayload(Stream output) {
    // Build CPIO archive in memory
    byte[] cpioData;
    using (var cpioMs = new MemoryStream()) {
      using (var cpioWriter = new CpioWriter(cpioMs, leaveOpen: true)) {
        foreach (var (path, data) in this._files)
          cpioWriter.AddFile(path, data);
      }
      cpioData = cpioMs.ToArray();
    }

    // Compress the CPIO data with the selected compressor
    CompressAndWrite(output, cpioData);
  }

  private void CompressAndWrite(Stream output, byte[] cpioData) {
    switch (this._payloadCompressor) {
      case "gzip": {
        using var gz = new Gzip.GzipStream(output, CompressionStreamMode.Compress, leaveOpen: true);
        gz.Write(cpioData);
        break;
      }
      case "bzip2": {
        using var bz2 = new Bzip2.Bzip2Stream(output, CompressionStreamMode.Compress, leaveOpen: true);
        bz2.Write(cpioData);
        break;
      }
      case "xz": {
        using var xz = new Xz.XzStream(output, CompressionStreamMode.Compress, leaveOpen: true);
        xz.Write(cpioData);
        break;
      }
      case "zstd": {
        using var zstd = new Zstd.ZstdStream(output, CompressionStreamMode.Compress, leaveOpen: true);
        zstd.Write(cpioData);
        break;
      }
      case "lzma": {
        Lzma.LzmaStream.Compress(new MemoryStream(cpioData), output);
        break;
      }
      default:
        throw new NotSupportedException($"Unsupported payload compressor for writing: {this._payloadCompressor}");
    }
  }

  private static void AlignTo8(Stream s) {
    if (!s.CanSeek) return;
    var rem = s.Position % 8;
    if (rem != 0) {
      var pad = (int)(8 - rem);
      Span<byte> zeros = stackalloc byte[pad];
      zeros.Clear();
      s.Write(zeros);
    }
  }
}
