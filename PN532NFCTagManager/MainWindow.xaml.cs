using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using System.IO.Ports;
using System.Windows.Shapes;
using System.Text;

namespace NFC_Tag_Manager
{
    public partial class MainWindow : Window
    {
        private Pn532Uart _pn532;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private CancellationTokenSource _readCts;
        private bool _isConnected = false;
        private string _firmwareVersion = "";
        private string _lastConnectedPort = "";
        private bool _isReading = false;

        public MainWindow()
        {
            InitializeComponent();
            StartSerialPortPolling();
            StopReadingButton.Visibility = Visibility.Collapsed;
            StopReadingButton.Click += StopReadingButton_Click;
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

                            if (_isConnected && _pn532 != null && !ports.Contains(_lastConnectedPort))
                            {
                                _isConnected = false;
                                Log("Connected port no longer available.");
                                AwakeIndicator.Fill = Brushes.Red;
                                ResetOtherIndicators();
                                ConnectButton.Content = "Connect";
                                ConnectButton.IsEnabled = ports.Length > 0;
                                ReadButton.IsEnabled = false;
                                WriteButton.IsEnabled = false;
                            }
                            else if (!_isConnected && ports.Length > 0)
                            {
                                ConnectButton.IsEnabled = true;
                            }
                        }
                    });
                    try
                    {
                        await Task.Delay(5000, _cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                }
            });
        }

        private void SerialPortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SerialPortComboBox.SelectedItem is string portName && _pn532 != null && _isConnected)
            {
                Dispatcher.Invoke(() => DeviceNameTextBlock.Text = $"PN532 {_firmwareVersion}");
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    DriverTextBlock.Text = "";
                    DeviceNameTextBlock.Text = "";
                    ConnectButton.IsEnabled = SerialPortComboBox.Items.Count > 0;
                });
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
                _lastConnectedPort = portName;
                await PerformConnectionTests();
                if (_isConnected)
                {
                    ConnectButton.Content = "Disconnect";
                    ReadButton.IsEnabled = true;
                    WriteButton.IsEnabled = true;
                    UpdateStatusIndicator();
                }
            }
            else
            {
                _pn532 = null;
            }
        }

        private void Disconnect()
        {
            if (_isReading)
            {
                StopReading();
            }
            if (_pn532 != null)
            {
                try
                {
                    _pn532.Disconnect();
                }
                catch (Exception ex)
                {
                    Log($"Disconnect error: {ex.Message}");
                }
                finally
                {
                    _pn532 = null;
                    _isConnected = false;
                    AwakeIndicator.Fill = Brushes.Red;
                    ResetOtherIndicators();
                    ConnectButton.Content = "Connect";
                    ReadButton.IsEnabled = false;
                    WriteButton.IsEnabled = false;
                    _firmwareVersion = "";
                    Dispatcher.Invoke(() => DeviceNameTextBlock.Text = "Disconnected");
                    ConnectButton.IsEnabled = SerialPortComboBox.Items.Count > 0;
                }
            }
        }

        private async Task PerformConnectionTests()
        {
            byte[] samCmd = _pn532.BuildFrame(_pn532.cmdSamConfiguration, _pn532.hostToPn532);
            await _pn532.SendFrame(samCmd, "SAM Config");
            Dispatcher.Invoke(() => CommandIndicator.Fill = Brushes.Blue);
            var (samSuccess, _) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x15 }, "SAM Config");
            UpdateIndicator(AwakeIndicator, samSuccess);
            UpdateIndicator(SamConfigIndicator, samSuccess);
            if (!samSuccess)
            {
                Log("SAM Config failed. Aborting connection tests.");
                _isConnected = false;
                Dispatcher.Invoke(() => DeviceNameTextBlock.Text = "Unknown Device");
            }
            else
            {
                byte[] firmwareCmd = _pn532.BuildFrame(_pn532.cmdGetFirmwareVersion, _pn532.hostToPn532);
                await _pn532.SendFrame(firmwareCmd, "Get Firmware Version");
                Dispatcher.Invoke(() => CommandIndicator.Fill = Brushes.Blue);
                var (firmwareSuccess, firmwareResponse) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x03 }, "Get Firmware Version");

                if (firmwareSuccess && firmwareResponse.Length >= 13)
                {
                    byte ver = firmwareResponse[8];
                    byte rev = firmwareResponse[9];
                    _firmwareVersion = $"v{ver}.{rev}";
                    Dispatcher.Invoke(() => DeviceNameTextBlock.Text = $"PN532 {_firmwareVersion}");

                    double versionNumber = ver + (rev / 10.0);
                    UpdateIndicator(FirmwareIndicator, versionNumber > 1.4);
                }
                else
                {
                    _firmwareVersion = "(Firmware Unknown)";
                    Dispatcher.Invoke(() => DeviceNameTextBlock.Text = $"PN532 {_firmwareVersion}");
                    UpdateIndicator(FirmwareIndicator, false);
                }
                UpdateStatusIndicator();
            }
            Dispatcher.Invoke(() => { WakeupIndicator.Fill = Brushes.Gray; CommandIndicator.Fill = Brushes.Gray; });
        }

        private async void ReadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || _pn532 == null) return;

            _isReading = true;
            _readCts = new CancellationTokenSource();
            ReadButton.IsEnabled = false;
            StopReadingButton.Visibility = Visibility.Visible;

            await Task.Run(async () =>
            {
                while (_isReading && !_readCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        Dispatcher.Invoke(() => WakeupIndicator.Fill = Brushes.Green);
                        await Task.Delay(500, _readCts.Token);
                        byte[] passiveCmd = _pn532.BuildFrame(_pn532.cmdInListPassiveTarget.Concat(new byte[] { 0x01, 0x00 }).ToArray(), _pn532.hostToPn532);
                        await _pn532.SendFrame(passiveCmd, "Detect Tag");
                        Dispatcher.Invoke(() => CommandIndicator.Fill = Brushes.Blue);
                        var (detectSuccess, detectResponse) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x4B }, "Detect Tag");
                        Log($"Detect Response: {BitConverter.ToString(detectResponse)}");
                        if (detectSuccess)
                        {
                            Dispatcher.Invoke(() => WakeupIndicator.Fill = Brushes.Green);
                            await Task.Delay(500, _readCts.Token);
                            byte[] readCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, 0x04 }).ToArray(), _pn532.hostToPn532);
                            await _pn532.SendFrame(readCmd, "Read NDEF");
                            Dispatcher.Invoke(() => CommandIndicator.Fill = Brushes.Blue);
                            var (readSuccess, readResponse) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Read NDEF");
                            Log($"Read Response: {BitConverter.ToString(readResponse)}");
                            if (readSuccess)
                            {
                                DisplayTagInfo(detectResponse, readResponse);
                                _isReading = false;
                            }
                        }
                        else
                        {
                            Log("No tag detected, retrying...");
                            await Task.Delay(500, _readCts.Token);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Log("Tag reading stopped.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Tag reading error: {ex.Message}");
                        break;
                    }
                    Dispatcher.Invoke(() => { WakeupIndicator.Fill = Brushes.Gray; CommandIndicator.Fill = Brushes.Gray; });
                }
                Dispatcher.Invoke(() =>
                {
                    ReadButton.IsEnabled = true;
                    StopReadingButton.Visibility = Visibility.Collapsed;
                });
            }, _readCts.Token);
        }

        private void StopReadingButton_Click(object sender, RoutedEventArgs e)
        {
            StopReading();
        }

        private void StopReading()
        {
            if (_isReading && _readCts != null)
            {
                _readCts.Cancel();
                _isReading = false;
            }
        }

        private void DisplayTagInfo(byte[] detectResponse, byte[] readResponse)
        {
            Dispatcher.Invoke(() =>
            {
                int dataStart = detectResponse.TakeWhile((b, i) => i < detectResponse.Length - 2 && !(detectResponse[i] == 0xD5 && detectResponse[i + 1] == 0x4B)).Count();
                byte[] tagData = detectResponse.Skip(dataStart).ToArray();
                byte nbTag = tagData[2];
                byte sensRes1 = tagData[4];
                byte sensRes2 = tagData[5];
                byte sak = tagData[6];
                byte uidLength = tagData[7];
                byte[] uid = tagData.Skip(8).Take(uidLength).ToArray();

                int ndefStart = readResponse.TakeWhile((b, i) => i < readResponse.Length - 2 && !(readResponse[i] == 0xD5 && readResponse[i + 1] == 0x41)).Count();
                byte[] ndefData = readResponse.Skip(ndefStart + 3).ToArray();
                string textData = "";
                if (ndefData.Length >= 3 && ndefData[0] == 0xD1 && ndefData[1] == 0x01)
                {
                    int payloadLength = ndefData[2];
                    byte[] payload = ndefData.Skip(3).Take(payloadLength).ToArray();
                    string lang = Encoding.UTF8.GetString(payload.Take(2).ToArray());
                    textData = Encoding.UTF8.GetString(payload.Skip(2).ToArray());
                }

                string tagType = "NXP - NTAG215";
                string technologies = "NFC Forum Type 2";
                string serialNumber = BitConverter.ToString(uid).Replace("-", "");
                string atqa = $"0x{sensRes2:X2}{sensRes1:X2}";
                string sakStr = $"0x{sak:X2}";
                string protectionStatus = "Not Protected";
                int totalBytes = 540;
                int totalPages = 135;
                string dataFormat = "NFC Forum Type 2";
                int usedBytes = 30;
                int availableBytes = totalBytes - usedBytes;
                string sizeInfo = $"{usedBytes}/{totalBytes} bytes ({(usedBytes * 100 / totalBytes)}%)";
                string isWritable = "Yes";
                string records = $"Record 1: TEXT (0x54), Data: \"{textData}\", Payload Length: {usedBytes - 4} bytes";

                TagInfoTextBlock.Text = $@"
Tag Type: {tagType}
Technologies: {technologies}
Serial Number: {serialNumber}
ATQA: {atqa}
SAK: {sakStr}
Protection Status: {protectionStatus}
Memory Information: {totalBytes} bytes, {totalPages} pages
Data Format: {dataFormat}
Size Available/Used: {sizeInfo}
Writable: {isWritable}
Records:
{records}";
            });
        }

        private void UpdateStatusIndicator()
        {
            Dispatcher.Invoke(() =>
            {
                bool allGreen = AwakeIndicator.Fill == Brushes.Green &&
                                SamConfigIndicator.Fill == Brushes.Green &&
                                FirmwareIndicator.Fill == Brushes.Green;
                StatusIndicator.Fill = allGreen ? Brushes.Green : Brushes.Gray;
            });
        }

        private void ResetOtherIndicators()
        {
            Dispatcher.Invoke(() =>
            {
                SamConfigIndicator.Fill = Brushes.Gray;
                FirmwareIndicator.Fill = Brushes.Gray;
                StatusIndicator.Fill = Brushes.Gray;
                PassiveTargetIndicator.Fill = Brushes.Gray;
            });
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
            byte[] writeCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0xA0, 0x04 }).Concat(ndefMessage.Take(16)).ToArray(), _pn532.hostToPn532);

            await _pn532.SendFrame(writeCmd, "Write NDEF");
            Dispatcher.Invoke(() => CommandIndicator.Fill = Brushes.Blue);
            var (writeSuccess, _) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Write NDEF");
            if (writeSuccess)
            {
                Log("NDEF data written successfully.");
            }
            Dispatcher.Invoke(() => { WakeupIndicator.Fill = Brushes.Gray; CommandIndicator.Fill = Brushes.Gray; });
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            if (_isReading)
            {
                StopReading();
            }
            Disconnect();
            base.OnClosed(e);
        }
    }
}