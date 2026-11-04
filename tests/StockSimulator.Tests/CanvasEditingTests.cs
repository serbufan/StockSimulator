using StockSimulator.Models;
using StockSimulator.ViewModels;

namespace StockSimulator.Tests;

/// <summary>手绘编辑状态机测试（不依赖任何绘图库）。</summary>
public class CanvasEditingTests
{
    private static (Workspace Ws, KlineCanvasViewModel Vm) Make(int bars = 6)
    {
        var ws = new Workspace();
        ws.Settings.Period = BarPeriod.Day;
        ws.Settings.StartDate = new DateTime(2024, 1, 2);
        var vm = new KlineCanvasViewModel(ws, _ => { });

        var list = new List<Kline>();
        for (var i = 0; i < bars; i++)
        {
            list.Add(new Kline(new DateTime(2024, 1, 2).AddDays(i), 10 + i * 0.1, 10.3 + i * 0.1, 9.8 + i * 0.1, 10.1 + i * 0.1, 100000));
        }

        ws.ReplaceBars(list, recordUndo: false);
        return (ws, vm);
    }

    [Fact]
    public void SelectTool_DragModifiesClose()
    {
        var (ws, vm) = Make();
        vm.Tool = DrawTool.Select;

        vm.PointerDown(2, 10.25, 0.5);
        Assert.Equal(2, vm.SelectedIndex);

        vm.PointerMove(2, 10.90, 0.5);
        vm.PointerUp(2, 10.90, 0.5);

        Assert.Equal(10.90, ws.Bars[2].Close, 2);
        Assert.True(ws.Bars[2].High >= ws.Bars[2].Close);
        Assert.True(ws.CanUndo);
    }

    [Fact]
    public void SelectTool_DragNeverExposesInvalidBarState()
    {
        var (ws, vm) = Make();
        vm.Tool = DrawTool.Select;

        var violations = new List<string>();
        var target = ws.Bars[2];
        target.PropertyChanged += (_, e) =>
        {
            var b = ws.Bars[2];
            if (b.High < Math.Max(b.Open, b.Close) - 1e-9 ||
                b.Low > Math.Min(b.Open, b.Close) + 1e-9 ||
                b.High < b.Low || b.Low <= 0)
            {
                violations.Add($"{e.PropertyName}: O={b.Open} H={b.High} L={b.Low} C={b.Close}");
            }
        };

        vm.PointerDown(2, 10.2, 0.5);
        for (var p = 10.2; p <= 11.5; p += 0.1) vm.PointerMove(2, p, 0.5);
        vm.PointerUp(2, 11.5, 0.5);

        Assert.Empty(violations);   // 通知期间绝不能出现 high<close 之类非法状态
    }

    [Fact]
    public void SelectTool_UndoRestoresOriginalBar()
    {
        var (ws, vm) = Make();
        vm.Tool = DrawTool.Select;
        var originalClose = ws.Bars[1].Close;

        vm.PointerDown(1, 10.15, 0.5);
        vm.PointerMove(1, 11.00, 0.5);
        vm.PointerUp(1, 11.00, 0.5);
        Assert.NotEqual(originalClose, ws.Bars[1].Close);

        ws.Undo();
        Assert.Equal(originalClose, ws.Bars[1].Close, 2);
    }

    [Fact]
    public void DrawBarTool_AppendsNewBarAtEnd()
    {
        var (ws, vm) = Make();
        vm.Tool = DrawTool.DrawBar;
        var count = ws.Bars.Count;

        vm.PointerDown(count, 11.00, 0.5);
        vm.PointerMove(count, 11.60, 0.5);
        vm.PointerUp(count, 11.60, 0.5);

        Assert.Equal(count + 1, ws.Bars.Count);
        Assert.Equal(11.60, ws.Bars[^1].Close, 2);
        Assert.True(ws.Bars[^1].High >= ws.Bars[^1].Close);
        Assert.True(ws.Bars[^1].Time > ws.Bars[^2].Time);
    }

    [Fact]
    public void DrawBarTool_OverwritesExistingBar()
    {
        var (ws, vm) = Make();
        vm.Tool = DrawTool.DrawBar;

        vm.PointerDown(3, 10.20, 0.5);
        vm.PointerMove(3, 10.80, 0.5);
        vm.PointerUp(3, 10.80, 0.5);

        Assert.Equal(6, ws.Bars.Count);
        Assert.Equal(10.80, ws.Bars[3].Close, 2);
        Assert.Equal(10.20, ws.Bars[3].Open, 2);
    }

    [Fact]
    public void TrendLineTool_GeneratesBarsAlongLine()
    {
        var (ws, vm) = Make(10);
        vm.Tool = DrawTool.TrendLine;

        vm.PointerDown(0, 10.00, 0.5);
        vm.PointerMove(4, 12.00, 0.5);
        vm.PointerUp(4, 12.00, 0.5);

        Assert.Equal(10, ws.Bars.Count);
        Assert.InRange(ws.Bars[4].Close, 11.8, 12.2);
        for (var i = 0; i < 5; i++)
            Assert.True(ws.Bars[i].High >= ws.Bars[i].Low);
    }

    [Fact]
    public void EraserTool_RemovesBar()
    {
        var (ws, vm) = Make();
        vm.Tool = DrawTool.Erase;
        var count = ws.Bars.Count;
        var removedTime = ws.Bars[3].Time;

        vm.PointerDown(3, 10.4, 0.5);

        Assert.Equal(count - 1, ws.Bars.Count);
        Assert.DoesNotContain(ws.Bars, b => b.Time == removedTime);
    }

    [Fact]
    public void VolumeTool_SetsVolume()
    {
        var (ws, vm) = Make();
        vm.Tool = DrawTool.DrawVolume;

        vm.PointerDown(2, 10.4, 0.25);
        vm.PointerMove(2, 10.4, 0.8);
        vm.PointerUp(2, 10.4, 0.8);

        Assert.True(ws.Bars[2].Volume > 100000);
        Assert.True(ws.Bars[2].Amount > 0);
    }

    [Fact]
    public void PatternTool_InsertedBarsAreConsistent()
    {
        var (ws, vm) = Make(10);
        vm.Tool = DrawTool.Pattern;
        vm.PatternName = "DoubleTop";
        vm.PatternBars = 20;

        vm.PointerDown(0, 10.0, 0.5);

        Assert.Equal(20, ws.Bars.Count);
        Assert.All(ws.Bars, b => Assert.True(b.High >= Math.Max(b.Open, b.Close) && b.Low <= Math.Min(b.Open, b.Close)));
    }

    [Fact]
    public void SnapPrice_RoundsToTick()
    {
        var (_, vm) = Make();
        vm.SnapToTick = true;
        vm.SnapToGrid = false;

        Assert.Equal(10.12, vm.SnapPrice(10.1234), 2);

        vm.SnapToGrid = true;
        vm.GridStep = 0.05;
        Assert.Equal(10.10, vm.SnapPrice(10.1234), 2);
    }

    [Fact]
    public void Workspace_UndoRedo_RoundTrip()
    {
        var (ws, _) = Make();
        var firstClose = ws.Bars[0].Close;

        ws.BeginEdit();
        ws.Bars[0].Close = 99.99;
        ws.NotifyBarsChanged();
        ws.Undo();
        Assert.Equal(firstClose, ws.Bars[0].Close, 2);

        ws.Redo();
        Assert.Equal(99.99, ws.Bars[0].Close, 2);
    }
}
