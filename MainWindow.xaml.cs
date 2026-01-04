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
using System.Linq;

namespace STM32Bootloader
{
    public partial class MainWindow : Window
    {
        private readonly BootloaderService _bootloader;
        private string? _selectedFilePath;
        private Thread? _monitorThread;
        private bool _monitorRunning = false;
        private Thread? _healthCheckThread;
        private bool _healthCheckRunning = false;
        private string? _connectedPort = null;
        private AppSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            _bootloader = new BootloaderService();
            _bootloader.LogReceived += OnLogReceived;
            _bootloader.ProgressChanged += OnProgressChanged;
            
            // Load saved settings
            _settings = AppSettings.Load();
            
            RefreshPorts();
            LoadSettings(); // Call LoadSettings AFTER RefreshPorts to restore selection
            
            Closed += (s, e) => SaveSettings();
            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Detect and apply system theme after window is fully loaded
            DetectAndApplySystemTheme();
        }

        // Theme Management
        private void DetectAndApplySystemTheme()
        {
            try
            {
                // Read Windows 10/11 theme preference
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var appsUseLightTheme = key.GetValue("AppsUseLightTheme");
                        if (appsUseLightTheme != null)
                        {
                            bool isDark = (int)appsUseLightTheme == 0;
                            ThemeToggle.IsChecked = isDark;
                            ApplyTheme(isDark);
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Fallback to light mode if detection fails
            }

            // Default to light mode
            ThemeToggle.IsChecked = false;
            ApplyTheme(false);
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(ThemeToggle.IsChecked == true);
        }

        private void ApplyTheme(bool isDark)
        {
            // Helper to set color in Window Resources
            void SetColor(string key, string hex) => this.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

            if (isDark)
            {
                SetColor("AppBackgroundBrush", "#121212");
                SetColor("CardBackgroundBrush", "#1E1E1E");
                SetColor("TextPrimaryBrush", "#FFFFFF");
                SetColor("TextSecondaryBrush", "#B0B0B0");
                SetColor("BorderBrush", "#333333");
                SetColor("HeaderBackgroundBrush", "#202020");
                SetColor("ControlBackgroundBrush", "#2D2D2D");
                SetColor("ControlBorderBrush", "#444444");
            }
            else
            {
                SetColor("AppBackgroundBrush", "#F3F3F3");
                SetColor("CardBackgroundBrush", "#FFFFFF");
                SetColor("TextPrimaryBrush", "#333333");
                SetColor("TextSecondaryBrush", "#666666");
                SetColor("BorderBrush", "#E0E0E0");
                SetColor("HeaderBackgroundBrush", "#005A9E");
                SetColor("ControlBackgroundBrush", "#FFFFFF");
                SetColor("ControlBorderBrush", "#D0D0D0");
            }

            // Apply DWM Dark Mode to Title Bar
            try 
            {
                var windowInteropHelper = new System.Windows.Interop.WindowInteropHelper(this);
                int useDarkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(windowInteropHelper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            }
            catch { /* Ignore on older OS */ }
        }

        // DWM API for Dark Title Bar
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private void RefreshPorts()
        {
            var currentSelection = PortComboBox.SelectedItem as string;
            
            PortComboBox.Items.Clear();
            var ports = _bootloader.GetAvailablePorts();
            foreach (var port in ports)
            {
                PortComboBox.Items.Add(port);
            }

            // Try to restore previous selection, or select first available
            if (!string.IsNullOrEmpty(currentSelection) && ports.Contains(currentSelection))
            {
                PortComboBox.SelectedItem = currentSelection;
            }
            else if (ports.Count > 0)
            {
                PortComboBox.SelectedIndex = 0;
            }
            
            AddLog($"Found {ports.Count} port(s)");
        }

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_bootloader.IsConnected)
            {
                _bootloader.Disconnect();
                StopHealthCheck();
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
                    _connectedPort = port;
                    AddLog($"Connected to {port} @ {baudRate} baud", Brushes.CornflowerBlue);
                    StartMonitor(); // Auto-start monitor
                    StartHealthCheck(); // Start health monitoring
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
            StatusText.Foreground = connected ? System.Windows.Media.Brushes.LightGreen : (Brush)FindResource("TextSecondaryBrush");
            // Change status dot color safely (if needed, access via XAML name if exposing property or logic)
            if (StatusDot != null) StatusDot.Fill = connected ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Silver;

            if (!connected)
            {
                StopMonitor();
                StopHealthCheck();
            }
            
            PortComboBox.IsEnabled = !connected;
            BaudComboBox.IsEnabled = !connected;
            EraseBtn.IsEnabled = connected;
            FlashBtn.IsEnabled = connected && !string.IsNullOrEmpty(_selectedFilePath);
            JumpComboBox.IsEnabled = connected;
            
            // Set default selection when connecting
            if (connected && JumpComboBox.SelectedIndex == -1)
            {
                JumpComboBox.SelectedIndex = 0; // Default to "Jump to Application"
            }
            
            ReadMemoryBtn.IsEnabled = connected;
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
                AddLog($"Selected: {Path.GetFileName(_selectedFilePath)}", Brushes.CornflowerBlue);
                UpdateUI();
            }
        }

        private async void EraseBtn_Click(object sender, RoutedEventArgs e)
        {
            EraseBtn.IsEnabled = false;
            
            // Pause monitor
            var wasMonitoring = _monitorRunning;
            if (wasMonitoring) StopMonitor();
            
            AddLog("Erasing...", Brushes.CornflowerBlue);
            
            var success = await _bootloader.EraseAsync();
            if (success)
                AddLog("Erase complete", Brushes.Green);
            else
            {
                AddLog("Erase failed", Brushes.Red);
                
                // Check if still connected - if not, update UI
                if (!_bootloader.IsConnected)
                {
                    AddLog("Device disconnected", Brushes.Red);
                    UpdateUI();
                }
            }
            
            // Resume monitor
            if (wasMonitoring) StartMonitor();
            
            EraseBtn.IsEnabled = _bootloader.IsConnected;
        }

        private async void FlashBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedFilePath))
                return;

            EraseBtn.IsEnabled = false;
            FlashBtn.IsEnabled = false;
            JumpComboBox.IsEnabled = false;
            
            // Pause monitor during flash
            var wasMonitoring = _monitorRunning;
            if (wasMonitoring) StopMonitor();
            
            var totalStart = DateTime.Now;

            try
            {
                // Erase
                AddLog("Step 1/3: Erasing...", Brushes.CornflowerBlue);
                var eraseStart = DateTime.Now;
                if (!await _bootloader.EraseAsync())
                {
                    AddLog("Erase failed", Brushes.Red);
                    return;
                }
                var eraseTime = (DateTime.Now - eraseStart).TotalSeconds;
                AddLog($"Erase complete ({eraseTime:F2}s)", Brushes.Green);

                // Write
                AddLog("Step 2/3: Writing firmware...", Brushes.CornflowerBlue);
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
                AddLog("Step 3/3: Jump to app...", Brushes.CornflowerBlue);
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
                JumpComboBox.IsEnabled = true;
            }
        }

        private void JumpComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (JumpComboBox.SelectedIndex == -1)
                return;

            var selectedItem = JumpComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
                return;

            string selection = selectedItem.Content.ToString();

            if (selection == "Jump to Application")
            {
                _bootloader.Jump();
                AddLog("Jump to Application command sent (0x55)", Brushes.CornflowerBlue);
            }
            else if (selection == "Jump to Bootloader")
            {
                _bootloader.JumpToBootloader();
                AddLog("Jump to Bootloader command sent (0x54)", Brushes.CornflowerBlue);
            }

            // Keep selection visible to show current state
        }

        private void ReadMemoryBtn_Click(object sender, RoutedEventArgs e)
        {
            // Open the popup window
            ReadMemoryPopup.IsOpen = true;
        }

        private void CloseReadMemoryPopup_Click(object sender, RoutedEventArgs e)
        {
            // Close the popup
            ReadMemoryPopup.IsOpen = false;
        }

        private async void ReadMemoryExecuteBtn_Click(object sender, RoutedEventArgs e)
        {
            // Parse address from hex string
            uint address;
            try
            {
                string addrText = ReadAddressBox.Text.Trim();
                if (addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    addrText = addrText.Substring(2);
                
                address = Convert.ToUInt32(addrText, 16);
            }
            catch
            {
                AddLog("Invalid address format. Use hex format (e.g., 0x08008000)", Brushes.Red);
                return;
            }

            // Get length from ComboBox
            if (ReadLengthBox.SelectedItem is not ComboBoxItem lengthItem)
                return;
            
            int length = int.Parse(lengthItem.Content.ToString());

            // Disable button during read
            ReadMemoryExecuteBtn.IsEnabled = false;
            ReadMemoryOutput.Text = "Reading...";

            // Pause monitor during read
            var wasMonitoring = _monitorRunning;
            if (wasMonitoring) StopMonitor();

            try
            {
                AddLog($"Reading {length} bytes from 0x{address:X8}...", Brushes.CornflowerBlue);
                
                var data = await _bootloader.ReadMemoryAsync(address, length);
                
                if (data != null && data.Length == length)
                {
                    // Format as hex dump
                    var hexDump = FormatHexDump(address, data);
                    ReadMemoryOutput.Text = hexDump;
                    AddLog($"Read complete: {length} bytes", Brushes.Green);
                }
                else
                {
                    ReadMemoryOutput.Text = "Read failed or timeout";
                    AddLog("Read failed", Brushes.Red);
                }
            }
            finally
            {
                // Resume monitor
                if (wasMonitoring) StartMonitor();
                
                ReadMemoryExecuteBtn.IsEnabled = true;
            }
        }

        private string FormatHexDump(uint baseAddress, byte[] data)
        {
            var sb = new System.Text.StringBuilder();
            
            for (int i = 0; i < data.Length; i += 16)
            {
                // Address column
                sb.Append($"{baseAddress + i:X8}:  ");
                
                // Hex bytes (16 per line, with space every 8 bytes)
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < data.Length)
                        sb.Append($"{data[i + j]:X2} ");
                    else
                        sb.Append("   ");
                    
                    if (j == 7) sb.Append(" ");
                }
                
                sb.Append(" ");
                
                // ASCII representation
                for (int j = 0; j < 16 && i + j < data.Length; j++)
                {
                    byte b = data[i + j];
                    char c = (b >= 32 && b <= 126) ? (char)b : '.';
                    sb.Append(c);
                }
                
                sb.AppendLine();
            }
            
            return sb.ToString();
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
                    catch (Exception ex)
                    {
                        // Device likely disconnected
                        Dispatcher.BeginInvoke(() =>
                        {
                            _bootloader.Disconnect();
                            AddLog("Connection failed", Brushes.Red);
                            UpdateUI();
                        });
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
            
            // Only join if we are NOT on the monitor thread logic
            if (_monitorThread != null && _monitorThread != Thread.CurrentThread)
            {
                _monitorThread.Join(1000);
            }
            AddLog("Monitor stopped");
        }

        private void StartHealthCheck()
        {
            if (_healthCheckRunning) return;
            
            _healthCheckRunning = true;
            AddLog("Health check started", Brushes.Gray);
            
            _healthCheckThread = new Thread(() =>
            {
                while (_healthCheckRunning && _bootloader.IsConnected)
                {
                    try
                    {
                        // Check if the port still exists in system
                        var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
                        
                        if (!string.IsNullOrEmpty(_connectedPort) && !availablePorts.Contains(_connectedPort))
                        {
                            // Port no longer exists - device disconnected
                            Dispatcher.BeginInvoke(() =>
                            {
                                _bootloader.Disconnect();
                                AddLog("Device disconnected", Brushes.Red);
                                UpdateUI();
                                _healthCheckRunning = false;
                            });
                            break;
                        }
                        
                        Thread.Sleep(500); // Check every 500ms
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(() => AddLog($"Health check error: {ex.Message}", Brushes.Orange));
                        break;
                    }
                }
                
                // Check if loop exited unexpectedly (while still supposed to be running)
                if (_healthCheckRunning)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_healthCheckRunning) // Double check on UI thread
                        {
                            _bootloader.Disconnect();
                            AddLog("Device disconnected", Brushes.Red);
                            UpdateUI();
                            _healthCheckRunning = false;
                        }
                        AddLog("Health check stopped", Brushes.Gray);
                    });
                }
                else
                {
                    Dispatcher.BeginInvoke(() => AddLog("Health check stopped", Brushes.Gray));
                }
            })
            {
                IsBackground = true
            };
            _healthCheckThread.Start();
        }

        private void StopHealthCheck()
        {
            if (!_healthCheckRunning) return;
            
            _healthCheckRunning = false;
            
            // Avoid deadlock if called from within the thread
            if (_healthCheckThread != null && _healthCheckThread != Thread.CurrentThread)
            {
                _healthCheckThread.Join(1000);
            }
            _connectedPort = null;
        }

        private void OnLogReceived(object? sender, string message)
        {
            Dispatcher.Invoke(() => AddToMonitor($"[LOG] {message}\n"));
        }

        private void AddToMonitor(string message)
        {
            bool autoScroll = MonitorAutoScroll.IsChecked == true;
            double savedOffset = MonitorScroll.VerticalOffset;

            MonitorText.AppendText(message);

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
                var time = DateTime.Now.ToString("HH:mm:ss");
                var run = new Run($"[{time}] {message}");
                
                if (color != null)
                {
                    run.Foreground = color;
                }

                var paragraph = new Paragraph(run) { Margin = new Thickness(0) };

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
                    AddLog($"Dropped: {Path.GetFileName(_selectedFilePath)}", Brushes.CornflowerBlue);
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
                UpdateUI(); // Enable buttons if file valid
            }

            // Restore Port
            if (!string.IsNullOrEmpty(_settings.LastPort))
            {
                // Check if port exists in list
                foreach (var item in PortComboBox.Items)
                {
                    if (item.ToString() == _settings.LastPort)
                    {
                        PortComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            // Restore Baud Rate
            if (_settings.LastBaudRate > 0)
            {
                foreach (ComboBoxItem item in BaudComboBox.Items)
                {
                    if (int.TryParse(item.Content.ToString(), out int baud) && baud == _settings.LastBaudRate)
                    {
                        BaudComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void SaveSettings()
        {
            // Save if connected or just UI state
            _settings.LastPort = PortComboBox.SelectedItem as string;
            
            if (BaudComboBox.SelectedItem is ComboBoxItem baudItem && int.TryParse(baudItem.Content.ToString(), out int baud))
            {
                _settings.LastBaudRate = baud;
            }
            
            _settings.LastFilePath = _selectedFilePath;
            _settings.Save();
        }
    }
}
