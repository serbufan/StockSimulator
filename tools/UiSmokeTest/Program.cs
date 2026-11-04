using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static string _shotDir;

    [STAThread]
    private static int Main()
    {
        var errors = new List<string>();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new CollectingListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.ResourceDictionarySource.Listeners.Add(new CollectingListener(errors));
        PresentationTraceSources.ResourceDictionarySource.Switch.Level = SourceLevels.Warning;

        _shotDir = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(_shotDir);

        try
        {
            var app = new StockSimulator.App();
            app.InitializeComponent();

            var window = new StockSimulator.MainWindow();
            window.Show();
            window.UpdateLayout();
            Pump();

            var vm = (StockSimulator.ViewModels.MainViewModel)window.DataContext;
            Console.WriteLine($"窗口已加载：导航 {vm.NavigationItems.Count} 项，阶段 {vm.Workspace.Segments.Count} 个");

            // 先生成一套行情，保证各页面都有数据
            vm.SelectedTabIndex = 0;
            vm.SegmentTab.GenerateCommand.Execute(null);
            vm.SegmentTab.ValidateCommand.Execute(null);
            window.UpdateLayout();
            Pump();
            Console.WriteLine("生成结果：" + vm.SegmentTab.GenerationSummary);

            for (var i = 0; i < vm.NavigationItems.Count; i++)
            {
                vm.SelectedTabIndex = i;
                window.UpdateLayout();
                Pump();
                Capture(window, $"page{i + 1}.png");
                Console.WriteLine($"页面 {i + 1}「{vm.NavigationItems[i].Title}」加载完成");
            }

            // 画布交互
            vm.SelectedTabIndex = 1;
            window.UpdateLayout();
            Pump();

            var canvas = vm.DrawTab.Canvas;
            canvas.SelectedIndex = 5;
            vm.DrawTab.AddAnnotationCommand.Execute(null);
            canvas.Tool = StockSimulator.ViewModels.DrawTool.Select;
            canvas.PointerDown(6, vm.Workspace.Bars[6].Close, 0.5);
            canvas.PointerMove(6, vm.Workspace.Bars[6].Close * 1.02, 0.5);
            canvas.PointerUp(6, vm.Workspace.Bars[6].Close * 1.02, 0.5);
            canvas.Tool = StockSimulator.ViewModels.DrawTool.TrendLine;
            canvas.PointerDown(10, 11.0, 0.5);
            canvas.PointerMove(20, 12.5, 0.5);
            canvas.PointerUp(20, 12.5, 0.5);
            canvas.ZoomCommand.Execute("out");
            canvas.ZoomCommand.Execute("fit");
            window.UpdateLayout();
            Pump();
            Capture(window, "page2-after-draw.png");
            Console.WriteLine($"交互后：{vm.Workspace.Bars.Count} 根，标注 {vm.Workspace.Annotations.Count} 条");

            // 批量变体
            vm.SelectedTabIndex = 2;
            vm.BatchTab.VariantCount = 6;
            vm.BatchTab.CaptureBaseCommand.Execute(null);
            vm.BatchTab.GenerateVariantsCommand.Execute(null);
            window.UpdateLayout();
            Pump();
            Capture(window, "page3-variants.png");
            Console.WriteLine("变体：" + vm.BatchTab.Summary);

            // 快照对比
            vm.SelectedTabIndex = 3;
            vm.ExportTab.TakeSnapshotCommand.Execute(null);
            vm.ExportTab.CompareSnapshotCommand.Execute(null);
            window.UpdateLayout();
            Pump();
            Capture(window, "page4-snapshot.png");
            Console.WriteLine("导出页加载完成");

            window.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 1;
        }

        var bad = errors.Where(e => e.Contains("BindingExpression") || e.Contains("Cannot find") || e.Contains("找不到资源"))
                        .Distinct().ToList();
        if (bad.Count > 0)
        {
            Console.WriteLine($"绑定/资源告警 {bad.Count} 条：");
            foreach (var e in bad.Take(20)) Console.WriteLine("  " + e);
        }
        else
        {
            Console.WriteLine("无绑定/资源告警");
        }

        Console.WriteLine("SMOKE TEST PASSED  shots=" + _shotDir);
        return 0;
    }

    private static void Capture(Window window, string fileName)
    {
        var w = (int)Math.Ceiling(window.ActualWidth);
        var h = (int)Math.Ceiling(window.ActualHeight);
        if (w < 10 || h < 10) return;

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(_shotDir, fileName));
        encoder.Save(fs);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class CollectingListener : TraceListener
    {
        private readonly List<string> _sink;
        public CollectingListener(List<string> sink) => _sink = sink;
        public override void Write(string message) { }
        public override void WriteLine(string message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _sink.Add(message);
        }
    }
}
