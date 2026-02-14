using System;
using Microsoft.UI.Xaml.Data;

namespace SAAIA.Client.WinUI.Converters;

public sealed class DateTimeToLocalShortConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        try
        {
            if (value is DateTime dt)
            {
                // Si c’est UTC (ton cas), on passe en local
                if (dt.Kind == DateTimeKind.Utc)
                    dt = dt.ToLocalTime();

                return dt.ToString("dd.MM HH:mm");
            }
        }
        catch { }

        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
