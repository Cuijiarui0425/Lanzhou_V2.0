using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;

namespace Lanzhou_v1._0
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            var vm = new MainViewModel();
            DataContext = vm;

            // 统一弹窗提示（由 ViewModel 触发）
            // 兼容两种签名：Action<string> 或 Action<string,string>
            vm.UiMessageRequested += OnUiMessageRequested;
        }

        private MainViewModel? Vm => DataContext as MainViewModel;

        
        // --- 弹窗提示（View 负责 MessageBox，VM 只发事件） ---
        // 兼容：
        // 1) event Action<string> UiMessageRequested
        // 2) event Action<string,string> UiMessageRequested（title, message）
        private void OnUiMessageRequested(string msg)
            => OnUiMessageRequested("提示", msg);

        private void OnUiMessageRequested(string title, string msg)
        {
            // 可能来自后台线程；切回 UI 线程显示
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnUiMessageRequested(title, msg));
                return;
            }

            var caption = string.IsNullOrWhiteSpace(title) ? "提示" : title;
            MessageBox.Show(this, msg ?? string.Empty, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void PressJog_Down(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement el) el.CaptureMouse();
            Vm?.JogPressDown();
        }

        private void PressJog_Up(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement el) el.ReleaseMouseCapture();
            Vm?.JogPressUp();
        }

        private void PressJog_Up(object sender, MouseEventArgs e)
        {
            if (sender is UIElement el) el.ReleaseMouseCapture();
            Vm?.JogPressUp();
        }

        private void BallJog_Down(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement el) el.CaptureMouse();
            Vm?.JogBallDown();
        }

        private void BallJog_Up(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement el) el.ReleaseMouseCapture();
            Vm?.JogBallUp();
        }

        // LostMouseCapture 事件的委托类型为 MouseEventHandler（MouseEventArgs）
        private void BallJog_Up(object sender, MouseEventArgs e)
        {
            if (sender is UIElement el) el.ReleaseMouseCapture();
            Vm?.JogBallUp();
        }

        private void DiskJog_Down(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement el) el.CaptureMouse();
            Vm?.JogDiskDown();
        }

        private void DiskJog_Up(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement el) el.ReleaseMouseCapture();
            Vm?.JogDiskUp();
        }

        private void DiskJog_Up(object sender, MouseEventArgs e)
        {
            if (sender is UIElement el) el.ReleaseMouseCapture();
            Vm?.JogDiskUp();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (Vm != null)
                {
                    Vm.UiMessageRequested -= OnUiMessageRequested;
                    Vm.Dispose();
                }
            }
            catch
            {
                // ignore
            }

            base.OnClosed(e);
        }

        /// <summary>
        /// 曲线轴尺度切换：支持 X/Y 线性↔对数。
        /// Tag 格式：PlotKey|Axis|Mode
        /// PlotKey: LoadTime / RollingTime / TempTime / MuTime / SpeedMu / SrrMu
        /// Axis: X / Y
        /// Mode: Linear / Log
        /// </summary>
        private void AxisScale_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is null) return;
            var tag = btn.Tag.ToString() ?? string.Empty;
            var parts = tag.Split('|');
            if (parts.Length != 3) return;

            var plotKey = parts[0];
            var axisName = parts[1];
            var mode = parts[2];

            var pv = plotKey switch
            {
                "LoadTime" => PV_LoadTime,
                "RollingTime" => PV_RollingTime,
                "TempTime" => PV_TempTime,
                "MuTime" => PV_MuTime,
                "SpeedMu" => PV_SpeedMu,
                "SrrMu" => PV_SrrMu,
                _ => null
            };

            if (pv?.Model == null) return;

            var pos = axisName.Equals("X", StringComparison.OrdinalIgnoreCase)
                ? AxisPosition.Bottom
                : AxisPosition.Left;

            var toLog = mode.Equals("Log", StringComparison.OrdinalIgnoreCase);
            SwitchAxisScale(pv.Model, pos, toLog);

            // 卷吸速度时间曲线：对数轴不支持 <=0，采用“双序列”切换（线性序列 vs 幅值序列）
            if (plotKey == "RollingTime" && axisName.Equals("Y", StringComparison.OrdinalIgnoreCase))
            {
                if (DataContext is Lanzhou_v1._0.MainViewModel vm)
                    vm.SetRollingTimeYAxisIsLog(toLog);
            }
            pv.Model.InvalidatePlot(true);
        }

        private static void SwitchAxisScale(PlotModel model, AxisPosition position, bool toLog)
        {
            var oldAxis = model.Axes.FirstOrDefault(a => a.Position == position);
            if (oldAxis == null) return;

            if (toLog && oldAxis is LogarithmicAxis) return;
            if (!toLog && oldAxis is LinearAxis) return;

            Axis newAxis = toLog ? new LogarithmicAxis() : new LinearAxis();

            // 复制常用外观/行为参数
            newAxis.Position = oldAxis.Position;
            newAxis.Title = oldAxis.Title;
            newAxis.Key = oldAxis.Key;
            newAxis.Unit = oldAxis.Unit;
            newAxis.StringFormat = oldAxis.StringFormat;
            newAxis.MajorGridlineStyle = oldAxis.MajorGridlineStyle;
            newAxis.MinorGridlineStyle = oldAxis.MinorGridlineStyle;
            newAxis.MajorGridlineColor = oldAxis.MajorGridlineColor;
            newAxis.MinorGridlineColor = oldAxis.MinorGridlineColor;
            newAxis.AxislineStyle = oldAxis.AxislineStyle;
            newAxis.AxislineColor = oldAxis.AxislineColor;
            newAxis.TicklineColor = oldAxis.TicklineColor;
            newAxis.TextColor = oldAxis.TextColor;
            newAxis.TitleColor = oldAxis.TitleColor;
            newAxis.IsZoomEnabled = oldAxis.IsZoomEnabled;
            newAxis.IsPanEnabled = oldAxis.IsPanEnabled;
            newAxis.MinimumPadding = oldAxis.MinimumPadding;
            newAxis.MaximumPadding = oldAxis.MaximumPadding;
            newAxis.AbsoluteMinimum = oldAxis.AbsoluteMinimum;
            newAxis.AbsoluteMaximum = oldAxis.AbsoluteMaximum;

            // 对数轴要求 >0：给一个保守下限，避免 0/负值导致异常
            if (toLog)
            {
                newAxis.Minimum = 1e-3;
            }
            else
            {
                newAxis.Minimum = double.NaN;
                newAxis.Maximum = double.NaN;
            }

            var idx = model.Axes.IndexOf(oldAxis);
            if (idx >= 0) model.Axes[idx] = newAxis;
        }
    }
}
