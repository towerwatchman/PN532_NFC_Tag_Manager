using System;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Linq;

namespace NFC_Tag_Manager
{
    public class Pn532Uart
    {
        private SerialPort _serialPort;
        public readonly byte[] wakeupCmd = new byte[] { 0x55, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        public readonly byte[] ackFrame = new byte[] { 0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00 };
        public readonly byte[] nackFrame = new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00 };
        public readonly byte[] cmdSamConfiguration = new byte[] { 0x14 };
        public readonly byte[] cmdGetFirmwareVersion = new byte[] { 0x02 };
        public readonly byte[] cmdGetGeneralStatus = new byte[] { 0x04 };
        public readonly byte[] cmdInListPassiveTarget = new byte[] { 0x4A };
        public readonly byte[] cmdInDataExchange = new byte[] { 0x40 };
        public readonly byte[] hostToPn532 = new byte[] { 0xD4 };
        public readonly byte[] pn532ToHost = new byte[] { 0xD5 };
        public readonly byte[] pn532Ready = new byte[] { 0x01 };
        private readonly Action<string> _log;

        public Pn532Uart(string portName, Action<string> logCallback)
        {
            _serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 5000
            };
            _log = logCallback;
        }

        public bool Connect()
        {
            try
            {
                _serialPort.Open();
                _log("Opened serial port: " + _serialPort.PortName);
                return true;
            }
            catch (Exception ex)
            {
                _log("Error opening port: " + ex.Message);
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _serialPort?.Close();
                _serialPort?.Dispose();
                _log("Disconnected from serial port.");
            }
            catch (Exception ex)
            {
                _log("Error closing port: " + ex.Message);
            }
        }

        public async Task<bool> SendCommand(byte[] commandData, string commandName)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                _log($"{commandName}: Serial port not open.");
                return false;
            }

            try
            {
                _serialPort.Write(wakeupCmd, 0, wakeupCmd.Length);
                _log("Wakeup Sent: " + BitConverter.ToString(wakeupCmd));

                byte[] frame = BuildFrame(commandData);
                _serialPort.Write(frame, 0, frame.Length);
                _log($"{commandName} Sent: " + BitConverter.ToString(frame));
                return true;
            }
            catch (Exception ex)
            {
                _log($"{commandName} Error: " + ex.Message);
                return false;
            }
        }

        public async Task<(bool success, byte[] data)> ReadResponse(byte[] expectedPrefix, string commandName)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                await Task.Run(() => _log($"{commandName}: Serial port not open."));
                return (false, null);
            }

            try
            {
                byte[] buffer = new byte[256];
                int totalBytesRead = 0;

                await Task.Run(async () =>
                {
                    try
                    {
                        totalBytesRead = _serialPort.Read(buffer, 0, buffer.Length);
                        await Task.Delay(50); // Wait for buffered data
                        if (_serialPort.BytesToRead > 0)
                        {
                            totalBytesRead += _serialPort.Read(buffer, totalBytesRead, buffer.Length - totalBytesRead);
                        }
                    }
                    catch (TimeoutException)
                    {
                        await Task.Run(() => _log($"{commandName}: Read timeout occurred."));
                    }
                });

                if (totalBytesRead == 0) return (false, null);

                byte[] response = buffer.Take(totalBytesRead).ToArray();
                await Task.Run(() => _log($"{commandName} Received: " + BitConverter.ToString(response)));

                bool success = response.Length >= expectedPrefix.Length &&
                               response.Take(expectedPrefix.Length).SequenceEqual(expectedPrefix);

                if (success)
                {
                    _serialPort.Write(ackFrame, 0, ackFrame.Length);
                    await Task.Run(() => _log($"{commandName} Sent ACK: " + BitConverter.ToString(ackFrame)));
                }

                return (success, response);
            }
            catch (Exception ex)
            {
                await Task.Run(() => _log($"{commandName} Error: " + ex.Message));
                return (false, null);
            }
        }

        public byte[] BuildFrame(byte[] commandData)
        {
            byte[] preamble = new byte[] { 0x00, 0x00, 0xFF };
            byte[] tfiAndData = hostToPn532.Concat(commandData).ToArray();
            byte len = (byte)(tfiAndData.Length + 1); // +1 for DCS
            byte lcs = (byte)(0x100 - len);
            byte dcs = (byte)(0xFF - tfiAndData.Sum(b => b) + 1); // Two's complement
            return preamble.Concat(new byte[] { len, lcs }).Concat(tfiAndData).Concat(new byte[] { dcs, 0x00 }).ToArray();
        }

        public byte[] BuildCustomSamFrame()
        {
            return BuildFrame(cmdSamConfiguration.Concat(new byte[] { 0x01, 0x14, 0x01 }).ToArray());
            // Results in: 00-00-FF-06-FA-D4-14-01-14-01-02-00
        }
    }
}