using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DustLoadScada;

public static class Texts
{
    public static string Verdict(Verdict verdict) => verdict switch
    {
        DustLoadScada.Verdict.Pass => "合格",
        DustLoadScada.Verdict.Fail => "不合格",
        _ => "—"
    };
}

public sealed class VerdictConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var verdict = value is Verdict result ? result : Verdict.Pending;
        if (targetType != typeof(Brush))
            return Texts.Verdict(verdict);
        string resource = verdict switch
        {
            Verdict.Pass => "Green",
            Verdict.Fail => "Red",
            _ => "Muted"
        };
        return Application.Current.FindResource(resource);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
