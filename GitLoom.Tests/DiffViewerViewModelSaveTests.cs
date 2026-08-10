using System;
using System.IO;
using GitLoom.App.ViewModels;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// <see cref="DiffViewerViewModel.SaveFileCommand"/>'s failure contract. Edit mode auto-enables
/// whenever the file carries conflict markers, so the editor buffer this command writes is
/// frequently a merge resolution — and it may be the only copy of it. The write used to sit in a
/// bare <c>catch { }</c> on a ViewModel with no error property at all, so a failed write left the
/// resolution unsaved, the markers on disk, and the UI showing nothing wrong.
/// </summary>
public class DiffViewerViewModelSaveTests : IDisposable
{
    private readonly string _repo;

    public DiffViewerViewModelSaveTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "gitloom-diffsave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_repo, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_repo, true);
        }
        catch { }
    }

    private DiffViewerViewModel NewVm() => new(new FakeGitService(), _repo);

    [Fact]
    public void SaveFile_WhenWriteSucceeds_ShouldPersist_AndReportNoError()
    {
        File.WriteAllText(Path.Combine(_repo, "resolved.txt"), "old\n");

        var vm = NewVm();
        vm.FilePath = "resolved.txt";
        vm.RawContent = "the resolved content\n";
        vm.SaveFileCommand.Execute(null);

        Assert.True(vm.LastSaveSucceeded);
        Assert.Null(vm.SaveError);
        Assert.False(vm.HasSaveError);
        Assert.Equal("the resolved content\n", File.ReadAllText(Path.Combine(_repo, "resolved.txt")));
    }

    /// <summary>
    /// The regression: a read-only working file (AV lock / EPERM / read-only checkout all land
    /// here). Before the fix this reported nothing and the resolution was gone.
    /// </summary>
    [Fact]
    public void SaveFile_WhenWriteFails_ShouldSurfaceTheError_AndNotClaimSuccess()
    {
        var path = Path.Combine(_repo, "conflicted.txt");
        var onDisk = "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> other\n";
        File.WriteAllText(path, onDisk);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        // A run with permission to write read-only files would make this vacuous.
        Assert.True(IsUnwritable(path), "target should be read-only for this test to mean anything");

        var vm = NewVm();
        vm.FilePath = "conflicted.txt";
        vm.RawContent = "the resolution the user typed\n";
        vm.SaveFileCommand.Execute(null);

        Assert.False(vm.LastSaveSucceeded);
        Assert.True(vm.HasSaveError);
        Assert.NotNull(vm.SaveError);
        Assert.Contains("conflicted.txt", vm.SaveError);
        // And the conflict really is still on disk — the user must not be told otherwise.
        Assert.Equal(onDisk, File.ReadAllText(path));
    }

    [Fact]
    public void SaveFile_WhenNoFileIsOpen_ShouldSurfaceTheError()
    {
        var vm = NewVm();
        vm.RawContent = "orphan content";

        vm.SaveFileCommand.Execute(null);

        Assert.False(vm.LastSaveSucceeded);
        Assert.True(vm.HasSaveError);
    }

    [Fact]
    public void SaveFile_AfterAFailureThenASuccess_ShouldClearTheError()
    {
        var path = Path.Combine(_repo, "flaky.txt");
        File.WriteAllText(path, "start\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var vm = NewVm();
        vm.FilePath = "flaky.txt";
        vm.RawContent = "attempt\n";
        vm.SaveFileCommand.Execute(null);
        Assert.True(vm.HasSaveError);

        File.SetAttributes(path, FileAttributes.Normal);
        vm.SaveFileCommand.Execute(null);

        Assert.True(vm.LastSaveSucceeded);
        Assert.False(vm.HasSaveError);
        Assert.Equal("attempt\n", File.ReadAllText(path));
    }

    private static bool IsUnwritable(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write);
            return false;
        }
        catch
        {
            return true;
        }
    }
}
