// <copyright file="RadialMenuWindow.xaml.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Views
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Windows;
    using System.Windows.Automation;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Threading;

    using FileConverter.RadialMenu;

    public enum RadialMenuInputMode
    {
        /// <summary>The menu takes the focus, slices are clicked and the menu closes when it loses the focus.</summary>
        Click,

        /// <summary>The menu doesn't take the focus, files are dropped on a slice.</summary>
        Drop,
    }

    public sealed class RadialPresetChosenEventArgs : EventArgs
    {
        public RadialPresetChosenEventArgs(ConversionPreset preset, IReadOnlyList<string> filePaths)
        {
            this.Preset = preset;
            this.FilePaths = filePaths;
        }

        public ConversionPreset Preset { get; }

        public IReadOnlyList<string> FilePaths { get; }
    }

    public partial class RadialMenuWindow : Window
    {
        private const double Center = RadialGeometry.WindowSize / 2;
        private const double LabelRadius = (RadialGeometry.InnerRadius + RadialGeometry.OuterRadius) / 2;
        private static readonly TimeSpan HubHoverPagingDelay = TimeSpan.FromMilliseconds(600);

        private readonly List<Button> sliceButtons = new List<Button>();
        private readonly DispatcherTimer hubHoverTimer;

        private RadialPager<ConversionPreset> pager;
        private IReadOnlyList<string> filePaths = new string[0];
        private RadialMenuInputMode mode;
        private List<ConversionPreset> compatiblePresets = new List<ConversionPreset>();
        private int hoverIndex = -1;
        private bool isShown;
        private bool hasChosen;
        private bool canArmHubPaging;
        private bool hasPendingBounds;
        private int pendingLeft;
        private int pendingTop;
        private int pendingSize;

        public RadialMenuWindow()
        {
            this.InitializeComponent();

            this.hubHoverTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = HubHoverPagingDelay };
            this.hubHoverTimer.Tick += this.HubHoverTimer_Tick;

            this.Loaded += (s, e) => this.ApplyPendingBounds();
            this.Deactivated += this.OnWindowDeactivated;
            this.PreviewKeyDown += this.OnWindowPreviewKeyDown;
            this.PreviewMouseWheel += this.OnWindowPreviewMouseWheel;
            this.MouseLeftButtonDown += this.OnWindowMouseLeftButtonDown;

            this.Root.MouseMove += this.OnRootMouseMove;
            this.Root.MouseLeave += (s, e) => this.SetHover(-1);
            this.Root.DragOver += this.OnRootDragOver;
            this.Root.DragLeave += (s, e) => this.OnDragLeave();
            this.Root.Drop += this.OnRootDrop;
        }

        public event EventHandler<RadialPresetChosenEventArgs> PresetChosen;

        public event EventHandler Dismissed;

        /// <summary>When true the window is hidden instead of closed on dismiss (the agent keeps it warm).</summary>
        public bool HideInsteadOfClose { get; set; }

        public void ShowMenu(IReadOnlyList<string> filePaths, IReadOnlyList<ConversionPreset> allPresets, System.Drawing.Point cursorScreenPx, RadialMenuInputMode mode)
        {
            this.mode = mode;
            this.filePaths = filePaths ?? new string[0];
            this.hasChosen = false;
            this.canArmHubPaging = false;
            this.hoverIndex = -1;

            this.compatiblePresets = PresetCompatibility.GetPresetsCompatibleWithAll(allPresets ?? new ConversionPreset[0], this.filePaths);
            this.pager = new RadialPager<ConversionPreset>(this.compatiblePresets);
            this.RebuildSlices();

            this.ShowActivated = mode == RadialMenuInputMode.Click;
            this.Root.AllowDrop = mode == RadialMenuInputMode.Drop;

            new WindowInteropHelper(this).EnsureHandle();
            this.ComputeBounds(cursorScreenPx);
            this.ApplyPendingBounds();

            this.isShown = true;
            if (this.IsVisible)
            {
                this.UpdateLayout();
            }
            else
            {
                this.Show();
            }

            if (mode == RadialMenuInputMode.Click)
            {
                this.Activate();
            }
        }

        public void Dismiss()
        {
            if (!this.isShown)
            {
                return;
            }

            this.isShown = false;
            this.hubHoverTimer.Stop();

            if (this.HideInsteadOfClose)
            {
                this.Hide();
            }
            else
            {
                this.Close();
            }

            this.Dismissed?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (this.isShown)
            {
                this.isShown = false;
                this.hubHoverTimer.Stop();
                this.Dismissed?.Invoke(this, EventArgs.Empty);
            }
        }

        private static Geometry CreateSectorGeometry(double startDegrees, double sweepDegrees)
        {
            if (sweepDegrees >= 359.999)
            {
                GeometryGroup ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
                ring.Children.Add(new EllipseGeometry(new Point(Center, Center), RadialGeometry.OuterRadius, RadialGeometry.OuterRadius));
                ring.Children.Add(new EllipseGeometry(new Point(Center, Center), RadialGeometry.InnerRadius, RadialGeometry.InnerRadius));
                ring.Freeze();
                return ring;
            }

            double endDegrees = startDegrees + sweepDegrees;
            bool isLargeArc = sweepDegrees > 180;
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(PointOnCircle(startDegrees, RadialGeometry.OuterRadius), true, true);
                context.ArcTo(PointOnCircle(endDegrees, RadialGeometry.OuterRadius), new Size(RadialGeometry.OuterRadius, RadialGeometry.OuterRadius), 0, isLargeArc, SweepDirection.Clockwise, true, false);
                context.LineTo(PointOnCircle(endDegrees, RadialGeometry.InnerRadius), true, false);
                context.ArcTo(PointOnCircle(startDegrees, RadialGeometry.InnerRadius), new Size(RadialGeometry.InnerRadius, RadialGeometry.InnerRadius), 0, isLargeArc, SweepDirection.Counterclockwise, true, false);
            }

            geometry.Freeze();
            return geometry;
        }

        private static Point PointOnCircle(double degrees, double radius)
        {
            double radians = degrees * Math.PI / 180d;
            return new Point(Center + (radius * Math.Sin(radians)), Center - (radius * Math.Cos(radians)));
        }

        private void ComputeBounds(System.Drawing.Point cursorScreenPx)
        {
            NativeMethods.POINT cursor = new NativeMethods.POINT { X = cursorScreenPx.X, Y = cursorScreenPx.Y };
            IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);

            uint dpi = 96;
            if (monitor != IntPtr.Zero)
            {
                try
                {
                    if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0 && dpiX > 0)
                    {
                        dpi = dpiX;
                    }
                }
                catch (Exception exception)
                {
                    Diagnostics.Debug.Log($"Can't retrieve monitor DPI: {exception.Message}");
                }
            }

            Rect workArea = new Rect(cursorScreenPx.X - 5000, cursorScreenPx.Y - 5000, 10000, 10000);
            NativeMethods.MONITORINFO info = new NativeMethods.MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                workArea = new Rect(info.WorkArea.Left, info.WorkArea.Top, info.WorkArea.Right - info.WorkArea.Left, info.WorkArea.Bottom - info.WorkArea.Top);
            }

            double sizePx = Math.Round(RadialGeometry.WindowSize * dpi / 96d);
            Rect bounds = RadialGeometry.ClampToWorkArea(new Point(cursorScreenPx.X, cursorScreenPx.Y), sizePx, workArea);
            this.pendingLeft = (int)Math.Round(bounds.Left);
            this.pendingTop = (int)Math.Round(bounds.Top);
            this.pendingSize = (int)sizePx;
            this.hasPendingBounds = true;
        }

        private void ApplyPendingBounds()
        {
            if (!this.hasPendingBounds)
            {
                return;
            }

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(handle, IntPtr.Zero, this.pendingLeft, this.pendingTop, this.pendingSize, this.pendingSize, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }

        private void RebuildSlices()
        {
            this.SliceCanvas.Children.Clear();
            this.sliceButtons.Clear();

            Style sliceStyle = (Style)this.FindResource("RadialSliceStyle");
            IReadOnlyList<ConversionPreset> page = this.pager.CurrentPage;

            for (int index = 0; index < page.Count; index++)
            {
                (double start, double sweep) = RadialGeometry.GetSliceAngles(index, page.Count);

                // A single slice is a full ring, its label sits at the top.
                Point labelCenter = page.Count == 1 ? new Point(Center, Center - LabelRadius) : PointOnCircle(start + (sweep / 2), LabelRadius);

                double chord = 2 * LabelRadius * Math.Sin(Math.Min(sweep, 180) / 2 * Math.PI / 180d);
                double labelWidth = Math.Max(50, Math.Min(92, chord * 0.9));

                TextBlock label = new TextBlock
                {
                    Text = page[index].ShortName,
                    Width = labelWidth,
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Foreground = (Brush)this.FindResource("ReadyForegroundBrush"),
                };
                label.Measure(new Size(labelWidth, double.PositiveInfinity));
                label.Margin = new Thickness(labelCenter.X - (labelWidth / 2), labelCenter.Y - (label.DesiredSize.Height / 2), 0, 0);

                Button button = new Button
                {
                    Style = sliceStyle,
                    Width = RadialGeometry.WindowSize,
                    Height = RadialGeometry.WindowSize,
                    Tag = CreateSectorGeometry(start, sweep),
                    Content = label,
                };

                int sliceIndex = index;
                AutomationProperties.SetName(button, page[index].FullName);
                AutomationProperties.SetAutomationId(button, "RadialSlice_" + index);
                button.Click += (s, e) => this.Choose(sliceIndex);

                this.sliceButtons.Add(button);
                this.SliceCanvas.Children.Add(button);
            }

            this.PageIndicator.Text = this.pager.Indicator;
            this.PageIndicator.Visibility = this.pager.ShowsPaging ? Visibility.Visible : Visibility.Collapsed;
            this.UpdateHub();
        }

        private void SetHover(int index)
        {
            if (index == this.hoverIndex)
            {
                return;
            }

            this.hoverIndex = index;
            Brush normal = (Brush)this.FindResource("Button_BackgroundBrush");
            Brush highlight = (Brush)this.FindResource("MainLightBrush");
            for (int i = 0; i < this.sliceButtons.Count; i++)
            {
                this.sliceButtons[i].Background = i == index ? highlight : normal;
            }

            this.UpdateHub();
        }

        private void UpdateHub()
        {
            IReadOnlyList<ConversionPreset> page = this.pager == null ? new ConversionPreset[0] : this.pager.CurrentPage;
            if (page.Count == 0)
            {
                this.HubText.Text = Properties.Resources.RadialMenuNoCompatiblePreset;
            }
            else if (this.hoverIndex >= 0 && this.hoverIndex < page.Count)
            {
                this.HubText.Text = page[this.hoverIndex].FullName;
            }
            else
            {
                this.HubText.Text = string.Empty;
            }
        }

        private void Choose(int index)
        {
            if (this.hasChosen || this.pager == null)
            {
                return;
            }

            IReadOnlyList<ConversionPreset> page = this.pager.CurrentPage;
            if (index < 0 || index >= page.Count)
            {
                return;
            }

            this.hasChosen = true;
            this.PresetChosen?.Invoke(this, new RadialPresetChosenEventArgs(page[index], this.filePaths));
        }

        private void ChangePage(bool forward)
        {
            if (this.pager == null || !this.pager.ShowsPaging)
            {
                return;
            }

            if (forward)
            {
                this.pager.Next();
            }
            else
            {
                this.pager.Previous();
            }

            this.hoverIndex = -1;
            this.RebuildSlices();
        }

        private int HitTest(Point positionInRoot, out double distance)
        {
            double x = positionInRoot.X - Center;
            double y = positionInRoot.Y - Center;
            distance = Math.Sqrt((x * x) + (y * y));
            int sliceCount = this.pager == null ? 0 : this.pager.CurrentPage.Count;
            return RadialGeometry.HitTestSlice(x, y, sliceCount, RadialGeometry.InnerRadius, RadialGeometry.OuterRadius);
        }

        private void OnRootMouseMove(object sender, MouseEventArgs e)
        {
            this.SetHover(this.HitTest(e.GetPosition(this.Root), out _));
        }

        private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (this.mode != RadialMenuInputMode.Click)
            {
                return;
            }

            int slice = this.HitTest(e.GetPosition(this.Root), out double distance);
            if (slice < 0 && distance >= RadialGeometry.InnerRadius)
            {
                this.Dismiss();
            }
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            if (this.mode == RadialMenuInputMode.Click)
            {
                this.Dismiss();
            }
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                this.Dismiss();
            }
            else if (e.Key == Key.Tab)
            {
                e.Handled = true;
                this.ChangePage((Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            }
        }

        private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0)
            {
                e.Handled = true;
                this.ChangePage(e.Delta < 0);
            }
        }

        private void OnRootDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (this.mode != RadialMenuInputMode.Drop)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            int slice = this.HitTest(e.GetPosition(this.Root), out double distance);
            this.SetHover(slice);
            e.Effects = slice >= 0 ? DragDropEffects.Copy : DragDropEffects.None;

            // The menu opens centred on the cursor, i.e. on the hub: hub hover only pages after the cursor has left the hub once.
            bool overHub = distance < RadialGeometry.InnerRadius;
            if (!overHub)
            {
                this.canArmHubPaging = true;
            }

            if (overHub && this.canArmHubPaging && this.pager != null && this.pager.ShowsPaging)
            {
                if (!this.hubHoverTimer.IsEnabled)
                {
                    this.hubHoverTimer.Start();
                }
            }
            else
            {
                this.hubHoverTimer.Stop();
            }
        }

        private void OnDragLeave()
        {
            this.hubHoverTimer.Stop();
            this.SetHover(-1);
        }

        private void OnRootDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            this.hubHoverTimer.Stop();

            if (this.mode != RadialMenuInputMode.Drop)
            {
                return;
            }

            int slice = this.HitTest(e.GetPosition(this.Root), out _);
            if (slice >= 0)
            {
                this.Choose(slice);
            }
        }

        private void HubHoverTimer_Tick(object sender, EventArgs e)
        {
            // A flip needs a fresh exit and re-entry of the hub before the next one.
            this.canArmHubPaging = false;
            this.hubHoverTimer.Stop();
            this.ChangePage(true);
        }
    }
}
