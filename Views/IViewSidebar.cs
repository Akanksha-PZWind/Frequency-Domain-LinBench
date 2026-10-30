using System.Windows;

namespace LinBench.Wpf.Views
{
    /// <summary>
    /// A results page that puts its own controls into the right sidebar, like the original LinBench:
    /// ▾ VIEW SETTINGS (e.g. Campbell: Frequency, Max frequency …) and extra content under ▾ RESULTS
    /// (e.g. the symmetry legend). ResultsView hands these to ResultsViewModel when the page is shown.
    /// </summary>
    public interface IViewSidebar
    {
        FrameworkElement? ViewSettings { get; }
        FrameworkElement? ResultsExtra { get; }
    }
}
