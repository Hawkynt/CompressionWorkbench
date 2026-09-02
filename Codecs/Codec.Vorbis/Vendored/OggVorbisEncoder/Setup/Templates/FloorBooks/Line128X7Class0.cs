namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 128 x 7 class 0.
/// </summary>
public class Line128X7Class0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        10, 7, 8, 13, 9, 6, 7, 11, 10, 8, 8, 12, 17, 17, 17, 17,
        7, 5, 5, 9, 6, 4, 4, 8, 8, 5, 5, 8, 16, 14, 13, 16,
        7, 5, 5, 7, 6, 3, 3, 5, 8, 5, 4, 7, 14, 12, 12, 15,
        10, 7, 8, 9, 7, 5, 5, 6, 9, 6, 5, 5, 15, 12, 9, 10
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = CodeBookMapType.None;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;
    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = null;
}
