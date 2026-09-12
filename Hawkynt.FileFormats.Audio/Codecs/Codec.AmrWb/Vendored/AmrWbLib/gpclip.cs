/*-----------------------------------------------------------------*
 * Gain pitch clipping routines                                    *
 * ~~~~~~~~~~~~~~~~~~~~~~~~~~~~                                    *
 * To avoid mismatch between encoder and decoder and  then unstable*
 * synthesis due to LTP error accumulation, the LTP gain is limited*
 * to 1 when pitch is highly predictive for a while (around 250ms).*
 *                                                                 *
 * The limitation (clipping) is done when pitch gain is higher than* 
 * a threshold for about 250 ms The threshold is fixed between     *
 * 0.94 and 1.                                                     *
 *    thres = 1.0 when minimum distance on ISF is 120 or higher.   *
 *    thres = 0.94 when minimum distance on ISF is 50 (ISF_GAP).   *
 * Typically, the threshold is decreased on high short term pred.  *
 * which reduces the impact of the clipping for less resonnant LP  *
 * filters                                                         * 
 *-----------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "count.h"
//#include "bits.h"


namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private const int DIST_ISF_MAX_IO = 384;                /* 150 Hz (6400Hz=16384) */
    private const int DIST_ISF_MAX = 307;                /* 120 Hz (6400Hz=16384) */
    private const int DIST_ISF_THRES = 154;                /* 60     (6400Hz=16384) */
    private const int GAIN_PIT_THRES = 14746;              /* 0.9 in Q14 */

    private const int GAIN_PIT_MIN = 9830;               /* 0.6 in Q14 */
    //private const int M = 16;

    private void Init_gp_clip(
         short[] mem)                          /* (o) : memory of gain of pitch clipping algorithm */
    {
        mem[0] = DIST_ISF_MAX;
        mem[1] = GAIN_PIT_MIN;
    }


    private short Gp_clip(
         short ser_size,                      /* (i)   : size of the bitstream                      */
         short[] mem)                          /* (i/o) : memory of gain of pitch clipping algorithm */
    {
        short clip;
        short thres;

        clip = 0;
        if ((ser_size == NBBITS_7k) || (ser_size == NBBITS_9k))
        {
            /* clipping is activated when filtered pitch gain > threshold (0.94 to 1 in Q14) */
            /* thres = 0.9f + (0.1f*mem[0]/DIST_ISF_MAX); */
            thres = add(14746, mult(1638, extract_l(L_mult(mem[0], (short)(16384 / DIST_ISF_MAX_IO)))));

            if (sub(mem[1], thres) > 0)
                clip = 1;
        }
        else if ((sub(mem[0], DIST_ISF_THRES) < 0) && (sub(mem[1], GAIN_PIT_THRES) > 0))
            clip = 1;

        return (clip);
    }

    private void Gp_clip_test_isf(
         short ser_size,                      /* (i)   : size of the bitstream                      */
         short[] isf,                         /* (i)   : isf values (in frequency domain)           */
         short[] mem)                          /* (i/o) : memory of gain of pitch clipping algorithm */
    {
        short i, dist, dist_min;

        dist_min = sub(isf[1], isf[0]);

        for (i = 2; i < M - 1; i++)
        {
            dist = sub(isf[i], isf[i - 1]);
            if (sub(dist, dist_min) < 0)
            {
                dist_min = dist;
            }
        }

        dist = extract_h(L_mac(L_mult(26214, mem[0]), 6554, dist_min));

        if ((ser_size == NBBITS_7k) || (ser_size == NBBITS_9k))
        {
            if (sub(dist, DIST_ISF_MAX_IO) > 0)
            {
                dist = DIST_ISF_MAX_IO;
            }
        }
        else
        {
            if (sub(dist, DIST_ISF_MAX) > 0)
            {
                dist = DIST_ISF_MAX;
            }
        }
        mem[0] = dist;

        return;
    }


    private void Gp_clip_test_gain_pit(
         short ser_size,                      /* (i)   : size of the bitstream                        */
         short gain_pit,                      /* (i) Q14 : gain of quantized pitch                    */
         short[] mem                          /* (i/o)   : memory of gain of pitch clipping algorithm */
)
    {
        short gain;
        int L_tmp;

        if ((ser_size == NBBITS_7k) || (ser_size == NBBITS_9k))
        {
            /* long term LTP gain average (>250ms) */
            /* gain = 0.98*mem[1] + 0.02*gain_pit; */
            L_tmp = L_mult(32113, mem[1]);
            L_tmp = L_mac(L_tmp, 655, gain_pit);
        }
        else
        {
            L_tmp = L_mult(29491, mem[1]);
            L_tmp = L_mac(L_tmp, 3277, gain_pit);
        }
        gain = extract_h(L_tmp);

        if (sub(gain, GAIN_PIT_MIN) < 0)
        {
            gain = GAIN_PIT_MIN;
        }
        mem[1] = gain;
    }
}
