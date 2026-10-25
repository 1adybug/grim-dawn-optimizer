using System.Runtime.InteropServices;
using System.Text.Json;
using GrimDawnOptimizer.Bridge.PathPicking;
using Xunit;

namespace GrimDawnOptimizer.Bridge.Tests;

public sealed class PathPickerTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"launch\"}")]
    [InlineData("{\"kind\":\"gameDirectory\",\"defaultPath\":42}")]
    [InlineData("{\"kind\":\"gameDirectory\",\"defaultPath\":\"a\\u0000b\"}")]
    public async Task InvalidRequestsNeverConstructADialog(string json)
    {
        var created = 0;
        using var service = new PathPickerService(() => { created++; throw new Exception("Must not construct"); });
        using var document = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PickAsync(document.RootElement));
        Assert.Equal(0, created);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unfinished relative path")]
    public void OptionalOrUnfinishedDefaultIsOnlyAHint(string? path)
    {
        var request = PathPickerRequest.Parse(Payload(new { kind = "saveDirectory", defaultPath = path }));
        Assert.Null(request.DefaultPath);
        Assert.True(request.IsFolder);
    }

    [Fact]
    public async Task SelectedPathReturnsOnDedicatedStaWithoutChangingTheFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "picker must not modify this");
            var callerThread = Environment.CurrentManagedThreadId;
            using var service = new PathPickerService(() => new FakeDialog((request, _) =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.NotEqual(callerThread, Environment.CurrentManagedThreadId);
                Assert.False(request.IsFolder);
                Assert.Equal(file, request.DefaultPath);
                return file;
            }));
            var result = await service.PickAsync(Payload(new { kind = "gameExecutable", defaultPath = file }));
            Assert.Equal(file, result.Path);
            Assert.Equal("picker must not modify this", File.ReadAllText(file));
            Assert.Equal("{\"path\":", JsonSerializer.Serialize(result, Program.JsonOptions)[..8]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task BusyRequestIsRejectedAndDisposeCancelsItsOwnDialogIgnoringLateSelection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        using var service = new PathPickerService(() => new FakeDialog((_, token) =>
        {
            entered.SetResult();
            token.WaitHandle.WaitOne();
            cancelled = token.IsCancellationRequested;
            return "late path must be discarded";
        }));
        var pending = service.PickAsync(Payload(new { kind = "gameDirectory" }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PickAsync(Payload(new { kind = "saveDirectory" })));
        service.Dispose();
        Assert.Null((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Path);
        Assert.True(cancelled);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.PickAsync(Payload(new { kind = "gameDirectory" })));
    }

    [Fact]
    public async Task DeadlineCancelsAndReturnsNormalEmptySelection()
    {
        using var service = new PathPickerService(() => new FakeDialog((_, token) =>
        {
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return "unreachable";
        }), TimeSpan.FromMilliseconds(60));
        var result = await service.PickAsync(Payload(new { kind = "gameDirectory" })).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.Path);
    }

    [Fact]
    public async Task DialogFailureReleasesTheSingleActiveSlotAndUserCancelIsNormal()
    {
        var attempts = 0;
        using var service = new PathPickerService(() => new FakeDialog((_, _) =>
        {
            if (++attempts == 1) throw new InvalidOperationException("fake failure");
            return null;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PickAsync(Payload(new { kind = "gameDirectory" })));
        Assert.Null((await service.PickAsync(Payload(new { kind = "gameDirectory" }))).Path);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("gameDirectory")]
    [InlineData("saveDirectory")]
    [InlineData("gameExecutable")]
    public async Task RealComObjectCanConfigureAndCloseWithoutEverShowingUi(string kind)
    {
        var inspected = false;
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        using var service = new PathPickerService(() => new WindowsPathPickerDialog((dialog, _) =>
        {
            inspected = true;
            dialog.GetOptions(out var options);
            Assert.NotEqual(0u, options & 0x40u); // FOS_FORCEFILESYSTEM
            Assert.NotEqual(0u, options & 0x800u); // FOS_PATHMUSTEXIST
            Assert.NotEqual(0u, options & 0x8u); // FOS_NOCHANGEDIR
            Assert.Equal(0u, options & 0x200u); // no multiple selection
            Assert.Equal(kind != "gameExecutable", (options & 0x20u) != 0);
            if (kind == "gameExecutable")
            {
                dialog.GetFileTypeIndex(out var filter);
                Assert.Equal(1u, filter);
                dialog.GetFileName(out var name);
                try { Assert.Equal("Grim Dawn.exe", Marshal.PtrToStringUni(name)); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            dialog.GetFolder(out var shellItem);
            try
            {
                shellItem.GetDisplayName(0x80058000, out var name);
                try { Assert.Equal(folder, Marshal.PtrToStringUni(name), ignoreCase: true); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.FinalReleaseComObject(shellItem); }
            // Close before Show may return a failure HRESULT; PreserveSig must
            // expose it safely. Never call Show or access a game process here.
            _ = dialog.Close(unchecked((int)0x800704C7));
            return unchecked((int)0x800704C7);
        }));
        Assert.Null((await service.PickAsync(Payload(new { kind, defaultPath = folder }))).Path);
        Assert.True(inspected);
    }

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    private sealed class FakeDialog(Func<PathPickerRequest, CancellationToken, string?> show) : IPathPickerDialog
    {
        public string? Show(PathPickerRequest request, CancellationToken cancellationToken) => show(request, cancellationToken);
    }
}
