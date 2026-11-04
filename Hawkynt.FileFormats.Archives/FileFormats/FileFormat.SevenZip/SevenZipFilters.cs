using System.Security.Cryptography;
using Compression.Core.Transforms;

namespace FileFormat.SevenZip;

/// <summary>A branch converter (BCJ, PPC, IA64, ARM, ARMT, SPARC, ARM64, RISCV) as a 7z filter.</summary>
internal sealed class SevenZipBranchFilter(BranchArchitecture architecture, bool decoding, uint startOffset) : ISevenZipFilter {
  private readonly BranchConverter _converter = BranchConverter.Create(architecture, decoding, startOffset);

  public int Process(Span<byte> data, bool final) {
    var done = this._converter.Convert(data);
    return final ? data.Length : done;
  }
}

/// <summary>
/// The 7z Delta filter (method 03): every byte is stored as its difference to the byte
/// <c>distance</c> positions before it; the history carries across buffers.
/// </summary>
internal sealed class SevenZipDeltaFilter(int distance, bool decoding) : ISevenZipFilter {
  private readonly byte[] _history = new byte[256];
  private int _index;

  public int Process(Span<byte> data, bool final) {
    var history = this._history;
    var index = this._index;
    for (var i = 0; i < data.Length; ++i) {
      var slot = (index + 256 - distance) & 0xFF;
      if (decoding) {
        data[i] += history[slot];
        history[index] = data[i];
      } else {
        var original = data[i];
        data[i] -= history[slot];
        history[index] = original;
      }
      index = (index + 1) & 0xFF;
    }
    this._index = index;
    return data.Length;
  }
}

/// <summary>The 7z SWAP2 / SWAP4 filters: byte order reversed in every 2- or 4-byte unit; a short tail stays.</summary>
internal sealed class SevenZipSwapFilter(int unit) : ISevenZipFilter {
  public int Process(Span<byte> data, bool final) {
    var whole = data.Length / unit * unit;
    for (var i = 0; i < whole; i += unit)
      data.Slice(i, unit).Reverse();
    return final ? data.Length : whole;
  }
}

/// <summary>
/// AES-256-CBC decryption for the 7z <c>06F10701</c> coder. 7-Zip pads the ciphertext to whole
/// blocks; the coder's declared unpack size cuts the padding off.
/// </summary>
internal sealed class SevenZipAesDecryptFilter : ISevenZipFilter, IDisposable {
  private readonly Aes _aes;
  private readonly ICryptoTransform _transform;

  public SevenZipAesDecryptFilter(byte[] key, byte[] iv) {
    this._aes = Aes.Create();
    this._aes.Mode = CipherMode.CBC;
    this._aes.Padding = PaddingMode.None;
    this._aes.Key = key;
    this._aes.IV = iv;
    this._transform = this._aes.CreateDecryptor();
  }

  public int Process(Span<byte> data, bool final) {
    var whole = data.Length & ~15;
    if (whole > 0) {
      var buffer = data[..whole].ToArray();
      this._transform.TransformBlock(buffer, 0, whole, buffer, 0);
      buffer.CopyTo(data);
    }
    // A trailing partial block cannot be decrypted; only the size limit can make it harmless.
    return final ? data.Length : whole;
  }

  public void Dispose() {
    this._transform.Dispose();
    this._aes.Dispose();
  }
}

/// <summary>Leaves the bytes as they are: the 7z Copy coder (00) when it sits above another coder.</summary>
internal sealed class SevenZipCopyFilter : ISevenZipFilter {
  public static readonly SevenZipCopyFilter Instance = new();
  public int Process(Span<byte> data, bool final) => data.Length;
}
