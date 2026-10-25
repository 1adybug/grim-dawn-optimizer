using System.Runtime.InteropServices;
using System.Text.Json;
using GrimDawnOptimizer.Bridge.PathPicking;
using Xunit;

namespace GrimDawnOptimizer.Bridge.Tests;

public sealed class PathPickerDpiTests
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private static readonly IntPtr Unaware = new(-1), PerMonitorV2 = new(-4);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealComPickerUsesPerMonitorV2AndRestoresUnawareThreadAfterCancelOrFailure(bool fail)
    {
        var inspected = false;
        using var service = new PathPickerService(() => new ProbeDialog((request, token) =>
        {
            // Reproduce the shipped console sidecar's DPI-unaware default even
            // if the test host itself happens to be DPI-aware. No UI is shown.
            var original = SetThreadDpiAwarenessContext(Unaware);
            Assert.NotEqual(IntPtr.Zero, original);
            try
            {
                var before = GetThreadDpiAwarenessContext();
                AssertContext(Unaware, before);
                var picker = new WindowsPathPickerDialog((dialog, _) =>
                {
                    inspected = true;
                    AssertContext(PerMonitorV2, GetThreadDpiAwarenessContext());
                    dialog.GetOptions(out var options);
                    Assert.NotEqual(0u, options & 0x40u);
                    // Exercise real COM close/configuration without ever Show.
                    _ = dialog.Close(Cancelled);
                    if (fail) throw new InvalidOperationException("DPI cleanup fixture");
                    return Cancelled;
                });
                if (fail)
                    Assert.Equal("DPI cleanup fixture", Assert.Throws<InvalidOperationException>(() => picker.Show(request, token)).Message);
                else
                    Assert.Null(picker.Show(request, token));
                AssertContext(before, GetThreadDpiAwarenessContext());
                return null;
            }
            finally { Assert.NotEqual(IntPtr.Zero, SetThreadDpiAwarenessContext(original)); }
        }));
        Assert.Null((await service.PickAsync(JsonSerializer.SerializeToElement(new { kind = "gameDirectory" }))).Path);
        Assert.True(inspected);
    }

    [Fact]
    public async Task PreCancelledPickerNeverChangesThreadDpiContext()
    {
        using var service = new PathPickerService(() => new ProbeDialog((request, _) =>
        {
            var before = GetThreadDpiAwarenessContext();
            var picker = new WindowsPathPickerDialog((_, _) => throw new Exception("Show must not run"));
            Assert.Throws<OperationCanceledException>(() => picker.Show(request, new CancellationToken(true)));
            AssertContext(before, GetThreadDpiAwarenessContext());
            return null;
        }));
        Assert.Null((await service.PickAsync(JsonSerializer.SerializeToElement(new { kind = "saveDirectory" }))).Path);
    }

    private static void AssertContext(IntPtr expected, IntPtr actual)
        => Assert.True(AreDpiAwarenessContextsEqual(expected, actual), "Unexpected thread DPI awareness context.");

    private sealed class ProbeDialog(Func<PathPickerRequest, CancellationToken, string?> show) : IPathPickerDialog
    {
        public string? Show(PathPickerRequest request, CancellationToken cancellationToken) => show(request, cancellationToken);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
