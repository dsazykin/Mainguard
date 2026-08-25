using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.Views;

/// <summary>
/// The repository picker (the workspace tree, moved out of MainWindow's docked sidebar —
/// revised design 2026-07-11). Shares MainWindowViewModel as DataContext; all tree gestures
/// (select, drag-to-categorize, rename, delete keys) transplanted from MainWindow intact.
/// Opening a repository closes the picker (wired VM-side via RepositoryOpened).
/// </summary>
public partial class RepoPickerWindow : Window
{
    private Point _dragStartPoint;
    private bool _isDragging;

    public RepoPickerWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, Category_Drop);
        KeyUp += Window_KeyUp;
    }

    private void Repo_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        if (DataContext is MainWindowViewModel vm && sender is Control control && control.DataContext != null)
            vm.SelectedNode = control.DataContext;
    }

    private async void Repo_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_isDragging)
        {
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _dragStartPoint.X) > 3 || Math.Abs(point.Y - _dragStartPoint.Y) > 3)
            {
                _isDragging = true;
                if (sender is Control control && control.DataContext is Repository repo)
                {
                    var data = new DataObject();
                    data.Set("Repository", repo);
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
                }
                _isDragging = false;
            }
        }
    }

    // Double-click a repo to open it; the picker closes so the workspace has the stage.
    private void Repo_DoubleTapped(object? sender, TappedEventArgs e) => ActivateRepo(sender);

    // The same open, reached without a mouse: RepoRow raises Activated for Enter/Space and for
    // UI Automation's InvokePattern (W2 — the rows used to expose no activation path at all).
    private void Repo_Activated(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ActivateRepo(sender);

    private void ActivateRepo(object? sender)
    {
        if (sender is Control { DataContext: Repository repo } && DataContext is MainWindowViewModel vm)
        {
            // The pointer path already selected on press; doing it here too makes the keyboard and
            // automation paths land in the same state rather than opening an unselected row.
            vm.SelectedNode = repo;
            vm.OpenRepository(repo);
            Close();
        }
    }

    private void Category_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        if (DataContext is MainWindowViewModel vm && sender is Control control && control.DataContext != null)
            vm.SelectedNode = control.DataContext;
    }

    private void Category_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control control && control.DataContext != null)
            vm.SelectedNode = control.DataContext;
    }

    private async void Category_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_isDragging)
        {
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - _dragStartPoint.X) > 3 || Math.Abs(point.Y - _dragStartPoint.Y) > 3)
            {
                _isDragging = true;
                if (sender is Control control && control.DataContext is WorkspaceCategory cat)
                {
                    var data = new DataObject();
                    data.Set("Category", cat);
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
                }
                _isDragging = false;
            }
        }
    }

    private void Category_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.Source is not Control control) return;

        if (e.Data.Get("Repository") is Repository droppedRepo)
        {
            if (control.DataContext is WorkspaceCategory targetCategory)
            {
                vm.MoveRepositoryToCategory(droppedRepo, targetCategory);
            }
            else if (control.DataContext is Repository targetRepo)
            {
                var targetCat = vm.Categories.FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId) ??
                                vm.Categories.SelectMany(c => c.SubCategories).FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId);
                if (targetCat != null)
                    vm.MoveRepositoryToCategory(droppedRepo, targetCat);
            }
        }
        else if (e.Data.Get("Category") is WorkspaceCategory droppedCategory)
        {
            if (control.DataContext is WorkspaceCategory targetCategory)
            {
                vm.MoveCategoryToCategory(droppedCategory, targetCategory);
            }
            else if (control.DataContext is Repository targetRepo)
            {
                var targetCat = vm.Categories.FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId) ??
                                vm.Categories.SelectMany(c => c.SubCategories).FirstOrDefault(c => c.CategoryId == targetRepo.CategoryId);
                if (targetCat != null)
                    vm.MoveCategoryToCategory(droppedCategory, targetCat);
            }
            else if (control.DataContext is MainWindowViewModel || control is ScrollViewer || control is ItemsControl)
            {
                // Dropped on the background, un-nest it
                vm.MoveCategoryToCategory(droppedCategory, null);
            }
        }
    }

    public void CategoryNameTextBox_KeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox { DataContext: WorkspaceCategory cat } && DataContext is MainWindowViewModel vm)
        {
            if (e.Key == Key.Enter)
                vm.SaveCategoryNameCommand.Execute(cat);
            else if (e.Key == Key.Escape)
                vm.CancelCategoryNameCommand.Execute(cat);
        }
    }

    public void CategoryNameTextBox_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void SidebarBackground_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            e.Source is Control control && control.DataContext is not WorkspaceCategory && control.DataContext is not Repository)
        {
            vm.SelectedNode = null;
        }
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (vm.IsDeleteConfirmationVisible)
        {
            if (e.Key == Key.Enter) { vm.ConfirmDeleteCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { vm.CancelDeleteCommand.Execute(null); e.Handled = true; }
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Delete)
        {
            if (vm.SelectedNode is Repository repo) vm.RemoveRepositoryCommand.Execute(repo);
            else if (vm.SelectedNode is WorkspaceCategory cat) vm.DeleteCategoryCommand.Execute(cat);
        }
        else if (e.Key == Key.F2 && vm.SelectedNode is WorkspaceCategory renameCat)
        {
            vm.RenameCategoryCommand.Execute(renameCat);
        }
    }
}
