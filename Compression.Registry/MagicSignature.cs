namespace Compression.Registry;

/// <summary>
/// A magic-byte signature for format identification.
/// </summary>
/// <param name="Bytes">The magic bytes to match.</param>
/// <param name="Offset">Byte offset from the start of the file where the signature appears.</param>
/// <param name="Confidence">Detection confidence (0.0 - 1.0).</param>
/// <param name="Mask">Optional bitmask applied before comparison (null = exact match).</param>
public sealed record MagicSignature(byte[] Bytes, int Offset = 0, double Confidence = 0.90, byte[]? Mask = null);
