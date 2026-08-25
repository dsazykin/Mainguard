using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DiffPlex;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

// Synchronized 3-pane merge editor (T-04): Ours (read-only) | Result (editable) | Theirs (read-only).
// Each chunk is padded with filler lines so a conflict occupies the same vertical band in all three
// panes. An add/add conflict reserves stacked slots in the Result (ours on top, theirs below) so a
// side always has a home even before the other is decided; the gutter draws a connector polygon that
// slants from each side's line to its destination slot, so an edit "flows down" into an already-taken
// line. MergeBandRenderer tints regions; the accept/reject glyphs live in MergeGutter columns hugging
// each side. Resolution logic lives in the ViewModel/engine — this code-behind only renders chunk
// state and turns gutter clicks / result edits into calls.
public partial class ConflictResolverWindow : ChromedWindow
{
    private ConflictResolverWindowViewModel? _vm;
    private readonly List<MergeBand> _bands = new();
    private readonly HashSet<int> _resultFillerLines = new();

    // Word-level highlight spans per document line (the exact changed words within a conflict line).
    private readonly Dictionary<int, List<CharSpan>> _oursSpans = new();
    private readonly Dictionary<int, List<CharSpan>> _theirsSpans = new();

    private readonly MergeBandRenderer _oursRenderer;
    private readonly MergeBandRenderer _resultRenderer;
    private readonly MergeBandRenderer _theirsRenderer;

    private ScrollViewer? _oursSv, _resultSv, _theirsSv;
    private bool _scrollWired;
    private bool _syncingScroll;
    private bool _settingText;
    private bool _suppressRebuild;
    private bool _chunkHandlersAttached;
    private int _navIndex = -1;

    public ConflictResolverWindow()
    {
        InitializeComponent();

        _oursRenderer = new MergeBandRenderer(MergePane.Ours, _bands, _oursSpans, null);
        _resultRenderer = new MergeBandRenderer(MergePane.Result, _bands, null, _resultFillerLines);
        _theirsRenderer = new MergeBandRenderer(MergePane.Theirs, _bands, _theirsSpans, null);
        OursEditor.TextArea.TextView.BackgroundRenderers.Add(_oursRenderer);
        ResultEditor.TextArea.TextView.BackgroundRenderers.Add(_resultRenderer);
        TheirsEditor.TextArea.TextView.BackgroundRenderers.Add(_theirsRenderer);

        OursGutter.Configure(OursEditor, _bands, MergePane.Ours, OnGutterAction);
        TheirsGutter.Configure(TheirsEditor, _bands, MergePane.Theirs, OnGutterAction);

        // Redraw gutters whenever their side pane re-lays-out its lines (scroll, resize, doc change).
        OursEditor.TextArea.TextView.VisualLinesChanged += (_, __) => OursGutter.InvalidateVisual();
        TheirsEditor.TextArea.TextView.VisualLinesChanged += (_, __) => TheirsGutter.InvalidateVisual();

        ResultEditor.TextChanged += OnResultTextChanged;

        PrevConflictButton.Click += (_, __) => NavigateConflict(-1);
        NextConflictButton.Click += (_, __) => NavigateConflict(1);
        AcceptAllOursButton.Click += (_, __) => AcceptAll(ConflictSide.Ours);
        AcceptAllTheirsButton.Click += (_, __) => AcceptAll(ConflictSide.Theirs);

        DataContextChanged += OnDataContextChanged;
        Opened += (_, __) => { WireScrollSync(); EnsureBuilt(); };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm = DataContext as ConflictResolverWindowViewModel;
        if (_vm == null) return;
        _vm.GetMergedText = ReadResultText;
        _vm.ChunksReady += () => Avalonia.Threading.Dispatcher.UIThread.Post(EnsureBuilt);
        EnsureBuilt();
    }

    private void EnsureBuilt()
    {
        if (_vm == null || !_vm.IsChunkMode || _vm.Chunks.Count == 0) return;
        AttachChunkHandlers();
        BuildDocuments();
    }

    private void AttachChunkHandlers()
    {
        if (_chunkHandlersAttached || _vm == null) return;
        foreach (var chunk in _vm.Chunks)
            chunk.ResolutionChanged += OnChunkResolutionChanged;
        _chunkHandlersAttached = true;
    }

    private void OnChunkResolutionChanged()
    {
        if (_suppressRebuild) return;
        BuildDocuments();
    }

    private void OnGutterAction(MergeBand band, MergePane side, bool accept)
    {
        if (_vm == null) return;
        var chunk = _vm.Chunks[band.ChunkIndex];
        if (side == MergePane.Ours)
        {
            if (accept) chunk.ToggleAcceptOurs(); else chunk.ToggleRejectOurs();
        }
        else
        {
            if (accept) chunk.ToggleAcceptTheirs(); else chunk.ToggleRejectTheirs();
        }
    }

    // ---- Document construction (filler alignment + stacked add/add slots) ----

    private static List<string> SplitLines(string text)
        => text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();

    private void BuildDocuments()
    {
        if (_vm == null) return;

        var ours = new List<string>();
        var result = new List<string>();
        var theirs = new List<string>();
        _bands.Clear();
        _resultFillerLines.Clear();
        _oursSpans.Clear();
        _theirsSpans.Clear();

        for (int i = 0; i < _vm.Chunks.Count; i++)
        {
            var c = _vm.Chunks[i];
            var oLines = SplitLines(c.OursText);
            var tLines = SplitLines(c.TheirsText);
            int start = ours.Count + 1;   // 1-based document line
            int oursLen = oLines.Count, theirsLen = tLines.Count;

            int h;
            int oursResultOffset = 0;
            int theirsResultOffset = 0;

            bool bothAccepted = c.OursChoice == SideChoice.Accepted && c.TheirsChoice == SideChoice.Accepted;
            // add/add reserves a stacked home for each side up-front (you see where both would land);
            // a modify/modify conflict stays a single row until BOTH sides are taken, at which point it
            // reflows so theirs flows *down* below the ours line already sitting in the Result.
            bool stacked = c.IsConflict && !c.IsManuallyEdited && (c.IsAddConflict || bothAccepted);

            if (stacked)
            {
                h = Math.Max(1, oursLen + theirsLen);
                oursResultOffset = 0;
                theirsResultOffset = oursLen;

                AppendWithFiller(ours, oLines, h);
                AppendWithFiller(theirs, tLines, h);

                for (int k = 0; k < oursLen; k++)
                    result.Add(c.OursChoice == SideChoice.Accepted ? oLines[k] : "");
                for (int k = 0; k < theirsLen; k++)
                    result.Add(c.TheirsChoice == SideChoice.Accepted ? tLines[k] : "");
                for (int k = oursLen + theirsLen; k < h; k++) result.Add("");

                if (c.OursChoice != SideChoice.Accepted)
                    for (int k = 0; k < oursLen; k++) _resultFillerLines.Add(start + k);
                if (c.TheirsChoice != SideChoice.Accepted)
                    for (int k = 0; k < theirsLen; k++) _resultFillerLines.Add(start + oursLen + k);
            }
            else if (c.IsConflict && !c.IsManuallyEdited)
            {
                // Single-row modify/modify: OURS and THEIRS aligned on the same rows (so word diffs
                // line up); the Result shows the accepted side (or stays empty while undecided).
                h = Math.Max(1, Math.Max(oursLen, theirsLen));
                var rLines = SplitLines(c.ResultText);
                AppendWithFiller(ours, oLines, h);
                AppendWithFiller(theirs, tLines, h);
                AppendWithFiller(result, rLines, h);
                for (int f = rLines.Count; f < h; f++) _resultFillerLines.Add(start + f);
            }
            else
            {
                var rLines = SplitLines(c.ResultText);
                h = Math.Max(oLines.Count, Math.Max(rLines.Count, tLines.Count));
                if (h == 0) continue;

                AppendWithFiller(ours, oLines, h);
                AppendWithFiller(result, rLines, h);
                AppendWithFiller(theirs, tLines, h);

                for (int f = rLines.Count; f < h; f++) _resultFillerLines.Add(start + f);
            }

            // Word-level highlight for modify/modify conflicts: mark the words that differ
            // between the two sides on each shared line.
            if (c.IsConflict && !c.IsAddConflict)
            {
                int shared = Math.Min(oLines.Count, tLines.Count);
                for (int k = 0; k < shared; k++)
                {
                    var (os, ts) = WordSpans(oLines[k], tLines[k]);
                    if (os.Count > 0) _oursSpans[start + k] = os;
                    if (ts.Count > 0) _theirsSpans[start + k] = ts;
                }
            }

            _bands.Add(new MergeBand
            {
                ChunkIndex = i,
                Kind = c.Kind,
                IsConflict = c.IsConflict,
                IsAddConflict = c.IsAddConflict,
                Resolved = c.IsResolved,
                Ours = c.OursChoice,
                Theirs = c.TheirsChoice,
                StartLine = start,
                Height = h,
                OursLines = oLines.Count,
                TheirsLines = tLines.Count,
                OursResultOffset = oursResultOffset,
                TheirsResultOffset = theirsResultOffset,
            });
        }

        _settingText = true;
        SetDocText(OursEditor, string.Join("\n", ours));
        SetDocText(ResultEditor, string.Join("\n", result));
        SetDocText(TheirsEditor, string.Join("\n", theirs));
        _settingText = false;

        InvalidateRenderers();
    }

    private static void AppendWithFiller(List<string> list, List<string> lines, int height)
    {
        list.AddRange(lines);
        for (int i = lines.Count; i < height; i++) list.Add("");
    }

    private static void SetDocText(TextEditor editor, string text)
    {
        if (editor.Document == null) editor.Document = new AvaloniaEdit.Document.TextDocument();
        if (editor.Document.Text != text) editor.Document.Text = text;
    }

    private void InvalidateRenderers()
    {
        OursEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        ResultEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        TheirsEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        OursGutter.InvalidateVisual();
        TheirsGutter.InvalidateVisual();
    }

    // ---- Reading the merged result (strip still-empty filler lines) ----

    private string ReadResultText()
    {
        var doc = ResultEditor.Document;
        if (doc == null) return "";
        var sb = new StringBuilder();
        bool any = false;
        foreach (var line in doc.Lines)
        {
            var text = doc.GetText(line);
            if (_resultFillerLines.Contains(line.LineNumber) && text.Trim().Length == 0)
                continue;   // filler gap we inserted for alignment
            if (any) sb.Append('\n');
            sb.Append(text);
            any = true;
        }
        return any ? sb.ToString() + "\n" : "";
    }

    private void AcceptAll(ConflictSide side)
    {
        if (_vm == null) return;
        _suppressRebuild = true;
        try
        {
            foreach (var c in _vm.Chunks.Where(c => c.IsConflict && !c.IsResolved))
            {
                if (side == ConflictSide.Ours) c.ForceOurs();
                else c.ForceTheirs();
            }
        }
        finally { _suppressRebuild = false; }
        BuildDocuments();
    }

    // ---- Editable result: capture typed edits as the conflict's custom resolution ----

    private void OnResultTextChanged(object? sender, EventArgs e)
    {
        if (_settingText || _vm == null) return;
        var caretLine = ResultEditor.TextArea.Caret.Line;
        var band = _bands.FirstOrDefault(b => b.IsConflict
            && caretLine >= b.StartLine && caretLine < b.StartLine + b.Height);
        if (band == null) return;

        var doc = ResultEditor.Document;
        var sb = new StringBuilder();
        for (int ln = band.StartLine; ln < band.StartLine + band.Height && ln <= doc.LineCount; ln++)
        {
            if (_resultFillerLines.Contains(ln)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(doc.GetText(doc.GetLineByNumber(ln)));
        }
        _vm.Chunks[band.ChunkIndex].SetCustomFromEditor(sb.ToString());
        _vm.NotifyResolvedByEdit();
    }

    // ---- Conflict navigation ----

    private void NavigateConflict(int dir)
    {
        var conflicts = _bands.Where(b => b.IsConflict).ToList();
        if (conflicts.Count == 0) return;
        _navIndex = ((_navIndex + dir) % conflicts.Count + conflicts.Count) % conflicts.Count;
        ResultEditor.ScrollToLine(conflicts[_navIndex].StartLine);
    }

    // ---- Synchronized scrolling ----

    private void WireScrollSync()
    {
        if (_scrollWired) return;
        _oursSv = OursEditor.FindDescendantOfType<ScrollViewer>();
        _resultSv = ResultEditor.FindDescendantOfType<ScrollViewer>();
        _theirsSv = TheirsEditor.FindDescendantOfType<ScrollViewer>();
        if (_oursSv == null || _resultSv == null || _theirsSv == null) return;

        _oursSv.ScrollChanged += (_, __) => SyncScroll(_oursSv);
        _resultSv.ScrollChanged += (_, __) => SyncScroll(_resultSv);
        _theirsSv.ScrollChanged += (_, __) => SyncScroll(_theirsSv);
        _scrollWired = true;
    }

    private void SyncScroll(ScrollViewer? source)
    {
        if (_syncingScroll || source == null) return;
        _syncingScroll = true;
        try
        {
            double y = source.Offset.Y;
            foreach (var sv in new[] { _oursSv, _resultSv, _theirsSv })
            {
                if (sv == null || ReferenceEquals(sv, source)) continue;
                if (Math.Abs(sv.Offset.Y - y) > 0.5)
                    sv.Offset = new Vector(sv.Offset.X, y);
            }
        }
        finally { _syncingScroll = false; }
        OursGutter.InvalidateVisual();
        TheirsGutter.InvalidateVisual();
    }

    // ---- Word-level (intra-line) diff spans ----

    private static (List<CharSpan> Ours, List<CharSpan> Theirs) WordSpans(string oursLine, string theirsLine)
    {
        // old = theirs, new = ours; split into words on whitespace so the changed words are highlighted.
        var r = DiffPlex.Differ.Instance.CreateDiffs(theirsLine, oursLine, false, false,
            new DiffPlex.Chunkers.DelimiterChunker(new[] { ' ', '\t' }));
        var oursOff = Offsets(r.PiecesNew);
        var theirsOff = Offsets(r.PiecesOld);
        var oursSpans = new List<CharSpan>();
        var theirsSpans = new List<CharSpan>();
        foreach (var b in r.DiffBlocks)
        {
            if (b.InsertCountB > 0)
                oursSpans.Add(new CharSpan(oursOff[b.InsertStartB],
                    oursOff[b.InsertStartB + b.InsertCountB] - oursOff[b.InsertStartB]));
            if (b.DeleteCountA > 0)
                theirsSpans.Add(new CharSpan(theirsOff[b.DeleteStartA],
                    theirsOff[b.DeleteStartA + b.DeleteCountA] - theirsOff[b.DeleteStartA]));
        }
        return (oursSpans, theirsSpans);
    }

    private static int[] Offsets(System.Collections.Generic.IReadOnlyList<string> pieces)
    {
        var offs = new int[pieces.Count + 1];
        for (int i = 0; i < pieces.Count; i++) offs[i + 1] = offs[i] + pieces[i].Length;
        return offs;
    }
}

internal enum MergePane { Ours, Result, Theirs }

internal readonly record struct CharSpan(int Start, int Length);

internal sealed class MergeBand
{
    public int ChunkIndex;
    public ChunkKind Kind;
    public bool IsConflict;
    public bool IsAddConflict;
    public bool Resolved;
    public SideChoice Ours;
    public SideChoice Theirs;
    public int StartLine;   // 1-based
    public int Height;
    public int OursLines;
    public int TheirsLines;
    // Where each side's content lands in the Result, in rows relative to StartLine. Ours is normally
    // at the top (0); theirs slides below ours when ours occupies the slot above it.
    public int OursResultOffset;
    public int TheirsResultOffset;
}

// Draws per-band region backgrounds + word-level highlights. Color semantics:
//  modify/modify conflict = red, add/add conflict = grey; a side turns green only when accepted,
//  and an accepted line in the Result turns green. Side panes tint only their own content rows.
internal sealed class MergeBandRenderer : IBackgroundRenderer
{
    private readonly MergePane _pane;
    private readonly List<MergeBand> _bands;
    private readonly Dictionary<int, List<CharSpan>>? _spans;
    private readonly HashSet<int>? _resultFillers;

    public MergeBandRenderer(MergePane pane, List<MergeBand> bands,
        Dictionary<int, List<CharSpan>>? spans, HashSet<int>? resultFillers)
    {
        _pane = pane;
        _bands = bands;
        _spans = spans;
        _resultFillers = resultFillers;
    }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (_bands.Count == 0) return;
        textView.EnsureVisualLines();

        var red = ThemeBrush.Resolve("DiffRemovedBg", "#33191E");
        var green = ThemeBrush.Resolve("DiffAddedBg", "#11271B");
        var grey = ThemeBrush.Resolve("SurfaceHover", "#252B34");
        var theirsTint = ThemeBrush.Resolve("AccentSelection", "#268B8BF5");
        var filler = ThemeBrush.Resolve("SurfaceCard", "#1A1E24");
        var word = ThemeBrush.Translucent(ThemeBrush.Resolve("DangerBrush", "#E5484D"), 0.28);
        double width = textView.Bounds.Width;

        foreach (var vl in textView.VisualLines)
        {
            if (vl.FirstDocumentLine == null || vl.FirstDocumentLine.IsDeleted) continue;
            int ln = vl.FirstDocumentLine.LineNumber;
            var band = FindBand(ln);
            if (band == null) continue;

            var (brush, showsConflict) = BandBrush(band, red, green, grey, theirsTint, filler, ln);
            if (brush != null)
                dc.DrawRectangle(brush, null, new Rect(0, vl.VisualTop, width, vl.Height));

            // Word-level highlight of the changed words, only while this side still shows the conflict.
            if (_spans != null && showsConflict && _spans.TryGetValue(ln, out var spans))
            {
                foreach (var sp in spans)
                {
                    if (sp.Length <= 0) continue;
                    double x1 = textView.GetVisualPosition(new TextViewPosition(ln, sp.Start + 1), VisualYPosition.LineTop).X;
                    double x2 = textView.GetVisualPosition(new TextViewPosition(ln, sp.Start + sp.Length + 1), VisualYPosition.LineTop).X;
                    dc.DrawRectangle(word, null, new Rect(x1, vl.VisualTop, Math.Max(1, x2 - x1), vl.Height));
                }
            }
        }
    }

    // Returns the band background for this line in this pane, and whether it is still showing the
    // (un-accepted) conflict color (so word highlights apply).
    private (IBrush? Brush, bool ShowsConflict) BandBrush(MergeBand band, IBrush red, IBrush green,
        IBrush grey, IBrush theirsTint, IBrush filler, int ln)
    {
        if (band.IsConflict)
        {
            IBrush conflictColor = band.IsAddConflict ? grey : red;
            int row = ln - band.StartLine;
            switch (_pane)
            {
                case MergePane.Ours:
                    if (row >= band.OursLines) return (filler, false);   // empty part of the band
                    return band.Ours == SideChoice.Accepted ? (green, false) : (conflictColor, true);
                case MergePane.Theirs:
                    if (row >= band.TheirsLines) return (filler, false);
                    return band.Theirs == SideChoice.Accepted ? (green, false) : (conflictColor, true);
                default: // Result: an accepted line is green; a reserved-but-empty slot shows the
                         // conflict color until resolved, then fades to filler.
                    bool empty = _resultFillers != null && _resultFillers.Contains(ln);
                    if (!empty) return (green, false);
                    return band.Resolved ? (filler, false) : (conflictColor, false);
            }
        }

        int idx = ln - band.StartLine;
        int contentCount = _pane == MergePane.Ours ? band.OursLines
                         : _pane == MergePane.Theirs ? band.TheirsLines
                         : band.Height;
        if (_pane != MergePane.Result && idx >= contentCount) return (filler, false);   // alignment gap
        return _pane switch
        {
            MergePane.Ours => (band.Kind == ChunkKind.LeftOnly ? green : null, false),
            MergePane.Theirs => (band.Kind == ChunkKind.RightOnly ? theirsTint : null, false),
            _ => (null, false),
        };
    }

    private MergeBand? FindBand(int line)
    {
        foreach (var b in _bands)
            if (line >= b.StartLine && line < b.StartLine + b.Height) return b;
        return null;
    }
}

// A gutter column between two panes. For each conflict it draws a connector polygon that links the
// side's change (on the pane-facing edge) to where that change lands in the Result (on the
// result-facing edge) — so it slants when the destination is offset — plus an accept-chevron and a
// reject-✕ hugging the side it belongs to. Row positions are computed from the adjacent pane's line
// geometry (with a line-height fallback) so they render even before the editor realizes its lines.
public sealed class MergeGutter : Control
{
    private const double GlyphSize = 16;

    private TextEditor? _editor;
    private List<MergeBand>? _bands;
    private MergePane _side;
    private Action<MergeBand, MergePane, bool>? _action;

    internal void Configure(TextEditor editor, List<MergeBand> bands, MergePane side,
        Action<MergeBand, MergePane, bool> action)
    {
        _editor = editor;
        _bands = bands;
        _side = side;
        _action = action;
    }

    private double LineHeight()
    {
        var tv = _editor!.TextArea.TextView;
        foreach (var vl in tv.VisualLines) if (vl.Height > 0) return vl.Height;
        return _editor.FontSize * 1.35;
    }

    private double BandTop(MergeBand band, double lineHeight)
    {
        var tv = _editor!.TextArea.TextView;
        double scroll = tv.ScrollOffset.Y;
        var vl = tv.GetVisualLine(band.StartLine);
        return (vl != null ? vl.VisualTop : (band.StartLine - 1) * lineHeight) - scroll;
    }

    // Draw origins for the two glyphs, both hugging the right edge of the gutter — which is the side
    // each group acts on: ours' gutter sits just left of the Result, theirs' gutter just left of the
    // Theirs pane. Inner is the rightmost glyph; outer sits just to its left.
    private static (double Outer, double Inner) GlyphXs(double w) => (w - 37, w - 19);

    public override void Render(DrawingContext context)
    {
        // Full-bounds fill so the gutter blends with the card and is hit-testable.
        context.DrawRectangle(ThemeBrush.Resolve("SurfaceDeep", "#0B0D10"), null,
            new Rect(0, 0, Bounds.Width, Bounds.Height));
        if (_editor == null || _bands == null) return;

        double w = Bounds.Width;
        double lh = LineHeight();

        var accentAvail = ThemeBrush.Resolve("AccentBrush", "#8487F0");
        var accepted = ThemeBrush.Resolve("SuccessBrush", "#42B968");
        var rejected = ThemeBrush.Resolve("DangerBrush", "#E5484D");
        var muted = ThemeBrush.Resolve("TextMuted", "#8A93A6");
        var red = ThemeBrush.Resolve("DiffRemovedBg", "#33191E");
        var green = ThemeBrush.Resolve("DiffAddedBg", "#11271B");
        var grey = ThemeBrush.Resolve("SurfaceHover", "#252B34");

        var (outerX, innerX) = GlyphXs(w);

        foreach (var band in _bands.Where(b => b.IsConflict))
        {
            double top = BandTop(band, lh);
            double bandHeight = lh * band.Height;
            if (top + bandHeight < 0 || top > Bounds.Height) continue;   // off-screen

            var choice = _side == MergePane.Ours ? band.Ours : band.Theirs;
            int sideLines = _side == MergePane.Ours ? band.OursLines : band.TheirsLines;
            int resOffset = _side == MergePane.Ours ? band.OursResultOffset : band.TheirsResultOffset;
            if (sideLines <= 0) continue;

            IBrush conflictColor = band.IsAddConflict ? grey : red;
            IBrush sideColor = choice == SideChoice.Accepted ? green : conflictColor;

            // Connector: side content on the pane-facing edge -> destination slot on the result edge.
            double sideTop = top, sideBot = top + sideLines * lh;
            double resTop = top + resOffset * lh, resBot = resTop + sideLines * lh;
            context.DrawGeometry(sideColor, null, Connector(w, sideTop, sideBot, resTop, resBot));

            var acceptBrush = choice == SideChoice.Accepted ? accepted : accentAvail;
            var rejectBrush = choice == SideChoice.Rejected ? rejected : muted;
            string acceptGlyph = _side == MergePane.Ours ? "»" : "«";
            // Ours reads "✕ »" (accept nearest the Result); theirs reads "« ✕" (reject nearest Theirs).
            double acceptX = _side == MergePane.Ours ? innerX : outerX;
            double rejectX = _side == MergePane.Ours ? outerX : innerX;
            double centerY = sideTop + sideLines * lh / 2;

            DrawGlyph(context, acceptGlyph, acceptX, centerY, acceptBrush, choice == SideChoice.Accepted);
            DrawGlyph(context, "✕", rejectX, centerY, rejectBrush, choice == SideChoice.Rejected);
        }
    }

    // Quad linking the side edge (span sideTop..sideBot) to the result edge (span resTop..resBot).
    private StreamGeometry Connector(double w, double sideTop, double sideBot, double resTop, double resBot)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        if (_side == MergePane.Ours)
        {
            // ours pane is on the left; result on the right.
            g.BeginFigure(new Point(0, sideTop), true);
            g.LineTo(new Point(w, resTop));
            g.LineTo(new Point(w, resBot));
            g.LineTo(new Point(0, sideBot));
        }
        else
        {
            // result on the left; theirs pane on the right.
            g.BeginFigure(new Point(w, sideTop), true);
            g.LineTo(new Point(0, resTop));
            g.LineTo(new Point(0, resBot));
            g.LineTo(new Point(w, sideBot));
        }
        g.EndFigure(true);
        return geo;
    }

    // Draws a glyph centered vertically on centerY so it sits on one baseline with its neighbor.
    private static void DrawGlyph(DrawingContext dc, string glyph, double x, double centerY,
        IBrush brush, bool active)
    {
        var ft = new FormattedText(glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), GlyphSize, brush);
        double top = centerY - ft.Height / 2;
        if (active)
        {
            var pill = new Rect(x - 3, top - 1, ft.Width + 6, ft.Height + 2);
            dc.DrawRectangle(new SolidColorBrush(Colors.Black, 0.30), null, pill, 4, 4);
        }
        dc.DrawText(ft, new Point(x, top));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor == null || _bands == null || _action == null) return;

        double lh = LineHeight();
        var p = e.GetPosition(this);
        var (outerX, innerX) = GlyphXs(Bounds.Width);
        double mid = (outerX + innerX) / 2 + GlyphSize / 3;   // boundary between the two glyphs

        foreach (var band in _bands.Where(b => b.IsConflict))
        {
            double top = BandTop(band, lh);
            if (p.Y < top || p.Y >= top + lh * band.Height) continue;

            // Ours: outer glyph = reject, inner = accept. Theirs: outer = accept, inner = reject.
            bool inner = p.X >= mid;
            bool accept = _side == MergePane.Ours ? inner : !inner;
            _action(band, _side, accept);
            e.Handled = true;
            return;
        }
    }
}

internal static class ThemeBrush
{
    public static IBrush Resolve(string key, string fallback)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush b)
            return b;
        return new SolidColorBrush(Color.Parse(fallback));
    }

    public static IBrush Translucent(IBrush brush, double alpha)
        => brush is ISolidColorBrush s ? new SolidColorBrush(s.Color, alpha) : brush;
}
