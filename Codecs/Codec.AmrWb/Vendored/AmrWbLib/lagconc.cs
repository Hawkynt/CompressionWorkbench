
namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    /*---------------------------------------------------------*
     *                         LAGCONC.C                       *
     *---------------------------------------------------------*
     * Concealment of LTP lags during bad frames               *
     *---------------------------------------------------------*/

    //private const int L_LTPHIST = 5;
    private const int ONE_PER_3 = 10923;
    private const int ONE_PER_LTPHIST = 6554;

    //void insertion_sort(short array[], short n);
    //void insert(short array[], short num, short x);

    private void Init_Lagconc(short[] lag_hist)
    {
        short i;

        for (i = 0; i < L_LTPHIST; i++)
        {
            lag_hist[i] = 64;
        }
    }

    private unsafe void lagconc(
         short[] gain_hist,                   /* (i) : Gain history     */
         int gain_hist_index,                 // 12 May 25 PHR -- added the index into gain_hist
         short[] lag_hist,                    /* (i) : Subframe size    */
         short* T0,
         ref short old_T0,
         ref short seed,
         short unusable_frame
    )
    {
        short maxLag, minLag, lastLag, lagDif, meanLag = 0;
        short[] lag_hist2 = new short[L_LTPHIST];
        short i, tmp, tmp2;
        short minGain, lastGain, secLastGain;
        short D, D2;

        /* Is lag index such that it can be aplied directly or does it has to be subtituted */

        lastGain = gain_hist[4 + gain_hist_index];
        secLastGain = gain_hist[3 + gain_hist_index];

        lastLag = lag_hist[0];

        /***********SMALLEST history lag***********/
        minLag = lag_hist[0];
        for (i = 1; i < L_LTPHIST; i++)
        {
            if (sub(lag_hist[i], minLag) < 0)
            {
                minLag = lag_hist[i];
            }
        }
        /*******BIGGEST history lag*******/
        maxLag = lag_hist[0];
        for (i = 1; i < L_LTPHIST; i++)
        {
            if (sub(lag_hist[i], maxLag) > 0)
            {
                maxLag = lag_hist[i];
            }
        }
        /***********SMALLEST history gain***********/
        minGain = gain_hist[0 + gain_hist_index];
        for (i = 1; i < L_LTPHIST; i++)
        {
            if (sub(gain_hist[i + gain_hist_index], minGain) < 0)
            {
                minGain = gain_hist[i + gain_hist_index];
            }
        }
        /***Difference between MAX and MIN lag**/
        lagDif = sub(maxLag, minLag);

        if (unusable_frame != 0)
        {
            /* LTP-lag for RX_SPEECH_LOST */
            /**********Recognition of the LTP-history*********/
            if ((sub(minGain, 8192) > 0) && (sub(lagDif, 10) < 0))
            {
                *T0 = old_T0;
            }
            else if (sub(lastGain, 8192) > 0 && sub(secLastGain, 8192) > 0)
            {
                *T0 = lag_hist[0];
            }
            else
            {
                /********SORT************/
                /* The sorting of the lag history */
                for (i = 0; i < L_LTPHIST; i++)
                {
                    lag_hist2[i] = lag_hist[i];
                }
                insertion_sort(lag_hist2, 5);

                /* Lag is weighted towards bigger lags */
                /* and random variation is added */
                lagDif = sub(lag_hist2[4], lag_hist2[2]);

                if (sub(lagDif, 40) > 0)
                    lagDif = 40;

                D = Random(ref seed);              /* D={-1, ...,1} */
                /* D2={-lagDif/2..lagDif/2} */
                tmp = shr(lagDif, 1);
                D2 = mult(tmp, D);
                tmp = add(add(lag_hist2[2], lag_hist2[3]), lag_hist2[4]);
                *T0 = add(mult(tmp, ONE_PER_3), D2);
            }
            /* New lag is not allowed to be bigger or smaller than last lag values */
            if (sub(*T0, maxLag) > 0)
            {
                *T0 = maxLag;
            }
            if (sub(*T0, minLag) < 0)
            {
                *T0 = minLag;
            }
        }
        else
        {
            /* LTP-lag for RX_BAD_FRAME */

            /***********MEAN lag**************/
            meanLag = 0;
            for (i = 0; i < L_LTPHIST; i++)
            {
                meanLag = add(meanLag, lag_hist[i]);
            }
            meanLag = mult(meanLag, ONE_PER_LTPHIST);

            tmp = sub(*T0, maxLag);
            tmp2 = sub(*T0, lastLag);

            if (sub(lagDif, 10) < 0 && (sub(*T0, sub(minLag, 5)) > 0) && (sub(tmp, 5) < 0))
            {
                *T0 = *T0;
            }
            else if (sub(lastGain, 8192) > 0 && sub(secLastGain, 8192) > 0 && (add(tmp2, 10) > 0 && sub(tmp2, 10) < 0))
            {
                *T0 = *T0;
            }
            else if (sub(minGain, 6554) < 0 && sub(lastGain, minGain) == 0 && (sub(*T0, minLag) > 0 && sub(*T0, maxLag) < 0))
            {
                *T0 = *T0;
            }
            else if (sub(lagDif, 70) < 0 && sub(*T0, minLag) > 0 && sub(*T0, maxLag) < 0)
            {
                *T0 = *T0;
            }
            else if (sub(*T0, meanLag) > 0 && sub(*T0, maxLag) < 0)
            {
                *T0 = *T0;
            }
            else
            {
                if ((sub(minGain, 8192) > 0) && (sub(lagDif, 10) < 0))
                {
                    *T0 = lag_hist[0];
                }
                else if (sub(lastGain, 8192) > 0 && sub(secLastGain, 8192) > 0)
                {
                    *T0 = lag_hist[0];
                }
                else
                {
                    /********SORT************/
                    /* The sorting of the lag history */
                    for (i = 0; i < L_LTPHIST; i++)
                    {
                        lag_hist2[i] = lag_hist[i];
                    }
                    insertion_sort(lag_hist2, 5);

                    /* Lag is weighted towards bigger lags */
                    /* and random variation is added */
                    lagDif = sub(lag_hist2[4], lag_hist2[2]);
                    if (sub(lagDif, 40) > 0)
                        lagDif = 40;

                    D = Random(ref seed);          /* D={-1,.., 1} */
                    /* D2={-lagDif/2..lagDif/2} */
                    tmp = shr(lagDif, 1);
                    D2 = mult(tmp, D);
                    tmp = add(add(lag_hist2[2], lag_hist2[3]), lag_hist2[4]);
                    *T0 = add(mult(tmp, ONE_PER_3), D2);
                }
                /* New lag is not allowed to be bigger or smaller than last lag values */
                if (sub(*T0, maxLag) > 0)
                {
                    *T0 = maxLag;
                }
                if (sub(*T0, minLag) < 0)
                {
                    *T0 = minLag;
                }
            }
        }
    }

    private void insertion_sort(short[] array, short n)
    {
        short i;

        for (i = 0; i < n; i++)
        {
            insert(array, i, array[i]);
        }
    }


    private void insert(short[] array, short n, short x)
    {
        short i;

        for (i = (short)(n - 1); i >= 0; i--)
        {
            if (sub(x, array[i]) < 0)
            {
                array[i + 1] = array[i];
            }
            else
                break;
        }
        array[i + 1] = x;
    }
}
