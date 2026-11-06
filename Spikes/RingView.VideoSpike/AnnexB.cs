namespace RingView.VideoSpike;

/// <summary>Splits an Annex-B H.264 byte stream into access units (for the offline benchmark).</summary>
public static class AnnexB
{
    public static IEnumerable<byte[]> AccessUnits(byte[] stream)
    {
        var nals = new List<(int Start, int End)>();
        int i = 0, start = -1;
        while (i + 3 <= stream.Length)
        {
            int len = stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 ? 3
                : i + 4 <= stream.Length && stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 0 && stream[i + 3] == 1 ? 4 : 0;
            if (len > 0)
            {
                if (start >= 0) nals.Add((start, i));
                start = i;
                i += len;
            }
            else i++;
        }
        if (start >= 0) nals.Add((start, stream.Length));

        int auStart = -1;
        bool sawSlice = false;
        foreach (var (s, e) in nals)
        {
            int header = stream[s + 2] == 1 ? s + 3 : s + 4;
            int type = stream[header] & 0x1f;
            bool isSlice = type is 1 or 5;
            bool firstMb = isSlice && header + 1 < e && (stream[header + 1] & 0x80) != 0;
            bool boundary = (type is 7 or 9 && sawSlice) || (firstMb && sawSlice);
            if (boundary && auStart >= 0)
            {
                yield return stream[auStart..s];
                auStart = s;
                sawSlice = false;
            }
            if (auStart < 0) auStart = s;
            if (isSlice) sawSlice = true;
        }
        if (auStart >= 0) yield return stream[auStart..];
    }
}
