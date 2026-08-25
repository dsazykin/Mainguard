using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Mainguard.Git.Analytics;
using Mainguard.Git.Services;
using Mainguard.UI.Charts;
using Mainguard.UI.ViewModels;
using SkiaSharp;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// Drives the analytics screen. Runs the two <see cref="RepositoryAnalyzer"/> walks off the UI thread
/// under a <see cref="CancellationTokenSource"/> that is cancelled when the workspace is replaced
/// (<see cref="Dispose"/>), then folds the commit stats through the pure aggregators and builds the four
/// LiveCharts series (language pie, punch-card heatmap, weekly churn, top contributors). Every paint
/// colour comes from <see cref="ChartTheme"/> (theme tokens), never a hardcoded hex.
/// </summary>
public partial class AnalyticsViewModel : ViewModelBase, IDisposable
{
    private const int TopLanguages = 5;   // + an "Other" slice
    private const int TopContributors = 8;

    private readonly RepositoryAnalyzer _analyzer;
    private readonly string _repositoryPath;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasCommitData;

    [ObservableProperty]
    private bool _hasLanguageData;

    [ObservableProperty]
    private ISeries[]? _languageSeries;

    [ObservableProperty]
    private ISeries[]? _punchCardSeries;

    [ObservableProperty]
    private ISeries[]? _churnSeries;

    [ObservableProperty]
    private ISeries[]? _contributorSeries;

    // Observable so the async load's post-construction assignment propagates to the already-bound
    // chart controls (a plain CLR property would leave the charts on their default numeric axes).
    [ObservableProperty] private Axis[] _punchCardXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _punchCardYAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _churnXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _churnYAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _contributorXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _contributorYAxes = Array.Empty<Axis>();

    public AnalyticsViewModel(string repositoryPath, IGitService? git = null)
    {
        _repositoryPath = repositoryPath;
        _analyzer = new RepositoryAnalyzer(git);

        _ = LoadAnalyticsAsync();
    }

    private async Task LoadAnalyticsAsync()
    {
        IsLoading = true;
        var ct = _cts.Token;

        try
        {
            var languagesTask = _analyzer.CalculateLanguageBreakdownAsync(_repositoryPath, ct);
            var commitsTask = _analyzer.CollectCommitStatsAsync(_repositoryPath, ct);
            await Task.WhenAll(languagesTask, commitsTask).ConfigureAwait(true);

            var commits = commitsTask.Result;
            BuildLanguageSeries(languagesTask.Result);
            BuildPunchCardSeries(PunchCardStats.FromCommits(commits));
            BuildChurnSeries(ChurnStats.FromCommits(commits));
            BuildContributorSeries(ContributorStats.FromCommits(commits));
            HasCommitData = commits.Count > 0;
        }
        catch (OperationCanceledException)
        {
            return; // workspace switched away — drop the results
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---- Language breakdown: categorical pie (identity) ----------------------------------------

    private void BuildLanguageSeries(Dictionary<LanguageModel, long> data)
    {
        var total = data.Values.Sum();
        HasLanguageData = total > 0;
        if (!HasLanguageData) { LanguageSeries = Array.Empty<ISeries>(); return; }

        var palette = ChartTheme.CategoricalPalette();
        var ordered = data.OrderByDescending(x => x.Value).ToList();

        var series = new List<ISeries>();
        for (int i = 0; i < ordered.Count && i < TopLanguages; i++)
        {
            var (lang, bytes) = (ordered[i].Key, ordered[i].Value);
            series.Add(NamedPie(lang.Name, bytes, palette[i % palette.Length], total));
        }

        // Everything past the top N folds into a single neutral "Other" slice — never a generated hue.
        var otherBytes = ordered.Skip(TopLanguages).Sum(x => x.Value);
        if (otherBytes > 0)
            series.Add(NamedPie("Other", otherBytes, ChartTheme.OtherColor(), total));

        LanguageSeries = series.ToArray();
    }

    private static PieSeries<long> NamedPie(string name, long value, SKColor color, long total)
    {
        double pct = total == 0 ? 0 : 100.0 * value / total;
        return new PieSeries<long>
        {
            Values = new[] { value },
            Name = name,
            Fill = new SolidColorPaint(color),
            InnerRadius = 60,
            ToolTipLabelFormatter = _ => $"{name}: {value:N0} bytes ({pct:F1}%)",
        };
    }

    // ---- Punch card: sequential heatmap (magnitude) --------------------------------------------

    private void BuildPunchCardSeries(PunchCardStats stats)
    {
        // Emit the full 7×24 grid (including zeros) so the heatmap reads as a complete calendar; the
        // sequential ramp sends empty cells to the near-surface colour and the peak to Accent.
        var cells = new List<WeightedPoint>(7 * 24);
        for (int d = 0; d < 7; d++)
            for (int h = 0; h < 24; h++)
                cells.Add(new WeightedPoint(h, d, stats.CommitsByDayHour[d, h]));

        PunchCardSeries = new ISeries[]
        {
            new HeatSeries<WeightedPoint>
            {
                Values = cells,
                HeatMap = ChartTheme.HeatRamp(),
                Name = "Commits",
            }
        };

        PunchCardXAxes = new[]
        {
            LabelAxis(new[]
            {
                "12a","1a","2a","3a","4a","5a","6a","7a","8a","9a","10a","11a",
                "12p","1p","2p","3p","4p","5p","6p","7p","8p","9p","10p","11p"
            })
        };
        PunchCardYAxes = new[] { LabelAxis(new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }) };
    }

    // ---- Churn: weekly time series (change over time) ------------------------------------------

    private void BuildChurnSeries(ChurnStats churn)
    {
        var added = churn.Weeks
            .Select(w => new DateTimePoint(w.WeekStart.ToDateTime(TimeOnly.MinValue), w.Added)).ToList();
        var removed = churn.Weeks
            .Select(w => new DateTimePoint(w.WeekStart.ToDateTime(TimeOnly.MinValue), w.Removed)).ToList();

        var success = ChartTheme.Color("SuccessBrush", "#42B968");
        var danger = ChartTheme.Color("DangerBrush", "#E5484D");

        ChurnSeries = new ISeries[]
        {
            ChurnLine("Added", added, success),
            ChurnLine("Removed", removed, danger),
        };

        var muted = new SolidColorPaint(ChartTheme.Color("TextMuted", "#8A93A6")) { StrokeThickness = 1 };
        var hairline = new SolidColorPaint(ChartTheme.Color("BorderHairline", "#262B33")) { StrokeThickness = 1 };

        ChurnXAxes = new[]
        {
            new Axis
            {
                Labeler = v => v <= 0 ? string.Empty : new DateTime((long)v).ToString("MMM d"),
                UnitWidth = TimeSpan.FromDays(7).Ticks,
                MinStep = TimeSpan.FromDays(7).Ticks,
                TextSize = 12,
                LabelsPaint = muted,
            }
        };
        ChurnYAxes = new[]
        {
            new Axis { MinLimit = 0, TextSize = 12, LabelsPaint = muted, SeparatorsPaint = hairline }
        };
    }

    private static LineSeries<DateTimePoint> ChurnLine(string name, List<DateTimePoint> pts, SKColor color)
        => new()
        {
            Name = name,
            Values = pts,
            Stroke = new SolidColorPaint(color) { StrokeThickness = 2 },
            Fill = null, // two overlapping translucent areas muddy each other — lines + markers read cleaner
            GeometryFill = new SolidColorPaint(color),
            GeometryStroke = new SolidColorPaint(color) { StrokeThickness = 2 },
            GeometrySize = 8,
            LineSmoothness = 0,
        };

    // ---- Contributors: ranked single-hue bars (magnitude) -------------------------------------

    private void BuildContributorSeries(IReadOnlyList<ContributorStat> contributors)
    {
        // Highest at the top: RowSeries plots index 0 at the bottom, so reverse the top-N slice.
        var top = contributors.Take(TopContributors).Reverse().ToList();
        var counts = top.Select(c => c.Commits).ToArray();
        var names = top.Select(c => c.Name).ToArray();

        var accent = ChartTheme.Color("AccentBrush", "#8487F0");
        var textPrimary = new SolidColorPaint(ChartTheme.Color("TextPrimary", "#E6E9EF"));
        var muted = new SolidColorPaint(ChartTheme.Color("TextMuted", "#8A93A6")) { StrokeThickness = 1 };
        var hairline = new SolidColorPaint(ChartTheme.Color("BorderHairline", "#262B33")) { StrokeThickness = 1 };

        ContributorSeries = new ISeries[]
        {
            new RowSeries<int>
            {
                Name = "Commits",
                Values = counts,
                Fill = new SolidColorPaint(accent),
                MaxBarWidth = 26,
                Padding = 6,
                DataLabelsPaint = textPrimary,
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("N0"),
            }
        };
        ContributorXAxes = new[]
        {
            new Axis
            {
                MinLimit = 0, MinStep = 1, // commit counts are whole numbers — no fractional ticks
                TextSize = 12, LabelsPaint = muted, SeparatorsPaint = hairline,
            }
        };
        ContributorYAxes = new[]
        {
            new Axis { Labels = names, TextSize = 12, LabelsPaint = muted }
        };
    }

    private static Axis LabelAxis(string[] labels) => new()
    {
        Labels = labels,
        TextSize = 12,
        LabelsPaint = new SolidColorPaint(ChartTheme.Color("TextMuted", "#8A93A6")),
    };

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
