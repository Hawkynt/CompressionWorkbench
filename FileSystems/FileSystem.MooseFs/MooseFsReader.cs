#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.MooseFs;

/// <summary>
/// Partial R/O reader for MooseFS master-metadata images (<c>metadata.mfs</c>).
/// MooseFS is a fault-tolerant distributed FS — the master server keeps the
/// namespace + chunk-server topology in a single binary metadata file, while
/// file data lives on chunk servers. This reader understands the master
/// metadata's outer envelope:
/// <list type="bullet">
///   <item>8-byte ASCII signature (e.g. <c>MFSM 2.0</c>, <c>MFSM 1.6</c>,
///         <c>MFSM 1.5</c>, <c>MFSM 1.4</c>, <c>MFSM NEW</c>).</item>
///   <item>For 1.6+ images: two 8-byte big-endian counters
///         (file-id counter, metadata version) immediately after the signature.</item>
///   <item>Sequence of sections. Each section: 8-byte ASCII type tag
///         (<c>SESS 1.0</c>, <c>STAT 1.0</c>, <c>NODE 1.0</c>, <c>EDGE 1.0</c>,
///         <c>FREE 1.0</c>, <c>XATR 1.0</c>, <c>CHNK 1.0</c>, <c>OPEN 1.0</c>,
///         <c>FLCK 1.0</c>, <c>QUOT 1.0</c>, <c>ACLS 1.0</c>, …) + 8-byte
///         big-endian payload length + that many payload bytes.</item>
///   <item>Final 16-byte terminator <c>[MFS EOF MARKER]</c>.</item>
/// </list>
///
/// <para>
/// The reader walks the <em>section index</em> only — it does not attempt to
/// decode NODE / EDGE record bodies, which differ between MooseFS minor
/// versions and require ground-truth golden samples to validate. NODE/EDGE
/// would give path tree + inode metadata; CHNK gives chunk-id mappings. None
/// of those by themselves yield file content — MooseFS data lives on chunk
/// servers and is only reachable via the live MooseFS protocol. Therefore
/// the reader exposes:
/// </para>
/// <list type="bullet">
///   <item><c>metadata.ini</c> — human-readable summary of header + section
///         table (name, payload offset, payload length).</item>
///   <item><c>moosefs-master.bin</c> — the raw image, byte-for-byte.</item>
///   <item><c>section_&lt;NAME&gt;.bin</c> — the raw payload bytes of each
///         section the index walk surfaced (NODE, EDGE, CHNK, …). Useful for
///         offline forensics; we make no claim about their internal
///         structure.</item>
/// </list>
///
/// <para>
/// If section-walk fails (signature past the 8-byte tag is not recognised,
/// a section length runs past EOF, the EOF marker is missing, …), the
/// reader falls back to a header-only surface (metadata.ini + raw) and
/// records the parse failure in <c>metadata.ini</c>'s <c>parse_status</c>
/// field. This is the honest "we recognise the envelope but couldn't walk
/// the contents" mode rather than silently inventing entries.
/// </para>
/// </summary>
public sealed class MooseFsReader : IDisposable {

  /// <summary>MooseFS master metadata 4-byte prefix: ASCII "MFSM".</summary>
  public static readonly byte[] MasterTag = "MFSM"u8.ToArray();

  /// <summary>16-byte MooseFS end-of-file marker following the last section.</summary>
  public static readonly byte[] EofMarker = "[MFS EOF MARKER]"u8.ToArray();

  private const int HeaderSize = 8;
  // After the 8-byte signature, modern (1.6+) images have two BE uint64s
  // followed by the section stream. Section tag + length = 16 bytes.
  private const int SectionTagSize = 8;
  private const int SectionLenSize = 8;
  // Section payloads can be huge on a real cluster — cap what we'll hold in
  // memory as a synthetic per-section entry. Larger sections still show up
  // in metadata.ini's table but their raw payload is not surfaced.
  private const long MaxInMemorySection = 64L * 1024 * 1024;

  private readonly byte[] _data;
  private readonly List<MooseFsEntry> _entries = [];
  private readonly List<SectionEntry> _sections = [];

  /// <summary>Listing of every entry this image surfaces.</summary>
  public IReadOnlyList<MooseFsEntry> Entries => _entries;

  /// <summary>Section index walked from the master metadata stream.</summary>
  public IReadOnlyList<SectionEntry> Sections => _sections;

  /// <summary>The 8-byte ASCII signature at offset 0 (e.g. <c>"MFSM 2.0"</c>).</summary>
  public string Signature { get; private set; } = "";

  /// <summary>True when the 4-byte <c>MFSM</c> tag was present at offset 0.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// File-id counter from the modern (1.6+) post-signature header, or
  /// <c>null</c> when the image is too short or pre-1.6.
  /// </summary>
  public ulong? FileIdCounter { get; private set; }

  /// <summary>
  /// Metadata version counter from the modern (1.6+) post-signature header,
  /// or <c>null</c> when the image is too short or pre-1.6.
  /// </summary>
  public ulong? MetadataVersion { get; private set; }

  /// <summary>
  /// Human-readable description of how the section walk terminated:
  /// <c>"ok"</c> (full walk + EOF marker), <c>"truncated"</c> (section walk
  /// stopped before EOF marker), <c>"header-only"</c> (image too short for
  /// any sections), or <c>"unsupported-header"</c> (no MFSM tag).
  /// </summary>
  public string ParseStatus { get; private set; } = "unsupported-header";

    /// <summary>
  /// Initializes a new instance of <see cref="MooseFsReader"/>.
  /// </summary>
public MooseFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException("MooseFS: file too small for master metadata header.");

    if (!_data.AsSpan(0, 4).SequenceEqual(MasterTag))
      throw new InvalidDataException("MooseFS: missing 'MFSM' tag at offset 0.");

    this.ValidHeader = true;
    this.Signature = Encoding.ASCII.GetString(_data, 0, 8).TrimEnd('\0');

    // Modern (1.6+) images carry two BE uint64s right after the 8-byte
    // signature. Older 1.4/1.5 images stream sections immediately. We pull
    // the counters when the image is long enough; downstream code does not
    // assume they're meaningful for non-1.6+ signatures.
    if (_data.Length >= HeaderSize + 16) {
      this.FileIdCounter = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(HeaderSize, 8));
      this.MetadataVersion = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(HeaderSize + 8, 8));
    }

    WalkSections();

    var meta = BuildMetadata();
    _entries.Add(new MooseFsEntry {
      Name = "metadata.ini",
      Size = meta.Length,
      IsDirectory = false,
      Offset = 0,
      Data = meta,
    });
    _entries.Add(new MooseFsEntry {
      Name = "moosefs-master.bin",
      Size = _data.Length,
      IsDirectory = false,
      Offset = 0,
      Data = _data,
    });

    foreach (var s in _sections) {
      // Bound the per-section payload we materialise so a 4-GB CHNK section
      // does not allocate 4 GB of synthetic-entry buffer. The section still
      // appears in metadata.ini's table — we just refuse to mirror the
      // payload as a separate entry. The full raw image stays accessible
      // via moosefs-master.bin.
      if (s.Length <= 0 || s.Length > MaxInMemorySection)
        continue;
      // Defensive: the walker already bounds Offset+Length to _data.Length,
      // but re-check before slicing to guard against any future refactor.
      if (s.Offset < 0 || s.Offset + s.Length > _data.Length)
        continue;
      var payload = _data.AsSpan((int)s.Offset, (int)s.Length).ToArray();
      _entries.Add(new MooseFsEntry {
        Name = $"section_{SanitiseSectionName(s.Tag)}.bin",
        Size = payload.Length,
        IsDirectory = false,
        Offset = s.Offset,
        Data = payload,
      });
    }
  }

  private void WalkSections() {
    // Section stream begins after signature for 1.4/1.5, after signature + 16
    // counter bytes for 1.6+. We pick the offset by checking signature
    // version: anything other than the 1.4/1.5 strings gets the modern
    // offset. This still correctly rejects malformed images because the
    // first section tag must be all-ASCII printable.
    var sectionStart = HeaderSize;
    if (this.Signature is not ("MFSM 1.4" or "MFSM 1.5") && _data.Length >= HeaderSize + 16)
      sectionStart = HeaderSize + 16;

    if (_data.Length < sectionStart + SectionTagSize + SectionLenSize) {
      this.ParseStatus = "header-only";
      return;
    }

    var offset = sectionStart;
    while (offset + SectionTagSize + SectionLenSize <= _data.Length) {
      // EOF marker is 16 bytes [MFS EOF MARKER] and is NOT followed by a
      // length field — once we see it, the walk is complete.
      if (offset + EofMarker.Length <= _data.Length
          && _data.AsSpan(offset, EofMarker.Length).SequenceEqual(EofMarker)) {
        this.ParseStatus = "ok";
        return;
      }

      var tagBytes = _data.AsSpan(offset, SectionTagSize);
      if (!IsPlausibleSectionTag(tagBytes)) {
        // Section walk derailed. Keep what we have, mark truncated. We do
        // not throw — header-level surfaces remain useful even if section
        // walk failed (older / future MooseFS versions, partially-written
        // images, dump tools that strip framing, …).
        this.ParseStatus = "truncated";
        return;
      }
      var tag = Encoding.ASCII.GetString(tagBytes).TrimEnd();
      var lenU64 = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(offset + SectionTagSize, SectionLenSize));
      // A single section larger than long.MaxValue would mean the image is
      // pathological or this isn't really a section header — treat as walk
      // failure rather than overflowing int math.
      if (lenU64 > long.MaxValue) {
        this.ParseStatus = "truncated";
        return;
      }
      var len = (long)lenU64;
      var payloadOffset = (long)offset + SectionTagSize + SectionLenSize;
      if (payloadOffset + len > _data.Length) {
        // Section claims more bytes than the image has — truncated dump or
        // bogus length. Record what we got and stop.
        this.ParseStatus = "truncated";
        return;
      }

      _sections.Add(new SectionEntry(tag, payloadOffset, len));
      offset = (int)(payloadOffset + len);
    }

    // Walked past the last possible section header but never saw the EOF
    // marker — image is truncated. The accumulated sections are still real.
    this.ParseStatus = "truncated";
  }

  private static bool IsPlausibleSectionTag(ReadOnlySpan<byte> tagBytes) {
    // A valid MooseFS section tag is 8 ASCII bytes: 4-char family
    // ("SESS", "NODE", "EDGE", "CHNK", "FREE", "XATR", "STAT", "OPEN",
    // "FLCK", "QUOT", "ACLS", …), a space, then a version string like
    // "1.0" or "2.0". We sanity-check that every byte is printable ASCII —
    // this rejects random binary that just happens to be at the right
    // offset without locking us to a fixed family allow-list (MooseFS
    // adds new section types between minor versions).
    foreach (var b in tagBytes)
      if (b is < 0x20 or > 0x7E)
        return false;
    return true;
  }

  private static string SanitiseSectionName(string tag) {
    // Section tags include a space ("NODE 1.0") which makes for awkward
    // file names. Collapse whitespace runs to a single underscore and
    // drop any non-alphanumeric tail so the synthetic entry names stay
    // POSIX-portable and round-trip through Extract → WriteFile.
    var sb = new StringBuilder(tag.Length);
    foreach (var c in tag) {
      if (char.IsLetterOrDigit(c))
        sb.Append(c);
      else if (sb.Length > 0 && sb[^1] != '_')
        sb.Append('_');
    }
    var s = sb.ToString().TrimEnd('_');
    return s.Length == 0 ? "unnamed" : s;
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={this.ParseStatus}\n");
    bldr.Append("format=MooseFS master metadata\n");
    bldr.Append(CultureInfo.InvariantCulture, $"signature={this.Signature}\n");
    bldr.Append("magic_tag=MFSM\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    if (this.FileIdCounter.HasValue)
      bldr.Append(CultureInfo.InvariantCulture, $"file_id_counter={this.FileIdCounter.Value}\n");
    if (this.MetadataVersion.HasValue)
      bldr.Append(CultureInfo.InvariantCulture, $"metadata_version={this.MetadataVersion.Value}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"section_count={_sections.Count}\n");
    for (var i = 0; i < _sections.Count; i++) {
      var s = _sections[i];
      bldr.Append(CultureInfo.InvariantCulture,
        $"section[{i}]={s.Tag} offset={s.Offset} length={s.Length}\n");
    }
    bldr.Append("note=Partial R/O — outer envelope (signature + section index) ");
    bldr.Append("only. NODE/EDGE/CHNK body decoding is version-specific and ");
    bldr.Append("requires golden samples to validate honestly. File content ");
    bldr.Append("lives on chunk servers and is not reachable from this image.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>Returns the bytes that back the given entry (in-memory).</summary>
  public byte[] Extract(MooseFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }

  /// <summary>One walked section from the master metadata stream.</summary>
  /// <param name="Tag">The 8-byte ASCII tag (e.g. <c>"NODE 1.0"</c>), trimmed.</param>
  /// <param name="Offset">Byte offset of the section payload (after the 16-byte tag+length).</param>
  /// <param name="Length">Length of the section payload in bytes.</param>
  public readonly record struct SectionEntry(string Tag, long Offset, long Length);
}
