using System.Globalization;
using System.Windows.Data;

namespace VirtualLANPlatform.UI.Emoji;

/// <summary>Converts a raw emoji character to its bundled 3D image, or null if none is bundled.</summary>
public sealed class Emoji3DConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = value as string ?? "";
        return Emoji3DImages.TryGet(text, out var image) ? image : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
