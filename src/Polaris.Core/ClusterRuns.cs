namespace Polaris.Core;

/// <summary>A run of used clusters of a volume.</summary>
public readonly record struct ClusterRun(long Start, long Length);

/// <summary>Turns the allocation bitmap of a volume (bit = cluster in use, lowest bit first) into runs of used
/// clusters. The bitmap comes in pieces; a run may continue from one piece into the next.</summary>
public sealed class ClusterRunBuilder
{
    private readonly List<ClusterRun> _runs = [];
    private long _runStart = -1;
    private long _runLength;
    private long _expectedNext;

    public long UsedClusters { get; private set; }

    /// <summary>Adds <paramref name="bitCount"/> bits of the bitmap that start at cluster <paramref name="startLcn"/>.
    /// The pieces must follow one another.</summary>
    public void Add(ReadOnlySpan<byte> bitmap, long startLcn, long bitCount)
    {
        if (startLcn != _expectedNext)
        {
            throw new ArgumentException($"Bitmapa nepokračuje: čakal som klaster {_expectedNext}, prišiel {startLcn}.");
        }

        if (bitCount > (long)bitmap.Length * 8)
        {
            throw new ArgumentException("Bitmapa je kratšia, než udáva počet klastrov.");
        }

        long i = 0;
        while (i < bitCount)
        {
            if ((i & 7) == 0 && bitCount - i >= 8)
            {
                var b = bitmap[(int)(i >> 3)];
                if (b == 0xFF)
                {
                    Extend(startLcn + i, 8);
                    i += 8;
                    continue;
                }

                if (b == 0)
                {
                    Close();
                    i += 8;
                    continue;
                }
            }

            var used = (bitmap[(int)(i >> 3)] >> (int)(i & 7) & 1) != 0;
            if (used)
            {
                Extend(startLcn + i, 1);
            }
            else
            {
                Close();
            }

            i++;
        }

        _expectedNext = startLcn + bitCount;
    }

    public IReadOnlyList<ClusterRun> Finish()
    {
        Close();
        return _runs;
    }

    private void Extend(long lcn, long count)
    {
        if (_runStart < 0)
        {
            _runStart = lcn;
            _runLength = 0;
        }

        _runLength += count;
        UsedClusters += count;
    }

    private void Close()
    {
        if (_runStart >= 0)
        {
            _runs.Add(new ClusterRun(_runStart, _runLength));
            _runStart = -1;
            _runLength = 0;
        }
    }
}

public static class ClusterRuns
{
    /// <summary>Cuts runs into pieces of at most <paramref name="maxClusters"/> clusters (one read and one write each).</summary>
    public static IEnumerable<ClusterRun> Chunks(IEnumerable<ClusterRun> runs, long maxClusters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxClusters, 1);
        foreach (var run in runs)
        {
            var start = run.Start;
            var left = run.Length;
            while (left > 0)
            {
                var n = Math.Min(left, maxClusters);
                yield return new ClusterRun(start, n);
                start += n;
                left -= n;
            }
        }
    }

    public static long TotalClusters(IEnumerable<ClusterRun> runs) => runs.Sum(r => r.Length);
}
