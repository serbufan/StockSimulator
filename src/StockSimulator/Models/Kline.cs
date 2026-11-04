using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace StockSimulator.Models;

/// <summary>
/// 单根K线（OHLCV）。
/// 说明：仅依赖 BCL 的 INotifyPropertyChanged，不含任何 WPF/ScottPlot 依赖，
/// 因此可直接被 Core 引擎与单元测试使用，同时又能直接绑定到界面。
/// </summary>
public sealed class Kline : INotifyPropertyChanged
{
    private DateTime _time;
    private double _open;
    private double _high;
    private double _low;
    private double _close;
    private long _volume;
    private double _amount;
    private string _flag = string.Empty;
    private double _adjFactor = 1.0;

    public Kline()
    {
    }

    public Kline(DateTime time, double open, double high, double low, double close,
        long volume = 0, double amount = 0, string flag = "")
    {
        _time = time;
        _open = open;
        _high = high;
        _low = low;
        _close = close;
        _volume = volume;
        _amount = amount;
        _flag = flag ?? string.Empty;
    }

    public DateTime Time
    {
        get => _time;
        set => Set(ref _time, value);
    }

    public double Open
    {
        get => _open;
        set => Set(ref _open, value);
    }

    public double High
    {
        get => _high;
        set => Set(ref _high, value);
    }

    public double Low
    {
        get => _low;
        set => Set(ref _low, value);
    }

    public double Close
    {
        get => _close;
        set => Set(ref _close, value);
    }

    public long Volume
    {
        get => _volume;
        set => Set(ref _volume, value);
    }

    /// <summary>成交额（元）。留空时导出可按均价*成交量估算。</summary>
    public double Amount
    {
        get => _amount;
        set => Set(ref _amount, value);
    }

    /// <summary>
    /// 标记位："" 正常，S 停牌，U 涨停，D 跌停，X 除权除息，G 跳空。
    /// 多个标记可组合（如 "XG" 表示除权日且跳空）。
    /// 导出 CSV 时写入 Flag 列，回测软件可用于过滤。
    /// </summary>
    public string Flag
    {
        get => _flag;
        set => Set(ref _flag, value ?? string.Empty);
    }

    /// <summary>复权因子（1.0 表示原始价）。除权除息时累乘。</summary>
    public double AdjFactor
    {
        get => _adjFactor;
        set => Set(ref _adjFactor, value);
    }

    /// <summary>ScottPlot 使用的 x 坐标（OADate）。</summary>
    [JsonIgnore]
    public double X => _time.ToOADate();

    [JsonIgnore] public bool IsSuspended => _flag.Contains('S');

    [JsonIgnore] public bool IsUpLimit => _flag.Contains('U');

    [JsonIgnore] public bool IsDownLimit => _flag.Contains('D');

    [JsonIgnore] public bool IsGap => _flag.Contains('G');

    [JsonIgnore] public bool IsExRight => _flag.Contains('X');

    /// <summary>实体涨跌额。</summary>
    [JsonIgnore] public double Change => _close - _open;

    /// <summary>实体涨跌幅（相对开盘）。</summary>
    [JsonIgnore] public double ChangePct => _open == 0 ? 0 : (_close - _open) / _open;

    [JsonIgnore] public bool IsBullish => _close >= _open;

    [JsonIgnore] public double Amplitude => _low == 0 ? 0 : (_high - _low) / _low;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is not (nameof(Time) or nameof(Volume) or nameof(Amount) or nameof(Flag) or nameof(AdjFactor)))
        {
            // OHLC 变化会影响派生属性显示
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangePct)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBullish)));
        }
    }

    public Kline Clone()
    {
        var k = new Kline(_time, _open, _high, _low, _close, _volume, _amount, _flag)
        {
            AdjFactor = _adjFactor,
        };
        return k;
    }

    public void CopyFrom(Kline other)
    {
        Time = other.Time;
        Open = other.Open;
        High = other.High;
        Low = other.Low;
        Close = other.Close;
        Volume = other.Volume;
        Amount = other.Amount;
        Flag = other.Flag;
        AdjFactor = other.AdjFactor;
    }

    /// <summary>修正 OHLC 内部一致性（保证 high/low 包住实体）。</summary>
    public void FixConsistency()
    {
        if (_high < _open) _high = _open;
        if (_high < _close) _high = _close;
        if (_high < _low) _high = _low;
        if (_low > _open) _low = _open;
        if (_low > _close) _low = _close;
        if (_low > _high) _low = _high;
        if (_low <= 0) _low = Math.Max(0.01, Math.Min(_open, _close) * 0.99);
        if (_volume < 0) _volume = 0;
    }

    /// <summary>
    /// 原子更新 OHLC：一次性写入并自动修正一致性，只发出一次通知。
    /// 手绘拖拽时用它，避免界面/画布看到「收盘价已变、最高价还没跟上」的中间非法状态。
    /// </summary>
    public void SetOhlc(double open, double high, double low, double close)
    {
        _open = open;
        _close = close;
        _high = Math.Max(high, Math.Max(open, close));
        _low = Math.Min(low, Math.Min(open, close));
        if (_low > _high) _low = _high;
        if (_low <= 0) _low = Math.Max(0.01, Math.Min(_open, _close) * 0.99);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Open)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(High)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Low)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Close)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangePct)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBullish)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Amplitude)));
    }

    public override string ToString() =>
        $"{_time:yyyy-MM-dd HH:mm} O:{_open:F2} H:{_high:F2} L:{_low:F2} C:{_close:F2} V:{_volume}";
}
