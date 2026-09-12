#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.MooseFs;

/// <summary>
/// Partial reader for MooseFS master-metadata images (<c>metadata.mfs</c>).
/// It understands the versioned outer envelope and section framing, but does
/// not decode the version-specific NODE / EDGE / CHNK bodies or contact chunk
/// servers. Consequently all surfaced archive entries are synthetic forensic
/// views of the metadata image rather than the mounted MooseFS namespace.
/// </summary>
public sealed class MooseFsReader : IDisposable {

  /// <summary>MooseFS master metadata 4-byte prefix: ASCII <c>MFSM</c>.</summary>
  public static readonly byte[] MasterTag = "MFSM"u8.ToArray();

  /// <summary>Modern (1.6+) 16-byte MooseFS end-of-file marker.</summary>
  public static readonly byte[] EofMarker = "[MFS EOF MARKER]"u8.ToArray();

  private const int SignatureSize = 8;
  private const int MetadataHeaderSize = 16;
  private const int SectionHeaderSize = 16;
  private const long MaxInMemorySection = 64L * 1024 * 1024;

  private readonly byte[] _data;
  private readonly List<MooseFsEntry> _entries = [];
  private readonly List<SectionEntry> _sections = [];

  /// <summary>Listing of every synthetic entry surfaced from this image.</summary>
  public IReadOnlyList<MooseFsEntry> Entries => _entries;

  /// <summary>Section index walked from section-framed (1.6+) metadata.</summary>
  public IReadOnlyList<SectionEntry> Sections => _sections;

  /// <summary>The 8-byte ASCII signature, for example <c>MFSM 2.0</c>.</summary>
  public string Signature { get; private set; } = "";

  /// <summary>True once the <c>MFSM</c> prefix has been verified.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Compatibility view of the first eight bytes after the signature, matching
  /// the value older CompressionWorkbench builds exposed under this incorrect
  /// name. MooseFS does not define this field as a file-id counter.
  /// </summary>
  [Obsolete("MooseFS metadata has no file-id counter at this location; use MetadataVersion, MetaId, MaxNodeId and NextSessionId.")]
  public ulong? FileIdCounter
    => _data.Length >= SignatureSize + 8
      ? BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(SignatureSize, 8))
      : null;

  /// <summary>
  /// Packed file-format version (<c>0x16</c> for 1.6, <c>0x20</c> for 2.0),
  /// or <c>null</c> for the special <c>MFSM NEW</c> bootstrap image.
  /// </summary>
  public byte? FileFormatVersion { get; private set; }

  /// <summary>Whether this is MooseFS's official eight-byte empty bootstrap image.</summary>
  public bool IsEmptyBootstrap { get; private set; }

  /// <summary>
  /// Maximum node id from the pre-2.0 metadata header. Not present in 2.0+.
  /// </summary>
  public uint? MaxNodeId { get; private set; }

  /// <summary>Metadata/changelog version stored in the master metadata header.</summary>
  public ulong? MetadataVersion { get; private set; }

  /// <summary>
  /// Next session id from the pre-2.0 metadata header. Not present in 2.0+.
  /// </summary>
  public uint? NextSessionId { get; private set; }

  /// <summary>Metadata instance id stored by 2.0+ images.</summary>
  public ulong? MetaId { get; private set; }

  /// <summary>The exact image size consumed by this reader.</summary>
  public long ImageSize => _data.LongLength;

  /// <summary>
  /// Human-readable parse result: <c>ok</c>, <c>header-only</c>,
  /// <c>truncated</c>, <c>trailing-data</c>, or <c>unsupported-header</c>.
  /// </summary>
  public string ParseStatus { get; private set; } = "unsupported-header";

  /// <summary>Initializes a reader over one MooseFS metadata image.</summary>
  public MooseFsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SignatureSize)
      throw new InvalidDataException("MooseFS: file too small for master metadata signature.");

    if (!_data.AsSpan(0, MasterTag.Length).SequenceEqual(MasterTag))
      throw new InvalidDataException("MooseFS: missing 'MFSM' tag at offset 0.");

    this.ValidHeader = true;
    this.Signature = Encoding.ASCII.GetString(_data, 0, SignatureSize);

    if (this.Signature == "MFSM NEW") {
      this.IsEmptyBootstrap = true;
      this.ParseStatus = _data.Length == SignatureSize ? "ok" : "trailing-data";
      BuildEntries();
      return;
    }

    if (!TryParseFileFormatVersion(_data.AsSpan(0, SignatureSize), out var fileVersion)) {
      this.ParseStatus = "unsupported-header";
      BuildEntries();
      return;
    }

    this.FileFormatVersion = fileVersion;
    if (_data.Length < SignatureSize + MetadataHeaderSize) {
      this.ParseStatus = "header-only";
      BuildEntries();
      return;
    }

    ParseMetadataHeader(fileVersion);

    if (fileVersion < 0x16)
      ParseLegacyBody();
    else
      WalkSections();

    BuildEntries();
  }

  private void ParseMetadataHeader(byte fileVersion) {
    var header = _data.AsSpan(SignatureSize, MetadataHeaderSize);
    if (fileVersion >= 0x20) {
      this.MetadataVersion = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
      this.MetaId = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
      return;
    }

    this.MaxNodeId = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
    this.MetadataVersion = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(4, 8));
    this.NextSessionId = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
  }

  private void ParseLegacyBody() {
    // Before metadata format 1.6 MooseFS did not wrap NODE/EDGE/etc. in the
    // modern 16-byte section envelope. The authoritative checker only relies
    // on the final 16 zero bytes, so preserve the whole legacy body as opaque
    // metadata instead of guessing record boundaries.
    var minimumLength = SignatureSize + MetadataHeaderSize + 16;
    if (_data.Length < minimumLength) {
      this.ParseStatus = "truncated";
      return;
    }

    this.ParseStatus = _data.AsSpan(_data.Length - 16, 16).IndexOfAnyExcept((byte)0) < 0
      ? "ok"
      : "truncated";
  }

  private void WalkSections() {
    var offset = SignatureSize + MetadataHeaderSize;
    while (offset + SectionHeaderSize <= _data.Length) {
      if (_data.AsSpan(offset, EofMarker.Length).SequenceEqual(EofMarker)) {
        this.ParseStatus = offset + EofMarker.Length == _data.Length ? "ok" : "trailing-data";
        return;
      }

      var tagBytes = _data.AsSpan(offset, 8);
      if (!IsPlausibleSectionTag(tagBytes)) {
        this.ParseStatus = "truncated";
        return;
      }

      var length64 = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(offset + 8, 8));
      if (length64 > long.MaxValue) {
        this.ParseStatus = "truncated";
        return;
      }

      var length = (long)length64;
      var payloadOffset = (long)offset + SectionHeaderSize;
      if (length > _data.LongLength - payloadOffset) {
        this.ParseStatus = "truncated";
        return;
      }

      var tag = Encoding.ASCII.GetString(tagBytes);
      _sections.Add(new SectionEntry(tag, payloadOffset, length));
      offset = checked((int)(payloadOffset + length));
    }

    this.ParseStatus = "truncated";
  }

  private void BuildEntries() {
    var metadata = BuildMetadata();
    _entries.Add(new MooseFsEntry {
      Name = "metadata.ini",
      Size = metadata.Length,
      IsDirectory = false,
      Offset = 0,
      Data = metadata,
    });
    _entries.Add(new MooseFsEntry {
      Name = "moosefs-master.bin",
      Size = _data.Length,
      IsDirectory = false,
      Offset = 0,
      Data = _data,
    });

    foreach (var section in _sections) {
      if (section.Length <= 0 || section.Length > MaxInMemorySection)
        continue;
      if (section.Offset < 0 || section.Offset + section.Length > _data.LongLength)
        continue;

      var payload = _data.AsSpan((int)section.Offset, (int)section.Length).ToArray();
      _entries.Add(new MooseFsEntry {
        Name = $"section_{SanitiseSectionName(section.Tag)}.bin",
        Size = payload.Length,
        IsDirectory = false,
        Offset = section.Offset,
        Data = payload,
      });
    }
  }

  private static bool TryParseFileFormatVersion(ReadOnlySpan<byte> signature, out byte version) {
    version = 0;
    if (signature.Length != SignatureSize
        || !signature[..5].SequenceEqual("MFSM "u8)
        || signature[5] is < (byte)'1' or > (byte)'9'
        || signature[6] != (byte)'.'
        || signature[7] is < (byte)'0' or > (byte)'9')
      return false;

    version = (byte)(((signature[5] - (byte)'0') << 4) | (signature[7] - (byte)'0'));
    return true;
  }

  private static bool IsPlausibleSectionTag(ReadOnlySpan<byte> tag) {
    if (tag.Length != 8 || tag[4] != (byte)' ' || tag[6] != (byte)'.')
      return false;

    for (var i = 0; i < 4; ++i)
      if (tag[i] is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'0' and <= (byte)'9'))
        return false;

    return tag[5] is >= (byte)'0' and <= (byte)'9'
        && tag[7] is >= (byte)'0' and <= (byte)'9';
  }

  private static string SanitiseSectionName(string tag) {
    var sb = new StringBuilder(tag.Length);
    foreach (var c in tag) {
      if (char.IsLetterOrDigit(c))
        sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_')
        sb.Append('_');
    }
    var result = sb.ToString().TrimEnd('_');
    return result.Length == 0 ? "unnamed" : result;
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={this.ParseStatus}\n");
    bldr.Append("format=MooseFS master metadata\n");
    bldr.Append(CultureInfo.InvariantCulture, $"signature={this.Signature}\n");
    bldr.Append("magic_tag=MFSM\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    if (this.IsEmptyBootstrap)
      bldr.Append("empty_bootstrap=true\n");
    if (this.FileFormatVersion is byte version)
      bldr.Append(CultureInfo.InvariantCulture,
        $"file_format_version={version >> 4}.{version & 0x0F}\n");
    if (this.MaxNodeId.HasValue)
      bldr.Append(CultureInfo.InvariantCulture, $"max_node_id={this.MaxNodeId.Value}\n");
    if (this.MetadataVersion.HasValue)
      bldr.Append(CultureInfo.InvariantCulture, $"metadata_version={this.MetadataVersion.Value}\n");
    if (this.NextSessionId.HasValue)
      bldr.Append(CultureInfo.InvariantCulture, $"next_session_id={this.NextSessionId.Value}\n");
    if (this.MetaId.HasValue)
      bldr.Append(CultureInfo.InvariantCulture, $"meta_id={this.MetaId.Value}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"section_count={_sections.Count}\n");
    for (var i = 0; i < _sections.Count; ++i) {
      var section = _sections[i];
      bldr.Append(CultureInfo.InvariantCulture,
        $"section[{i}]={section.Tag} offset={section.Offset} length={section.Length}\n");
    }
    bldr.Append("note=Partial metadata-image view only. NODE/EDGE/CHNK body decoding is version-specific; ");
    bldr.Append("file content lives on chunk servers and is not reachable from metadata.mfs alone.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>Returns the bytes backing a surfaced synthetic entry.</summary>
  public byte[] Extract(MooseFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() { }

  /// <summary>One walked section from a 1.6+ master metadata stream.</summary>
  /// <param name="Tag">Eight-byte section tag, for example <c>NODE 1.0</c>.</param>
  /// <param name="Offset">Byte offset of the section payload.</param>
  /// <param name="Length">Payload length in bytes.</param>
  public readonly record struct SectionEntry(string Tag, long Offset, long Length);
}
