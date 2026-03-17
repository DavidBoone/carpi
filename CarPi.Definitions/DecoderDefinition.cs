using System.Globalization;
using System.Text;

public enum ByteOrder
{
    BigEndian,
    LittleEndian
}

public class DecoderDefinition
{
    public int StartIndex { get; set; }
    public int Length { get; set; } = 1;
    public bool Signed { get; set; }
    public ByteOrder ByteOrder { get; set; } = ByteOrder.BigEndian;
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }

    public double Evaluate(byte[] data)
    {
        if (Length <= 0)
            return 0;

        var slice = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            var index = StartIndex + i;
            slice[i] = index < data.Length ? data[index] : (byte)0;
        }

        long raw = 0;
        if (ByteOrder == ByteOrder.BigEndian)
        {
            foreach (var b in slice)
                raw = (raw << 8) | b;
        }
        else
        {
            for (int i = slice.Length - 1; i >= 0; i--)
                raw = (raw << 8) | slice[i];
        }

        if (Signed)
        {
            int bits = Length * 8;
            long signBit = 1L << (bits - 1);
            if ((raw & signBit) != 0)
                raw -= 1L << bits;
        }

        return (raw * Scale) + Offset;
    }
}

public static class DecoderDefinitionLegacyConverter
{
    public static string? ToLegacyEquation(DecoderDefinition decoder)
    {
        if (decoder.Length <= 0)
            return null;

        var rawExpr = BuildRawExpression(decoder);
        if (rawExpr == null)
            return null;

        string expr = rawExpr;
        if (Math.Abs(decoder.Scale - 1) > double.Epsilon)
            expr = $"({expr})*{FormatNumber(decoder.Scale)}";
        if (Math.Abs(decoder.Offset) > double.Epsilon)
            expr = decoder.Offset > 0
                ? $"({expr})+{FormatNumber(decoder.Offset)}"
                : $"({expr}){FormatNumber(decoder.Offset)}";

        return expr;
    }

    private static string? BuildRawExpression(DecoderDefinition decoder)
    {
        if (decoder.StartIndex < 0)
            return null;

        if (decoder.Length == 1)
            return ByteRef(decoder.StartIndex, decoder.Signed);

        var refs = Enumerable.Range(0, decoder.Length)
            .Select(i => decoder.StartIndex + i)
            .ToArray();

        if (decoder.ByteOrder == ByteOrder.LittleEndian)
            Array.Reverse(refs);

        var builder = new StringBuilder();
        for (int i = 0; i < refs.Length; i++)
        {
            var shift = (refs.Length - i - 1) * 8;
            if (i > 0)
                builder.Append('+');

            var part = ByteRef(refs[i], decoder.Signed && i == 0);
            if (shift > 0)
                builder.Append('(').Append(part).Append('<').Append(shift.ToString(CultureInfo.InvariantCulture)).Append(')');
            else
                builder.Append(part);
        }

        return builder.ToString();
    }

    private static string ByteRef(int index, bool signed)
    {
        string reference = index switch
        {
            0 => "A",
            1 => "B",
            2 => "C",
            3 => "D",
            _ => $"${index}"
        };
        return signed ? $"Signed({reference})" : reference;
    }

    private static string FormatNumber(double value)
        => value.ToString("0.###############", CultureInfo.InvariantCulture);
}
