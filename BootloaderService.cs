using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace STM32Bootloader.Services
{
    public class BootloaderService
    {
        // Commands  
        private const byte CMD_GO = 0x55; // Jump to Application
        private const byte CMD_JUMP_TO_BOOTLOADER = 0x54; // Jump to Bootloader
        private const byte CMD_ERASE_APP = 0x56;
        private const byte CMD_WRITE_MEM = 0x57;
        private const byte CMD_READ_MEM = 0x59;
        private const byte ACK = 0x06;
        private const byte NACK = 0x15;

        // Configuration
        private const int CHUNK_SIZE = 128;
        private const int BYTE_TX_DELAY = 1;  // Balanced: safe for UART while faster than 3ms
        private const int CHUNK_DELAY = 5;    // Adequate for flash write completion
        private const int RETRY_COUNT = 3;
        private const int ERASE_TIMEOUT = 10000;
        private const int WRITE_TIMEOUT = 2000;

        // Flash Memory Map
        private const uint FLASH_START = 0x08000000;
        private const uint FLASH_END = 0x08020000;
        private const uint BOOTLOADER_SIZE = 0x8000;
        private const uint APP_START = FLASH_START + BOOTLOADER_SIZE;

        private SerialPort? _port;
        private readonly List<byte> _rxBuffer = new();

        public event EventHandler<string>? LogReceived;
        public event EventHandler<(int written, int total)>? ProgressChanged;

        public List<string> GetAvailablePorts()
        {
            return SerialPort.GetPortNames().ToList();
        }

        public bool Connect(string portName, int baudRate)
        {
            try
            {
                if (_port?.IsOpen == true)
                    _port.Close();

                _port = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000
                };

                _port.Open();
                _rxBuffer.Clear();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Disconnect()
        {
            if (_port?.IsOpen == true)
            {
                _port.Close();
                _port.Dispose();
                _port = null;
            }
        }

        public bool IsConnected => _port?.IsOpen == true;

        private byte? ReadOneByte(int timeout = 1000)
        {
            if (_port == null) return null;

            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeout)
            {
                if (_rxBuffer.Count > 0)
                {
                    var b = _rxBuffer[0];
                    _rxBuffer.RemoveAt(0);
                    return b;
                }

                try 
                {
                    var available = _port.BytesToRead;
                    if (available > 0)
                    {
                        var buffer = new byte[1];
                        _port.Read(buffer, 0, 1);
                        
                        if (buffer[0] == 0x5B) // '['
                        {
                            // Attempt to read "LOG] " (5 bytes) with a small timeout
                            var prefixBuffer = new List<byte>();
                            var prefixStart = DateTime.Now;
                            
                            while (prefixBuffer.Count < 5 && (DateTime.Now - prefixStart).TotalMilliseconds < 50)
                            {
                                if (_port.BytesToRead > 0)
                                {
                                    var b = new byte[1];
                                    _port.Read(b, 0, 1);
                                    prefixBuffer.Add(b[0]);
                                }
                                else
                                {
                                    Thread.Sleep(1);
                                }
                            }

                            if (prefixBuffer.Count == 5 && System.Text.Encoding.ASCII.GetString(prefixBuffer.ToArray()) == "LOG] ")
                            {
                                // It is a log! Read the rest of the line
                                try 
                                {
                                    var line = _port.ReadLine();
                                    LogReceived?.Invoke(this, line.Trim());
                                    continue; // Loop back to get next byte
                                }
                                catch
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                // Not a log. Push everything we read into the buffer
                                _rxBuffer.Add(buffer[0]);
                                _rxBuffer.AddRange(prefixBuffer);
                                
                                var b = _rxBuffer[0];
                                _rxBuffer.RemoveAt(0);
                                return b;
                            }
                        }
                        else
                        {
                            return buffer[0];
                        }
                    }
                }
                catch
                {
                    // Ignore port errors during read attempt
                }

                Thread.Sleep(5);
            }

            return null; // Timeout
        }

        private byte[] ReadBytes(int count, int timeout = 1000)
        {
            var result = new List<byte>();
            var startTime = DateTime.Now;

            while (result.Count < count)
            {
                var remaining = timeout - (int)(DateTime.Now - startTime).TotalMilliseconds;
                if (remaining <= 0) break;

                var b = ReadOneByte(remaining);
                if (b == null) break;
                
                result.Add(b.Value);
            }

            return result.ToArray();
        }

        public async Task<bool> EraseAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (_port == null) return false;

                    _port.DiscardInBuffer();
                    _rxBuffer.Clear();

                    // Send erase command
                    _port.Write(new byte[] { CMD_ERASE_APP }, 0, 1);

                    var resp = ReadBytes(1, ERASE_TIMEOUT);
                    return resp.Length > 0 && resp[0] == ACK;
                }
                catch (Exception ex)
                {
                    // Port disconnected or communication error
                    try
                    {
                        if (_port?.IsOpen == true)
                        {
                            _port.Close();
                            _port.Dispose();
                        }
                    }
                    catch { }
                    
                    _port = null;
                    return false;
                }
            });
        }

        public async Task<bool> WriteAsync(byte[] data)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (_port == null) return false;

                    // Pad to even length (Flash requires half-word alignment)
                    if (data.Length % 2 != 0)
                    {
                        var newData = new byte[data.Length + 1];
                        Array.Copy(data, newData, data.Length);
                        newData[data.Length] = 0xFF; // Padding
                        data = newData;
                    }

                    int totalWritten = 0;
                    var totalLen = data.Length;

                    for (int i = 0; i < totalLen; i += CHUNK_SIZE)
                    {
                        var chunkSize = Math.Min(CHUNK_SIZE, totalLen - i);
                        var chunk = new byte[chunkSize];
                        Array.Copy(data, i, chunk, 0, chunkSize);
                        var addr = APP_START + (uint)i;

                        bool success = false;

                        for (int attempt = 0; attempt < RETRY_COUNT; attempt++)
                        {
                            try
                            {
                                _port.DiscardInBuffer();
                                _rxBuffer.Clear();

                                // Send write command
                                _port.Write(new byte[] { CMD_WRITE_MEM }, 0, 1);
                                var resp = ReadBytes(1);
                                if (resp.Length == 0 || resp[0] != ACK)
                                {
                                    Thread.Sleep(100);
                                    continue;
                                }

                                // Send address (little-endian)
                                var addrBytes = BitConverter.GetBytes(addr);
                                _port.Write(addrBytes, 0, addrBytes.Length);

                                resp = ReadBytes(1);
                                if (resp.Length == 0 || resp[0] != ACK)
                                {
                                    Thread.Sleep(100);
                                    continue;
                                }

                                // Send length
                                _port.Write(new byte[] { (byte)chunk.Length }, 0, 1);
                                resp = ReadBytes(1);
                                if (resp.Length == 0 || resp[0] != ACK)
                                {
                                    Thread.Sleep(100);
                                    continue;
                                }

                                // Send data - byte by byte
                                // Restored paced writing to prevent buffer overflow on device
                                foreach (var b in chunk)
                                {
                                    _port.Write(new byte[] { b }, 0, 1);
                                    // No explicit sleep needed if loop overhead is enough, 
                                    // but we check for ACK at the end.
                                }

                                resp = ReadBytes(1, WRITE_TIMEOUT);
                                if (resp.Length > 0 && resp[0] == ACK)
                                {
                                    success = true;
                                    break;
                                }

                                Thread.Sleep(100);
                            }
                            catch
                            {
                                Thread.Sleep(500);
                            }
                        }

                        if (!success)
                            return false;

                        totalWritten += chunk.Length;
                        ProgressChanged?.Invoke(this, (totalWritten, totalLen));
                        
                        // Small delay to allow device to process
                        if (CHUNK_DELAY > 0) Thread.Sleep(CHUNK_DELAY);
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        public async Task<byte[]?> ReadMemoryAsync(uint address, int length)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (_port == null) return null;

                    _port.DiscardInBuffer();
                    _rxBuffer.Clear();

                    // Send read command
                    _port.Write(new byte[] { CMD_READ_MEM }, 0, 1);
                    var resp = ReadBytes(1);
                    if (resp.Length == 0 || resp[0] != ACK)
                        return null;

                    // Send address (little-endian) byte-by-byte
                    var addrBytes = BitConverter.GetBytes(address);
                    foreach (var b in addrBytes)
                    {
                        _port.Write(new byte[] { b }, 0, 1);
                        Thread.Sleep(BYTE_TX_DELAY);
                    }

                    resp = ReadBytes(1);
                    if (resp.Length == 0 || resp[0] != ACK)
                        return null;

                    // Send length
                    _port.Write(new byte[] { (byte)length }, 0, 1);
                    resp = ReadBytes(1);
                    if (resp.Length == 0 || resp[0] != ACK)
                        return null;

                    // Read data (no reversal - raw bytes from device)
                    var data = ReadBytes(length);
                    return data.Length == length ? data : null;
                }
                catch
                {
                    return null;
                }
            });
        }

        public void Jump()
        {
            if (_port != null && _port.IsOpen)
            {
                _port.Write(new byte[] { CMD_GO }, 0, 1);
            }
        }

        public void JumpToBootloader()
        {
            if (_port != null && _port.IsOpen)
            {
                _port.Write(new byte[] { CMD_JUMP_TO_BOOTLOADER }, 0, 1);
            }
        }

        public string? ReadAvailableData()
        {
            if (_port == null || !_port.IsOpen)
                return null;

            try
            {
                var bytesToRead = _port.BytesToRead;
                if (bytesToRead > 0)
                {
                    var buffer = new byte[bytesToRead];
                    _port.Read(buffer, 0, bytesToRead);
                    return System.Text.Encoding.UTF8.GetString(buffer);
                }
            }
            catch (Exception ex)
            {
                // Port has been disconnected, close it properly
                try
                {
                    if (_port?.IsOpen == true)
                    {
                        _port.Close();
                        _port.Dispose();
                    }
                }
                catch { }
                
                _port = null;
                throw; // Re-throw to notify caller
            }

            return null;
        }
    }
}
