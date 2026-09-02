namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 128 x 7 class 1.
/// </summary>
public class Line128X7Class1 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        8, 13, 17, 17, 8, 11, 17, 17, 11, 13, 17, 17, 17, 17, 17, 17,
        6, 10, 16, 17, 6, 10, 15, 17, 8, 10, 16, 17, 17, 17, 17, 17,
        9, 13, 15, 17, 8, 11, 17, 17, 10, 12, 17, 17, 17, 17, 17, 17,
        17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
        6, 11, 15, 17, 7, 10, 15, 17, 8, 10, 17, 17, 17, 15, 17, 17,
        4, 8, 13, 17, 4, 7, 13, 17, 6, 8, 15, 17, 16, 15, 17, 17,
        6, 11, 15, 17, 6, 9, 13, 17, 8, 10, 17, 17, 15, 17, 17, 17,
        16, 17, 17, 17, 12, 14, 15, 17, 13, 14, 15, 17, 17, 17, 17, 17,
        5, 10, 14, 17, 5, 9, 14, 17, 7, 9, 15, 17, 15, 15, 17, 17,
        3, 7, 12, 17, 3, 6, 11, 17, 5, 7, 13, 17, 12, 12, 17, 17,
        5, 9, 14, 17, 3, 7, 11, 17, 5, 8, 13, 17, 13, 11, 16, 17,
        12, 17, 17, 17, 9, 14, 15, 17, 10, 11, 14, 17, 16, 14, 17, 17,
        8, 12, 17, 17, 8, 12, 17, 17, 10, 12, 17, 17, 17, 17, 17, 17,
        5, 10, 17, 17, 5, 9, 15, 17, 7, 9, 17, 17, 13, 13, 17, 17,
        7, 11, 17, 17, 6, 10, 15, 17, 7, 9, 15, 17, 12, 11, 17, 17,
        12, 15, 17, 17, 11, 14, 17, 17, 11, 10, 15, 17, 17, 16, 17, 17
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
