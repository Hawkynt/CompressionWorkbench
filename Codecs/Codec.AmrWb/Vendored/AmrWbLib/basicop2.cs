
namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
 /*___________________________________________________________________________
 |                                                                           |
 |   Local Functions                                                         |
 |___________________________________________________________________________|
*/
 //   short saturate(int L_var1);

    /*___________________________________________________________________________
     |                                                                           |
     |   Constants and Globals                                                   |
     |___________________________________________________________________________|
    */
    int Overflow = 0;
    int Carry = 0;

    /*___________________________________________________________________________
     |                                                                           |
     |   Functions                                                               |
     |___________________________________________________________________________|
    */

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : saturate                                                |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |    Limit the 32 bit input to the range of a 16 bit word.                  |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var1 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short saturate(int L_var1)
    {
        short var_out;

        if (L_var1 > 0X00007fffL)
        {
            Overflow = 1;
            var_out = short.MaxValue;
        }
        //else if (L_var1 < (int)0xffff8000) -- 8 Jan 24 PHR
        else if (L_var1 < short.MinValue)
        {
            Overflow = 1;
            var_out = short.MinValue;
        }
        else
        {
            var_out = extract_l(L_var1);
        }

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : add                                                     |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |    Performs the addition (var1+var2) with overflow control and saturation;|
     |    the 16 bit result is set at +32767 when overflow occurs or at -32768   |
     |    when underflow occurs.                                                 |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short add(short var1, short var2)
    {
        short var_out;
        int L_sum;

        L_sum = (int)var1 + var2;
        var_out = saturate(L_sum);

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : sub                                                     |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |    Performs the subtraction (var1+var2) with overflow control and satu-   |
     |    ration; the 16 bit result is set at +32767 when overflow occurs or at  |
     |    -32768 when underflow occurs.                                          |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short sub(short var1, short var2)
    {
        short var_out;
        int L_diff;

        L_diff = (int)var1 - var2;
        var_out = saturate(L_diff);
        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : abs_s                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |    Absolute value of var1; abs_s(-32768) = 32767.                         |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0x0000 0000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short abs_s(short var1)
    {
        short var_out;

        if (var1 == short.MinValue)
        {
            var_out = short.MaxValue;
        }
        else
        {
            if (var1 < 0)
            {
                var_out = (short) - var1;
            }
            else
            {
                var_out = var1;
            }
        }

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : shl                                                     |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Arithmetically shift the 16 bit input var1 left var2 positions.Zero fill|
     |   the var2 LSB of the result. If var2 is negative, arithmetically shift   |
     |   var1 right by -var2 with sign extension. Saturate the result in case of |
     |   underflows or overflows.                                                |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short shl(short var1, short var2)
    {
        short var_out;
        int result;

        if (var2 < 0)
        {
            if (var2 < -16)
                var2 = -16;
            var_out = shr(var1, (short) - var2);
        }
        else
        {
            result = (int)var1 * ((int)1 << var2);

            if ((var2 > 15 && var1 != 0) || (result != (int)((short)result)))
            {
                Overflow = 1;
                var_out = (short)((var1 > 0) ? short.MaxValue : short.MinValue);
            }
            else
            {
                var_out = extract_l(result);
            }
        }

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : shr                                                     |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Arithmetically shift the 16 bit input var1 right var2 positions with    |
     |   sign extension. If var2 is negative, arithmetically shift var1 left by  |
     |   -var2 with sign extension. Saturate the result in case of underflows or |
     |   overflows.                                                              |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short shr(short var1, short var2)
    {
        short var_out;

        if (var2 < 0)
        {
            if (var2 < -16)
                var2 = -16;
            var_out = shl(var1, (short) - var2);
        }
        else
        {
            if (var2 >= 15)
            {
                var_out = (short)((var1 < 0) ? -1 : 0);
            }
            else
            {
                if (var1 < 0)
                {
                    var_out = (short)(~((~var1) >> var2));
                }
                else
                {
                    var_out = (short)(var1 >> var2);
                }
            }
        }

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : mult                                                    |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |    Performs the multiplication of var1 by var2 and gives a 16 bit result  |
     |    which is scaled i.e.:                                                  |
     |             mult(var1,var2) = extract_l(L_shr((var1 times var2),15)) and  |
     |             mult(-32768,-32768) = 32767.                                  |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short mult(short var1, short var2)
    {
        short var_out;
        int L_product;

        L_product = (int)var1 * (int)var2;

        // Need to use unchecked in c# to exactly match the C language result
        unchecked
        {
            L_product = (int)var1 * (int)var2;
            L_product = (L_product & (int)0xffff8000L) >> 15;
            if ((L_product & (int)0x00010000) != 0)
                L_product = L_product | (int)0xffff0000L;
        }

        var_out = saturate(L_product);
        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_mult                                                  |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   L_mult is the 32 bit result of the multiplication of var1 times var2    |
     |   with one shift left i.e.:                                               |
     |        L_mult(var1,var2) = L_shl((var1 times var2),1) and                   |
     |        L_mult(-32768,-32768) = 2147483647.                                |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_mult(short var1, short var2)
    {
        int L_var_out;

        L_var_out = (int)var1 * (int)var2;

        if (L_var_out != (int)0x40000000)
        {
            L_var_out *= 2;
        }
        else
        {
            Overflow = 1;
            L_var_out = int.MaxValue;
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : negate                                                  |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Negate var1 with saturation, saturate in the case where input is -32768:|
     |                negate(var1) = sub(0,var1).                                |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short negate(short var1)
    {
        short var_out;

        var_out = (short)((var1 == short.MinValue) ? short.MaxValue : -var1);
        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : extract_h                                               |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Return the 16 MSB of L_var1.                                            |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int ) whose value falls in the |
     |             range : 0x8000 0000 <= L_var1 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short extract_h(int L_var1)
    {
        short var_out;

        var_out = (short)(L_var1 >> 16);
        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : extract_l                                               |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Return the 16 LSB of L_var1.                                            |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int ) whose value falls in the |
     |             range : 0x8000 0000 <= L_var1 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short extract_l(int L_var1)
    {
        short var_out;

        var_out = (short)L_var1;

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : round                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Round the lower 16 bits of the 32 bit input number into the MS 16 bits  |
     |   with saturation. Shift the resulting bits right by 16 and return the 16 |
     |   bit number:                                                             |
     |               round(L_var1) = extract_h(L_add(L_var1,32768))              |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int ) whose value falls in the |
     |             range : 0x8000 0000 <= L_var1 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short round(int L_var1)
    {
        short var_out;
        int L_rounded;

        L_rounded = L_add(L_var1, (int)0x00008000);

        var_out = extract_h(L_rounded);
        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_mac                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Multiply var1 by var2 and shift the result left by 1. Add the 32 bit    |
     |   result to L_var3 with saturation, return a 32 bit result:               |
     |        L_mac(L_var3,var1,var2) = L_add(L_var3,L_mult(var1,var2)).         |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var3   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_mac(int L_var3, short var1, short var2)
    {
        int L_var_out;
        int L_product;

        L_product = L_mult(var1, var2);

        L_var_out = L_add(L_var3, L_product);
        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_msu                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Multiply var1 by var2 and shift the result left by 1. Subtract the 32   |
     |   bit result to L_var3 with saturation, return a 32 bit result:           |
     |        L_msu(L_var3,var1,var2) = L_sub(L_var3,L_mult(var1,var2)).         |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var3   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_msu(int L_var3, short var1, short var2)
    {
        int L_var_out;
        int L_product;

        L_product = L_mult(var1, var2);

        L_var_out = L_sub(L_var3, L_product);
        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_macNs                                                 |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Multiply var1 by var2 and shift the result left by 1. Add the 32 bit    |
     |   result to L_var3 without saturation, return a 32 bit result. Generate   |
     |   carry and overflow values :                                             |
     |        L_macNs(L_var3,var1,var2) = L_add_c(L_var3,L_mult(var1,var2)).     |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var3   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |                                                                           |
     |   Caution :                                                               |
     |                                                                           |
     |    In some cases the Carry flag has to be cleared or set before using     |
     |    operators which take into account its value.                           |
     |___________________________________________________________________________|
    */

    private int L_macNs(int L_var3, short var1, short var2)
    {
        int L_var_out;

        L_var_out = L_mult(var1, var2);

        L_var_out = L_add_c(L_var3, L_var_out);

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_msuNs                                                 |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Multiply var1 by var2 and shift the result left by 1. Subtract the 32   |
     |   bit result from L_var3 without saturation, return a 32 bit result. Ge-  |
     |   nerate carry and overflow values :                                      |
     |        L_msuNs(L_var3,var1,var2) = L_sub_c(L_var3,L_mult(var1,var2)).     |
     |                                                                           |
     |   Complexity weight : 1                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var3   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |                                                                           |
     |   Caution :                                                               |
     |                                                                           |
     |    In some cases the Carry flag has to be cleared or set before using     |
     |    operators which take into account its value.                           |
     |___________________________________________________________________________|
    */

    private int L_msuNs(int L_var3, short var1, short var2)
    {
        int L_var_out;

        L_var_out = L_mult(var1, var2);

        L_var_out = L_sub_c(L_var3, L_var_out);
        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_add                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   32 bits addition of the two 32 bits variables (L_var1+L_var2) with      |
     |   overflow control and saturation; the result is set at +2147483647 when  |
     |   overflow occurs or at -2147483648 when underflow occurs.                |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    L_var2   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_add(int L_var1, int L_var2)
    {
        int L_var_out;

        //L_var_out = L_var1 + L_var2;

        //if (((L_var1 ^ L_var2) & int.MinValue) == 0)
        //{
        //    if ((L_var_out ^ L_var1) & int.MinValue)
        //    {
        //        L_var_out = (L_var1 < 0) ? int.MinValue : int.MaxValue;
        //        Overflow = 1;
        //    }
        //}

        // 3 May 25 PHR -- use unchecked to get the exact same result as the C code
        unchecked
        {
            L_var_out = L_var1 + L_var2;

            if (((L_var1 ^ L_var2) & int.MinValue) == 0)
            {
                if (((L_var_out ^ L_var1) & int.MinValue) != 0)
                {
                    L_var_out = (L_var1 < 0) ? int.MinValue : int.MaxValue;
                    Overflow = 1;
                }
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_sub                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   32 bits subtraction of the two 32 bits variables (L_var1-L_var2) with   |
     |   overflow control and saturation; the result is set at +2147483647 when  |
     |   overflow occurs or at -2147483648 when underflow occurs.                |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    L_var2   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_sub(int L_var1, int L_var2)
    {
        int L_var_out;

        // 3 May 25 PHR -- use unchecked to get the exact same value as the C code
        unchecked
        {
            L_var_out = L_var1 - L_var2;
            if (((L_var1 ^ L_var2) & int.MinValue) != 0)
            {
                if (((L_var_out ^ L_var1) & int.MinValue) != 0)
                {
                    L_var_out = (L_var1 < 0L) ? int.MinValue : int.MaxValue;
                    Overflow = 1;
                }
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_add_c                                                 |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Performs 32 bits addition of the two 32 bits variables (L_var1+L_var2+C)|
     |   with carry. No saturation. Generate carry and Overflow values. The car- |
     |   ry and overflow values are binary variables which can be tested and as- |
     |   signed values.                                                          |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    L_var2   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |                                                                           |
     |   Caution :                                                               |
     |                                                                           |
     |    In some cases the Carry flag has to be cleared or set before using     |
     |    operators which take into account its value.                           |
     |___________________________________________________________________________|
    */
    private int L_add_c(int L_var1, int L_var2)
    {
        int L_var_out;
        int L_test;
        //long L_test;    // 8 Jan 24 PHR

        // 3 May 25 PHR -- use unchecked to get the exact same result as the C code
        unchecked
        {
            int carry_int = 0;

            L_var_out = L_var1 + L_var2 + Carry;

            L_test = L_var1 + L_var2;

            if ((L_var1 > 0) && (L_var2 > 0) && (L_test < 0))
            {
                Overflow = 1;
                carry_int = 0;
            }
            else
            {
                if ((L_var1 < 0) && (L_var2 < 0))
                {
                    if (L_test >= 0)
                    {
                        Overflow = 1;
                        carry_int = 1;
                    }
                    else
                    {
                        Overflow = 0;
                        carry_int = 1;
                    }
                }
                else
                {
                    if (((L_var1 ^ L_var2) < 0) && (L_test >= 0))
                    {
                        Overflow = 0;
                        carry_int = 1;
                    }
                    else
                    {
                        Overflow = 0;
                        carry_int = 0;
                    }
                }
            }

            if (Carry == 1)
            {
                if (L_test == int.MaxValue)
                {
                    Overflow = 1;
                    Carry = carry_int;
                }
                else
                {
                    if (L_test == (int)0xFFFFFFFFL)
                    {
                        Carry = 1;
                    }
                    else
                    {
                        Carry = carry_int;
                    }
                }
            }
            else
            {
                Carry = carry_int;
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_sub_c                                                 |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Performs 32 bits subtraction of the two 32 bits variables with carry    |
     |   (borrow) : L_var1-L_var2-C. No saturation. Generate carry and Overflow  |
     |   values. The carry and overflow values are binary variables which can    |
     |   be tested and assigned values.                                          |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    L_var2   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |                                                                           |
     |   Caution :                                                               |
     |                                                                           |
     |    In some cases the Carry flag has to be cleared or set before using     |
     |    operators which take into account its value.                           |
     |___________________________________________________________________________|
    */

    private int L_sub_c(int L_var1, int L_var2)
    {
        int L_var_out;
        int L_test;
        int carry_int = 0;

        if (Carry == 1)
        {
            Carry = 0;
            if (L_var2 != int.MinValue)
            {
                L_var_out = L_add_c(L_var1, -L_var2);
            }
            else
            {
                L_var_out = L_var1 - L_var2;
                if (L_var1 > 0L)
                {
                    Overflow = 1;
                    Carry = 0;
                }
            }
        }
        else
        {
            L_var_out = L_var1 - L_var2 - (int)0X00000001L;
            L_test = L_var1 - L_var2;

            if ((L_test < 0) && (L_var1 > 0) && (L_var2 < 0))
            {
                Overflow = 1;
                carry_int = 0;
            }
            else if ((L_test > 0) && (L_var1 < 0) && (L_var2 > 0))
            {
                Overflow = 1;
                carry_int = 1;
            }
            else if ((L_test > 0) && ((L_var1 ^ L_var2) > 0))
            {
                Overflow = 0;
                carry_int = 1;
            }
            if (L_test == int.MinValue)
            {
                Overflow = 1;
                Carry = carry_int;
            }
            else
            {
                Carry = carry_int;
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_negate                                                |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Negate the 32 bit variable L_var1 with saturation; saturate in the case |
     |   where input is -2147483648 (0x8000 0000).                               |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_negate(int L_var1)
    {
        int L_var_out;

        L_var_out = (L_var1 == int.MinValue) ? int.MaxValue : -L_var1;

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : mult_r                                                  |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Same as mult with rounding, i.e.:                                       |
     |     mult_r(var1,var2) = extract_l(L_shr(((var1 * var2) + 16384),15)) and  |
     |     mult_r(-32768,-32768) = 32767.                                        |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the  |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the  |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short mult_r(short var1, short var2)
    {
        short var_out;
        int L_product_arr;

        // 3 May 25 PHR -- use unchecked to get the same result as the C code
        unchecked
        {
            L_product_arr = (int)var1 * (int)var2;       /* product */
            L_product_arr += (int)0x00004000L;      /* round */
            L_product_arr &= (int)0xffff8000L;
            L_product_arr >>= 15;       /* shift */

            if ((L_product_arr & (int)0x00010000L) != 0)   /* sign extend when necessary */
            {
                L_product_arr |= (int)0xffff0000L;
            }

            var_out = saturate(L_product_arr);
        }

        //L_product_arr = (int)var1 * (int)var2;       /* product */
        //L_product_arr += (int)0x00004000L;      /* round */
        //L_product_arr &= (int)0xffff8000L;
        //L_product_arr >>= 15;       /* shift */

        //if (L_product_arr & (int)0x00010000L)   /* sign extend when necessary */
        //{
        //    L_product_arr |= (int)0xffff0000L;
        //}

        // 8 Jan 24 PHR
        //L_product_arr = extract_l(L_shr(((var1 * var2) + 16384), 15));
        //var_out = saturate(L_product_arr);

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_shl                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Arithmetically shift the 32 bit input L_var1 left var2 positions. Zero  |
     |   fill the var2 LSB of the result. If var2 is negative, arithmetically    |
     |   shift L_var1 right by -var2 with sign extension. Saturate the result in |
     |   case of underflows or overflows.                                        |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_shl(int L_var1, short var2)
    {
        int L_var_out = 0;

        if (var2 <= 0)
        {
            if (var2 < -32)
                var2 = -32;

            L_var_out = L_shr(L_var1, (short) -var2);
        }
        else
        {
            for (; var2 > 0; var2--)
            {
                if (L_var1 > (int)0X3fffffffL)
                {
                    Overflow = 1;
                    L_var_out = int.MaxValue;
                    break;
                }
                else
                {
                    int Temp = -1073741824; // 8 Jan 24 PHR -- corresponds to 0xc0000000
                    if (L_var1 < Temp)
                    {
                        Overflow = 1;
                        L_var_out = int.MinValue;
                        break;
                    }
                }
                L_var1 *= 2;
                L_var_out = L_var1;
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_shr                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Arithmetically shift the 32 bit input L_var1 right var2 positions with  |
     |   sign extension. If var2 is negative, arithmetically shift L_var1 left   |
     |   by -var2 and zero fill the -var2 LSB of the result. Saturate the result |
     |   in case of underflows or overflows.                                     |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var_out <= 0x7fff ffff.              |
     |___________________________________________________________________________|
    */

    private int L_shr(int L_var1, short var2)
    {
        int L_var_out;

        if (var2 < 0)
        {
            if (var2 < -32)
                var2 = -32;

            L_var_out = L_shl(L_var1, (short) -var2);
        }
        else
        {
            if (var2 >= 31)
            {
                L_var_out = (L_var1 < 0L) ? -1 : 0;
            }
            else
            {
                if (L_var1 < 0)
                {
                    L_var_out = ~((~L_var1) >> var2);
                }
                else
                {
                    L_var_out = L_var1 >> var2;
                }
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : shr_r                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Same as shr(var1,var2) but with rounding. Saturate the result in case of|
     |   underflows or overflows :                                               |
     |    - If var2 is greater than zero :                                       |
     |          if (sub(shl(shr(var1,var2),1),shr(var1,sub(var2,1))))            |
     |          is equal to zero                                                 |
     |                     then                                                  |
     |                     shr_r(var1,var2) = shr(var1,var2)                     |
     |                     else                                                  |
     |                     shr_r(var1,var2) = add(shr(var1,var2),1)              |
     |    - If var2 is less than or equal to zero :                              |
     |                     shr_r(var1,var2) = shr(var1,var2).                    |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private short shr_r(short var1, short var2)
    {
        short var_out;

        if (var2 > 15)
        {
            var_out = 0;
        }
        else
        {
            var_out = shr(var1, var2);

            if (var2 > 0)
            {
                if ((var1 & ((short)1 << (var2 - 1))) != 0)
                {
                    var_out++;
                }
            }
        }

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : mac_r                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Multiply var1 by var2 and shift the result left by 1. Add the 32 bit    |
     |   result to L_var3 with saturation. Round the LS 16 bits of the result    |
     |   into the MS 16 bits with saturation and shift the result right by 16.   |
     |   Return a 16 bit result.                                                 |
     |            mac_r(L_var3,var1,var2) = round(L_mac(L_var3,var1,var2))       |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var3   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0x0000 8000 <= L_var_out <= 0x0000 7fff.              |
     |___________________________________________________________________________|
    */

    private short mac_r(int L_var3, short var1, short var2)
    {
        short var_out;

        L_var3 = L_mac(L_var3, var1, var2);

        L_var3 = L_add(L_var3, (int)0x00008000L);

        var_out = extract_h(L_var3);

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : msu_r                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Multiply var1 by var2 and shift the result left by 1. Subtract the 32   |
     |   bit result to L_var3 with saturation. Round the LS 16 bits of the res-  |
     |   ult into the MS 16 bits with saturation and shift the result right by   |
     |   16. Return a 16 bit result.                                             |
     |            msu_r(L_var3,var1,var2) = round(L_msu(L_var3,var1,var2))       |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var3   32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= L_var3 <= 0x7fff ffff.                 |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0x0000 8000 <= L_var_out <= 0x0000 7fff.              |
     |___________________________________________________________________________|
    */

    private short msu_r(int L_var3, short var1, short var2)
    {
        short var_out;

        L_var3 = L_msu(L_var3, var1, var2);

        L_var3 = L_add(L_var3, (int)0x00008000L);

        var_out = extract_h(L_var3);

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_deposit_h                                             |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Deposit the 16 bit var1 into the 16 MS bits of the 32 bit output. The   |
     |   16 LS bits of the output are zeroed.                                    |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= var_out <= 0x7fff 0000.                |
     |___________________________________________________________________________|
    */

    private int L_deposit_h(short var1)
    {
        int L_var_out;

        L_var_out = (int)var1 << 16;

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_deposit_l                                             |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Deposit the 16 bit var1 into the 16 LS bits of the 32 bit output. The   |
     |   16 MS bits of the output are sign extended.                             |
     |                                                                           |
     |   Complexity weight : 2                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0xFFFF 8000 <= var_out <= 0x0000 7fff.                |
     |___________________________________________________________________________|
    */

    private int L_deposit_l(short var1)
    {
        int L_var_out;

        L_var_out = (int)var1;

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_shr_r                                                 |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Same as L_shr(L_var1,var2) but with rounding. Saturate the result in    |
     |   case of underflows or overflows :                                       |
     |    - If var2 is greater than zero :                                       |
     |          if (L_sub(L_shl(L_shr(L_var1,var2),1),L_shr(L_var1,sub(var2,1))))|
     |          is equal to zero                                                 |
     |                     then                                                  |
     |                     L_shr_r(L_var1,var2) = L_shr(L_var1,var2)             |
     |                     else                                                  |
     |                     L_shr_r(L_var1,var2) = L_add(L_shr(L_var1,var2),1)    |
     |    - If var2 is less than or equal to zero :                              |
     |                     L_shr_r(L_var1,var2) = L_shr(L_var1,var2).            |
     |                                                                           |
     |   Complexity weight : 3                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= var1 <= 0x7fff ffff.                   |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= var_out <= 0x7fff ffff.                |
     |___________________________________________________________________________|
    */

    private int L_shr_r(int L_var1, short var2)
    {
        int L_var_out;

        if (var2 > 31)
        {
            L_var_out = 0;
        }
        else
        {
            L_var_out = L_shr(L_var1, var2);

            if (var2 > 0)
            {
                if ((L_var1 & ((int)1 << (var2 - 1))) != 0)
                {
                    L_var_out++;
                }
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_abs                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |    Absolute value of L_var1; Saturate in case where the input is          |
     |                                                               -214783648  |
     |                                                                           |
     |   Complexity weight : 3                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= var1 <= 0x7fff ffff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x0000 0000 <= var_out <= 0x7fff ffff.                |
     |___________________________________________________________________________|
    */

    private int L_abs(int L_var1)
    {
        int L_var_out;

        if (L_var1 == int.MinValue)
        {
            L_var_out = int.MaxValue;
        }
        else
        {
            if (L_var1 < 0)
            {
                L_var_out = -L_var1;
            }
            else
            {
                L_var_out = L_var1;
            }
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : L_sat                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |    32 bit L_var1 is set to 2147483647 if an overflow occured or to        |
     |    -2147483648 if an underflow occured on the most recent L_add_c,        |
     |    L_sub_c, L_macNs or L_msuNs operations. The carry and overflow values  |
     |    are binary values which can be tested and assigned values.             |
     |                                                                           |
     |   Complexity weight : 4                                                   |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= var1 <= 0x7fff ffff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    L_var_out                                                              |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= var_out <= 0x7fff ffff.                |
     |___________________________________________________________________________|
    */

    private int L_sat(int L_var1)
    {
        int L_var_out;

        L_var_out = L_var1;

        if (Overflow == 1)
        {

            if (Carry == 1)
            {
                L_var_out = int.MinValue;
            }
            else
            {
                L_var_out = int.MaxValue;
            }

            Carry = 0;
            Overflow = 0;
        }

        return (L_var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : norm_s                                                  |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Produces the number of left shift needed to normalize the 16 bit varia- |
     |   ble var1 for positive values on the interval with minimum of 16384 and  |
     |   maximum of 32767, and for negative values on the interval with minimum  |
     |   of -32768 and maximum of -16384; in order to normalize the result, the  |
     |   following operation must be done :                                      |
     |                    norm_var1 = shl(var1,norm_s(var1)).                    |
     |                                                                           |
     |   Complexity weight : 15                                                  |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0xffff 8000 <= var1 <= 0x0000 7fff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0x0000 0000 <= var_out <= 0x0000 000f.                |
     |___________________________________________________________________________|
    */

    private short norm_s(short var1)
    {
        short var_out;

        if (var1 == 0)
        {
            var_out = 0;
        }
        else
        {
            if (var1 == -1)
            {
                var_out = 15;
            }
            else
            {
                if (var1 < 0)
                {
                    var1 = (short)~var1;
                }
                for (var_out = 0; var1 < 0x4000; var_out++)
                {
                    var1 <<= 1;
                }
            }
        }

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : div_s                                                   |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Produces a result which is the fractional integer division of var1  by  |
     |   var2; var1 and var2 must be positive and var2 must be greater or equal  |
     |   to var1; the result is positive (leading bit equal to 0) and truncated  |
     |   to 16 bits.                                                             |
     |   If var1 = var2 then div(var1,var2) = 32767.                             |
     |                                                                           |
     |   Complexity weight : 18                                                  |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    var1                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0x0000 0000 <= var1 <= var2 and var2 != 0.            |
     |                                                                           |
     |    var2                                                                   |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : var1 <= var2 <= 0x0000 7fff and var2 != 0.            |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0x0000 0000 <= var_out <= 0x0000 7fff.                |
     |             It's a Q15 value (point between b15 and b14).                 |
     |___________________________________________________________________________|
    */

    private short div_s(short var1, short var2)
    {
        short var_out = 0;
        short iteration;
        int L_num;
        int L_denom;

        if ((var1 > var2) || (var1 < 0) || (var2 < 0))
        {
            throw new ArgumentException($"var1 = {var1}, var2 = {var2}");
            //printf("Division Error var1=%d  var2=%d\n", var1, var2);
            //abort(); /* exit (0); */
        }
        if (var2 == 0)
        {
            throw new ArgumentException($"Division by zero. var2 == 0");
            //printf("Division by 0, Fatal error \n");
            //abort(); /* exit (0); */
        }
        if (var1 == 0)
        {
            var_out = 0;
        }
        else
        {
            if (var1 == var2)
            {
                var_out = short.MaxValue;
            }
            else
            {
                L_num = L_deposit_l(var1);
                L_denom = L_deposit_l(var2);
                for (iteration = 0; iteration < 15; iteration++)
                {
                    var_out <<= 1;
                    L_num <<= 1;

                    if (L_num >= L_denom)
                    {
                        L_num = L_sub(L_num, L_denom);
                        var_out = add(var_out, 1);
                    }
                }
            }
        }

        return (var_out);
    }

    /*___________________________________________________________________________
     |                                                                           |
     |   Function Name : norm_l                                                  |
     |                                                                           |
     |   Purpose :                                                               |
     |                                                                           |
     |   Produces the number of left shifts needed to normalize the 32 bit varia-|
     |   ble L_var1 for positive values on the interval with minimum of          |
     |   1073741824 and maximum of 2147483647, and for negative values on the in-|
     |   terval with minimum of -2147483648 and maximum of -1073741824; in order |
     |   to normalize the result, the following operation must be done :         |
     |                   norm_L_var1 = L_shl(L_var1,norm_l(L_var1)).             |
     |                                                                           |
     |   Complexity weight : 30                                                  |
     |                                                                           |
     |   Inputs :                                                                |
     |                                                                           |
     |    L_var1                                                                 |
     |             32 bit long signed integer (int) whose value falls in the  |
     |             range : 0x8000 0000 <= var1 <= 0x7fff ffff.                   |
     |                                                                           |
     |   Outputs :                                                               |
     |                                                                           |
     |    none                                                                   |
     |                                                                           |
     |   Return Value :                                                          |
     |                                                                           |
     |    var_out                                                                |
     |             16 bit short signed integer (short) whose value falls in the |
     |             range : 0x0000 0000 <= var_out <= 0x0000 001f.                |
     |___________________________________________________________________________|
    */

    private short norm_l(int L_var1)
    {
        short var_out;

        // 3 May 25 PHR -- use unchecked to get the same result as the C code
        unchecked
        {
            if (L_var1 == 0)
            {
                var_out = 0;
            }
            else
            {
                if (L_var1 == (int)0xffffffffL)
                //if (L_var1 == Temp)
                {
                    var_out = 31;
                }
                else
                {
                    if (L_var1 < 0)
                    {
                        L_var1 = ~L_var1;
                    }
                    for (var_out = 0; L_var1 < (int)0x40000000; var_out++)
                    {
                        L_var1 <<= 1;
                    }
                }
            }
        }

        //if (L_var1 == 0)
        //{
        //    var_out = 0;
        //}
        //else
        //{
        //    int Temp = -1; // 8 Jan 24 PHR -- corresponds to 0xffffffff
        //    // if (L_var1 == int)0xffffffffL)
        //    if (L_var1 == Temp)
        //    {
        //        var_out = 31;
        //    }
        //    else
        //    {
        //        if (L_var1 < 0)
        //        {
        //            L_var1 = ~L_var1;
        //        }
        //        for (var_out = 0; L_var1 < (int)0x40000000; var_out++)
        //        {
        //            L_var1 <<= 1;
        //        }
        //    }
        //}

        return (var_out);
    }

}
