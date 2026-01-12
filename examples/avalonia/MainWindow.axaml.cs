using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SlickLadder.Core;
using SlickLadder.Rendering.Core;
using SlickLadder.Rendering.Simulation;
using SlickLadder.Rendering.ViewModels;

namespace SlickLadder.Avalonia.Demo;

/// <summary>
/// Avalonia Demo Application showcasing the SlickLadder control with shared SkiaSharp rendering.
/// Features: Market data simulation, performance metrics, interactive controls.
/// </summary>
public partial class MainWindow : Window
{
    private PriceLadderViewModel? _viewModel;
    private MarketDataSimulator? _simulator;
    private DispatcherTimer? _metricsTimer;

    // Control references
    private Button? _startButton;
    private Button? _stopButton;
    private ComboBox? _updateRateCombo;
    private CheckBox? _showVolumeBarsCheckbox;
    private CheckBox? _showOrderCountCheckbox;
    private ComboBox? _dataModeCombo;
    private ComboBox? _removalModeCombo;
    private ComboBox? _tickSizeCombo;

    public MainWindow()
    {
        InitializeComponent();
        InitializeControlReferences();
        InitializeDemo();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void InitializeControlReferences()
    {
        // Get references to named controls
        _startButton = this.FindControl<Button>("StartButton");
        _stopButton = this.FindControl<Button>("StopButton");
        _updateRateCombo = this.FindControl<ComboBox>("UpdateRateCombo");
        _showVolumeBarsCheckbox = this.FindControl<CheckBox>("ShowVolumeBarsCheckbox");
        _showOrderCountCheckbox = this.FindControl<CheckBox>("ShowOrderCountCheckbox");
        _dataModeCombo = this.FindControl<ComboBox>("DataModeCombo");
        _removalModeCombo = this.FindControl<ComboBox>("RemovalModeCombo");
        _tickSizeCombo = this.FindControl<ComboBox>("TickSizeCombo");

        // Wire up checkbox changed events
        if (_showVolumeBarsCheckbox != null)
        {
            _showVolumeBarsCheckbox.IsCheckedChanged += ShowVolumeBarsCheckbox_Changed;
        }
        if (_showOrderCountCheckbox != null)
        {
            _showOrderCountCheckbox.IsCheckedChanged += ShowOrderCountCheckbox_Changed;
        }
    }

    private void InitializeDemo()
    {
        var priceLadder = this.FindControl<global::SlickLadder.Avalonia.Controls.PriceLadderControl>("PriceLadder");
        if (priceLadder == null) return;

        // Create ViewModel and bind to control
        _viewModel = new PriceLadderViewModel();
        priceLadder.DataContext = _viewModel;

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

    private void UpdateMetrics(object? sender, EventArgs e)
    {
        if (_viewModel == null) return;

        var priceLadder = this.FindControl<global::SlickLadder.Avalonia.Controls.PriceLadderControl>("PriceLadder");
        if (priceLadder == null) return;

        // Update performance metrics using named TextBlocks
        var fpsText = this.FindControl<TextBlock>("FpsText");
        var frameTimeText = this.FindControl<TextBlock>("FrameTimeText");
        var bidCountText = this.FindControl<TextBlock>("BidCountText");
        var askCountText = this.FindControl<TextBlock>("AskCountText");
        var spreadText = this.FindControl<TextBlock>("SpreadText");

        if (fpsText != null)
            fpsText.Text = priceLadder.GetMetrics().CurrentFps.ToString("F1");
        if (frameTimeText != null)
            frameTimeText.Text = priceLadder.GetMetrics().AverageFrameTime.ToString("F2");
        if (bidCountText != null)
            bidCountText.Text = _viewModel.BidLevelCount.Value.ToString();
        if (askCountText != null)
            askCountText.Text = _viewModel.AskLevelCount.Value.ToString();
        if (spreadText != null)
            spreadText.Text = $"${_viewModel.Spread.Value:F2}";
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        _simulator?.Start();
        if (_startButton != null) _startButton.IsEnabled = false;
        if (_stopButton != null) _stopButton.IsEnabled = true;
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _simulator?.Stop();
        if (_startButton != null) _startButton.IsEnabled = true;
        if (_stopButton != null) _stopButton.IsEnabled = false;
    }

    private void UpdateRateCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_simulator == null || _updateRateCombo?.SelectedItem == null) return;

        var selectedItem = (ComboBoxItem)_updateRateCombo.SelectedItem;
        if (selectedItem.Tag is string tag)
        {
            var rate = int.Parse(tag);
            _simulator.UpdatesPerSecond = rate;
        }
    }

    private void ShowVolumeBarsCheckbox_Changed(object? sender, RoutedEventArgs e)
    {
        var priceLadder = this.FindControl<global::SlickLadder.Avalonia.Controls.PriceLadderControl>("PriceLadder");
        if (priceLadder?.GetViewport() != null && _showVolumeBarsCheckbox != null)
        {
            priceLadder.GetViewport().ShowVolumeBars = _showVolumeBarsCheckbox.IsChecked ?? true;
        }
    }

    private void ShowOrderCountCheckbox_Changed(object? sender, RoutedEventArgs e)
    {
        var priceLadder = this.FindControl<global::SlickLadder.Avalonia.Controls.PriceLadderControl>("PriceLadder");
        if (priceLadder?.GetViewport() != null && _showOrderCountCheckbox != null)
        {
            priceLadder.GetViewport().ShowOrderCount = _showOrderCountCheckbox.IsChecked ?? false;
        }
    }

    private void DataModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || _dataModeCombo?.SelectedItem == null || _simulator == null) return;

        var selectedItem = (ComboBoxItem)_dataModeCombo.SelectedItem;
        if (selectedItem.Tag is not string mode) return;

        bool wasRunning = _simulator.IsRunning;
        if (wasRunning)
        {
            _simulator.Stop();
        }

        var dataMode = mode == "MBO" ? DataMode.MBO : DataMode.PriceLevel;
        _viewModel.Core.SetDataMode(dataMode);
        _simulator.UseMBOMode = mode == "MBO";

        _viewModel.Core.Reset();

        if (wasRunning)
        {
            _simulator.Start();
            if (_startButton != null) _startButton.IsEnabled = false;
            if (_stopButton != null) _stopButton.IsEnabled = true;
        }
    }

    private void RemovalModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || _removalModeCombo?.SelectedItem == null) return;

        var selectedItem = (ComboBoxItem)_removalModeCombo.SelectedItem;
        if (selectedItem.Tag is string mode)
        {
            var isShowEmpty = mode == "ShowEmpty";

            var priceLadder = this.FindControl<global::SlickLadder.Avalonia.Controls.PriceLadderControl>("PriceLadder");

            // Update viewport rendering mode
            if (priceLadder?.GetViewport() != null)
            {
                priceLadder.GetViewport().RemovalMode = isShowEmpty
                    ? LevelRemovalMode.ShowEmpty
                    : LevelRemovalMode.RemoveRow;
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
    }

    private void TickSizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tickSizeCombo?.SelectedItem == null || _viewModel == null) return;

        var selectedItem = (ComboBoxItem)_tickSizeCombo.SelectedItem;
        if (selectedItem.Tag is string tickSizeStr)
        {
            var tickSize = decimal.Parse(tickSizeStr, CultureInfo.InvariantCulture);

            var currentMode = "PriceLevel";
            if (_dataModeCombo?.SelectedItem is ComboBoxItem modeItem && modeItem.Tag is string modeTag)
            {
                currentMode = modeTag;
            }
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
            var priceLadder = this.FindControl<global::SlickLadder.Avalonia.Controls.PriceLadderControl>("PriceLadder");
            if (priceLadder != null)
            {
                priceLadder.DataContext = _viewModel;
            }

            _viewModel.Core.SetDataMode(isMBOMode ? DataMode.MBO : DataMode.PriceLevel);

            // Recreate simulator with new tick size
            var updateRate = 1000;
            if (_updateRateCombo?.SelectedItem is ComboBoxItem rateItem && rateItem.Tag is string rateTag)
            {
                updateRate = int.Parse(rateTag);
            }

            _simulator = new MarketDataSimulator(_viewModel.Core, tickSize)
            {
                UpdatesPerSecond = updateRate,
                BasePrice = 50000.00m,
                UseMBOMode = isMBOMode
            };

            // Update viewport tick size
            if (priceLadder?.GetViewport() != null)
            {
                priceLadder.GetViewport().TickSize = tickSize;

                // Re-center viewport on a price aligned to the new tick size
                var basePrice = 50000.00m;
                var alignedPrice = Math.Round(basePrice / tickSize) * tickSize;
                priceLadder.GetViewport().CenterPrice = alignedPrice;
            }

            // Restart market data if it was running
            if (wasRunning)
            {
                _simulator?.Start();
                if (_startButton != null) _startButton.IsEnabled = false;
                if (_stopButton != null) _stopButton.IsEnabled = true;
            }
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
