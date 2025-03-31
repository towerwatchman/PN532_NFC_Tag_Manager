using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using System.IO.Ports;
using System.Windows.Shapes;

namespace NFC_Tag_Manager
{
    public partial class MainWindow : Window
    {
        private Pn532Uart _pn532;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isConnected = false;

        public MainWindow()
        {
            InitializeComponent();
            StartSerialPortPolling();
        }

        private async void StartSerialPortPolling()
        {
            await Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var ports = SerialPort.GetPortNames();
                    Dispatcher.Invoke(() =>
                    {
                        if (!ports.SequenceEqual(SerialPortComboBox.Items.Cast<string>()))
                        {
                            SerialPortComboBox.ItemsSource = ports;
                            if (ports.Length > 0 && SerialPortComboBox.SelectedIndex == -1)
                                SerialPortComboBox.SelectedIndex = 0;
                        }
                    });
                    await Task.Delay(5000, _cts.Token);
                }
            });
        }

        private void SerialPortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SerialPortComboBox.SelectedItem is string portName)
            {
                DriverTextBlock.Text = "Driver: Serial (UART)";
                DeviceNameTextBlock.Text = "Device: PN532";
                ConnectButton.IsEnabled = true;
            }
            else
            {
                DriverTextBlock.Text = "";
                DeviceNameTextBlock.Text = "";
                ConnectButton.IsEnabled = false;
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                Disconnect();
                return;
            }

            string portName = SerialPortComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(portName)) return;

            _pn532 = new Pn532Uart(portName, Log);
            _isConnected = _pn532.Connect();
            if (_isConnected)
            {
                await PerformConnectionTests();
                ConnectButton.Content = "Disconnect";
                ReadButton.IsEnabled = true;
                WriteButton.IsEnabled = true;
            }
            else
            {
                _pn532 = null;
            }
        }

        private void Disconnect()
        {
            _pn532?.Disconnect();
            _pn532 = null;
            _isConnected = false;
            ConnectButton.Content = "Connect";
            ReadButton.IsEnabled = false;
            WriteButton.IsEnabled = false;
            ResetIndicators();
        }

        private async Task PerformConnectionTests()
        {
            byte[] samCmd = _pn532.BuildCustomSamFrame();
            await _pn532.SendCommand(samCmd, "SAM Config");
            var (samSuccess, _) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x15 }, "SAM Config");
            UpdateIndicator(AwakeIndicator, samSuccess);
            UpdateIndicator(SamConfigIndicator, samSuccess);
            if (!samSuccess)
            {
                Log("SAM Config failed. Aborting connection tests.");
            }
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} - {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private void UpdateIndicator(Ellipse indicator, bool success)
        {
            Dispatcher.Invoke(() => indicator.Fill = success ? Brushes.Green : Brushes.Red);
        }

        private void ResetIndicators()
        {
            AwakeIndicator.Fill = Brushes.Gray;
            SamConfigIndicator.Fill = Brushes.Gray;
            FirmwareIndicator.Fill = Brushes.Gray;
            StatusIndicator.Fill = Brushes.Gray;
            PassiveTargetIndicator.Fill = Brushes.Gray;
        }

        private async void ReadButton_Click(object sender, RoutedEventArgs e)
        {
            byte[] passiveCmd = _pn532.BuildFrame(_pn532.cmdInListPassiveTarget.Concat(new byte[] { 0x01, 0x00 }).ToArray());
            await _pn532.SendCommand(passiveCmd, "Detect Tag");
            var (detectSuccess, _) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x4B }, "Detect Tag");
            if (!detectSuccess)
            {
                Log("No tag detected for reading.");
                return;
            }

            byte[] readCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, 0x04 }).ToArray());
            await _pn532.SendCommand(readCmd, "Read NDEF");
            var (readSuccess, _) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Read NDEF");
            if (readSuccess)
            {
                Log("NDEF data read successfully. Check log for raw data.");
            }
        }

        private async void WriteButton_Click(object sender, RoutedEventArgs e)
        {
            string text = WriteDataTextBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                Log("No data entered for writing.");
                return;
            }

            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] ndefRecord = new byte[] { 0xD1, 0x01, (byte)(textBytes.Length + 3), 0x54, 0x02, 0x65, 0x6E }.Concat(textBytes).ToArray();
            byte[] ndefMessage = new byte[] { 0x00, 0x03, (byte)ndefRecord.Length }.Concat(ndefRecord).Concat(new byte[] { 0xFE }).ToArray();
            byte[] writeCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0xA0, 0x04 }).Concat(ndefMessage.Take(16)).ToArray());

            await _pn532.SendCommand(writeCmd, "Write NDEF");
            var (writeSuccess, _) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Write NDEF");
            if (writeSuccess)
            {
                Log("NDEF data written successfully.");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            Disconnect();
            base.OnClosed(e);
        }
    }
}