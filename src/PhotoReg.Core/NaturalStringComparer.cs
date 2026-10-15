using System.Globalization;

namespace PhotoReg.Core;

public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    private NaturalStringComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var compareInfo = CultureInfo.CurrentCulture.CompareInfo;
        int ix = 0;
        int iy = 0;

        while (ix < x.Length && iy < y.Length)
        {
            bool digitX = char.IsDigit(x[ix]);
            bool digitY = char.IsDigit(y[iy]);
            if (digitX && digitY)
            {
                int startX = ix;
                int startY = iy;
                while (ix < x.Length && char.IsDigit(x[ix]))
                    ix++;
                while (iy < y.Length && char.IsDigit(y[iy]))
                    iy++;

                int numberOrder = CompareNumbers(x, startX, ix - startX, y, startY, iy - startY);
                if (numberOrder != 0)
                    return numberOrder;
            }
            else
            {
                int charOrder = compareInfo.Compare(x, ix, 1, y, iy, 1, CompareOptions.IgnoreCase);
                if (charOrder != 0)
                    return charOrder;
                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static int CompareNumbers(string a, int aStart, int aLen, string b, int bStart, int bLen)
    {
        int aIndex = aStart;
        int bIndex = bStart;
        int aEnd = aStart + aLen;
        int bEnd = bStart + bLen;

        while (aIndex < aEnd && a[aIndex] == '0')
            aIndex++;
        while (bIndex < bEnd && b[bIndex] == '0')
            bIndex++;

        int aDigits = aEnd - aIndex;
        int bDigits = bEnd - bIndex;
        if (aDigits != bDigits)
            return aDigits.CompareTo(bDigits);

        for (int i = 0; i < aDigits; i++)
        {
            int digitOrder = a[aIndex + i] - b[bIndex + i];
            if (digitOrder != 0)
                return digitOrder;
        }

        return aLen.CompareTo(bLen);
    }
}
