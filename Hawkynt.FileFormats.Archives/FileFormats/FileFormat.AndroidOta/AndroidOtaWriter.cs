#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.AndroidOta;

/// <summary>
/// WORM writer for Android A/B OTA payload (<c>CrAU</c>) containers. Emits a
/// structurally valid 24-byte header followed by a minimal
/// <c>DeltaArchiveManifest</c> protobuf, an optional metadata signature blob,
/// and a payload blob. The manifest is intentionally tiny — a single
/// <c>block_size = 4096</c> field — so the output round-trips through our
/// reader without forcing the writer to embed a full protobuf emitter.
/// </summary>
public static class AndroidOtaWriter {

  /// <summary>Magic bytes that introduce every OTA payload.</summary>
  public static ReadOnlySpan<byte> Magic => "CrAU"u8;

  /// <summary>Default major payload version emitted when none is requested.</summary>
  public const ulong DefaultVersion = 2UL;

  /// <summary>Default block size advertised in the synthesised manifest.</summary>
  public const uint DefaultBlockSize = 4096u;

  /// <summary>
  /// Writes an OTA payload from <paramref name="inputs"/>. Recognised input
  /// names — chosen to round-trip our own <c>Extract</c> output — are
  /// <c>manifest.pb</c>, <c>metadata_signature.bin</c> and <c>data.bin</c>.
  /// Any other inputs are concatenated into the data region in the order
  /// they appear.
  /// </summary>
  public static void Write(
      Stream output,
      IReadOnlyList<ArchiveInputInfo> inputs,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    byte[]? manifest = null;
    byte[]? signature = null;
    var dataBlobs = new List<byte[]>();

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = input.ArchiveName;
      var bytes = input.ReadContent();
      // Special slot names mirror the descriptor's Extract output.
      if (name.Equals("manifest.pb", StringComparison.OrdinalIgnoreCase)) {
        manifest = bytes;
      } else if (name.Equals("metadata_signature.bin", StringComparison.OrdinalIgnoreCase)) {
        signature = bytes;
      } else if (name.Equals("data.bin", StringComparison.OrdinalIgnoreCase)) {
        dataBlobs.Add(bytes);
      } else if (name.Equals("FULL.bin", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)) {
        // FULL.bin / metadata.ini are synthetic outputs of Extract; ignore on round-trip.
        continue;
      } else {
        dataBlobs.Add(bytes);
      }
    }

    manifest ??= BuildMinimalManifest(options?.GetOptionInt("ota_block_size", (int)DefaultBlockSize) ?? (int)DefaultBlockSize);
    signature ??= [];

    var version = options is null
      ? DefaultVersion
      : (ulong)Math.Max(1, options.GetOptionInt("ota_version", (int)DefaultVersion));

    Span<byte> header = stackalloc byte[24];
    Magic.CopyTo(header);
    BinaryPrimitives.WriteUInt64BigEndian(header[4..12], version);
    BinaryPrimitives.WriteUInt64BigEndian(header[12..20], (ulong)manifest.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header[20..24], (uint)signature.Length);
    output.Write(header);
    if (manifest.Length > 0) output.Write(manifest);
    if (signature.Length > 0) output.Write(signature);
    foreach (var blob in dataBlobs)
      if (blob.Length > 0) output.Write(blob);
  }

  /// <summary>
  /// Builds a minimal protobuf-encoded <c>DeltaArchiveManifest</c> carrying a
  /// single <c>block_size</c> field. Wire format is varint-tag + varint-value:
  /// field 3 (block_size) tag = (3 &lt;&lt; 3) | wire_type_varint(0) = 0x18,
  /// followed by the LEB128 encoding of <paramref name="blockSize"/>.
  /// </summary>
  internal static byte[] BuildMinimalManifest(int blockSize) {
    if (blockSize <= 0) blockSize = (int)DefaultBlockSize;
    using var ms = new MemoryStream();
    // Tag for field 3, varint wire type.
    ms.WriteByte(0x18);
    WriteVarint(ms, (ulong)blockSize);
    return ms.ToArray();
  }

  private static void WriteVarint(Stream s, ulong value) {
    while (value >= 0x80) {
      s.WriteByte((byte)(value | 0x80));
      value >>= 7;
    }
    s.WriteByte((byte)value);
  }
}
