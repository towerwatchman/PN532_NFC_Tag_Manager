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
using NLog;
using Microsoft.Data.Sqlite;
using System.Net.Http;
using System.IO;
using Newtonsoft.Json.Linq;
using Path = System.IO.Path;

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
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private string _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "amiibo.db");

        public MainWindow()
        {
            InitializeComponent();
            ConfigureNLog();
            CheckAndUpdateAmiiboDatabase();
            StartSerialPortPolling();
            StopReadingButton.Visibility = Visibility.Collapsed;
            StopReadingButton.Click += StopReadingButton_Click;
        }

        private void ConfigureNLog()
        {
            var config = new NLog.Config.LoggingConfiguration();
            var logConsole = new NLog.Targets.ConsoleTarget("logconsole");
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logConsole);
            LogManager.Configuration = config;
        }

        private async void CheckAndUpdateAmiiboDatabase()
        {
            if (!File.Exists(_dbPath) || IsDatabaseOutdated())
            {
                var result = MessageBox.Show("Amiibo database is missing or outdated. Download now?", "Database Update", MessageBoxButton.YesNo);
                if (result == MessageBoxResult.Yes)
                {
                    await DownloadAmiiboDatabase();
                }
            }
            CreateAmiiboTableIfNotExists();
        }

        private bool IsDatabaseOutdated()
        {
            if (!File.Exists(_dbPath)) return true;
            var lastWrite = File.GetLastWriteTime(_dbPath);
            return (DateTime.Now - lastWrite).TotalDays > 30; // Update every 30 days
        }

        private async Task DownloadAmiiboDatabase()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var response = await client.GetStringAsync("https://www.amiiboapi.com/api/amiibo/");
                    var json = JObject.Parse(response);
                    var amiibos = json["amiibo"];

                    using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
                    {
                        await conn.OpenAsync();
                        var cmd = new SqliteCommand("DROP TABLE IF EXISTS amiibos; CREATE TABLE amiibos (char_id INTEGER, variation INTEGER, name TEXT)", conn);
                        await cmd.ExecuteNonQueryAsync();

                        foreach (var amiibo in amiibos)
                        {
                            string characterIdHex = amiibo["characterId"]?.ToString().Substring(0, 4); // First 4 hex digits for char_id
                            string variationHex = amiibo["characterId"]?.ToString().Substring(4, 2); // Next 2 hex digits for variation
                            ushort charId = Convert.ToUInt16(characterIdHex, 16);
                            byte variation = Convert.ToByte(variationHex, 16);
                            string name = amiibo["name"]?.ToString();

                            cmd = new SqliteCommand("INSERT INTO amiibos (char_id, variation, name) VALUES (@char_id, @variation, @name)", conn);
                            cmd.Parameters.AddWithValue("@char_id", charId);
                            cmd.Parameters.AddWithValue("@variation", variation);
                            cmd.Parameters.AddWithValue("@name", name);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    Logger.Info("Amiibo database downloaded and updated.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to download Amiibo database: {ex.Message}");
                MessageBox.Show("Failed to download Amiibo database. Using existing data if available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CreateAmiiboTableIfNotExists()
        {
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS amiibos (char_id INTEGER, variation INTEGER, name TEXT)", conn);
                cmd.ExecuteNonQuery();
            }
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
                                Logger.Info("Connected port no longer available.");
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

            _pn532 = new Pn532Uart(portName, Logger.Info);
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
                    Logger.Error($"Disconnect error: {ex.Message}");
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
                Logger.Error("SAM Config failed. Aborting connection tests.");
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
                        Logger.Debug($"Detect Response: {BitConverter.ToString(detectResponse ?? Array.Empty<byte>())}");
                        if (detectSuccess && detectResponse != null)
                        {
                            Dispatcher.Invoke(() => WakeupIndicator.Fill = Brushes.Green);
                            await Task.Delay(500, _readCts.Token);

                            int dataStart = detectResponse.TakeWhile((b, i) => i < detectResponse.Length - 2 && !(detectResponse[i] == 0xD5 && detectResponse[i + 1] == 0x4B)).Count();
                            byte[] tagData = detectResponse.Skip(dataStart).ToArray();
                            byte nbTag = tagData.Length > 2 ? tagData[2] : (byte)0;
                            byte sensRes1 = tagData.Length > 4 ? tagData[4] : (byte)0;
                            byte sensRes2 = tagData.Length > 5 ? tagData[5] : (byte)0;
                            byte sak = tagData.Length > 6 ? tagData[6] : (byte)0;
                            byte uidLength = tagData.Length > 7 ? tagData[7] : (byte)0;
                            byte[] uid = tagData.Length > 8 ? tagData.Skip(8).Take(uidLength).ToArray() : Array.Empty<byte>();

                            string baseTagType = DetectCardType(sensRes1, sensRes2, sak, uidLength);
                            string protectionStatus = "Unknown";
                            string tagType = baseTagType;
                            int totalBytes = 0;
                            int totalPages = 0;
                            bool isAmiibo = false;
                            string amiiboCharacter = "Unknown";

                            if (baseTagType.Contains("NTAG"))
                            {
                                byte[] readCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, 0x04 }).ToArray(), _pn532.hostToPn532);
                                await _pn532.SendFrame(readCmd, "Check Protection");
                                var (readSuccess, readResponse) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Check Protection");
                                if (readSuccess && readResponse != null && readResponse.Length >= 8)
                                {
                                    byte status = readResponse[7];
                                    if (status == 0x00)
                                    {
                                        protectionStatus = "Not Protected";
                                        tagType = await DetermineNtagVariant();
                                        totalBytes = tagType.Contains("NTAG216") ? 888 : tagType.Contains("NTAG215") ? 540 : tagType.Contains("NTAG213") ? 144 : 0;
                                        totalPages = tagType.Contains("NTAG216") ? 222 : tagType.Contains("NTAG215") ? 135 : tagType.Contains("NTAG213") ? 36 : 0;
                                        isAmiibo = totalBytes == 540 || totalBytes == 144; // Amiibo typically NTAG215
                                    }
                                    else
                                    {
                                        protectionStatus = "Protected";
                                        tagType = "NXP - NTAG (Protected)";
                                        isAmiibo = true; // Assume Amiibo due to protection
                                        totalBytes = 540; // Force NTAG215 size for Amiibo
                                        totalPages = 135;
                                    }
                                }
                                else
                                {
                                    protectionStatus = "Protected";
                                    tagType = "NXP - NTAG (Protected)";
                                    isAmiibo = true; // Assume Amiibo due to protection
                                    totalBytes = 540; // Force NTAG215 size for Amiibo
                                    totalPages = 135;
                                }
                            }

                            byte[] fullNdefData = Array.Empty<byte>();
                            bool terminatorFound = false;
                            byte[] page21Data = null;
                            byte[] page22Data = null;

                            if (protectionStatus == "Protected" && isAmiibo)
                            {
                                // Calculate Amiibo password from UID
                                byte[] password = new byte[4];
                                password[0] = (byte)(0xAA ^ (uid[1] ^ uid[3]));
                                password[1] = (byte)(0x55 ^ (uid[2] ^ uid[4]));
                                password[2] = (byte)(0xAA ^ (uid[3] ^ uid[5]));
                                password[3] = (byte)(0x55 ^ (uid[4] ^ uid[6]));

                                // Authenticate with password
                                byte[] authCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x1B }).Concat(password).ToArray(), _pn532.hostToPn532);
                                await _pn532.SendFrame(authCmd, "Authenticate Amiibo");
                                var (authSuccess, authResponse) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Authenticate Amiibo");
                                Logger.Debug($"Auth Response: {BitConverter.ToString(authResponse ?? Array.Empty<byte>())}");

                                if (authSuccess && authResponse != null && authResponse.Length >= 8 && authResponse[7] == 0x00)
                                {
                                    Logger.Info("Amiibo authentication successful");
                                    protectionStatus = "Authenticated";

                                    // Read page 21
                                    byte[] readPage21Cmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, 0x15 }).ToArray(), _pn532.hostToPn532);
                                    await _pn532.SendFrame(readPage21Cmd, "Read Page 21");
                                    var (page21Success, page21Response) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Read Page 21");
                                    if (page21Success && page21Response != null && page21Response.Length >= 8 && page21Response[7] == 0x00)
                                    {
                                        page21Data = page21Response.Skip(page21Response.Length - 4).Take(4).ToArray();
                                        Logger.Debug($"Page 21 Data: {BitConverter.ToString(page21Data)}");
                                    }

                                    // Read page 22
                                    byte[] readPage22Cmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, 0x16 }).ToArray(), _pn532.hostToPn532);
                                    await _pn532.SendFrame(readPage22Cmd, "Read Page 22");
                                    var (page22Success, page22Response) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Read Page 22");
                                    if (page22Success && page22Response != null && page22Response.Length >= 8 && page22Response[7] == 0x00)
                                    {
                                        page22Data = page22Response.Skip(page22Response.Length - 4).Take(4).ToArray();
                                        Logger.Debug($"Page 22 Data: {BitConverter.ToString(page22Data)}");
                                    }

                                    // Read initial data blocks
                                    byte blockNumber = 0x04;
                                    while (blockNumber < totalPages && !terminatorFound)
                                    {
                                        byte[] readCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, blockNumber }).ToArray(), _pn532.hostToPn532);
                                        await _pn532.SendFrame(readCmd, $"Read NDEF Block {blockNumber}");
                                        var (readSuccess, readResponse) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, $"Read NDEF Block {blockNumber}");
                                        Logger.Debug($"Read Response Block {blockNumber}: {BitConverter.ToString(readResponse ?? Array.Empty<byte>())}");
                                        if (readSuccess && readResponse != null && readResponse.Length >= 8)
                                        {
                                            byte status = readResponse[7];
                                            if (status == 0x00)
                                            {
                                                int ndefStart = readResponse.TakeWhile((b, i) => i < readResponse.Length - 2 && !(readResponse[i] == 0xD5 && readResponse[i + 1] == 0x41)).Count();
                                                byte[] blockData = readResponse.Skip(ndefStart + 3).Take(4).ToArray();
                                                fullNdefData = fullNdefData.Concat(blockData).ToArray();
                                                if (Array.IndexOf(fullNdefData, (byte)0xFE) != -1)
                                                {
                                                    terminatorFound = true;
                                                    Logger.Debug($"Terminator FE found at block {blockNumber}");
                                                    break;
                                                }
                                            }
                                            else
                                            {
                                                Logger.Warn($"Error reading block {blockNumber}: status {status:X2}");
                                                break;
                                            }
                                        }
                                        blockNumber++;
                                    }

                                    if (page21Data != null && page22Data != null)
                                    {
                                        byte charIdHigh = page21Data[0];
                                        byte charIdLow = page22Data[0];
                                        byte variation = page22Data[1];
                                        ushort charId = (ushort)((charIdHigh << 8) | charIdLow);
                                        amiiboCharacter = GetAmiiboCharacterName(charId, variation);
                                    }
                                }
                                else
                                {
                                    Logger.Warn("Amiibo authentication failed");
                                }
                            }

                            DisplayTagInfo(detectResponse, fullNdefData, terminatorFound, tagType, protectionStatus, totalBytes, totalPages, isAmiibo, amiiboCharacter);
                            _isReading = false;
                        }
                        else
                        {
                            Logger.Info("No tag detected, retrying...");
                            await Task.Delay(500, _readCts.Token);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Logger.Info("Tag reading stopped.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Tag reading error: {ex.Message}");
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

        private async Task<string> DetermineNtagVariant()
        {
            byte[] readCmd216 = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, 0xE0 }).ToArray(), _pn532.hostToPn532);
            await _pn532.SendFrame(readCmd216, "Check NTAG216");
            var (success216, response216) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Check NTAG216");
            if (success216 && response216 != null && response216.Length >= 8 && response216[7] == 0x00)
            {
                Logger.Debug("Detected NTAG216");
                return "NXP - NTAG216";
            }

            byte[] readCmd215 = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0x30, 0x80 }).ToArray(), _pn532.hostToPn532);
            await _pn532.SendFrame(readCmd215, "Check NTAG215");
            var (success215, response215) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Check NTAG215");
            if (success215 && response215 != null && response215.Length >= 8 && response215[7] == 0x00)
            {
                Logger.Debug("Detected NTAG215");
                return "NXP - NTAG215";
            }

            Logger.Debug("Defaulting to NTAG213");
            return "NXP - NTAG213";
        }

        private string DetectCardType(byte sensRes1, byte sensRes2, byte sak, byte uidLength)
        {
            string atqa = $"0x{sensRes1:X2}{sensRes2:X2}";
            string sakStr = $"0x{sak:X2}";

            if (atqa == "0x0004" && sakStr == "0x08" && uidLength == 4) return "NXP - MIFARE Classic 1K";
            if (atqa == "0x0004" && sakStr == "0x18" && uidLength == 4) return "NXP - MIFARE Classic 4K";
            if (atqa == "0x0044" && sakStr == "0x00" && uidLength == 4) return "NXP - MIFARE Ultralight";
            if (atqa == "0x0044" && sakStr == "0x00" && uidLength == 7) return "NXP - NTAG";
            if (atqa == "0x0344" && sakStr == "0x20" && uidLength == 7) return "NXP - NTAG I2C";
            if (atqa == "0x0002" && sakStr == "0x18" && uidLength == 4) return "NXP - MIFARE Plus 2K";
            if (atqa == "0x0002" && sakStr == "0x10" && uidLength == 4) return "NXP - MIFARE Plus 4K";
            if (atqa == "0x0004" && sakStr == "0x20" && uidLength == 7) return "NXP - MIFARE DESFire";
            if (atqa == "0x0044" && sakStr == "0x08" && uidLength == 4) return "NXP - MIFARE Mini";
            return $"Unknown (ATQA: {atqa}, SAK: {sakStr}, UID Length: {uidLength})";
        }

        private string GetAmiiboCharacterName(ushort charId, byte variation)
        {
            try
            {
                using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
                {
                    conn.Open();
                    var cmd = new SqliteCommand("SELECT name FROM amiibos WHERE char_id = @char_id AND variation = @variation", conn);
                    cmd.Parameters.AddWithValue("@char_id", charId);
                    cmd.Parameters.AddWithValue("@variation", variation);
                    var result = cmd.ExecuteScalar();
                    if (result != null)
                    {
                        return result.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to query Amiibo database: {ex.Message}");
            }
            return $"Unknown Character (ID: 0x{charId:X4}, Variation: 0x{variation:X2})";
        }

        private void DisplayTagInfo(byte[] detectResponse, byte[] fullNdefData, bool terminatorFound, string tagType, string protectionStatus, int totalBytes, int totalPages, bool isAmiibo, string amiiboCharacter)
        {
            Dispatcher.Invoke(() =>
            {
                int dataStart = detectResponse.TakeWhile((b, i) => i < detectResponse.Length - 2 && !(detectResponse[i] == 0xD5 && detectResponse[i + 1] == 0x4B)).Count();
                byte[] tagData = detectResponse.Skip(dataStart).ToArray();
                byte nbTag = tagData.Length > 2 ? tagData[2] : (byte)0;
                byte sensRes1 = tagData.Length > 4 ? tagData[4] : (byte)0;
                byte sensRes2 = tagData.Length > 5 ? tagData[5] : (byte)0;
                byte sak = tagData.Length > 6 ? tagData[6] : (byte)0;
                byte uidLength = tagData.Length > 7 ? tagData[7] : (byte)0;
                byte[] uid = tagData.Length > 8 ? tagData.Skip(8).Take(uidLength).ToArray() : Array.Empty<byte>();

                string textData = "";
                string language = "";
                string encoding = "UTF-8";
                string recordStatus = terminatorFound ? "" : " (Incomplete)";
                byte[] rawPayload = Array.Empty<byte>();
                string recordType = isAmiibo ? "Amiibo (Encrypted)" : "Unknown";

                if (fullNdefData.Length > 0 && protectionStatus != "Protected")
                {
                    int terminatorIndex = Array.IndexOf(fullNdefData, (byte)0xFE);
                    byte[] trimmedData = terminatorIndex != -1 ? fullNdefData.Take(terminatorIndex).ToArray() : fullNdefData;
                    if (trimmedData.Length >= 2)
                    {
                        byte[] payload = trimmedData.Skip(2).ToArray();
                        if (payload.Length >= 6 && payload[0] == 0xD1 && payload[1] == 0x01 && payload[3] == 0x54)
                        {
                            int langLength = payload[4];
                            if (payload.Length >= 5 + langLength)
                            {
                                language = Encoding.UTF8.GetString(payload.Skip(5).Take(langLength).ToArray());
                                textData = Encoding.UTF8.GetString(payload.Skip(5 + langLength).ToArray());
                                rawPayload = payload.Skip(4).Take(payload[2]).ToArray();
                                encoding = "UTF-8";
                                recordType = "TEXT (0x54)";
                            }
                        }
                        else if (isAmiibo)
                        {
                            textData = $"Encrypted Amiibo data (requires decryption keys)\nCharacter: {amiiboCharacter}";
                            rawPayload = trimmedData;
                            encoding = "Raw Hex";
                            recordType = "Amiibo (Encrypted)";
                        }
                        else
                        {
                            textData = BitConverter.ToString(payload).Replace("-", " ");
                            rawPayload = payload;
                            encoding = "Raw Hex";
                            recordType = "Unknown";
                        }
                    }
                    else
                    {
                        textData = BitConverter.ToString(trimmedData).Replace("-", " ");
                        rawPayload = trimmedData;
                        encoding = "Raw Hex";
                    }
                }

                string technologies = "NFC Forum Type 2";
                string serialNumber = BitConverter.ToString(uid).Replace("-", "");
                string atqa = uidLength > 0 ? $"0x{sensRes1:X2}{sensRes2:X2}" : "Unknown";
                string sakStr = uidLength > 0 ? $"0x{sak:X2}" : "Unknown";
                string dataFormat = "NFC Forum Type 2";
                int usedBytes = rawPayload.Length > 0 ? rawPayload.Length + 4 : 0;
                int availableBytes = totalBytes - usedBytes;
                string sizeInfo = totalBytes > 0 ? $"{usedBytes}/{totalBytes} bytes ({(usedBytes * 100 / totalBytes)}%)" : "Unknown";
                string isWritable = protectionStatus == "Not Protected" || protectionStatus == "Authenticated" ? "Yes" : "No";
                string rawDataHex = rawPayload.Length > 0 ? BitConverter.ToString(rawPayload).Replace("-", " ") : "None";
                string records = rawPayload.Length > 0 ? $"Record 1: {recordType}{recordStatus}\n  Encoding: {encoding}\n  Language: \"{language}\"\n  Text: \"{textData}\"\n  Payload Length: {rawPayload.Length} bytes\n  Raw Data: {rawDataHex}" : "No records";

                TagInfoTextBlock.Text = $@"
Tag Type: {tagType}{(isAmiibo ? " (Amiibo)" : "")}
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

        private void UpdateIndicator(Ellipse indicator, bool success)
        {
            Dispatcher.Invoke(() => indicator.Fill = success ? Brushes.Green : Brushes.Red);
        }

        private void StopReadingButton_Click(object sender, RoutedEventArgs e)
        {
            StopReading();
        }

        private void StopReading()
        {
            if (_readCts != null)
            {
                _readCts.Cancel();
                _isReading = false;
            }
        }

        private async void WriteButton_Click(object sender, RoutedEventArgs e)
        {
            string text = WriteDataTextBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                Logger.Info("No data entered for writing.");
                return;
            }

            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            byte[] ndefRecord = new byte[] { 0xD1, 0x01, (byte)(textBytes.Length + 3), 0x54, 0x02, 0x65, 0x6E }.Concat(textBytes).ToArray();
            byte[] ndefMessage = new byte[] { 0x00, 0x03, (byte)ndefRecord.Length }.Concat(ndefRecord).Concat(new byte[] { 0xFE }).ToArray();
            byte[] writeCmd = _pn532.BuildFrame(_pn532.cmdInDataExchange.Concat(new byte[] { 0x01, 0xA0, 0x04 }).Concat(ndefMessage.Take(16)).ToArray(), _pn532.hostToPn532);

            await _pn532.SendFrame(writeCmd, "Write NDEF");
            Dispatcher.Invoke(() => CommandIndicator.Fill = Brushes.Blue);
            var (writeSuccess, _) = await _pn532.ReadResponse(new byte[] { 0xD5, 0x41 }, "Write NDEF");
            if (writeSuccess)
            {
                Logger.Info("NDEF data written successfully.");
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