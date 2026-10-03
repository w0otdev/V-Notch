using System.Runtime.CompilerServices;
using System.Windows.Controls;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch.Tools;

// Run in its own process: forcing GC in a shared WPF test host can collect
// native tray-icon delegates still used by unrelated UI tests.
internal static class SystemMonitorLifetimeProbe
{
    [STAThread]
    private static int Main()
    {
        using var module = new SystemMonitorModule();
        var (livePresenter, liveControl) = CreateLivePresenter(module);
        Collect();
        if (!liveControl.IsAlive) throw new InvalidOperationException("The positive retention control failed.");
        livePresenter.Dispose();

        var references = new List<WeakReference>();
        for (int i = 0; i < 100; i++) references.AddRange(CreateDisposedPresenter(module));
        Collect();
        int retained = references.Count(reference => reference.IsAlive);
        Console.WriteLine($"Disposed presenters/visual trees: {references.Count} weak references; {retained} retained after full GC (100 lifetimes).");
        GC.KeepAlive(module);
        if (retained != 0) throw new InvalidOperationException("Disposed UI is still retained by the live module.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SystemMonitorPresenter Presenter, WeakReference Control) CreateLivePresenter(SystemMonitorModule module)
    {
        var refs = CreateRefs();
        return (new(module, new ImmediateDispatcher(), refs), new WeakReference(refs.CpuValueText));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateDisposedPresenter(SystemMonitorModule module)
    {
        var refs = CreateRefs();
        var root = new StackPanel();
        root.Children.Add(refs.CpuValueText);
        root.Children.Add(refs.CpuBar);
        root.Children.Add(refs.RamValueText);
        root.Children.Add(refs.RamBar);
        root.Children.Add(refs.NetDownText);
        root.Children.Add(refs.NetUpText);
        var presenter = new SystemMonitorPresenter(module, new ImmediateDispatcher(), refs);
        var references = new[] { new WeakReference(presenter), new WeakReference(root),
            new WeakReference(refs.CpuValueText), new WeakReference(refs.CpuBar) };
        presenter.Dispose();
        presenter.Dispose();
        return references;
    }

    private static SystemMonitorViewRefs CreateRefs() => new(new TextBlock(), new Border(),
        new TextBlock(), new Border(), new TextBlock(), new TextBlock());

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class ImmediateDispatcher : IDispatcherService
    {
        public bool CheckAccess() => true;
        public void BeginInvoke(Action action) => action();
        public void Invoke(Action action) => action();
    }
}
