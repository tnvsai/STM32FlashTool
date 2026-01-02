using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using STM32Bootloader.Services;

namespace STM32Bootloader
{
    public partial class MainWindow : Window
    {
        private readonly BootloaderService _bootloader;
        private string? _selectedFilePath;
        private Thread? _monitorThread;
        private bool _monitorRunning = false;
        private AppSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            _bootloader = new BootloaderService();
            _bootloader.LogReceived += OnLogReceived;
            _bootloader.ProgressChanged += OnProgressChanged;
            
            // Load saved settings
            _settings = AppSettings.Load();
            LoadSettings();
            
            RefreshPorts();
            
            Closed += (s, e) => SaveSettings();
        }

        private void RefreshPorts()
        {
            PortComboBox.Items.Clear();
            var ports = _bootloader.GetAvailablePorts();
            foreach (var port in ports)
            {
                PortComboBox.Items.Add(port);
            }
            if (ports.Count > 0)
                PortComboBox.SelectedIndex = 0;
            
            AddLog($"Found {ports.Count} port(s)");
        }

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_bootloader.IsConnected)
            {
                _bootloader.Disconnect();
                AddLog("Disconnected");
                UpdateUI();
            }
            else
            {
                var port = PortComboBox.SelectedItem as string;
                if (string.IsNullOrEmpty(port))
                {
                    AddLog("Please select a port", Brushes.Red);
                    return;
                }

                // Get selected baud rate
                var baudItem = BaudComboBox.SelectedItem as ComboBoxItem;
                int baudRate = int.Parse(baudItem?.Content?.ToString() ?? "115200");

                if (_bootloader.Connect(port, baudRate))
                {
                    AddLog($"Connected to {port} @ {baudRate} baud", Brushes.Blue);
                    StartMonitor(); // Auto-start monitor
                    UpdateUI();
                }
                else
                {
                    AddLog("Connection failed", Brushes.Red);
                }
            }
        }

        private void UpdateUI()
        {
            var connected = _bootloader.IsConnected;
            
            ConnectBtn.Content = connected ? "Disconnect" : "Connect";
            StatusText.Text = connected ? "Connected" : "Disconnected";
            StatusText.Foreground = connected ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
            
            if (!connected)
            {
                StopMonitor();
            }
            
            PortComboBox.IsEnabled = !connected;
            BaudComboBox.IsEnabled = !connected;
            EraseBtn.IsEnabled = connected;
            FlashBtn.IsEnabled = connected && !string.IsNullOrEmpty(_selectedFilePath);
            JumpBtn.IsEnabled = connected;
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*",
                Title = "Select Firmware File"
            };

            if (dialog.ShowDialog() == true)
            {
                _selectedFilePath = dialog.FileName;
                FilePathText.Text = _selectedFilePath;
                AddLog($"Selected: {Path.GetFileName(_selectedFilePath)}", Brushes.Blue);
                UpdateUI();
            }
        }

        private async void EraseBtn_Click(object sender, RoutedEventArgs e)
        {
            EraseBtn.IsEnabled = false;
            
            // Pause monitor
            var wasMonitoring = _monitorRunning;
            if (wasMonitoring) StopMonitor();
            
            AddLog("Erasing...", Brushes.Blue);
            
            var success = await _bootloader.EraseAsync();
            if (success)
                AddLog("Erase complete", Brushes.Green);
            else
                AddLog("Erase failed", Brushes.Red);
            
            // Resume monitor
            if (wasMonitoring) StartMonitor();
            
            EraseBtn.IsEnabled = true;
        }

        private async void FlashBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedFilePath))
                return;

            EraseBtn.IsEnabled = false;
            FlashBtn.IsEnabled = false;
            JumpBtn.IsEnabled = false;
            
            // Pause monitor during flash
            var wasMonitoring = _monitorRunning;
            if (wasMonitoring) StopMonitor();
            
            var totalStart = DateTime.Now;

            try
            {
                // Erase
                AddLog("Step 1/3: Erasing...", Brushes.Blue);
                var eraseStart = DateTime.Now;
                if (!await _bootloader.EraseAsync())
                {
                    AddLog("Erase failed", Brushes.Red);
                    return;
                }
                var eraseTime = (DateTime.Now - eraseStart).TotalSeconds;
                AddLog($"Erase complete ({eraseTime:F2}s)", Brushes.Green);

                // Write
                AddLog("Step 2/3: Writing firmware...", Brushes.Blue);
                var data = await File.ReadAllBytesAsync(_selectedFilePath);
                _writeStartTime = DateTime.Now; // Initialize for progress bar
                var writeStart = DateTime.Now; // Keep for total time calculation below
                
                if (!await _bootloader.WriteAsync(data))
                {
                    AddLog("Write failed", Brushes.Red);
                    return;
                }
                
                var writeTime = (DateTime.Now - writeStart).TotalSeconds;
                var bytesPerSec = (int)(data.Length / writeTime);
                AddLog($"Write complete ({writeTime:F2}s, {bytesPerSec} bytes/sec)", Brushes.Green);

                // Jump
                AddLog("Step 3/3: Jump to app...", Brushes.Blue);
                await Task.Delay(500);
                _bootloader.Jump();
                
                var totalTime = (DateTime.Now - totalStart).TotalSeconds;
                AddLog($"✓ Flashing complete! Total time: {totalTime:F2}s", Brushes.Green);
            }
            finally
            {
                // Resume monitor
                if (wasMonitoring) 
                {
                    await Task.Delay(1000); // Wait for device to reset
                    StartMonitor();
                }
                
                EraseBtn.IsEnabled = true;
                FlashBtn.IsEnabled = true;
                JumpBtn.IsEnabled = true;
            }
        }

        private void JumpBtn_Click(object sender, RoutedEventArgs e)
        {
            _bootloader.Jump();
            AddLog("Jump command sent", Brushes.Blue);
        }

        private DateTime _writeStartTime;

        private void OnProgressChanged(object? sender, (int written, int total) e)
        {
            Dispatcher.Invoke(() =>
            {
                var percent = (int)((e.written / (double)e.total) * 100);
                FlashProgressBar.Value = percent;
                
                // Real-time Address Calculation (Base 0x08008000)
                uint baseAddr = 0x08008000;
                uint currentAddr = baseAddr + (uint)e.written;
                
                DetailsText.Text = $"Writing: 0x{currentAddr:X8}  •  {e.written / 1024.0:F1} kB / {e.total / 1024.0:F1} kB";

                // Calculate times
                var elapsed = DateTime.Now - _writeStartTime;
                double speed = e.written / elapsed.TotalSeconds; // bytes per second
                double remainingSeconds = speed > 0 ? (e.total - e.written) / speed : 0;
                
                string timeInfo = $"Elapsed: {elapsed.TotalSeconds:F1}s";
                if (speed > 0 && e.written > 1024) 
                {
                   timeInfo += $" / Est: {remainingSeconds:F1}s"; 
                }
                
                ProgressText.Text = $"{percent}% ({timeInfo})";
            });
        }

        private void StartMonitor()
        {
            if (_monitorRunning) return;
            
            _monitorRunning = true;
            AddLog("Monitor started");
            
            _monitorThread = new Thread(() =>
            {
                while (_monitorRunning && _bootloader.IsConnected)
                {
                    try
                    {
                        var data = _bootloader.ReadAvailableData();
                        if (!string.IsNullOrEmpty(data))
                        {
                            Dispatcher.Invoke(() => AddToMonitor(data));
                        }
                        Thread.Sleep(10);
                    }
                    catch
                    {
                        break;
                    }
                }
            })
            {
                IsBackground = true
            };
            _monitorThread.Start();
        }

        private void StopMonitor()
        {
            if (!_monitorRunning) return;
            
            _monitorRunning = false;
            _monitorThread?.Join(1000);
            AddLog("Monitor stopped");
        }

        private void OnLogReceived(object? sender, string message)
        {
            Dispatcher.Invoke(() => AddToMonitor($"[LOG] {message}\n"));
        }

        private void AddToMonitor(string message)
        {
            bool autoScroll = MonitorAutoScroll.IsChecked == true;
            double savedOffset = MonitorScroll.VerticalOffset;

            MonitorText.AppendText(message); // More efficient than +=

            if (autoScroll)
            {
                MonitorScroll.ScrollToEnd();
            }
            else
            {
                MonitorScroll.ScrollToVerticalOffset(savedOffset);
            }
        }

        private void ClearMonitor_Click(object sender, RoutedEventArgs e)
        {
            MonitorText.Clear();
        }

        private void CopyMonitor_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(MonitorText.Text))
            {
                Clipboard.SetDataObject(MonitorText.Text);
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogRichText.Document.Blocks.Clear();
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            LogRichText.SelectAll();
            LogRichText.Copy();
            LogRichText.Selection.Select(LogRichText.Document.ContentEnd, LogRichText.Document.ContentEnd); // Deselect
        }



        private void AddLog(string message, Brush? color = null)
        {
            Dispatcher.Invoke(() =>
            {
                color ??= Brushes.Black; // Default color
                
                var time = DateTime.Now.ToString("HH:mm:ss");
                var run = new Run($"[{time}] {message}") { Foreground = color };
                var paragraph = new Paragraph(run) { Margin = new Thickness(0) }; // Compact spacing

                LogRichText.Document.Blocks.Add(paragraph);
                
                if (LogAutoScroll.IsChecked == true)
                {
                    LogRichText.ScrollToEnd();
                }
            });
        }

        // Drag & Drop Handlers
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                e.Effects = files.Length == 1 && files[0].EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length == 1 && files[0].EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedFilePath = files[0];
                    FilePathText.Text = _selectedFilePath;
                    AddLog($"Dropped: {Path.GetFileName(_selectedFilePath)}", Brushes.Blue);
                    UpdateUI();
                }
            }
        }

        // Settings Methods
        private void LoadSettings()
        {
            // Load last file path
            if (!string.IsNullOrEmpty(_settings.LastFilePath) && File.Exists(_settings.LastFilePath))
            {
                _selectedFilePath = _settings.LastFilePath;
                FilePathText.Text = _selectedFilePath;
            }
        }

        private void SaveSettings()
        {
            _settings.LastPort = PortComboBox.SelectedItem as string;
            _settings.LastBaudRate = int.TryParse((BaudComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int baud) ? baud : 115200;
            _settings.LastFilePath = _selectedFilePath;
            _settings.Save();
        }
    }
}
