#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.Crypto;
using Compression.Registry;

namespace FileFormat.Uif;

/// <summary>
/// A MagicISO UIF image. A 64-byte <c>bbis</c> trailer ends the file. It points at a <c>blhr</c> block
/// table (zlib-compressed when flagged); each 24-byte entry places a run of sectors in the output and
/// says how it is stored: copied, zero-filled or zlib-compressed. A mixed-mode image (type 9) follows the
/// table with a <c>blms</c> track layout and a <c>blss</c> block holding the original cue/mds/ccd
/// descriptor. A password-protected image puts a <c>bsdr</c> block (the DES-encrypted real trailer) in
/// front of the table and DES-encrypts the table and every stored run. Layout in <c>docs/UIF-ON-DISK.md</c>.
/// Only what real files proved is read: version 1 and 2, types 8 and 9, plain or password-protected.
/// LZMA-compressed version 3/4 images and the two key-in-trailer encryptions are refused.
/// </summary>
public sealed class UifImage {
  public const int TrailerSize = 64;
  public const int EntrySize = 24;
  /// <summary>The <c>bsdr</c> block: signature, size, then the 64-byte encrypted trailer.</summary>
  public const int PasswordBlockSize = 8 + TrailerSize;
  /// <summary>MagicISO takes at most 31 password characters.</summary>
  public const int MaxPasswordLength = 31;

  /// <summary>One run of output sectors.</summary>
  public sealed record Block(ulong Offset, uint StoredSize, uint Sector, uint Sectors, uint Type);

  /// <summary>The image format a type-9 UIF was made from, as its <c>blss</c> block records it.</summary>
  public enum SourceKind : uint { Iso = 0, Cue = 1, Mds = 2, Ccd = 3, Nrg = 4 }

  public enum PartKind { Image, Descriptor, Subchannel }

  /// <summary>One file the image decodes to.</summary>
  public sealed record Part(string Name, long Size, PartKind Kind);

  public ushort Version { get; private init; }
  public ushort ImageType { get; private init; }
  public uint Sectors { get; private init; }
  public uint SectorSize { get; private init; }
  public uint LastDiff { get; private init; }
  public byte[] Hash { get; private init; } = [];
  public IReadOnlyList<Block> Blocks { get; private init; } = [];
  /// <summary>True when the image is password-protected (a <c>bsdr</c> block).</summary>
  public bool IsEncrypted { get; private init; }
  /// <summary>True when the image is protected and no password was given: only the trailer is known.</summary>
  public bool NeedsPassword => this.IsEncrypted && this._cipher is null;
  public SourceKind Source { get; private init; }
  /// <summary>The original image and descriptor names MagicISO stored (type 9), or null.</summary>
  public string? StoredImageName { get; private init; }
  public string? StoredDescriptorName { get; private init; }
  /// <summary>The original descriptor bytes (type 9; for CloneCD both lengths, the .ccd and the .sub).</summary>
  public byte[] Descriptor { get; private init; } = [];
  /// <summary>The decoded <c>blms</c> track layout (type 9).</summary>
  public byte[] TrackLayout { get; private init; } = [];
  public IReadOnlyList<Part> Parts { get; private set; } = [];

  private TripleDes? _cipher;

  /// <summary>Bytes of the image: all sectors, less the unused tail of the last one when the trailer says so.</summary>
  public ulong Length => (ulong)this.Sectors * this.SectorSize - (this.LastDiff == 0 ? 0 : this.SectorSize - this.LastDiff);

  /// <exception cref="InvalidDataException">Not a UIF image, or inconsistent.</exception>
  /// <exception cref="NotSupportedException">A version, type or encryption no sample proves.</exception>
  /// <exception cref="ArchivePasswordException">A password was given and is wrong.</exception>
  public static UifImage Read(Stream file, string? password = null) {
    var length = file.Length;
    if (length < TrailerSize) throw new InvalidDataException("UIF: too short for a trailer.");
    var outer = ReadAt(file, length - TrailerSize, TrailerSize);
    if (!outer.AsSpan(0, 4).SequenceEqual("bbis"u8)) throw new InvalidDataException("UIF: missing 'bbis' trailer.");
    var t = outer;
    var tableAt = BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(28));
    if (tableAt + 16 > (ulong)length) throw new InvalidDataException("UIF: the block table lies outside the file.");
    var dataEnd = tableAt;
    var head = ReadAt(file, (long)tableAt, 16);

    TripleDes? cipher = null;
    var encrypted = head.AsSpan(0, 4).SequenceEqual("bsdr"u8);
    if (encrypted) {
      // The real trailer sits DES-encrypted in the bsdr block; the table header follows it in the clear.
      if (tableAt + PasswordBlockSize + 16 > (ulong)length) throw new InvalidDataException("UIF: the password block is cut short.");
      if (password is not null) {
        cipher = new TripleDes(PasswordKey(password));
        var inner = ReadAt(file, (long)tableAt + 8, TrailerSize);
        Crypt(cipher, inner, decrypt: true);
        if (!inner.AsSpan(0, 4).SequenceEqual("bbis"u8)) throw new ArchivePasswordException("UIF: the password is wrong.", passwordWasSupplied: true);
        t = inner;
      }
      tableAt += PasswordBlockSize;
      head = ReadAt(file, (long)tableAt, 16);
    }

    var version = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(8));
    var imageType = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(10));
    var sectors = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(16));
    var sectorSize = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(20));
    var lastDiff = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(24));
    var (key1, key2) = (t[56], t[57]);
    // Key-in-trailer encryption: MagicISO's own cipher cascade, or DES under one of sixteen fixed keys.
    if (key2 == 2 || (version >= 2 && key1 is > 0 and <= 0x10)) throw new NotSupportedException("UIF: key-in-trailer encryption is not supported (no sample proves it).");
    if (version >= 3) throw new NotSupportedException($"UIF: version {version} (LZMA) images are not supported (no sample proves them).");
    if (imageType is not (8 or 9)) throw new NotSupportedException($"UIF: image type {imageType} is not supported (8 is an ISO image, 9 a mixed-mode image).");
    if (sectorSize is 0 or > 1 << 16 || lastDiff >= sectorSize) throw new InvalidDataException("UIF: impossible sector geometry.");

    if (!head.AsSpan(0, 4).SequenceEqual("blhr"u8)) throw new InvalidDataException("UIF: missing 'blhr' block table.");
    var size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
    var compressed = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
    var count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
    if (size < 8 || tableAt + 8 + size > (ulong)length || count > int.MaxValue / EntrySize) throw new InvalidDataException("UIF: the block table is cut short.");

    // Mixed mode: blms (track layout) then blss (source kind + original descriptor) follow the table.
    // Their headers are in the clear, so the source kind is known even without the password.
    Section blms = default, blss = default;
    var source = SourceKind.Iso;
    if (imageType == 9) {
      blms = Section.At(file, tableAt + 8 + size, "blms"u8, 16, length);
      blss = Section.At(file, (ulong)blms.DataAt + blms.Size - 8, "blss"u8, 20, length);
      source = (SourceKind)BinaryPrimitives.ReadUInt32LittleEndian(ReadAt(file, blss.DataAt - 4, 4));
      // CUE, CCD and NRG sources are proven by MagicISO-made images; MDS (and a bare ISO) never came up.
      if (source is not (SourceKind.Cue or SourceKind.Ccd or SourceKind.Nrg))
        throw new NotSupportedException($"UIF: mixed-mode source kind {(uint)source} is not supported (no sample proves it).");
    }

    List<Block> blocks = [];
    byte[] layout = [], descriptor = [];
    string? imageName = null, descriptorName = null;
    if (!encrypted || cipher is not null) {
      var table = Payload(file, cipher, (long)tableAt + 16, size - 8, count * EntrySize, compressed != 0);
      blocks = new List<Block>((int)count);
      for (var i = 0; i < count; ++i) {
        var e = table.AsSpan(i * EntrySize, EntrySize);
        var block = new Block(BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
          BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), BinaryPrimitives.ReadUInt32LittleEndian(e[16..]), BinaryPrimitives.ReadUInt32LittleEndian(e[20..]));
        if (block.Type is not (1 or 3 or 5)) throw new InvalidDataException($"UIF: block {i} has unknown type {block.Type}.");
        if ((ulong)block.Sector + block.Sectors > sectors) throw new InvalidDataException($"UIF: block {i} reaches past sector {sectors}.");
        if (block.Type != 3 && block.Offset + block.StoredSize > dataEnd) throw new InvalidDataException($"UIF: block {i} lies outside the data area.");
        if ((ulong)block.Sectors * sectorSize > int.MaxValue) throw new InvalidDataException($"UIF: block {i} is too large.");
        blocks.Add(block);
      }
      if (imageType == 9) {
        layout = Payload(file, cipher, blms.DataAt, blms.Size - 8, blms.Count, blms.Compressed != 0);
        descriptor = blss.Count == 0 ? [] : Payload(file, cipher, blss.DataAt, blss.Size - 12, blss.Count, blss.Compressed != 0);
        imageName = StoredName(layout, 0x04);
        descriptorName = StoredName(layout, 0x10);
        if (source == SourceKind.Ccd && descriptor.Length > 0 && CloneCdLengths(descriptor) is null)
          throw new InvalidDataException("UIF: the CloneCD descriptor is cut short.");
      }
    }

    var image = new UifImage {
      Version = version, ImageType = imageType, Sectors = sectors, SectorSize = sectorSize, LastDiff = lastDiff,
      Hash = outer.AsSpan(40, 16).ToArray(), IsEncrypted = encrypted, Source = source, Blocks = blocks,
      TrackLayout = layout, Descriptor = descriptor, StoredImageName = imageName, StoredDescriptorName = descriptorName,
      _cipher = cipher,
    };
    image.Parts = image.BuildParts();
    return image;
  }

  /// <summary>The 8-byte DES key of a password: its first 31 bytes, zero-padded to 32, summed as four little-endian 64-bit words.</summary>
  public static byte[] PasswordKey(string password) {
    Span<byte> buffer = stackalloc byte[32];
    buffer.Clear();
    var bytes = Encoding.Latin1.GetBytes(password);
    bytes.AsSpan(0, Math.Min(bytes.Length, MaxPasswordLength)).CopyTo(buffer);
    var sum = 0UL;
    for (var i = 0; i < 4; ++i) sum += BinaryPrimitives.ReadUInt64LittleEndian(buffer[(i * 8)..]);
    var key = new byte[24];
    for (var i = 0; i < 3; ++i) BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(i * 8), sum); // single DES as K1=K2=K3
    return key;
  }

  /// <summary>DES-ECB over every whole 8-byte block; a shorter tail stays as it is.</summary>
  internal static void Crypt(TripleDes cipher, Span<byte> data, bool decrypt) {
    for (var at = 0; at + TripleDes.BlockSize <= data.Length; at += TripleDes.BlockSize)
      if (decrypt) cipher.Decrypt(data.Slice(at, TripleDes.BlockSize));
      else cipher.Encrypt(data.Slice(at, TripleDes.BlockSize));
  }

  private readonly record struct Section(uint Size, uint Compressed, uint Count, long DataAt) {
    /// <summary>Reads a blhr-shaped header (signature, size, compressed flag, count) and checks its data fits.</summary>
    public static Section At(Stream file, ulong at, ReadOnlySpan<byte> signature, int headerSize, long length) {
      if (at + (ulong)headerSize > (ulong)length) throw new InvalidDataException($"UIF: the '{Encoding.ASCII.GetString(signature)}' block is missing.");
      var h = ReadAt(file, (long)at, 16);
      if (!h.AsSpan(0, 4).SequenceEqual(signature)) throw new InvalidDataException($"UIF: missing '{Encoding.ASCII.GetString(signature)}' block.");
      var size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
      var dataSize = (long)size + 8 - headerSize;
      if (dataSize < 0 || at + (ulong)headerSize + (ulong)dataSize > (ulong)length) throw new InvalidDataException($"UIF: the '{Encoding.ASCII.GetString(signature)}' block is cut short.");
      return new Section(size, BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)), (long)at + headerSize);
    }
  }

  /// <summary>Reads a table/blms/blss payload: compressed ones are decrypted and inflated, plain ones taken as they are.</summary>
  private static byte[] Payload(Stream file, TripleDes? cipher, long at, long storedSize, long size, bool compressed) {
    if (size > int.MaxValue || storedSize > int.MaxValue) throw new InvalidDataException("UIF: a header block is too large.");
    if (!compressed) {
      if (size > storedSize) throw new InvalidDataException("UIF: a header block is cut short.");
      return ReadAt(file, at, (int)size);
    }
    var raw = ReadAt(file, at, (int)storedSize);
    if (cipher is not null) Crypt(cipher, raw, decrypt: true);
    var data = Inflate(raw, cipher is not null);
    if (data.Length != size) throw new InvalidDataException($"UIF: a header block holds {data.Length} bytes, not {size}.");
    return data;
  }

  /// <summary>A file name stored in the blms layout at the offset the given field holds, without its directory.</summary>
  private static string? StoredName(byte[] layout, int field) {
    if (layout.Length < 0x40) return null;
    var at = BinaryPrimitives.ReadUInt32LittleEndian(layout.AsSpan(field));
    if (at == 0 || at >= layout.Length) return null;
    var end = Array.IndexOf(layout, (byte)0, (int)at);
    if (end < 0) end = layout.Length;
    var path = Encoding.Latin1.GetString(layout, (int)at, end - (int)at);
    var name = path[(path.LastIndexOfAny(['\\', '/', ':']) + 1)..].Trim();
    return name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ? null : name;
  }

  private static (uint Ccd, uint Sub)? CloneCdLengths(byte[] descriptor) {
    if (descriptor.Length < 8) return null;
    var ccd = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
    var sub = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(4));
    return 8UL + ccd + sub > (ulong)descriptor.Length ? null : (ccd, sub);
  }

  /// <summary>
  /// The files this image decodes to. Type 8 is <c>image.iso</c>. Type 9 keeps the names MagicISO stored, as its
  /// own decompressor writes the descriptor unchanged and the descriptor names the image file; without them
  /// (or before the password is given) it falls back to <c>image.*</c>.
  /// </summary>
  private List<Part> BuildParts() {
    var (imageExt, descriptorExt) = this.Source switch {
      SourceKind.Cue => (".bin", ".cue"),
      SourceKind.Mds => (".mdf", ".mds"),
      SourceKind.Ccd => (".img", ".ccd"),
      SourceKind.Nrg => (".nrg", null),
      _ => (".iso", (string?)null),
    };
    if (this.ImageType == 8) return [new("image.iso", (long)this.Length, PartKind.Image)];
    var imageName = this.StoredImageName ?? "image" + imageExt;
    var descriptorName = descriptorExt is null ? null : this.StoredDescriptorName ?? Path.GetFileNameWithoutExtension(imageName) + descriptorExt;
    if (descriptorName is not null && string.Equals(descriptorName, imageName, StringComparison.OrdinalIgnoreCase)) {
      imageName = "image" + imageExt;
      descriptorName = "image" + descriptorExt;
    }
    List<Part> parts = [new(imageName, (long)this.Length, PartKind.Image)];
    if (descriptorName is null || this.NeedsPassword) {
      if (descriptorName is not null) parts.Add(new(descriptorName, 0, PartKind.Descriptor));
      return parts;
    }
    if (this.Source == SourceKind.Ccd) {
      if (CloneCdLengths(this.Descriptor) is not var (ccd, sub)) return parts;
      parts.Add(new(descriptorName, ccd, PartKind.Descriptor));
      var subName = Path.GetFileNameWithoutExtension(descriptorName) + ".sub";
      if (!string.Equals(subName, imageName, StringComparison.OrdinalIgnoreCase)) parts.Add(new(subName, sub, PartKind.Subchannel));
      return parts;
    }
    if (this.Descriptor.Length > 0) parts.Add(new(descriptorName, this.Descriptor.Length, PartKind.Descriptor));
    return parts;
  }

  /// <summary>The bytes of a descriptor or subchannel part.</summary>
  public byte[] PartBytes(Part part) {
    if (this.NeedsPassword) throw new ArchivePasswordException("UIF: this image is protected by a password.", passwordWasSupplied: false);
    return part.Kind switch {
      PartKind.Descriptor when this.Source == SourceKind.Ccd => CloneCdLengths(this.Descriptor) is var (ccd, _) ? this.Descriptor.AsSpan(8, (int)ccd).ToArray() : [],
      PartKind.Subchannel => CloneCdLengths(this.Descriptor) is var (ccd, sub) ? this.Descriptor.AsSpan(8 + (int)ccd, (int)sub).ToArray() : [],
      PartKind.Descriptor => this.Descriptor,
      _ => throw new ArgumentException("UIF: the image part is written with CopyTo.", nameof(part)),
    };
  }

  /// <summary>Writes the image: every block decoded into place, sectors no block covers left as zeros.</summary>
  /// <exception cref="ArchivePasswordException">The image is protected and was read without its password.</exception>
  public void CopyTo(Stream file, Stream output) {
    if (this.NeedsPassword) throw new ArchivePasswordException("UIF: this image is protected by a password.", passwordWasSupplied: false);
    var start = output.Position;
    foreach (var block in this.Blocks) {
      var bytes = (int)(block.Sectors * this.SectorSize);
      byte[] data;
      if (block.Type == 3) data = new byte[bytes];
      else {
        var stored = ReadAt(file, (long)block.Offset, (int)block.StoredSize);
        if (this._cipher is not null) Crypt(this._cipher, stored, decrypt: true);
        data = block.Type == 1 ? Stored(stored, bytes) : Inflate(stored, this._cipher is not null);
      }
      if (data.Length != bytes) throw new InvalidDataException($"UIF: the block at sector {block.Sector} decodes to {data.Length} bytes, not {bytes}.");
      var at = (ulong)block.Sector * this.SectorSize;
      var keep = (int)Math.Min((ulong)bytes, this.Length - Math.Min(at, this.Length));
      output.Position = start + (long)at;
      output.Write(data, 0, keep);
    }
    output.SetLength(start + (long)this.Length);
  }

  private static byte[] Stored(byte[] data, int bytes) {
    if (data.Length > bytes) throw new InvalidDataException("UIF: a stored block is larger than its sectors.");
    if (data.Length == bytes) return data;
    var padded = new byte[bytes];
    data.CopyTo(padded, 0);
    return padded;
  }

  private static byte[] Inflate(byte[] data, bool decrypted) {
    try {
      return FileFormat.Zlib.ZlibStream.Decompress(data);
    } catch (Exception ex) {
      // With the password checked against the trailer, garbage here means damage, not a wrong password.
      throw new InvalidDataException(decrypted ? "UIF: an encrypted zlib block does not decode." : "UIF: a zlib block does not decode.", ex);
    }
  }

  private static byte[] ReadAt(Stream file, long offset, int count) {
    var buffer = new byte[count];
    file.Position = offset;
    try {
      file.ReadExactly(buffer);
    } catch (EndOfStreamException ex) {
      throw new InvalidDataException("UIF: the image is truncated.", ex);
    }
    return buffer;
  }

  public string RenderMetadata() {
    var sb = new StringBuilder("[Uif]\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={this.Version}\nimage_type={this.ImageType}\nsectors={this.Sectors}\nsector_size={this.SectorSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"last_sector_bytes={this.LastDiff}\nhash={Convert.ToHexStringLower(this.Hash)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"encrypted={(this.IsEncrypted ? "password" : "no")}\n");
    if (this.ImageType == 9) {
      sb.Append(CultureInfo.InvariantCulture, $"source={this.Source.ToString().ToLowerInvariant()}\n");
      if (this.StoredImageName is not null) sb.Append(CultureInfo.InvariantCulture, $"stored_image_name={this.StoredImageName}\n");
      if (this.StoredDescriptorName is not null) sb.Append(CultureInfo.InvariantCulture, $"stored_descriptor_name={this.StoredDescriptorName}\n");
    }
    if (this.NeedsPassword) return sb.Append("blocks=unknown (password needed)\n").ToString();
    sb.Append(CultureInfo.InvariantCulture, $"blocks={this.Blocks.Count}\n");
    foreach (var type in this.Blocks.GroupBy(b => b.Type).OrderBy(g => g.Key))
      sb.Append(CultureInfo.InvariantCulture, $"blocks_type_{type.Key}={type.Count()}\n");
    return sb.ToString();
  }
}
