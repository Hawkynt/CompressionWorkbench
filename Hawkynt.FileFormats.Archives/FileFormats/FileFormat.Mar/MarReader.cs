using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mar;

/// <summary>One file of a Mozilla ARchive: where its bytes sit, how many there are, and its permission bits.</summary>
public sealed record MarEntry(string Name, uint Offset, uint Size, uint Flags);

/// <summary>A signature block entry (algorithm 1 = RSA-PKCS1-SHA1, 2 = RSA-PKCS1-SHA384).</summary>
public sealed record MarSignature(uint AlgorithmId, byte[] Data);

/// <summary>An additional-section block entry; id 1 is the product-information block.</summary>
public sealed record MarAdditionalSection(uint Id, byte[] Data) {
  /// <summary>Product-information block id.</summary>
  public const uint ProductInformationId = 1;
}

/// <summary>
/// Reads a Mozilla ARchive (MAR), the container of Firefox/Thunderbird update packages.
/// </summary>
/// <remarks>
/// Layout (all integers big-endian), per <c>https://wiki.mozilla.org/Software_Update:MAR</c> and
/// Mozilla's <c>modules/libmar</c>:
/// <code>
/// "MAR1"  UInt32 indexOffset
/// [UInt64 fileSize  UInt32 sigCount  {UInt32 algorithm UInt32 size byte[size]}*]   (signed-era MARs)
/// [UInt32 sectionCount {UInt32 size(incl. these 8) UInt32 id byte[size-8]}*]        (signed-era MARs)
/// file data …
/// index: UInt32 indexSize  {UInt32 offset UInt32 size UInt32 flags  name\0}*
/// </code>
/// Old MARs omit both optional blocks; as Mozilla's own readers do, their presence is inferred from
/// where the first file's data starts. Member bytes are returned exactly as stored — update
/// packages hold each file bzip2- or xz-compressed, and like <c>mar -x</c> we do not unwrap that.
/// </remarks>
public sealed class MarReader {

  /// <summary>The "MAR1" magic.</summary>
  public static ReadOnlySpan<byte> Magic => "MAR1"u8;

  private readonly Stream _stream;

  public MarReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var length = stream.Length;
    Span<byte> head = stackalloc byte[8];
    stream.Position = 0;
    stream.ReadExactly(head);
    if (!head[..4].SequenceEqual(Magic))
      throw new InvalidDataException("Not a MAR archive (missing MAR1 magic).");
    this.IndexOffset = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
    if (this.IndexOffset < 8 || this.IndexOffset > length - 4)
      throw new InvalidDataException($"MAR index offset {this.IndexOffset} lies outside the file.");

    stream.Position = this.IndexOffset;
    var indexSize = ReadUInt32();
    if (indexSize > length - this.IndexOffset - 4)
      throw new InvalidDataException("MAR index overruns the file.");
    var index = new byte[indexSize];
    stream.ReadExactly(index);

    var entries = new List<MarEntry>();
    var pos = 0;
    while (pos < index.Length) {
      if (index.Length - pos < 13)
        throw new InvalidDataException("Truncated MAR index entry.");
      var offset = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(pos));
      var size = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(pos + 4));
      var flags = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(pos + 8));
      pos += 12;
      var end = Array.IndexOf(index, (byte)0, pos);
      if (end < 0)
        throw new InvalidDataException("Unterminated MAR index entry name.");
      var name = Encoding.UTF8.GetString(index, pos, end - pos);
      pos = end + 1;
      if (offset < 8 || (ulong)offset + size > this.IndexOffset)
        throw new InvalidDataException($"MAR entry '{name}' points outside the data area.");
      entries.Add(new MarEntry(name, offset, size, flags));
    }
    this.Entries = entries;

    // Optional blocks: present when the earliest file data starts beyond the 8-byte header.
    var dataOffset = entries.Count == 0 ? this.IndexOffset : Math.Max(8u, entries.Min(e => e.Offset));
    if (dataOffset <= 8)
      return;

    stream.Position = 8;
    if (dataOffset < 20)
      throw new InvalidDataException("MAR data starts inside the signature block header.");
    this.DeclaredFileSize = ReadUInt64();
    var sigCount = ReadUInt32();
    var sigs = new List<MarSignature>();
    for (var i = 0u; i < sigCount; ++i) {
      if (stream.Position + 8 > dataOffset)
        throw new InvalidDataException("MAR signature block overruns the file data.");
      var algorithm = ReadUInt32();
      var size = ReadUInt32();
      if (stream.Position + size > dataOffset)
        throw new InvalidDataException("MAR signature overruns the file data.");
      var data = new byte[size];
      stream.ReadExactly(data);
      sigs.Add(new MarSignature(algorithm, data));
    }
    this.Signatures = sigs;

    if (stream.Position + 4 > dataOffset)
      return;
    var sectionCount = ReadUInt32();
    var sections = new List<MarAdditionalSection>();
    for (var i = 0u; i < sectionCount; ++i) {
      if (stream.Position + 8 > dataOffset)
        throw new InvalidDataException("MAR additional-section block overruns the file data.");
      var size = ReadUInt32();
      var id = ReadUInt32();
      if (size < 8 || stream.Position + size - 8 > dataOffset)
        throw new InvalidDataException("MAR additional section overruns the file data.");
      var data = new byte[size - 8];
      stream.ReadExactly(data);
      sections.Add(new MarAdditionalSection(id, data));
    }
    this.AdditionalSections = sections;
  }

  /// <summary>Offset of the index.</summary>
  public uint IndexOffset { get; }

  /// <summary>Files in index order.</summary>
  public IReadOnlyList<MarEntry> Entries { get; }

  /// <summary>Whether the archive carries the signature/additional-section header blocks.</summary>
  public bool HasSignatureBlock => this.DeclaredFileSize.HasValue;

  /// <summary>The total file size the signature block declares, when present.</summary>
  public ulong? DeclaredFileSize { get; }

  /// <summary>Signatures; empty for unsigned or old-style archives.</summary>
  public IReadOnlyList<MarSignature> Signatures { get; } = [];

  /// <summary>Additional sections (product information and any unknown ones), in file order.</summary>
  public IReadOnlyList<MarAdditionalSection> AdditionalSections { get; } = [];

  /// <summary>Channel and product version of the product-information block, when present.</summary>
  public (string Channel, string ProductVersion)? ProductInformation {
    get {
      var block = this.AdditionalSections.FirstOrDefault(s => s.Id == MarAdditionalSection.ProductInformationId);
      if (block == null)
        return null;
      var parts = Encoding.ASCII.GetString(block.Data).Split('\0');
      return parts.Length >= 2 ? (parts[0], parts[1]) : null;
    }
  }

  /// <summary>Returns a member's stored bytes.</summary>
  public byte[] Extract(MarEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var data = new byte[entry.Size];
    this._stream.Position = entry.Offset;
    this._stream.ReadExactly(data);
    return data;
  }

  /// <summary>
  /// Names the compression the update tooling applied to a member ("xz", "bzip2") by its magic,
  /// or "stored" — informational only: extraction always returns the stored bytes.
  /// </summary>
  public static string DescribeStoredData(ReadOnlySpan<byte> head)
    => head.StartsWith((ReadOnlySpan<byte>)[0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00]) ? "xz"
      : head.StartsWith("BZh"u8) ? "bzip2"
      : "stored";

  private uint ReadUInt32() {
    Span<byte> b = stackalloc byte[4];
    this._stream.ReadExactly(b);
    return BinaryPrimitives.ReadUInt32BigEndian(b);
  }

  private ulong ReadUInt64() {
    Span<byte> b = stackalloc byte[8];
    this._stream.ReadExactly(b);
    return BinaryPrimitives.ReadUInt64BigEndian(b);
  }
}
