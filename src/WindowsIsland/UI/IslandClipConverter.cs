using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WindowsIsland;

/// <summary>
/// 生成与岛形状一致的圆角裁剪路径，交给 Border.Clip。
/// 之所以要自己搭几何：WPF 的 ClipToBounds 只裁矩形，底部圆角处
/// 进度线会“漏”到岛外面；而贴顶时上方两角是直角、下方是圆角，
/// 四角半径不一致，RectangleGeometry 表达不了。
/// </summary>
public sealed class IslandClipConverter : IMultiValueConverter
{
    public static readonly IslandClipConverter Instance = new();

    private const double FloatingTopMargin = 10d;
    private const double MaxRadius = 40d;

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var marginTop = values is { Length: > 0 } && values[0] is Thickness margin ? margin.Top : FloatingTopMargin;
        var height = values is { Length: > 1 } && values[1] is double h ? h : 0d;
        var width = values is { Length: > 2 } && values[2] is double w ? w : 0d;

        if (width <= 0d || height <= 0d)
        {
            return null;
        }

        var attach = Math.Clamp(1d - (marginTop / FloatingTopMargin), 0d, 1d);
        var bottom = Math.Min(height / 2d, MaxRadius);
        var top = bottom * (1d - attach);

        return BuildGeometry(width, height, top, top, bottom, bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Geometry BuildGeometry(double width, double height, double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(topLeft, 0), isFilled: true, isClosed: true);

            context.LineTo(new Point(width - topRight, 0), isStroked: true, isSmoothJoin: false);
            if (topRight > 0)
            {
                context.ArcTo(new Point(width, topRight), new Size(topRight, topRight), 0, false, SweepDirection.Clockwise, true, false);
            }

            context.LineTo(new Point(width, height - bottomRight), isStroked: true, isSmoothJoin: false);
            if (bottomRight > 0)
            {
                context.ArcTo(new Point(width - bottomRight, height), new Size(bottomRight, bottomRight), 0, false, SweepDirection.Clockwise, true, false);
            }

            context.LineTo(new Point(bottomLeft, height), isStroked: true, isSmoothJoin: false);
            if (bottomLeft > 0)
            {
                context.ArcTo(new Point(0, height - bottomLeft), new Size(bottomLeft, bottomLeft), 0, false, SweepDirection.Clockwise, true, false);
            }

            context.LineTo(new Point(0, topLeft), isStroked: true, isSmoothJoin: false);
            if (topLeft > 0)
            {
                context.ArcTo(new Point(topLeft, 0), new Size(topLeft, topLeft), 0, false, SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
