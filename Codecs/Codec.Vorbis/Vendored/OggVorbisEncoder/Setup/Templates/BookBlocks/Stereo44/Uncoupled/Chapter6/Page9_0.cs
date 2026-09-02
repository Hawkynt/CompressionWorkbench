namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter6;

/// <summary>
/// Represents a page 9 0.
/// </summary>
public class Page9_0 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 3, 2, 9, 8,15,15,15,15,15,15,15,15,15,15, 4,
         8, 9,13,14,14,14,14,14,14,14,14,14,14,14, 5, 8,
         9,14,14,14,14,14,14,14,14,14,14,14,14,11,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,11,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -514071552;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1627381760;
        /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 4;
        /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

        /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        7,
        6,
        8,
        5,
        9,
        4,
        10,
        3,
        11,
        2,
        12,
        1,
        13,
        0,
        14,
    };
}
