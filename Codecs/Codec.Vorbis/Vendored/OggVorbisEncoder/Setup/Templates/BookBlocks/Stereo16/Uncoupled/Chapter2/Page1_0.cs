namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Uncoupled.Chapter2;

/// <summary>
/// Represents a page 1 0.
/// </summary>
public class Page1_0 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 4;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 5, 5, 5, 7, 7, 5, 7, 7, 5, 7, 7, 7, 9, 9, 7,
         9, 9, 5, 7, 7, 7, 9, 9, 8, 9, 9, 5, 7, 7, 8, 9,
         9, 7, 9, 9, 7, 9, 9, 9,10,11, 9,10,10, 7, 9, 9,
         9,10, 9, 9,10,11, 5, 8, 7, 7, 9, 9, 8, 9, 9, 7,
         9, 9, 9,11,10, 9, 9,10, 7, 9, 9, 9,10,10, 9,11,
        10,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -535822336;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1611661312;
        /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 2;
        /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

        /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        1,
        0,
        2,
    };
}
