/*--------------------------------------------------------------------------*
 *                         CNST.H                                           *
 *--------------------------------------------------------------------------*
 *       Codec constant parameters (coder and decoder)                      *
 *--------------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    private const string CODEC_VERSION = "7.0.0";

    private const int L_FRAME16k = 320;                 /* Frame size at 16kHz                        */
    private const int L_FRAME = 256;                    /* Frame size                                 */
    private const int L_SUBFR16k = 80;                  /* Subframe size at 16kHz                     */

    private const int L_SUBFR = 64;                     /* Subframe size                              */
    private const int NB_SUBFR = 4;                     /* Number of subframe per frame               */

    private const int L_NEXT = 64;                      /* Overhead in LP analysis                    */
    // Already defined in ham_wind.cs -- 9 Jan 24 PHR
    //private const int L_WINDOW = 384;                   /* window size in LP analysis                 */
    private const int L_TOTAL = 384;                    /* Total size of speech buffer.               */
    // Already defined in az_isp.cs -- 9 Jan 24 PHR
    //private const int M = 16;                           /* Order of LP filter                         */

    private const int M16k = 20;

    private const int L_FILT16k = 15;                   /* Delay of down-sampling filter              */
    private const int L_FILT = 12;                     /* Delay of up-sampling filter                */

    private const int GP_CLIP = 15565;                 /* Pitch gain clipping = 0.95 Q14             */
    private const int PIT_SHARP = 27853;               /* pitch sharpening factor = 0.85 Q15         */

    private const int PIT_MIN = 34;                    /* Minimum pitch lag with resolution 1/4      */
    private const int PIT_FR2 = 128;                   /* Minimum pitch lag with resolution 1/2      */
    private const int PIT_FR1_9b = 160;                /* Minimum pitch lag with resolution 1        */
    private const int PIT_FR1_8b = 92;                 /* Minimum pitch lag with resolution 1        */
    private const int PIT_MAX = 231;                   /* Maximum pitch lag                          */
    private const int L_INTERPOL = (16 + 1);           /* Length of filter for interpolation         */

    private const int OPL_DECIM = 2;                   /* Decimation in open-loop pitch analysis     */

    private const int PREEMPH_FAC = 22282;             /* preemphasis factor (0.68 in Q15)           */
    private const int GAMMA1 = 30147;                  /* Weighting factor (numerator) (0.92 in Q15) */
    private const int TILT_FAC = 22282;                /* tilt factor (denominator) (0.68 in Q15)    */

    private const int Q_MAX = 8;                       /* scaling max for signal (see syn_filt_32)   */

    private const int RANDOM_INITSEED = 21845;         /* own random init value                      */

    private const int L_MEANBUF = 3;
    private const int ONE_PER_MEANBUF = 10923;

    private const int MODE_7k = 0;
    private const int MODE_9k = 1;
    private const int MODE_12k = 2;
    private const int MODE_14k = 3;
    private const int MODE_16k = 4;
    private const int MODE_18k = 5;
    private const int MODE_20k = 6;
    private const int MODE_23k = 7;
    private const int MODE_24k = 8;
    private const int MRDTX = 9;
    private const int NUM_OF_MODES = 10;                   /* see bits.h for bits definition             */

    private const short EHF_MASK = 0x0008;                 /* homing frame pattern                       */
}
