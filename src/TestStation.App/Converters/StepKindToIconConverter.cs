using System.Globalization;
using System.Windows.Data;
using MaterialDesignThemes.Wpf;

namespace TestStation.App.Converters;

public sealed class StepKindToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString()?.ToLowerInvariant().Replace(' ', '_') switch
        {
            "initialize" => PackIconKind.PowerPlug,
            "set_value" => PackIconKind.Flash,
            "read_value" or "get_value" => PackIconKind.Eye,
            "wait" => PackIconKind.TimerSand,
            "assert" => PackIconKind.ClipboardCheckOutline,
            "if" => PackIconKind.SourceBranch,
            "loop" or "for" => PackIconKind.Repeat,
            "while" => PackIconKind.Autorenew,
            "break" => PackIconKind.StopCircleOutline,
            "continue" => PackIconKind.SkipNext,
            "call_sequence" or "sequence_call" => PackIconKind.PlaylistPlay,
            "comment" => PackIconKind.CommentTextOutline,
            "statement" => PackIconKind.CodeBraces,
            "log" => PackIconKind.TextBoxOutline,
            _ => PackIconKind.HelpCircleOutline
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
