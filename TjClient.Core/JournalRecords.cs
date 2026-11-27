namespace TjClient.Core;

// Границы записей ТЖ задаёт перевод строки вне значения в одинарных или двойных кавычках.
internal static class JournalRecords
{
    public static IEnumerable<long> Ends(FileStream stream, long start, long size, CancellationToken ct)
    {
        stream.Position = start;
        var buffer = new byte[65536];
        byte quote = 0;
        bool afterQuote = false, afterEquals = false, inValue = false;
        while (start < size)
        {
            ct.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, size - start));
            if (count == 0) throw new FileChangedException();
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[i];
                if (quote != 0)
                {
                    if (!afterQuote) { afterQuote = b == quote; continue; }
                    afterQuote = false;
                    if (b == quote) continue;
                    quote = 0;
                }
                if (afterEquals && b is (byte)'\'' or (byte)'"') quote = b;
                afterEquals = !inValue && b == '=';
                if (afterEquals) inValue = true;
                if (b == ',') inValue = false;
                if (b != '\n') continue;
                inValue = false;
                yield return start + i + 1;
            }
            start += count;
        }
    }
}
