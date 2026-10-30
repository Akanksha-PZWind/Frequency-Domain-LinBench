LinBench.Wpf - Modes page + header (dataset details, Ctrl+K palette, Settings) like the original LinBench

Copy every file over the same path inside  LinBench.Wpf\LinBench.Wpf\  (same subfolders), then Rebuild.
Apply on top of the previous deliveries (UI + Campbell). The Backend folder is not touched.

CHANGED  Views\Panels\ModesPanel.xaml / .xaml.cs        Eigenvalues (s-plane) | Mode list with participation bars;
                                                        sidebar: aero/hydro states, max frequency; modes shown, least damped, unstable
CHANGED  Views\Dialogs\TurbineSpecsDialog.xaml / .cs    dataset details (click the case pill): tiles, notes, Turbine,
                                                        Operating range, Linear model, Input files
CHANGED  Views\Dialogs\SettingsDialog.xaml / .cs        Settings: Units, Appearance, Plots, Accessibility, Python backend,
                                                        Restore defaults; saved on this PC
CHANGED  MainWindow.xaml / .xaml.cs                     search bar opens the command palette (Ctrl+K: views, wind speeds,
                                                        actions; arrows / Enter / Esc); text size + view list from Settings;
                                                        status bar "N operating points · open loop"
CHANGED  Controls\LbChart.cs                            settings for all charts (legend auto/inside/beside/above/off, fold limit,
                                                        plot height, crosshair with axis values, colour sets, contrast),
                                                        per-point markers
CHANGED  ViewModels\ResultsViewModel.cs                 summary for the details window, status text, Modes "in this view"
CHANGED  Views\Panels\CampbellPanel.xaml.cs             legend now follows Settings > Fold legends above
