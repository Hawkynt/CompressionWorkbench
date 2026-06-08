#pragma warning disable CS1591
namespace FileFormat.Macrium;

/// <summary>
/// A single Macrium Reflect X metadata block, as parsed from the chain that
/// starts at the offset stored in the file footer.
/// <para>
/// On-disk layout per <see href="https://github.com/macrium/mrimgx_file_layout"/>:
/// </para>
/// <list type="bullet">
///   <item><description>Bytes 0..7: ASCII <c>block_name</c> (e.g. <c>"$JSON   "</c>, <c>"$INDEX  "</c>).</description></item>
///   <item><description>Bytes 8..11: <c>uint32</c> little-endian payload length.</description></item>
///   <item><description>Bytes 12..27: 16-byte MD5 hash of the (decompressed / decrypted) payload.</description></item>
///   <item><description>Byte 28: flags — bit 0 = <c>last_block</c>, bit 1 = <c>compression</c>, bit 2 = <c>encryption</c>, bits 3..7 reserved.</description></item>
///   <item><description>Bytes 29..31: padding for 32-byte header alignment.</description></item>
/// </list>
/// </summary>
public sealed class MacriumBlock {
  /// <summary>ASCII block name with trailing spaces stripped (e.g. <c>"$JSON"</c>, <c>"$INDEX"</c>, <c>"$AUXDATA"</c>).</summary>
  public string Name { get; init; } = "";

  /// <summary>Absolute byte offset of the 32-byte block header within the file.</summary>
  public long HeaderOffset { get; init; }

  /// <summary>Absolute byte offset of the block payload (<see cref="HeaderOffset"/> + 32).</summary>
  public long PayloadOffset { get; init; }

  /// <summary>Payload length in bytes, as declared in the block header.</summary>
  public long PayloadLength { get; init; }

  /// <summary>16-byte MD5 hash of the payload (decompressed / decrypted form, per spec).</summary>
  public byte[] Md5Hash { get; init; } = [];

  /// <summary>Raw flags byte from the header.</summary>
  public byte Flags { get; init; }

  /// <summary>True when bit 0 (<c>last_block</c>) is set; terminates the chain walk.</summary>
  public bool IsLast { get; init; }

  /// <summary>True when bit 1 (<c>compression</c>) is set — payload is zstd-compressed.</summary>
  public bool IsCompressed { get; init; }

  /// <summary>True when bit 2 (<c>encryption</c>) is set — payload is AES-CBC encrypted.</summary>
  public bool IsEncrypted { get; init; }
}
