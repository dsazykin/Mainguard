using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using TextMateSharp.Grammars;

namespace Mainguard.App.Shell.Views;

public partial class DiffViewerView : UserControl
{
    private bool _isUpdatingFromViewModel;
    private TextMate.Installation _textMateInstallation;
    private RegistryOptions _registryOptions;
    private DiffMarginRenderer _diffRenderer;

    // Drag-select for the partial-staging line gutter. Handled at the container level with the
    // pointer captured on press, so it survives leaving/re-entering individual line rows and the
    // ScrollViewer can't steal the gesture. On press we decide whether the drag paints selection
    // on or off (from the first line's state); every change line the pointer passes over follows.
    private bool _isDraggingSelection;
    private bool _dragSelectValue;

    private void OnUnifiedPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Partial staging is unavailable in ignore-whitespace mode — don't start a selection.
        if (DataContext is DiffViewerViewModel vm && !vm.PartialStagingAvailable) return;

        var line = LineAt(e);
        if (line == null || !line.IsChange) return;

        _isDraggingSelection = true;
        _dragSelectValue = !line.IsSelected; // click toggles; drag paints this value
        line.IsSelected = _dragSelectValue;
        e.Pointer.Capture(sender as IInputElement);
    }

    private void OnUnifiedPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSelection) return;
        var line = LineAt(e);
        if (line != null && line.IsChange) line.IsSelected = _dragSelectValue;
    }

    private void OnUnifiedPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDraggingSelection = false;
        e.Pointer.Capture(null);
    }

    // Walks up from the visual under the pointer to the line row it belongs to.
    private DiffLineRowViewModel? LineAt(PointerEventArgs e)
    {
        Control? ctrl = this.InputHitTest(e.GetPosition(this)) as Control;
        while (ctrl != null)
        {
            if (ctrl.DataContext is DiffLineRowViewModel line) return line;
            ctrl = ctrl.Parent as Control;
        }
        return null;
    }

    // Split-view per-line drag-select (#74). Same threshold-free press/drag/release shape as the
    // unified view's, but resolves the side-by-side row + which column (old/new) was pressed, then
    // toggles that side's underlying DiffLineRowViewModel.IsSelected -- the exact same property the
    // unified view and BuildSelectedLinesPatch already use, so no new staging logic is needed.
    private bool _isDraggingSbsSelection;
    private bool _sbsDragSelectValue;

    private void OnSideBySidePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is DiffViewerViewModel vm && !vm.PartialStagingAvailable) return;

        var line = SideLineAt(e);
        if (line == null || !line.IsChange) return;

        _isDraggingSbsSelection = true;
        _sbsDragSelectValue = !line.IsSelected;
        line.IsSelected = _sbsDragSelectValue;
        e.Pointer.Capture(sender as IInputElement);
    }

    private void OnSideBySidePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSbsSelection) return;
        var line = SideLineAt(e);
        if (line != null && line.IsChange) line.IsSelected = _sbsDragSelectValue;
    }

    private void OnSideBySidePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDraggingSbsSelection = false;
        e.Pointer.Capture(null);
    }

    // Walks up to the Border carrying Grid.Column (0=old/left, 1=new/right) under a SideBySideLineRowViewModel
    // row, then returns that side's line (or null for a filler/context slot with no counterpart).
    private DiffLineRowViewModel? SideLineAt(PointerEventArgs e)
    {
        Control? ctrl = this.InputHitTest(e.GetPosition(this)) as Control;
        while (ctrl != null)
        {
            if (ctrl is Border && ctrl.DataContext is SideBySideLineRowViewModel row)
            {
                return Grid.GetColumn(ctrl) == 0 ? row.LeftLineRow : row.RightLineRow;
            }
            ctrl = ctrl.Parent as Control;
        }
        return null;
    }

    public DiffViewerView()
    {
        InitializeComponent();

        // The TextMate colour theme must follow the app theme: Daylight Loom (light) with DarkPlus
        // grammar colours is unreadable — never assume dark. Resolved from the actual theme variant
        // and re-applied on every live theme switch.
        _registryOptions = new RegistryOptions(CurrentTextMateTheme());
        _textMateInstallation = Editor.InstallTextMate(_registryOptions);

        _diffRenderer = new DiffMarginRenderer();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diffRenderer);

        DataContextChanged += (s, e) =>
        {
            if (DataContext is DiffViewerViewModel vm)
            {
                ApplyDocument(vm.RawContent ?? string.Empty);

                UpdateTextMate(vm.FilePath);
                _diffRenderer.ViewModel = vm;

                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(DiffViewerViewModel.SyntaxHighlightDiffs))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateTextMate(vm.FilePath));
                    }
                    if (args.PropertyName == nameof(DiffViewerViewModel.RawContent))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            ApplyDocument(vm.RawContent ?? string.Empty);
                            UpdateTextMate(vm.FilePath);
                            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                        });
                    }
                };

                Editor.TextChanged += (sender, args) =>
                {
                    if (!_isUpdatingFromViewModel && Editor.Document != null)
                    {
                        vm.RawContent = Editor.Document.Text;
                    }
                };
            }
        };
    }

    // Swaps the editor's content by assigning a *fresh* TextDocument on the UI thread, rather than
    // mutating the live Editor.Document.Text in place. This is the fix for GitHub #82: when the
    // watched file is renamed/removed on disk, a RepositoryWatcher refresh re-selects and reloads
    // the diff, clearing/replacing RawContent. An in-place `Document.Text = …` deletes the live
    // DocumentLines while AvaloniaEdit's LineNumberMargin can still hold VisualLines that reference
    // them; a render running between the mutation and the re-layout dereferences a now-deleted line
    // (DocumentLine.LineNumber throws "Operation is not valid due to the current state of the
    // object") and crashes the render pipeline. Replacing the whole Document makes the TextView drop
    // its visual lines synchronously, so no stale line ever survives into a render. The swap is
    // marshalled onto the UI thread and is a no-op when the text is unchanged (so typing in the
    // editor — which round-trips RawContent — never destroys the caret/document mid-edit).
    private void ApplyDocument(string text)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyDocument(text));
            return;
        }

        if (Editor.Document != null && Editor.Document.Text == text) return;

        _isUpdatingFromViewModel = true;
        Editor.Document = new AvaloniaEdit.Document.TextDocument(text);
        _isUpdatingFromViewModel = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Mainguard.UI.Theming.ThemeManager.ThemeChanged += OnAppThemeChanged;
        OnAppThemeChanged(); // the theme may have switched while this view was detached
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Mainguard.UI.Theming.ThemeManager.ThemeChanged -= OnAppThemeChanged;
    }

    // Light app themes (Daylight Loom) get a light TextMate palette, dark themes a dark one.
    private static ThemeName CurrentTextMateTheme()
        => Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;

    private void OnAppThemeChanged()
    {
        _textMateInstallation.SetTheme(_registryOptions.LoadTheme(CurrentTextMateTheme()));
        _diffRenderer.InvalidateThemeCache();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void UpdateTextMate(string filePath)
    {
        // Syntax-highlight preference (T-13): when off, unset the grammar so the editor renders
        // plain text instead of applying a TextMate grammar.
        bool syntaxOn = DataContext is not DiffViewerViewModel vm || vm.SyntaxHighlightDiffs;
        if (!syntaxOn)
        {
            _textMateInstallation.SetGrammar(null);
            return;
        }

        // An unrecognized extension must CLEAR the grammar — leaving the previous file's grammar
        // in place mis-highlights every unknown file opened after a known one.
        string? scope = null;
        if (!string.IsNullOrEmpty(filePath))
        {
            var ext = System.IO.Path.GetExtension(filePath);
            var langId = _registryOptions.GetLanguageByExtension(ext)?.Id;
            if (langId != null) scope = _registryOptions.GetScopeByLanguageId(langId);
        }
        _textMateInstallation.SetGrammar(scope);
    }
}

public class DiffMarginRenderer : IBackgroundRenderer
{
    public DiffViewerViewModel? ViewModel { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    // Draw() runs on every background-layer render; resolving the two theme brushes through the
    // resource system each time is avoidable work on the hot path. Cached until the view reports
    // a theme switch via InvalidateThemeCache().
    private IBrush? _addedBrush;
    private IBrush? _modifiedBrush;

    public void InvalidateThemeCache()
    {
        _addedBrush = null;
        _modifiedBrush = null;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (ViewModel == null) return;

        var addedLines = ViewModel.AddedLines;
        var modifiedLines = ViewModel.ModifiedLines;

        if (addedLines.Count == 0 && modifiedLines.Count == 0) return;

        // Resolve from the active theme so gutter bars follow theme switches.
        var addedBrush = _addedBrush ??= ResolveThemeBrush("SuccessBrush", "#42B968");
        var modifiedBrush = _modifiedBrush ??= ResolveThemeBrush("AccentBrush", "#8487F0");

        textView.EnsureVisualLines();
        foreach (var visualLine in textView.VisualLines)
        {
            if (visualLine.FirstDocumentLine == null || visualLine.FirstDocumentLine.IsDeleted) continue;
            int lineNumber = visualLine.FirstDocumentLine.LineNumber;

            bool isAdded = addedLines.Contains(lineNumber);
            bool isModified = modifiedLines.Contains(lineNumber);

            if (isAdded || isModified)
            {
                var brush = isModified ? modifiedBrush : addedBrush;

                // Draw a 4px wide bar on the left side of the text document space
                var rect = new Rect(0, visualLine.VisualTop, 4, visualLine.Height);
                drawingContext.DrawRectangle(brush, null, rect);
            }
        }
    }

    private static IBrush ResolveThemeBrush(string key, string fallback)
    {
        var app = Application.Current;
        if (app != null
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.Parse(fallback));
    }
}
