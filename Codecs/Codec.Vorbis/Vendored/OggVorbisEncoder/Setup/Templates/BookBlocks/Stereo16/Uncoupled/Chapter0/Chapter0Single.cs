namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Uncoupled.Chapter0;

/// <summary>
/// Represents a chapter 0 single.
/// </summary>
public class Chapter0Single : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         3, 5, 8, 7,14, 8, 9,19, 5, 2, 5, 5, 9, 6, 9,19,
         8, 4, 5, 7, 8, 9,13,19, 7, 4, 6, 5, 9, 6, 9,19,
        12, 8, 7, 9,10,11,13,19, 8, 5, 8, 6, 9, 6, 7,19,
         8, 8,10, 7, 7, 4, 5,19,12,17,19,15,18,13,11,18,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
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
