using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// Reproduces the reported "arrow keys do not move the selection" defect with
/// the real view model and a ListBox wired exactly like SpotlightWindow:
/// grouped default view, TwoWay SelectedItem binding, SelectedIndex moves.
/// </summary>
public sealed class SpotlightSelectionReproTests
{
    [Fact]
    public void ArrowNavigation_MovesSelection_AndSurvivesDeferredPublish()
    {
        RunSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var apps = Enumerable.Range(0, 8).Select(i => new SpotlightSearchItem(
                    $"app:{i}", SpotlightResultKind.Application, $"App {i}", "Application",
                    $"shell:AppsFolder\\App{i}!App")
                { Score = 900 - i }).ToArray();
            var service = new SpotlightSearchService(new ISpotlightProvider[]
            {
                new InstantProvider(apps)
            });
            var usagePath = Path.Combine(Path.GetTempPath(), $"vnotch-repro-{Guid.NewGuid():N}.json");
            var viewModel = new SpotlightViewModel(service, new SpotlightUsageStore(usagePath, () => DateTime.UtcNow));

            var list = new ListBox { DataContext = viewModel };
            list.SetBinding(ListBox.ItemsSourceProperty, new Binding(nameof(viewModel.Results)));
            list.SetBinding(ListBox.SelectedItemProperty,
                new Binding(nameof(viewModel.SelectedResult)) { Mode = BindingMode.TwoWay });
            var view = CollectionViewSource.GetDefaultView(viewModel.Results);
            view.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(SpotlightSearchItem.SectionTitle)));

            Task search = viewModel.SearchAsync("app");
            PumpUntil(() => viewModel.Results.Count > 0, "instant results never published");

            Assert.Equal("app:0", (list.SelectedItem as SpotlightSearchItem)?.Id);
            Assert.Equal(0, list.SelectedIndex);

            // Arrow Down — exactly what MoveSelection does.
            int count = viewModel.Results.Count;
            int next = (list.SelectedIndex + 1 + count) % count;
            list.SelectedIndex = next;

            Assert.Equal(1, list.SelectedIndex);
            Assert.Equal("app:1", viewModel.SelectedResult?.Id);

            // Let the deferred phase publish (identical merge) and verify the
            // user's selection is not snapped back to the first row.
            PumpUntil(() => search.IsCompleted, "search never completed");
            Assert.Equal(1, list.SelectedIndex);
            Assert.Equal("app:1", viewModel.SelectedResult?.Id);

            // A second arrow press keeps working after the merge publish.
            list.SelectedIndex = 2;
            Assert.Equal("app:2", viewModel.SelectedResult?.Id);
            File.Delete(usagePath);
        });
    }

    private static void PumpUntil(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(timeoutMessage);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (failure != null) throw failure;
    }

    private sealed class InstantProvider(IReadOnlyList<SpotlightSearchItem> items) : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public bool IsInstant => true;

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(
                items.Where(item => item.Score > 0).Take(limit).ToArray());
    }
}
