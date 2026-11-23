using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

using OmniRead.Localization;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Reading time per day for the last thirty days, against the daily goal.</summary>
public partial class StatsWindow : HandyControl.Controls.Window {
    private const int Days = 30;
    private const double ChartHeight = 180;

    private readonly ReaderSettings _settings;

    public StatsWindow(ReaderSettings settings) {
        _settings = settings;
        InitializeComponent();
        DataContext = settings;
        Refresh();
    }

    private void OnGoalChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        // Raised once while the window is being built, before the chart exists.
        if (IsInitialized && (Bars is not null)) {
            Refresh();
        }
    }

    private void Refresh() {
        ReadingStats stats = _settings.Stats;
        double goal = _settings.DailyGoalMinutes * 60;
        DateTime today = DateTime.Today;
        double[] seconds = [.. Enumerable.Range(0, Days).Select(index => stats.SecondsOn(today.AddDays(index - Days + 1)))];

        TodayText.Text = Duration(stats.TodaySeconds);
        TodayGoalText.Text = (goal > 0) ? Localizer.Format("History_OfGoal", Duration(goal)) : "";
        StreakText.Text = (goal > 0) ? Localizer.Format("History_Days", stats.Streak(goal)) : "–";
        MonthText.Text = Duration(seconds.Sum());
        TotalText.Text = Duration(stats.SecondsRead);

        // The tallest bar fills the chart, and the goal line is always within it.
        double top = Math.Max(Math.Max(seconds.Max(), goal), 60) * 1.08;
        Bars.Children.Clear();
        Labels.Children.Clear();
        for (int i = 0; i < Days; i++) {
            DateTime day = today.AddDays(i - Days + 1);
            bool met = (goal > 0) && (seconds[i] >= goal);
            Rectangle bar = new() {
                Height = Math.Max((seconds[i] > 0) ? 2 : 0, ChartHeight * seconds[i] / top),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 0),
                RadiusX = 2,
                RadiusY = 2,
                Opacity = (met || (goal <= 0)) ? 1 : 0.45
            };
            bar.SetResourceReference(Shape.FillProperty, "AccentBrush");
            // The whole column answers to the pointer, so short days can be read too.
            Grid column = new() { Background = System.Windows.Media.Brushes.Transparent, ToolTip = $"{day:D}\n{Duration(seconds[i])}" };
            column.Children.Add(bar);
            Bars.Children.Add(column);

            // Every fifth day is dated. A canvas lets the date run wider than its narrow column.
            TextBlock label = new() { Text = ((i % 5) == 0) ? day.ToString("d MMM") : "", FontSize = 11 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Canvas cell = new() { Height = 16 };
            cell.Children.Add(label);
            Labels.Children.Add(cell);
        }
        GoalLine.Visibility = (goal > 0) ? Visibility.Visible : Visibility.Collapsed;
        GoalLine.Margin = new Thickness(0, 0, 0, ChartHeight * goal / top);
    }

    private static string Duration(double seconds) {
        int minutes = (int)Math.Round(seconds / 60);
        return (minutes < 60) ? Localizer.Format("Format_ShortMinutes", minutes) : Localizer.Format("Format_HoursMinutes", minutes / 60, minutes % 60);
    }
}