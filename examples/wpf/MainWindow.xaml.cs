using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Globalization;
using SlickLadder.Core;
using SlickLadder.Rendering.Simulation;
using SlickLadder.Rendering.ViewModels;

namespace SlickLadder.WPF.Demo;

/// <summary>
/// WPF Demo Application showcasing the SlickLadder control with shared SkiaSharp rendering.
/// Features: Market data simulation, performance metrics, interactive controls.
/// </summary>
public partial class MainWindow : Window
{
    private PriceLadderViewModel? _viewModel;
    private MarketDataSimulator? _simulator;
    private DispatcherTimer? _metricsTimer;

    public MainWindow()
    {
        System.Diagnostics.Debug.WriteLine("===== SLICKL ADDER WPF DEMO STARTING =====");
        InitializeComponent();
        InitializeDemo();
        System.Diagnostics.Debug.WriteLine("===== SLICKLADDER WPF DEMO INITIALIZED =====");
    }

    private void InitializeDemo()
    {
        // Create ViewModel and bind to control
        _viewModel = new PriceLadderViewModel();
        PriceLadder.DataContext = _viewModel;

        // Subscribe to trade events
        _viewModel.OnTrade += OnTradeExecuted;

        // Create market data simulator (pass tick size from ViewModel)
        var tickSize = 0.01m; // Default tick size
        _simulator = new MarketDataSimulator(_viewModel.Core, tickSize)
        {
            UpdatesPerSecond = 1000,
            BasePrice = 50000.00m
        };

        // Setup metrics update timer (10 Hz for UI updates)
        _metricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _metricsTimer.Tick += UpdateMetrics;
        _metricsTimer.Start();
    }

    private void OnTradeExecuted(TradeRequest trade)
    {
        var action = trade.Side == SlickLadder.Core.Models.Side.ASK ? "BUY" : "SELL";
        System.Diagnostics.Debug.WriteLine($"{action} @ ${trade.Price:F2}");
        MessageBox.Show($"{action} @ ${trade.Price:F2}", "Trade Clicked");
    }

    private void UpdateMetrics(object? sender, EventArgs e)
    {
        if (_viewModel == null) return;

        // Update performance metrics
        FpsText.Text = PriceLadder.GetMetrics().CurrentFps.ToString("F1");
        FrameTimeText.Text = PriceLadder.GetMetrics().AverageFrameTime.ToString("F2");

        // Update order book metrics
        BidCountText.Text = _viewModel.BidLevelCount.Value.ToString();
        AskCountText.Text = _viewModel.AskLevelCount.Value.ToString();
        SpreadText.Text = $"${_viewModel.Spread.Value:F2}";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _simulator?.Start();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _simulator?.Stop();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void UpdateRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_simulator == null || UpdateRateCombo.SelectedItem == null) return;

        var selectedItem = (ComboBoxItem)UpdateRateCombo.SelectedItem;
        var rate = int.Parse((string)selectedItem.Tag);
        _simulator.UpdatesPerSecond = rate;
    }

    private void ShowVolumeBarsCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (PriceLadder?.GetViewport() != null)
        {
            PriceLadder.GetViewport().ShowVolumeBars = ShowVolumeBarsCheckbox.IsChecked ?? true;
        }
    }

    private void ShowOrderCountCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (PriceLadder?.GetViewport() != null)
        {
            PriceLadder.GetViewport().ShowOrderCount = ShowOrderCountCheckbox.IsChecked ?? false;
        }
    }

    private void MboOrderSizeFilterText_Changed(object sender, TextChangedEventArgs e)
    {
        if (PriceLadder == null)
        {
            return;
        }

        var text = (sender as TextBox)?.Text?.Trim();
        long filterValue = 0;

        if (!string.IsNullOrEmpty(text))
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out filterValue))
            {
                return;
            }
        }

        PriceLadder.SetMboOrderSizeFilter(filterValue);
    }

    private void DataModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("DataModeCombo_SelectionChanged CALLED");

        if (_viewModel == null || DataModeCombo.SelectedItem == null || _simulator == null)
        {
            System.Diagnostics.Debug.WriteLine($"DataModeCombo_SelectionChanged: EARLY RETURN - _viewModel={_viewModel != null}, SelectedItem={DataModeCombo.SelectedItem != null}, _simulator={_simulator != null}");
            return;
        }

        var selectedItem = (ComboBoxItem)DataModeCombo.SelectedItem;
        var mode = (string)selectedItem.Tag;

        System.Diagnostics.Debug.WriteLine($"DataModeCombo_SelectionChanged: mode={mode}");

        // Stop market data if running
        bool wasRunning = _simulator.IsRunning;
        if (wasRunning)
        {
            System.Diagnostics.Debug.WriteLine("DataModeCombo_SelectionChanged: Stopping simulator");
            _simulator.Stop();
        }

        // Set data mode
        var dataMode = mode == "MBO" ? DataMode.MBO : DataMode.PriceLevel;
        System.Diagnostics.Debug.WriteLine($"DataModeCombo_SelectionChanged: Setting dataMode to {dataMode}");
        _viewModel.Core.SetDataMode(dataMode);

        // Set simulator mode
        System.Diagnostics.Debug.WriteLine($"DataModeCombo_SelectionChanged: Setting simulator.UseMBOMode to {mode == "MBO"}");
        _simulator.UseMBOMode = mode == "MBO";

        // Clear order book
        System.Diagnostics.Debug.WriteLine("DataModeCombo_SelectionChanged: Calling Reset()");
        _viewModel.Core.Reset();

        // Restart market data if it was running
        if (wasRunning)
        {
            System.Diagnostics.Debug.WriteLine("DataModeCombo_SelectionChanged: Restarting simulator");
            _simulator.Start();
        }

        System.Diagnostics.Debug.WriteLine("DataModeCombo_SelectionChanged: DONE");
    }

    private void RemovalModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || RemovalModeCombo.SelectedItem == null) return;

        var selectedItem = (ComboBoxItem)RemovalModeCombo.SelectedItem;
        var mode = (string)selectedItem.Tag;

        var isShowEmpty = mode == "ShowEmpty";

        // Update viewport rendering mode
        if (PriceLadder?.GetViewport() != null)
        {
            PriceLadder.GetViewport().RemovalMode = isShowEmpty
                ? SlickLadder.Rendering.Core.LevelRemovalMode.ShowEmpty
                : SlickLadder.Rendering.Core.LevelRemovalMode.RemoveRow;
        }

        // Configure snapshot generation to fill empty levels
        if (_viewModel?.Core != null)
        {
            // Access the batcher through reflection or add a public property
            var batcherField = _viewModel.Core.GetType().GetField("_batcher",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (batcherField != null)
            {
                var batcher = batcherField.GetValue(_viewModel.Core) as dynamic;
                if (batcher != null)
                {
                    batcher.FillEmptyLevels = isShowEmpty;
                }
            }
        }
    }

    private void TickSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TickSizeCombo?.SelectedItem == null || _viewModel == null) return;

        var selectedItem = (ComboBoxItem)TickSizeCombo.SelectedItem;
        var tickSizeStr = (string)selectedItem.Tag;
        var tickSize = decimal.Parse(tickSizeStr, System.Globalization.CultureInfo.InvariantCulture);

        // Preserve current data mode
        var currentMode = DataModeCombo?.SelectedItem != null
            ? (string)((ComboBoxItem)DataModeCombo.SelectedItem).Tag
            : "PriceLevel";
        var isMBOMode = currentMode == "MBO";

        // Stop market data if running
        bool wasRunning = _simulator?.IsRunning ?? false;
        if (wasRunning)
        {
            _simulator?.Stop();
        }

        // Dispose old simulator
        _simulator?.Dispose();

        // Recreate ViewModel with new tick size
        _viewModel = new PriceLadderViewModel(tickSize);
        PriceLadder.DataContext = _viewModel;

        // Subscribe to trade events on new ViewModel
        _viewModel.OnTrade += OnTradeExecuted;

        // Restore data mode
        _viewModel.Core.SetDataMode(isMBOMode ? DataMode.MBO : DataMode.PriceLevel);

        // Recreate simulator with new tick size and restored mode
        _simulator = new MarketDataSimulator(_viewModel.Core, tickSize)
        {
            UpdatesPerSecond = int.Parse((string)((ComboBoxItem)UpdateRateCombo.SelectedItem).Tag),
            BasePrice = 50000.00m,
            UseMBOMode = isMBOMode
        };

        // Update viewport tick size
        if (PriceLadder?.GetViewport() != null)
        {
            PriceLadder.GetViewport().TickSize = tickSize;

            // Re-center viewport on a price aligned to the new tick size
            var basePrice = 50000.00m;
            var alignedPrice = Math.Round(basePrice / tickSize) * tickSize;
            PriceLadder.GetViewport().CenterPrice = alignedPrice;
        }

        // Restart market data if it was running
        if (wasRunning)
        {
            _simulator?.Start();
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Cleanup
        _metricsTimer?.Stop();
        _simulator?.Stop();
        _simulator?.Dispose();
        base.OnClosed(e);
    }
}
