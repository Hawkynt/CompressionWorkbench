/*-----------------------------------------------------------------------*
 *                         PITCH_F4.C                   *
 *-----------------------------------------------------------------------*
 * Find the closed loop pitch period with 1/4 subsample resolution.     *
 *-----------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "math_op.h"
//#include "acelp.h"
//#include "cnst.h"
//#include "count.h"

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private const int UP_SAMP = 4;
    private const int L_INTERPOL1 = 4;

    /* Local functions */

    //private unsafe static void Norm_Corr(
    //     short exc[],                         /* (i)     : excitation buffer                     */
    //     short xn[],                          /* (i)     : target vector                         */
    //     short h[],                           /* (i) Q15 : impulse response of synth/wgt filters */
    //     short L_subfr,                       /* (i)     : Length of subframe                    */
    //     short t_min,                         /* (i)     : minimum value of pitch lag.           */
    //     short t_max,                         /* (i)     : maximum value of pitch lag.           */
    //     short corr_norm[],                   /* (o) Q15 : normalized correlation                */
    //     short corr_offset                    /* (i)     : normalized correlation offset.        */
    //);

    //static short Interpol_4(                  /* (o)  : interpolated value  */
    //     short* x,                           /* (i)  : input vector        */
    //     short frac                           /* (i)  : fraction (-4..+3)   */
    //);


    private unsafe short Pitch_fr4(                          /* (o)     : pitch period.                         */
         short* exc,                         /* (i)     : excitation buffer                     */
         short* xn,                          /* (i)     : target vector                         */
         short* h,                           /* (i) Q15 : impulse response of synth/wgt filters */
         short t0_min,                        /* (i)     : minimum value in the searched range.  */
         short t0_max,                        /* (i)     : maximum value in the searched range.  */
         ref short pit_frac,                    /* (o)     : chosen fraction (0, 1, 2 or 3).       */
         short i_subfr,                       /* (i)     : indicator for first subframe.         */
         short t0_fr2,                        /* (i)     : minimum value for resolution 1/2      */
         short t0_fr1,                        /* (i)     : minimum value for resolution 1        */
         short L_subfr                        /* (i)     : Length of subframe                    */
    )
    {
        short i;
        short t_min, t_max;
        short max, t0, fraction, step, temp;
        short corr_v_offset;

        //short[] corr_v = new short[40];                     /* Total length = t0_max-t0_min+1+2*L_inter */
        short* corr_v = stackalloc short[40];                 /* Total length = t0_max-t0_min+1+2*L_inter */

        /* Find interval to compute normalized correlation */

        t_min = sub(t0_min, L_INTERPOL1);
        t_max = add(t0_max, L_INTERPOL1);

        corr_v_offset = (short) -t_min;

        /* Compute normalized correlation between target and filtered excitation */

        Norm_Corr(exc, xn, h, L_subfr, t_min, t_max, corr_v, corr_v_offset);

        /* Find integer pitch */

        max = corr_v[corr_v_offset + t0_min];
        t0 = t0_min;

        for (i = add(t0_min, 1); i <= t0_max; i++)
        {
            if (sub(corr_v[corr_v_offset + i], max) >= 0)
            {
                max = corr_v[corr_v_offset + i];
                t0 = i;
            }
        }

        /* If first subframe and t0 >= t0_fr1, do not search fractionnal pitch */
        if ((i_subfr == 0) && (sub(t0, t0_fr1) >= 0))
        {
            pit_frac = 0;
            return (t0);
        }
        /*------------------------------------------------------------------*
         * Search fractionnal pitch with 1/4 subsample resolution.          *
         * Test the fractions around t0 and choose the one which maximizes  *
         * the interpolated normalized correlation.                         *
         *------------------------------------------------------------------*/

        step = 1;
        fraction = -3;
        if (((i_subfr == 0) && (sub(t0, t0_fr2) >= 0)) || (sub(t0_fr2, PIT_MIN) == 0))
        {
            step = 2;
            fraction = -2;
        }

        if (sub(t0, t0_min) == 0)
        {
            fraction = 0;
        }
        max = Interpol_4(&corr_v[corr_v_offset + t0], fraction);

        for (i = add(fraction, step); i <= 3; i = (short)(i + step))
        {
            temp = Interpol_4(&corr_v[corr_v_offset + t0], i);

            if (sub(temp, max) > 0)
            {
                max = temp;
                fraction = i;
            }
        }

        /* limit the fraction value in the interval [0,1,2,3] */
        if (fraction < 0)
        {
            fraction = add(fraction, UP_SAMP);
            t0 = sub(t0, 1);
        }
        pit_frac = fraction;

        return (t0);
    }


    /*--------------------------------------------------------------------------*
     * Function Norm_Corr()                                                     *
     * ~~~~~~~~~~~~~~~~~~~~                                                     *
     * Find the normalized correlation between the target vector and the        *
     * filtered past excitation.                                                *
     * (correlation between target and filtered excitation divided by the       *
     *  square root of energy of target and filtered excitation).               *
     *--------------------------------------------------------------------------*/

    private unsafe void Norm_Corr(
         short* exc,                         /* (i)     : excitation buffer                     */
         short* xn,                          /* (i)     : target vector                         */
         short* h,                           /* (i) Q15 : impulse response of synth/wgt filters */
         short L_subfr,                       /* (i)     : Length of subframe                    */
         short t_min,                         /* (i)     : minimum value of pitch lag.           */
         short t_max,                         /* (i)     : maximum value of pitch lag.           */
         short* corr_norm,                   /* (o) Q15 : normalized correlation                */
         short corr_offset)                   /* (i)     : normalized correlation offset.        */
    {
        short i, k, t;
        short corr, exp_corr, norm, exp_norm, exp, scale;
        short* excf = stackalloc short[L_SUBFR];
        int L_tmp;

        /* compute the filtered excitation for the first delay t_min */

        k = negate(t_min);
        Convolve(&exc[k], h, excf, L_subfr);

        /* Compute rounded down 1/sqrt(energy of xn[]) */

        L_tmp = 1;
        for (i = 0; i < L_subfr; i++)
            L_tmp = L_mac(L_tmp, xn[i], xn[i]);

        exp = norm_l(L_tmp);
        exp = sub(30, exp);

        exp = add(exp, 2);                     /* energy of xn[] x 2 + rounded up     */
        scale = negate(shr(exp, 1));           /* (1<<scale) < 1/sqrt(energy rounded) */

        /* loop for every possible period */

        for (t = t_min; t <= t_max; t++)
        {
            /* Compute correlation between xn[] and excf[] */

            L_tmp = 1;
            for (i = 0; i < L_subfr; i++)
                L_tmp = L_mac(L_tmp, xn[i], excf[i]);

            exp = norm_l(L_tmp);
            L_tmp = L_shl(L_tmp, exp);
            exp_corr = sub(30, exp);

            corr = extract_h(L_tmp);

            /* Compute 1/sqrt(energy of excf[]) */

            L_tmp = 1;
            for (i = 0; i < L_subfr; i++)
                L_tmp = L_mac(L_tmp, excf[i], excf[i]);

            exp = norm_l(L_tmp);
            L_tmp = L_shl(L_tmp, exp);
            exp_norm = sub(30, exp);

            Isqrt_n(ref L_tmp, ref exp_norm);
            norm = extract_h(L_tmp);

            /* Normalize correlation = correlation * (1/sqrt(energy)) */

            L_tmp = L_mult(corr, norm);
            L_tmp = L_shl(L_tmp, add(add(exp_corr, exp_norm), scale));

            corr_norm[corr_offset + t] = round(L_tmp);

            /* modify the filtered excitation excf[] for the next iteration */

            if (sub(t, t_max) != 0)
            {
                k--;
                for (i = (short)(L_subfr - 1); i > 0; i--)
                {
                    /* saturation can occur in add() */
                    excf[i] = add(mult(exc[k], h[i]), excf[i - 1]);
                }
                excf[0] = mult(exc[k], h[0]);
            }
        }

        return;
    }


    /*--------------------------------------------------------------------------*
     * Procedure Interpol_4()                                                   *
     * ~~~~~~~~~~~~~~~~~~~~~~                                                   *
     * For interpolating the normalized correlation with 1/4 resolution.        *
     *--------------------------------------------------------------------------*/

    /* 1/4 resolution interpolation filter (-3 dB at 0.791*fs/2) in Q14 */

    private static short[] inter4_1 = new short[UP_SAMP * 2 * L_INTERPOL1]
    {
        -12, -26, 32, 206,
        420, 455, 73, -766,
        -1732, -2142, -1242, 1376,
        5429, 9910, 13418, 14746,
        13418, 9910, 5429, 1376,
        -1242, -2142, -1732, -766,
        73, 455, 420, 206,
        32, -26, -12, 0
    };

    /*** Coefficients in floating point
    static float inter4_1[UP_SAMP*L_INTERPOL1+1] = {
       0.900000,
       0.818959,  0.604850,  0.331379,  0.083958,
      -0.075795, -0.130717, -0.105685, -0.046774,
       0.004467,  0.027789,  0.025642,  0.012571,
       0.001927, -0.001571, -0.000753,  0.000000};
    ***/

    private unsafe short Interpol_4(                  /* (o)  : interpolated value  */
         short* x,                           /* (i)  : input vector        */
         short frac                           /* (i)  : fraction (-4..+3)   */
    )
    {
        short i, k, sum;
        int L_sum;

        if (frac < 0)
        {
            frac = add(frac, UP_SAMP);
            x--;
        }
        x = x - L_INTERPOL1 + 1;

        L_sum = 0;
        for (i = 0, k = sub(sub(UP_SAMP, 1), frac); i < 2 * L_INTERPOL1; i++, k += UP_SAMP)
        {
            L_sum = L_mac(L_sum, x[i], inter4_1[k]);
        }

        sum = round(L_shl(L_sum, 1));
        return (sum);
    }

}
