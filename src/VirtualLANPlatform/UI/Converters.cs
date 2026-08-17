using System.Globalization;
using System.Windows.Data;

namespace VirtualLANPlatform.UI;

/// <summary>Takes the leading character of a name for use as an avatar monogram.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string s = value as string ?? "";
        s = s.TrimStart();
        return s.Length == 0 ? "؟" : s[..1].ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
