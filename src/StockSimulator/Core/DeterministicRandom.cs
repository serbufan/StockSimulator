namespace StockSimulator.Core;

/// <summary>
/// 确定性伪随机数发生器（SplitMix64 + Box-Muller）。
/// 为什么不用 System.Random：不同 .NET 版本/平台的 Random 实现可能不同，
/// 而本工具要求“同一种子必定生成同一份K线”，便于回测复现与单元测试。
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;
    private double _spareGaussian;
    private bool _hasSpare;

    public DeterministicRandom(int seed)
    {
        _state = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL) ^ 0x5DEECE66DUL;
        if (_state == 0) _state = 0x2545F4914F6CDD1DUL;
    }

    private ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>[0,1) 均匀分布。</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>标准正态分布（Box-Muller，缓存互补值）。</summary>
    public double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spareGaussian;
        }

        double u1, u2, s;
        do
        {
            u1 = NextDouble() * 2.0 - 1.0;
            u2 = NextDouble() * 2.0 - 1.0;
            s = u1 * u1 + u2 * u2;
        } while (s >= 1.0 || s <= double.Epsilon);

        var mul = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spareGaussian = u2 * mul;
        _hasSpare = true;
        return u1 * mul;
    }

    /// <summary>[-1,1] 对称随机。</summary>
    public double NextSigned() => NextDouble() * 2.0 - 1.0;

    /// <summary>以给定概率返回 true。</summary>
    public bool Chance(double probability) => probability > 0 && NextDouble() < probability;

    /// <summary>[min,max) 之间均匀分布。</summary>
    public double NextRange(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>[0,max) 之间的整数。</summary>
    public int NextInt(int max) => max <= 0 ? 0 : (int)(NextUInt64() % (ulong)max);
}
