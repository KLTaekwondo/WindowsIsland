using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowsIsland;

/// <summary>
/// 由「上边距 + 当前高度」推导圆角：
/// 浮空时四个角都是胶囊圆角；贴到屏幕顶部时上方两角归零，只留底部圆角。
/// 之所以做成绑定，是因为 CornerRadius 不是可动画类型，只能跟着 Margin 的动画逐帧重算。
/// </summary>
public sealed class IslandRadiusConverter : IMultiValueConverter
{
    public static readonly IslandRadiusConverter Instance = new();

    private const double FloatingTopMargin = 10d;
    private const double MaxRadius = 40d;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var marginTop = values is { Length: > 0 } && values[0] is Thickness margin ? margin.Top : FloatingTopMargin;
        var height = values is { Length: > 1 } && values[1] is double h ? h : 0d;

        var attach = Math.Clamp(1d - (marginTop / FloatingTopMargin), 0d, 1d);
        var bottom = Math.Min(height / 2d, MaxRadius);
        var top = bottom * (1d - attach);

        return new CornerRadius(top, top, bottom, bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
