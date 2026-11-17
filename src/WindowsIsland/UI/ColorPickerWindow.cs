using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WindowsIsland;

/// <summary>
/// Apple-style colour picker: a saturation/value square with a hue strip and a
/// live hex label. Built entirely in code (no default WPF chrome) so it matches
/// the island's visual language. The window is explicitly activatable and
/// topmost, because the island itself is a NOACTIVATE layered window.
/// </summary>
internal sealed class ColorPickerWindow : Window
{
    private const double SquareWidth = 240;
    private const double SquareHeight = 150;
    private const double StripHeight = 16;
    private const double KnobSize = 14;

    private readonly Border _svSquare;
    private readonly Border _hueFill;
    private readonly Border _svKnob;
    private readonly Border _hueKnob;
    private readonly Border _preview;
    private readonly TextBlock _hexText;

    private double _hue;
    private double _sat;
    private double _val;

    public Color SelectedColor { get; private set; }

    public ColorPickerWindow(Color initial)
    {
        Title = "自定义颜色";
        Width = SquareWidth + 48;
        Height = 330;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        (_hue, _sat, _val) = ToHsv(initial);
        SelectedColor = initial;

        var panel = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1A, 0x1B, 0x21)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF))
        };

        var root = new StackPanel();

        // title row
        var title = new Grid { Margin = new Thickness(2, 0, 2, 12) };
        title.Children.Add(new TextBlock
        {
            Text = "自定义颜色",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))
        });

        var close = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand
        };
        close.MouseLeftButtonDown += (_, _) => DialogResult = false;
        title.Children.Add(close);
        root.Children.Add(title);

        // saturation / value square
        _svSquare = new Border
        {
            Width = SquareWidth,
            Height = SquareHeight,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0, 0)),
            ClipToBounds = true,
            Cursor = Cursors.Cross
        };

        var whiteFade = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(
                Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                new Point(0, 0.5),
                new Point(1, 0.5))
        };

        var blackFade = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(
                Color.FromArgb(0x00, 0, 0, 0),
                Color.FromArgb(0xFF, 0, 0, 0),
                new Point(0.5, 0),
                new Point(0.5, 1))
        };

        var squareHost = new Grid { Width = SquareWidth, Height = SquareHeight };
        squareHost.Children.Add(_svSquare);
        squareHost.Children.Add(whiteFade);
        squareHost.Children.Add(blackFade);

        _svKnob = new Border
        {
            Width = KnobSize,
            Height = KnobSize,
            CornerRadius = new CornerRadius(KnobSize / 2),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        squareHost.Children.Add(_svKnob);

        squareHost.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            squareHost.CaptureMouse();
            UpdateFromSquare(e.GetPosition(squareHost));
        };
        squareHost.MouseMove += (_, e) =>
        {
            if (squareHost.IsMouseCaptured)
            {
                UpdateFromSquare(e.GetPosition(squareHost));
            }
        };
        squareHost.MouseLeftButtonUp += (_, _) => squareHost.ReleaseMouseCapture();

        root.Children.Add(squareHost);

        // hue strip
        var hueHost = new Grid { Height = StripHeight, Margin = new Thickness(0, 14, 0, 0) };

        var hueTrack = new Border
        {
            CornerRadius = new CornerRadius(StripHeight / 2),
            Background = BuildHueGradient(),
            Cursor = Cursors.Hand
        };
        hueHost.Children.Add(hueTrack);

        _hueFill = new Border { IsHitTestVisible = false };

        _hueKnob = new Border
        {
            Width = StripHeight + 4,
            Height = StripHeight + 4,
            CornerRadius = new CornerRadius((StripHeight + 4) / 2),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        hueHost.Children.Add(_hueKnob);

        hueHost.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            hueHost.CaptureMouse();
            UpdateFromHue(e.GetPosition(hueHost));
        };
        hueHost.MouseMove += (_, e) =>
        {
            if (hueHost.IsMouseCaptured)
            {
                UpdateFromHue(e.GetPosition(hueHost));
            }
        };
        hueHost.MouseLeftButtonUp += (_, _) => hueHost.ReleaseMouseCapture();

        root.Children.Add(hueHost);

        // preview + hex
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _preview = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
        };
        footer.Children.Add(_preview);

        _hexText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
        };
        Grid.SetColumn(_hexText, 1);
        footer.Children.Add(_hexText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MakeButton("取消", false, () => DialogResult = false));
        buttons.Children.Add(MakeButton("确定", true, () => DialogResult = true));

        Grid.SetColumn(buttons, 2);
        footer.Children.Add(buttons);

        root.Children.Add(footer);

        panel.Child = root;
        Content = panel;

        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // not over the drag area
                }
            }
        };

        UpdateVisuals();
    }

    private Button MakeButton(string text, bool primary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Width = 66,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11.5,
            Foreground = primary
                ? new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E))
                : new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
            Background = primary
                ? new SolidColorBrush(SelectedColor)
                : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;

        button.Click += (_, _) => onClick();
        return button;
    }

    private static LinearGradientBrush BuildHueGradient()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };

        for (var i = 0; i <= 6; i++)
        {
            brush.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6d));
        }

        return brush;
    }

    private void UpdateFromSquare(Point point)
    {
        _sat = Math.Clamp(point.X / SquareWidth, 0d, 1d);
        _val = 1d - Math.Clamp(point.Y / SquareHeight, 0d, 1d);
        UpdateVisuals();
    }

    private void UpdateFromHue(Point point)
    {
        _hue = Math.Clamp(point.X / SquareWidth, 0d, 1d) * 360d;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var hueColor = FromHsv(_hue, 1, 1);
        _svSquare.Background = new SolidColorBrush(hueColor);

        var color = FromHsv(_hue, _sat, _val);
        SelectedColor = color;

        _preview.Background = new SolidColorBrush(color);
        _hexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        Canvas.SetLeft(_svKnob, (_sat * (SquareWidth - KnobSize)) );
        Canvas.SetTop(_svKnob, ((1 - _val) * (SquareHeight - KnobSize)));

        // knobs are positioned with margins inside a Grid (no Canvas), so use Margin:
        _svKnob.Margin = new Thickness(_sat * (SquareWidth - KnobSize), (1 - _val) * (SquareHeight - KnobSize), 0, 0);

        var hueX = (_hue / 360d) * SquareWidth;
        _hueKnob.Margin = new Thickness(
            Math.Clamp(hueX - (StripHeight + 4) / 2, -2, SquareWidth - StripHeight - 2), -2, 0, 0);
    }

    private static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;

        if (delta > 0.0001)
        {
            if (Math.Abs(max - r) < 0.0001)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (Math.Abs(max - g) < 0.0001)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max <= 0.0001 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = value - c;

        double r, g, b;

        if (hue < 60) (r, g, b) = (c, x, 0);
        else if (hue < 120) (r, g, b) = (x, c, 0);
        else if (hue < 180) (r, g, b) = (0, c, x);
        else if (hue < 240) (r, g, b) = (0, x, c);
        else if (hue < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);

        byte ToByte(double channel) => (byte)Math.Clamp(Math.Round((channel + m) * 255), 0, 255);

        return Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
    }
}
