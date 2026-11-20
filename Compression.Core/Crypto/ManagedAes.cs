using System.Buffers;
using System.Security.Cryptography;

namespace Compression.Core.Crypto;

/// <summary>
/// <see cref="Aes"/> on this library's own <see cref="AesBlockCipher"/>: a drop-in for <see cref="Aes.Create()"/>
/// whose ECB, CBC and CFB (8- and 128-bit feedback) work, with every <see cref="PaddingMode"/>, through
/// <see cref="ICryptoTransform"/> and the one-shot <c>EncryptEcb</c>/<c>DecryptCbc</c>/… methods alike.
/// </summary>
/// <remarks>
/// <see cref="Aes.Create()"/> hands the work to the operating system — CNG on Windows, OpenSSL on Linux,
/// CommonCrypto on macOS — and what one of them refuses, the format cannot be read with: Wine's CNG, for
/// one, takes no ECB, and every XTS volume steps through ECB. The modes are SP 800-38A's; padding follows
/// what .NET's own AES does, which the tests hold this class to.
/// </remarks>
public sealed class ManagedAes : Aes {
  private const int Block = AesBlockCipher.BlockSize;

  private AesBlockCipher? _cipher;
  private byte[]? _cipherKey;

  /// <summary>A new instance with .NET's defaults: a 256-bit key made on first use, CBC, PKCS #7.</summary>
  public new static ManagedAes Create() => new();

  /// <inheritdoc/>
  public override void GenerateKey() => this.KeyValue = RandomNumberGenerator.GetBytes(this.KeySizeValue / 8);

  /// <inheritdoc/>
  public override void GenerateIV() => this.IVValue = RandomNumberGenerator.GetBytes(Block);

  /// <inheritdoc/>
  public override ICryptoTransform CreateEncryptor(byte[] rgbKey, byte[]? rgbIV) => this.CreateTransform(rgbKey, rgbIV, true);

  /// <inheritdoc/>
  public override ICryptoTransform CreateDecryptor(byte[] rgbKey, byte[]? rgbIV) => this.CreateTransform(rgbKey, rgbIV, false);

  /// <inheritdoc/>
  protected override bool TryEncryptEcbCore(ReadOnlySpan<byte> plaintext, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
    => Encrypt(this.CurrentCipher(), CipherMode.ECB, Block, default, plaintext, destination, paddingMode, out bytesWritten);

  /// <inheritdoc/>
  protected override bool TryDecryptEcbCore(ReadOnlySpan<byte> ciphertext, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
    => Decrypt(this.CurrentCipher(), CipherMode.ECB, Block, default, ciphertext, destination, paddingMode, out bytesWritten);

  /// <inheritdoc/>
  protected override bool TryEncryptCbcCore(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
    => Encrypt(this.CurrentCipher(), CipherMode.CBC, Block, iv, plaintext, destination, paddingMode, out bytesWritten);

  /// <inheritdoc/>
  protected override bool TryDecryptCbcCore(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
    => Decrypt(this.CurrentCipher(), CipherMode.CBC, Block, iv, ciphertext, destination, paddingMode, out bytesWritten);

  /// <inheritdoc/>
  protected override bool TryEncryptCfbCore(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, int feedbackSizeInBits, out int bytesWritten)
    => Encrypt(this.CurrentCipher(), CipherMode.CFB, FeedbackBytes(CipherMode.CFB, feedbackSizeInBits), iv, plaintext, destination, paddingMode, out bytesWritten);

  /// <inheritdoc/>
  protected override bool TryDecryptCfbCore(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, int feedbackSizeInBits, out int bytesWritten)
    => Decrypt(this.CurrentCipher(), CipherMode.CFB, FeedbackBytes(CipherMode.CFB, feedbackSizeInBits), iv, ciphertext, destination, paddingMode, out bytesWritten);

  /// <inheritdoc/>
  protected override void Dispose(bool disposing) {
    if (disposing && this._cipherKey != null) {
      CryptographicOperations.ZeroMemory(this._cipherKey);
      this._cipherKey = null;
      this._cipher = null;
    }
    base.Dispose(disposing);
  }

  /// <summary>The expanded key of the current <see cref="SymmetricAlgorithm.Key"/>, expanded again only when that key changed.</summary>
  private AesBlockCipher CurrentCipher() {
    if (this.KeyValue == null)
      this.GenerateKey();
    var key = this.KeyValue!;
    if (this._cipher == null || this._cipherKey == null || !key.AsSpan().SequenceEqual(this._cipherKey)) {
      this._cipher = new AesBlockCipher(key);
      if (this._cipherKey != null)
        CryptographicOperations.ZeroMemory(this._cipherKey);
      this._cipherKey = (byte[])key.Clone();
    }
    return this._cipher;
  }

  private ICryptoTransform CreateTransform(byte[] key, byte[]? iv, bool encrypt) {
    ArgumentNullException.ThrowIfNull(key);
    if (!this.ValidKeySize(key.Length * 8))
      throw new ArgumentException("Specified key is not a valid size for this algorithm.", nameof(key));
    var mode = this.ModeValue;
    var unit = FeedbackBytes(mode, this.FeedbackSizeValue);
    byte[] start;
    if (mode == CipherMode.ECB)
      start = new byte[Block];
    else if (iv == null)
      throw new CryptographicException("The cipher mode specified requires that an initialization vector (IV) be used.");
    else if (iv.Length < Block)
      throw new CryptographicException("Specified initialization vector (IV) does not match the block size for this algorithm.");
    else
      start = iv.AsSpan(0, Block).ToArray();
    return new Transform(new AesBlockCipher(key), mode, this.PaddingValue, unit, start, encrypt);
  }

  /// <summary>The unit a mode pads to and steps by: a block for ECB and CBC, the feedback size for CFB.</summary>
  private static int FeedbackBytes(CipherMode mode, int feedbackBits) => mode switch {
    CipherMode.ECB or CipherMode.CBC => Block,
    CipherMode.CFB when feedbackBits is 8 or 128 => feedbackBits / 8,
    CipherMode.CFB => throw new CryptographicException($"The specified feedback size '{feedbackBits}' for CipherMode '{mode}' is not supported."),
    _ => throw new CryptographicException("Specified cipher mode is not valid for this algorithm."),
  };

  // ---- one-shot ----------------------------------------------------------------------------------------

  private static bool Encrypt(AesBlockCipher cipher, CipherMode mode, int unit, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, Span<byte> destination, PaddingMode padding, out int bytesWritten) {
    var length = PaddedLength(input.Length, padding, unit);
    if (destination.Length < length) {
      bytesWritten = 0;
      return false;
    }
    input.CopyTo(destination);
    Pad(destination[input.Length..length], padding);
    Span<byte> state = stackalloc byte[Block];
    if (mode != CipherMode.ECB)
      iv[..Block].CopyTo(state);
    Process(cipher, mode, unit, true, state, destination[..length]);
    bytesWritten = length;
    return true;
  }

  private static bool Decrypt(AesBlockCipher cipher, CipherMode mode, int unit, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, Span<byte> destination, PaddingMode padding, out int bytesWritten) {
    if (input.Length % unit != 0)
      throw new CryptographicException("The input data is not a complete block.");
    Span<byte> state = stackalloc byte[Block];
    if (mode != CipherMode.ECB)
      iv[..Block].CopyTo(state);
    // Decrypted where the caller asked when it holds the padded text; else beside it, and only the unpadded
    // part is handed over.
    byte[]? rented = null;
    var work = destination.Length >= input.Length ? destination[..input.Length] : (rented = ArrayPool<byte>.Shared.Rent(input.Length)).AsSpan(0, input.Length);
    try {
      input.CopyTo(work);
      Process(cipher, mode, unit, false, state, work);
      int length;
      try {
        length = Unpad(work, padding);
      } catch {
        CryptographicOperations.ZeroMemory(work);
        throw;
      }
      if (rented == null) {
        CryptographicOperations.ZeroMemory(work[length..]);
        bytesWritten = length;
        return true;
      }
      if (destination.Length < length) {
        bytesWritten = 0;
        return false;
      }
      work[..length].CopyTo(destination);
      bytesWritten = length;
      return true;
    } finally {
      if (rented != null) {
        CryptographicOperations.ZeroMemory(work);
        ArrayPool<byte>.Shared.Return(rented);
      }
    }
  }

  // ---- padding -----------------------------------------------------------------------------------------

  private static bool Unpads(PaddingMode padding) => padding is PaddingMode.PKCS7 or PaddingMode.ANSIX923 or PaddingMode.ISO10126;

  private static int PaddedLength(int length, PaddingMode padding, int unit) => padding switch {
    PaddingMode.None when length % unit != 0 => throw new CryptographicException("The input data is not a complete block."),
    PaddingMode.None => length,
    PaddingMode.Zeros => (length + unit - 1) / unit * unit,
    PaddingMode.PKCS7 or PaddingMode.ANSIX923 or PaddingMode.ISO10126 => (length / unit + 1) * unit,
    _ => throw new CryptographicException("Specified padding mode is not valid for this algorithm."),
  };

  private static void Pad(Span<byte> tail, PaddingMode padding) {
    if (tail.IsEmpty)
      return;
    switch (padding) {
      case PaddingMode.PKCS7:
        tail.Fill((byte)tail.Length);
        break;
      case PaddingMode.ANSIX923:
        tail[..^1].Clear();
        tail[^1] = (byte)tail.Length;
        break;
      case PaddingMode.ISO10126:
        RandomNumberGenerator.Fill(tail[..^1]);
        tail[^1] = (byte)tail.Length;
        break;
      default:
        tail.Clear();
        break;
    }
  }

  /// <summary>
  /// The length of the text once its padding is taken off; a padding that does not hold is refused. A pad
  /// may be as long as a block even where the mode pads to single bytes (CFB-8), as .NET reads it.
  /// </summary>
  private static int Unpad(ReadOnlySpan<byte> text, PaddingMode padding) {
    if (!Unpads(padding))
      return text.Length;
    if (text.IsEmpty)
      throw new CryptographicException("Padding is invalid and cannot be removed.");
    var count = text[^1];
    if (count == 0 || count > Block || count > text.Length)
      throw new CryptographicException("Padding is invalid and cannot be removed.");
    var pad = text[^count..^1];
    var bad = padding switch {
      PaddingMode.PKCS7 => pad.ContainsAnyExcept(count),
      PaddingMode.ANSIX923 => pad.ContainsAnyExcept((byte)0),
      _ => false,
    };
    if (bad)
      throw new CryptographicException("Padding is invalid and cannot be removed.");
    return text.Length - count;
  }

  // ---- modes (SP 800-38A) ------------------------------------------------------------------------------

  /// <summary>Runs whole units of <paramref name="data"/> through the mode in place, carrying the chaining value in <paramref name="state"/>.</summary>
  private static void Process(AesBlockCipher cipher, CipherMode mode, int unit, bool encrypt, Span<byte> state, Span<byte> data) {
    switch (mode) {
      case CipherMode.ECB:
        if (encrypt)
          cipher.Encrypt(data);
        else
          cipher.Decrypt(data);
        break;
      case CipherMode.CBC when encrypt:
        cipher.EncryptCbc(data, state);
        break;
      case CipherMode.CBC:
        cipher.DecryptCbc(data, state);
        break;
      case CipherMode.CFB when unit == Block && encrypt:
        cipher.EncryptCfb(data, state);
        break;
      case CipherMode.CFB when unit == Block:
        cipher.DecryptCfb(data, state);
        break;
      case CipherMode.CFB: {
        // CFB-8: one keystream byte per block encryption, the shift register taking in each ciphertext byte.
        Span<byte> stream = stackalloc byte[Block];
        for (var i = 0; i < data.Length; ++i) {
          cipher.Encrypt(state, stream);
          var input = data[i];
          var output = (byte)(input ^ stream[0]);
          data[i] = output;
          state[1..].CopyTo(state);
          state[^1] = encrypt ? output : input;
        }
        break;
      }
      default:
        throw new CryptographicException("Specified cipher mode is not valid for this algorithm.");
    }
  }

  // ---- streaming ---------------------------------------------------------------------------------------

  /// <summary>
  /// One direction of one key and IV for <see cref="CryptoStream"/> and friends. It steps by whole blocks in
  /// every mode, CFB-8 included, as .NET's transforms do, while the final block pads to the mode's unit. A
  /// decryptor that takes a padding off holds the last block back until <see cref="TransformFinalBlock"/>
  /// says which block is last.
  /// </summary>
  private sealed class Transform(AesBlockCipher cipher, CipherMode mode, PaddingMode padding, int unit, byte[] iv, bool encrypt) : ICryptoTransform {
    private readonly byte[] _state = (byte[])iv.Clone();
    private byte[]? _held;

    public int InputBlockSize => Block;
    public int OutputBlockSize => Block;
    public bool CanTransformMultipleBlocks => true;
    public bool CanReuseTransform => true;

    public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset) {
      ArgumentNullException.ThrowIfNull(inputBuffer);
      ArgumentNullException.ThrowIfNull(outputBuffer);
      ArgumentOutOfRangeException.ThrowIfNegative(inputOffset);
      ArgumentOutOfRangeException.ThrowIfNegative(inputCount);
      ArgumentOutOfRangeException.ThrowIfNegative(outputOffset);
      if (inputCount > inputBuffer.Length - inputOffset)
        throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
      if (inputCount == 0 || inputCount % Block != 0)
        throw new ArgumentException("Value was invalid.", nameof(inputCount));
      var input = inputBuffer.AsSpan(inputOffset, inputCount);

      if (encrypt || !Unpads(padding)) {
        var output = outputBuffer.AsSpan(outputOffset, inputCount);
        input.CopyTo(output);
        Process(cipher, mode, unit, encrypt, this._state, output);
        return inputCount;
      }

      var plain = input.ToArray();
      Process(cipher, mode, unit, false, this._state, plain);
      var written = 0;
      if (this._held != null) {
        this._held.CopyTo(outputBuffer.AsSpan(outputOffset));
        written = this._held.Length;
        CryptographicOperations.ZeroMemory(this._held);
      }
      plain.AsSpan(0, plain.Length - Block).CopyTo(outputBuffer.AsSpan(outputOffset + written));
      written += plain.Length - Block;
      this._held = plain.AsSpan(plain.Length - Block).ToArray();
      CryptographicOperations.ZeroMemory(plain);
      return written;
    }

    public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount) {
      ArgumentNullException.ThrowIfNull(inputBuffer);
      ArgumentOutOfRangeException.ThrowIfNegative(inputOffset);
      ArgumentOutOfRangeException.ThrowIfNegative(inputCount);
      if (inputCount > inputBuffer.Length - inputOffset)
        throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
      var input = inputBuffer.AsSpan(inputOffset, inputCount);
      try {
        if (encrypt) {
          var output = new byte[PaddedLength(inputCount, padding, unit)];
          input.CopyTo(output);
          Pad(output.AsSpan(inputCount), padding);
          Process(cipher, mode, unit, true, this._state, output);
          return output;
        }
        if (inputCount % unit != 0)
          throw new CryptographicException("The input data is not a complete block.");
        var heldLength = this._held?.Length ?? 0;
        var text = new byte[heldLength + inputCount];
        this._held?.CopyTo(text, 0);
        input.CopyTo(text.AsSpan(heldLength));
        Process(cipher, mode, unit, false, this._state, text.AsSpan(heldLength));
        if (text.Length == 0)
          return text;
        int length;
        try {
          length = Unpad(text, padding);
        } catch {
          CryptographicOperations.ZeroMemory(text);
          throw;
        }
        if (length == text.Length)
          return text;
        var result = text.AsSpan(0, length).ToArray();
        CryptographicOperations.ZeroMemory(text);
        return result;
      } finally {
        this.Reset();
      }
    }

    private void Reset() {
      iv.CopyTo(this._state, 0);
      if (this._held != null) {
        CryptographicOperations.ZeroMemory(this._held);
        this._held = null;
      }
    }

    public void Dispose() {
      CryptographicOperations.ZeroMemory(this._state);
      CryptographicOperations.ZeroMemory(iv);
      if (this._held != null)
        CryptographicOperations.ZeroMemory(this._held);
    }
  }
}
