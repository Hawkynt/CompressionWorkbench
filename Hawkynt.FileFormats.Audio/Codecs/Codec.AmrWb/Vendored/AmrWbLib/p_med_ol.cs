/*------------------------------------------------------------------------*
 *                         P_MED_OL.C                    *
 *------------------------------------------------------------------------*
 * Compute the open loop pitch lag.                      *
 *------------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "acelp.h"
//#include "oper_32b.h"
//#include "count.h"
//#include "math_op.h"
//#include "p_med_ol.tab"

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{

    private unsafe short Pitch_med_ol(                       /* output: open loop pitch lag                             */
         short* wsp,                         /* input : signal used to compute the open loop pitch      */
         /*         wsp[-pit_max] to wsp[-1] should be known        */
         short L_min,                         /* input : minimum pitch lag                               */
         short L_max,                         /* input : maximum pitch lag                               */
         short L_frame,                       /* input : length of frame to compute pitch                */
         short L_0,                           /* input : old_ open-loop pitch                            */
         ref short gain,                        /* output: normalize correlation of hp_wsp for the Lag     */
         short[] hp_wsp_mem,                  /* i:o   : memory of the hypass filter for hp_wsp[] (lg=9) */
         short[] old_hp_wsp,                  /* i:o   : hypass wsp[]                                    */
         short wght_flg)                       /* input : is weighting function used                      */
    {
        short i, j, Tm;
        short hi = 0, lo = 0;
//        short* ww, we;

        // 30 Apr 25 PHR
        //short* hp_wsp;

        short exp_R0, exp_R1, exp_R2;
        int max, R0, R1, R2;

        //ww = &corrweight[198];
        //we = &corrweight[98 + L_max - L_0];

        // Indexs into corrweight[]
        int wwindex = 198;
        int weindex = 98 + L_max - L_0;

        max = int.MinValue;
        Tm = 0;
        for (i = L_max; i > L_min; i--)
        {
            /* Compute the correlation */
            R0 = 0;
            for (j = 0; j < L_frame; j++)
                R0 = L_mac(R0, wsp[j], wsp[j - i]);

            /* Weighting of the correlation function.   */
            L_Extract(R0, ref hi, ref lo);
            R0 = Mpy_32_16(hi, lo, corrweight[wwindex]);
            wwindex--;

            if ((L_0 > 0) && (wght_flg > 0))
            {
                /* Weight the neighbourhood of the old lag. */
                L_Extract(R0, ref hi, ref lo);
                R0 = Mpy_32_16(hi, lo, corrweight[weindex]);
                weindex--;
            }

            if (L_sub(R0, max) >= 0)
            {
                max = R0;
                Tm = i;
            }
        }

        /* Hypass the wsp[] vector */

        //hp_wsp = old_hp_wsp + L_max;
        //Hp_wsp(wsp, hp_wsp, L_frame, hp_wsp_mem);
        Hp_wsp(wsp, old_hp_wsp, L_max, L_frame, hp_wsp_mem);

        /* Compute normalize correlation at delay Tm */

        R0 = 0;
        R1 = 1;
        R2 = 1;
        for (j = 0; j < L_frame; j++)
        {
            //R0 = L_mac(R0, hp_wsp[j], hp_wsp[j - Tm]);
            //R1 = L_mac(R1, hp_wsp[j - Tm], hp_wsp[j - Tm]);
            //R2 = L_mac(R2, hp_wsp[j], hp_wsp[j]);
            // 30 Apr 25 PHR
            R0 = L_mac(R0, old_hp_wsp[L_max + j], old_hp_wsp[L_max + j - Tm]);
            R1 = L_mac(R1, old_hp_wsp[L_max + j - Tm], old_hp_wsp[L_max + j - Tm]);
            R2 = L_mac(R2, old_hp_wsp[L_max + j], old_hp_wsp[L_max + j]);

        }

        /* gain = R0/ sqrt(R1*R2) */

        exp_R0 = norm_l(R0);
        R0 = L_shl(R0, exp_R0);

        exp_R1 = norm_l(R1);
        R1 = L_shl(R1, exp_R1);

        exp_R2 = norm_l(R2);
        R2 = L_shl(R2, exp_R2);

        R1 = L_mult(round(R1), round(R2));

        i = norm_l(R1);
        R1 = L_shl(R1, i);

        exp_R1 = add(exp_R1, exp_R2);
        exp_R1 = add(exp_R1, i);
        exp_R1 = sub(62, exp_R1);

        Isqrt_n(ref R1, ref exp_R1);

        R0 = L_mult(round(R0), round(R1));
        exp_R0 = sub(31, exp_R0);
        exp_R0 = add(exp_R0, exp_R1);

        gain = round(L_shl(R0, exp_R0));

        /* Shitf hp_wsp[] for next frame */

        for (i = 0; i < L_max; i++)
        {
            old_hp_wsp[i] = old_hp_wsp[i + L_frame];
        }

        return (Tm);
    }

    /*____________________________________________________________________
     |
     |
     |  FUNCTION NAME median5
     |
     |      Returns the median of the set {X[-2], X[-1],..., X[2]},
     |      whose elements are 16-bit integers.
     |
     |  INPUT
     |      X[-2:2]   16-bit integers.
     |
     |  RETURN VALUE
     |      The median of {X[-2], X[-1],..., X[2]}.
     |_____________________________________________________________________
     */

    private unsafe short median5(short* x)
    {
        short x1, x2, x3, x4, x5;
        short tmp;

        x1 = x[-2];
        x2 = x[-1];
        x3 = x[0];
        x4 = x[1];
        x5 = x[2];

        if (sub(x2, x1) < 0)
        {
            tmp = x1;
            x1 = x2;
            x2 = tmp;
        }
        if (sub(x3, x1) < 0)
        {
            tmp = x1;
            x1 = x3;
            x3 = tmp;
        }
        if (sub(x4, x1) < 0)
        {
            tmp = x1;
            x1 = x4;
            x4 = tmp;
        }
        if (sub(x5, x1) < 0)
        {
            x5 = x1;
        }
        if (sub(x3, x2) < 0)
        {
            tmp = x2;
            x2 = x3;
            x3 = tmp;
        }
        if (sub(x4, x2) < 0)
        {
            tmp = x2;
            x2 = x4;
            x4 = tmp;
        }
        if (sub(x5, x2) < 0)
        {
            x5 = x2;
        }
        if (sub(x4, x3) < 0)
        {
            x3 = x4;
        }
        if (sub(x5, x3) < 0)
        {
            x3 = x5;
        }
        return (x3);
    }

    // 30 Apr 25 PHR -- Used by Med_olag
    private short median5(short[] x, int x_index)
    {
        short x1, x2, x3, x4, x5;
        short tmp;

        x1 = x[x_index-2];
        x2 = x[x_index-1];
        x3 = x[x_index + 0];
        x4 = x[x_index + 1];
        x5 = x[x_index + 2];

        if (sub(x2, x1) < 0)
        {
            tmp = x1;
            x1 = x2;
            x2 = tmp;
        }
        if (sub(x3, x1) < 0)
        {
            tmp = x1;
            x1 = x3;
            x3 = tmp;
        }
        if (sub(x4, x1) < 0)
        {
            tmp = x1;
            x1 = x4;
            x4 = tmp;
        }
        if (sub(x5, x1) < 0)
        {
            x5 = x1;
        }
        if (sub(x3, x2) < 0)
        {
            tmp = x2;
            x2 = x3;
            x3 = tmp;
        }
        if (sub(x4, x2) < 0)
        {
            tmp = x2;
            x2 = x4;
            x4 = tmp;
        }
        if (sub(x5, x2) < 0)
        {
            x5 = x2;
        }
        if (sub(x4, x3) < 0)
        {
            x3 = x4;
        }
        if (sub(x5, x3) < 0)
        {
            x3 = x5;
        }
        return (x3);
    }

    /*____________________________________________________________________
     |
     |
     |  FUNCTION NAME med_olag
     |
     |
     |_____________________________________________________________________
     */

    private unsafe short Med_olag(                   /* output : median of  5 previous open-loop lags       */
         short prev_ol_lag,                   /* input  : previous open-loop lag                     */
         short[] old_ol_lag)                // Length is 5
    {
        short i;

        /* Use median of 5 previous open-loop lags as old lag */

        for (i = 4; i > 0; i--)
        {
            old_ol_lag[i] = old_ol_lag[i - 1];
        }

        old_ol_lag[0] = prev_ol_lag;

        i = median5(old_ol_lag, 2);

        return i;

    }

    /*-----------------------------------------------------*
    | Table for function Pitch_med_ol()           |
    *-----------------------------------------------------*/

    private static readonly short[] corrweight = new short[199] {
         10772, 10794, 10816, 10839, 10862, 10885, 10908, 10932, 10955, 10980,
         11004, 11029, 11054, 11079, 11105, 11131, 11157, 11183, 11210, 11238,
         11265, 11293, 11322, 11350, 11379, 11409, 11439, 11469, 11500, 11531,
         11563, 11595, 11628, 11661, 11694, 11728, 11763, 11798, 11834, 11870,
         11907, 11945, 11983, 12022, 12061, 12101, 12142, 12184, 12226, 12270,
         12314, 12358, 12404, 12451, 12498, 12547, 12596, 12647, 12699, 12751,
         12805, 12861, 12917, 12975, 13034, 13095, 13157, 13221, 13286, 13353,
         13422, 13493, 13566, 13641, 13719, 13798, 13880, 13965, 14053, 14143,
         14237, 14334, 14435, 14539, 14648, 14761, 14879, 15002, 15130, 15265,
         15406, 15554, 15710, 15874, 16056, 16384, 16384, 16384, 16384, 16384,
         16384, 16384, 16056, 15874, 15710, 15554, 15406, 15265, 15130, 15002,
         14879, 14761, 14648, 14539, 14435, 14334, 14237, 14143, 14053, 13965,
         13880, 13798, 13719, 13641, 13566, 13493, 13422, 13353, 13286, 13221,
         13157, 13095, 13034, 12975, 12917, 12861, 12805, 12751, 12699, 12647,
         12596, 12547, 12498, 12451, 12404, 12358, 12314, 12270, 12226, 12184,
         12142, 12101, 12061, 12022, 11983, 11945, 11907, 11870, 11834, 11798,
         11763, 11728, 11694, 11661, 11628, 11595, 11563, 11531, 11500, 11469,
         11439, 11409, 11379, 11350, 11322, 11293, 11265, 11238, 11210, 11183,
         11157, 11131, 11105, 11079, 11054, 11029, 11004, 10980, 10955, 10932,
         10908, 10885, 10862, 10839, 10816, 10794, 10772, 10750, 10728};

}