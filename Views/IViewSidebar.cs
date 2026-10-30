using System.Windows;

namespace LinBench.Wpf.Views
{
    /// <summary>
    /// NEW: a results page that puts its own controls into the right sidebar, like the original LinBench:
    /// ▾ VIEW SETTINGS (e.g. Campbell: Loop, Frequency, Max frequency …) and extra content under ▾ RESULTS
    /// (e.g. the symmetry legend). ResultsView hands these to ResultsViewModel when the page is shown.
    /// </summary>
    public interface IViewSidebar
    {
        FrameworkElement? ViewSettings { get; }
        FrameworkElement? ResultsExtra { get; }
        /// <summary>NEW: ▾ INSIGHTS (e.g. Transfer function: G(s), verdict, tiles, poles / zeros). null = section hidden.</summary>
        FrameworkElement? Insights => null;
    }
}
