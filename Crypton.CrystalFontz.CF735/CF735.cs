using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crypton.CrystalFontz
{

    public enum CursorStyles : byte
    {
        None = 0,
        BlinkingBlock = 1,
        Underscore = 2,
        BlinkingBlockUnderscore = 3,
        BlinkingUnderscore = 4
    }

    [Flags]
    public enum Keypad : byte
    {
        None = 0,
        Up = 0x01,
        Enter = 0x02,
        Cancel = 0x04,
        Left = 0x08,
        Right = 0x10,
        Down = 0x20
    }

    public enum KeypadEvent : byte
    {
        UpPress = 1,
        DownPress = 2,
        LeftPress = 3,
        RightPress = 4,
        EnterPress = 5,
        ExitPress = 6,
        UpRelease = 7,
        DownRelease = 8,
        LeftRelease = 9,
        RightRelease = 10,
        EnterRelease = 11,
        ExitRelease = 12
    }

    /// <summary>
    /// Provides methods for driving a CrystalFontz CFA-735 character serial display
    /// </summary>
    public sealed class CF735 : IDisposable
    {

        private readonly SerialPort devicePort; // disposed upstream
        private readonly List<CF735Packet> outPackets = new List<CF735Packet>();
        private readonly List<CF735Packet> inPackets = new List<CF735Packet>();
        private readonly AutoResetEvent sendPacket = new AutoResetEvent(false);
        private readonly AutoResetEvent waitForPacket = new AutoResetEvent(false);
        private readonly Thread sendThread = null;
        private readonly Thread recvThread = null;

        private bool disposedValue;
        private bool disposing = false;

        public CF735(SerialPort devicePort)
        {
            ReceiveTimeout = TimeSpan.FromSeconds(5);
            this.devicePort = devicePort;
            if (!devicePort.IsOpen)
            {
                throw new InvalidOperationException("Serial port is not open");
            }
            sendThread = new Thread(sendThreadImpl);
            recvThread = new Thread(recvThreadImpl);
            devicePort.DiscardInBuffer();
            devicePort.DiscardOutBuffer();
            sendThread.Start();
            recvThread.Start();
        }

        #region Properties
        /// <summary>
        /// Gets or sets anknowledgement packet timeout, before raising TimeoutException
        /// </summary>
        public TimeSpan ReceiveTimeout
        {
            get;
            set;
        }

        /// <summary>
        /// How many packets to keep in input buffer before overwriting earlier packets
        /// </summary>
        public int InBufferSize
        {
            get;
            set;
        } = 100;
        #endregion

        #region Events
        /// <summary>
        /// Fires whenever a key is pressed or released on the key pad
        /// </summary>
        public event EventHandler<KeypadEvent> KeypadKeyEvent;
        #endregion

        #region Send/Receive threads
        private void recvThreadImpl()
        {
            try
            {
                byte[] headerBuffer = new byte[2]; // header: command + data length
                bool headerRead = false;
                do
                {
                    if (!headerRead && devicePort.BytesToRead > 2)
                    {
                        try
                        {
                            devicePort.Read(headerBuffer, 0, 2);
                            headerRead = true;
                        }
                        catch (TimeoutException)
                        {
                            headerRead = false;
                        }
                        continue;
                    }

                    // do we have data + crc that matches the header?
                    if (headerRead && devicePort.BytesToRead >= headerBuffer[1] + 2)
                    {
                        byte dataLength = headerBuffer[1];
                        byte[] packetBytes = new byte[2 + headerBuffer[1] + 2];
                        Array.Copy(headerBuffer, packetBytes, 2);
                        try
                        {
                            devicePort.Read(packetBytes, 2, dataLength + 2);
                        }
                        catch (TimeoutException)
                        {
                            headerRead = false;
                            continue;
                        }

                        // valid packet?
                        CF735Packet packet;
                        try
                        {
                            packet = new CF735Packet(packetBytes);
                        }
                        catch (ArgumentException ex)
                        {
                            Debug.WriteLine($"[CF735.recvThreadImpl] reading packet: {ex.Message}");
                            continue;
                        }
                        finally
                        {
                            headerRead = false;
                        }

                        // special handling for Key Activity
                        if (packet.Command == 0x80 && packet.DataLength == 1)
                        {
                            KeypadEvent key = (KeypadEvent)packet.Data[0];
                            var handler = KeypadKeyEvent;
                            handler?.Invoke(this, key);
                            continue;
                        }

                        lock (inPackets)
                        {
                            if (inPackets.Count + 1 > InBufferSize)
                            {
                                inPackets.RemoveAt(0);
                                inPackets.Insert(0, packet);
                            }
                            else
                            {
                                inPackets.Add(packet);
                            }
                        }
                        waitForPacket.Set();
                    }
                    while (devicePort.BytesToRead == 0 && !disposing) Thread.Sleep(1);
                } while (!disposing);
            }
            catch (ThreadAbortException) { }
        }

        private void sendThreadImpl()
        {
            try
            {
                do
                {
                    lock (outPackets)
                    {
                        if (outPackets.Count > 0)
                        {
                            foreach (var packet in outPackets)
                            {
                                byte[] data = packet.GetBytes();
                                devicePort.Write(data, 0, data.Length);
                            }
                            outPackets.Clear();
                        }
                    }
                    sendPacket.WaitOne();
                } while (!disposing);
            }
            catch (ThreadAbortException) { }
        }
        #endregion

        #region Commands
        /// <summary>
        /// Sends a ping command to the display with a known payload and awaits for an exact response; returns true if data matched what was sent
        /// </summary>
        public bool Ping()
        {
            long testData = Environment.TickCount64;
            byte[] pingData = BitConverter.GetBytes(testData);
            SendPacket(new CF735Packet(0x00, pingData));
            var received = ExpectPacket(x => x.Command == 0x40);
            var receivedData = BitConverter.ToInt64(received.Data);
            return testData == receivedData;
        }

        /// <summary>
        /// Obtains hardware and firmware version
        /// </summary>
        /// <returns></returns>
        public string GetVersionInfo()
        {
            SendPacket(new CF735Packet(0x01, new byte[] { 0 }));
            var received = ExpectPacket(x => x.Command == 0x41 && x.DataLength == 16);
            return Encoding.ASCII.GetString(received.Data, 0, received.DataLength);
        }

        public string GetSerialNumber()
        {
            SendPacket(new CF735Packet(0x01, new byte[] { 1 }));
            var received = ExpectPacket(x => x.Command == 0x41 && x.DataLength >= 16);
            return Encoding.ASCII.GetString(received.Data, 0, received.DataLength);
        }

        public void Clear()
        {
            SendPacket(new CF735Packet(0x06, new byte[0]));
            ExpectPacket(x => x.Command == 0x46);
        }

        public void SetCursorStyle(CursorStyles style)
        {
            SendPacket(new CF735Packet(0x0C, new byte[] { (byte)style }));
            ExpectPacket(x => x.Command == 0x4c);
        }

        /// <summary>
        /// <para>Sets LCD Contrast</para>
        /// <para>0-65 = very light</para>
        /// <para>66 = light
        /// <para>85 = about right
        /// <para>125 = dark
        /// <para>126-254 = very dark(may be useful at cold temperatures)</para>
        /// </summary>
        /// <param name="value">Contrast value between 0 - 255</param>
        public void SetContrast(byte value)
        {
            SendPacket(new CF735Packet(0x0d, value));
            ExpectPacket(x => x.Command == 0x4d);
        }

        /// <summary>
        /// Sets display & keypad backlight brightness
        /// </summary>
        /// <param name="value">Value between 0 (off) and 100 (full brightness)</param>
        public void SetBacklight(byte value)
        {
            if (value > 100) throw new ArgumentOutOfRangeException(nameof(value));
            SendPacket(new CF735Packet(0x0e, value));
            ExpectPacket(x => x.Command == 0x4e);
        }

        /// <summary>
        /// Sets display & keypad backlight brightness individually
        /// </summary>
        /// <param name="lcd">Value between 0 (off) and 100 (full brightness)</param>
        /// <param name="keypad">Value between 0 (off) and 100 (full brightness)</param>
        public void SetBacklight(byte lcd, byte keypad)
        {
            if (lcd > 100) throw new ArgumentOutOfRangeException(nameof(lcd));
            if (keypad > 100) throw new ArgumentOutOfRangeException(nameof(keypad));
            SendPacket(new CF735Packet(0x0e, lcd, keypad));
            ExpectPacket(x => x.Command == 0x4e);
        }

        /// <summary>
        /// Changes cursor position
        /// </summary>
        /// <param name="row">Row number from 0 to 3</param>
        /// <param name="column">Column number from 0 to 19</param>
        public void SetCursorPosition(byte row, byte column)
        {
            if (row > 3) throw new ArgumentOutOfRangeException(nameof(row));
            if (column > 19) throw new ArgumentOutOfRangeException(nameof(column));
            SendPacket(new CF735Packet(0x0b, column, row));
            ExpectPacket(x => x.Command == 0x4b);
        }

        /// <summary>
        /// Returns keys that are currently pressed as a bit mask
        /// </summary>
        /// <returns></returns>
        public Keypad GetKeys()
        {
            SendPacket(new CF735Packet(0x18));
            var result = ExpectPacket(x => x.Command == 0x58);
            return (Keypad)result.Data[0];
        }

        /// <summary>
        /// Sends text data to display on the screen
        /// </summary>
        /// <param name="row">Row from 0 to 3</param>
        /// <param name="column">Column from 0 to 19</param>
        /// <param name="text">Text to write to the LCD, from 1 to 20 characters. If text is longer than 20 characters, only first 20 are sent</param>
        public void SendData(byte row, byte column, string text)
        {
            if (row > 3) throw new ArgumentOutOfRangeException(nameof(row));
            if (column > 19) throw new ArgumentOutOfRangeException(nameof(column));
            if (string.IsNullOrEmpty(text)) throw new ArgumentNullException(nameof(text));
            if (text.Length > 20) text = text.Substring(0, 20);
            byte[] data = new byte[2 + text.Length];
            data[0] = column;
            data[1] = row;
            byte[] textBytes = Encoding.ASCII.GetBytes(text);
            Array.Copy(textBytes, 0, data, 2, textBytes.Length);
            SendPacket(new CF735Packet(0x1f, data));
            ExpectPacket(x => x.Command == 0x5f);
        }

        /// <summary>
        /// Sets GPIO pin state
        /// </summary>
        /// <param name="index">Index of GPIO to modify, refer to datasheet "34 (0x22): Set or Set and Configure GPIO Pins" article for information</param>
        /// <param name="state">Pin output set state: 0=low, 1-99 duty cycle, 100=high, 101-255=invalid</param>
        public void SetGPIO(byte index, byte state)
        {
            if (index > 12) throw new ArgumentOutOfRangeException(nameof(index));
            if (state > 100) throw new ArgumentOutOfRangeException(nameof(state));
            SendPacket(new CF735Packet(0x22, index, state));
            ExpectPacket(x => x.Command == 0x62);
        }

        /// <summary>
        /// Sets the LED value for red and green intensity
        /// </summary>
        /// <param name="number">Number of LED with 0 on top and 3 bottom LED</param>
        /// <param name="red">0=off 100=fully on</param>
        /// <param name="green">0=off 100=fully on</param>
        public void SetLED(byte number, byte red, byte green)
        {
            if (number > 3) throw new ArgumentOutOfRangeException(nameof(number));
            if (red > 100) throw new ArgumentOutOfRangeException(nameof(red));
            if (green > 100) throw new ArgumentOutOfRangeException(nameof(green));
            switch (number)
            {
                case 0:
                    SetGPIO(12, red);
                    SetGPIO(11, green);
                    break;
                case 1:
                    SetGPIO(10, red);
                    SetGPIO(9, green);
                    break;
                case 2:
                    SetGPIO(8, red);
                    SetGPIO(7, green);
                    break;
                case 3:
                    SetGPIO(6, red);
                    SetGPIO(5, green);
                    break;
            }
        }

        /// <summary>
        /// Sets font definition of character in CGRAM
        /// </summary>
        /// <param name="index">Index of character (0-7)</param>
        /// <param name="bitmap">Bitmap of character (8 bytes, refer to datasheet)</param>
        public void SetCGRAM(byte index, byte[] bitmap)
        {
            if (index > 7) throw new ArgumentOutOfRangeException(nameof(index));
            if (bitmap.Length != 8) throw new ArgumentException("Bitmap must be an array of 8 bytes");
            byte[] data = new byte[9];
            data[0] = index;
            Array.Copy(bitmap, 0, data, 1, 8);
            SendPacket(new CF735Packet(0x09, data));
            ExpectPacket(x => x.Command == 0x49);
        }

        /// <summary>
        /// Stores current state of LCD module as its boot state
        /// </summary>
        /// <remarks>
        /// Refer to datasheet section "4 (0x04): Store Current State as Boot State" for more information
        /// </remarks>
        public void SaveState()
        {
            SendPacket(new CF735Packet(0x04));
            ExpectPacket(x => x.Command == 0x44);
        }

        /// <summary>
        /// Reads user flash area (16 bytes)
        /// </summary>
        /// <returns></returns>
        public byte[] ReadFlashArea()
        {
            SendPacket(new CF735Packet(0x03));
            var result = ExpectPacket(x => x.Command == 0x43);
            return result.Data;
        }

        /// <summary>
        /// Writes user flash area (16 bytes)
        /// </summary>
        /// <param name="value">Data to write, only up to first 16 bytes is written</param>
        public void WriteFlashArea(byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            int len = value.Length > 16 ? 16 : value.Length;
            byte[] data = new byte[16];
            Array.Copy(value, data, len);
            SendPacket(new CF735Packet(0x02, data));
            ExpectPacket(x => x.Command == 0x42);
        }

        #endregion

        #region Packet Helpers
        private CF735Packet ExpectPacket(Func<CF735Packet, bool> predicate)
        {
            do
            {
                lock (inPackets)
                {
                    var received = inPackets.FirstOrDefault(predicate);
                    if (received.Valid)
                    {
                        inPackets.Remove(received);
                        return received;
                    }
                }
                waitForPacket.WaitOne(ReceiveTimeout);
            } while (true);
        }

        private void SendPacket(CF735Packet packet)
        {
            lock (outPackets)
            {
                outPackets.Add(packet);
            }
            sendPacket.Set();
        }
        #endregion


        public void Dispose()
        {
            this.disposing = true;
            if (!disposedValue)
            {
                if (disposing)
                {
                    waitForPacket.Dispose();
                    sendPacket.Dispose();
                }

                outPackets.Clear();
                inPackets.Clear();

                disposedValue = true;
            }
            GC.SuppressFinalize(this);
        }
    }


    struct CF735Packet
    {
        public byte Command { get; private set; }
        public byte DataLength { get; private set; }
        public byte[] Data { get; private set; }
        public byte[] CRC { get; private set; }

        public bool Valid { get; private set; }

        public CF735Packet(byte command, params byte[] data)
        {
            if (data.Length > 255) throw new ArgumentException("Length of data exceeds 255 bytes");
            Command = command;
            DataLength = (byte)data.Length;
            Data = data;

            var crcpayload = new byte[2 + Data.Length]; // command + datalen + data
            crcpayload[0] = Command;
            crcpayload[1] = DataLength;
            Array.Copy(Data, 0, crcpayload, 2, Data.Length);
            var crc = CalculateCRC16(crcpayload);
            byte[] crcbytes = BitConverter.GetBytes(crc);
            CRC = crcbytes;
            Valid = true;
        }

        public CF735Packet(byte[] source)
        {
            if (!(source.Length >= 2)) throw new ArgumentException("Invalid packet (should be at least 2 bytes long)");
            Command = source[0];
            DataLength = source[1];

            byte[] data = new byte[DataLength];
            if (!(source.Length == (2 + DataLength + 2))) throw new ArgumentException("Invalid packet (length of data specified doesn't match length of received + 2 bytes for CRC");

            Array.Copy(source, 2, data, 0, DataLength);
            Data = data;

            var crcpayload = new byte[2 + DataLength]; // command + datalen + data
            crcpayload[0] = Command;
            crcpayload[1] = DataLength;
            Array.Copy(data, 0, crcpayload, 2, data.Length);
            var crc = CalculateCRC16(crcpayload);
            byte[] crcbytes = BitConverter.GetBytes(crc);

            byte[] recvcrc = new byte[2];
            Array.Copy(source, 2 + DataLength, recvcrc, 0, 2);

            // compare CRC with CRC received
            for (int i = 0; i < crcbytes.Length; i++)
            {
                byte left = recvcrc[i];
                byte right = crcbytes[i];
                if (left != right) throw new ArgumentException("Invalid packet (CRC mismatch)");
            }

            CRC = crcbytes;
            Valid = true;
        }

        public byte[] GetBytes()
        {
            byte[] data = new byte[2 + DataLength + 2];
            data[0] = Command;
            data[1] = DataLength;
            Array.Copy(Data, 0, data, 2, DataLength);
            Array.Copy(CRC, 0, data, 2 + DataLength, 2);
            return data;
        }

        static ushort[] CRCLUT = new ushort[]
        {
            0x0000,0x1189,0x2312,0x329B,0x4624,0x57AD,0x6536,0x74BF,
            0x8C48,0x9DC1,0xAF5A,0xBED3,0xCA6C,0xDBE5,0xE97E,0xF8F7,
            0x1081,0x0108,0x3393,0x221A,0x56A5,0x472C,0x75B7,0x643E,
            0x9CC9,0x8D40,0xBFDB,0xAE52,0xDAED,0xCB64,0xF9FF,0xE876,
            0x2102,0x308B,0x0210,0x1399,0x6726,0x76AF,0x4434,0x55BD,
            0xAD4A,0xBCC3,0x8E58,0x9FD1,0xEB6E,0xFAE7,0xC87C,0xD9F5,
            0x3183,0x200A,0x1291,0x0318,0x77A7,0x662E,0x54B5,0x453C,
            0xBDCB,0xAC42,0x9ED9,0x8F50,0xFBEF,0xEA66,0xD8FD,0xC974,
            0x4204,0x538D,0x6116,0x709F,0x0420,0x15A9,0x2732,0x36BB,
            0xCE4C,0xDFC5,0xED5E,0xFCD7,0x8868,0x99E1,0xAB7A,0xBAF3,
            0x5285,0x430C,0x7197,0x601E,0x14A1,0x0528,0x37B3,0x263A,
            0xDECD,0xCF44,0xFDDF,0xEC56,0x98E9,0x8960,0xBBFB,0xAA72,
            0x6306,0x728F,0x4014,0x519D,0x2522,0x34AB,0x0630,0x17B9,
            0xEF4E,0xFEC7,0xCC5C,0xDDD5,0xA96A,0xB8E3,0x8A78,0x9BF1,
            0x7387,0x620E,0x5095,0x411C,0x35A3,0x242A,0x16B1,0x0738,
            0xFFCF,0xEE46,0xDCDD,0xCD54,0xB9EB,0xA862,0x9AF9,0x8B70,
            0x8408,0x9581,0xA71A,0xB693,0xC22C,0xD3A5,0xE13E,0xF0B7,
            0x0840,0x19C9,0x2B52,0x3ADB,0x4E64,0x5FED,0x6D76,0x7CFF,
            0x9489,0x8500,0xB79B,0xA612,0xD2AD,0xC324,0xF1BF,0xE036,
            0x18C1,0x0948,0x3BD3,0x2A5A,0x5EE5,0x4F6C,0x7DF7,0x6C7E,
            0xA50A,0xB483,0x8618,0x9791,0xE32E,0xF2A7,0xC03C,0xD1B5,
            0x2942,0x38CB,0x0A50,0x1BD9,0x6F66,0x7EEF,0x4C74,0x5DFD,
            0xB58B,0xA402,0x9699,0x8710,0xF3AF,0xE226,0xD0BD,0xC134,
            0x39C3,0x284A,0x1AD1,0x0B58,0x7FE7,0x6E6E,0x5CF5,0x4D7C,
            0xC60C,0xD785,0xE51E,0xF497,0x8028,0x91A1,0xA33A,0xB2B3,
            0x4A44,0x5BCD,0x6956,0x78DF,0x0C60,0x1DE9,0x2F72,0x3EFB,
            0xD68D,0xC704,0xF59F,0xE416,0x90A9,0x8120,0xB3BB,0xA232,
            0x5AC5,0x4B4C,0x79D7,0x685E,0x1CE1,0x0D68,0x3FF3,0x2E7A,
            0xE70E,0xF687,0xC41C,0xD595,0xA12A,0xB0A3,0x8238,0x93B1,
            0x6B46,0x7ACF,0x4854,0x59DD,0x2D62,0x3CEB,0x0E70,0x1FF9,
            0xF78F,0xE606,0xD49D,0xC514,0xB1AB,0xA022,0x92B9,0x8330,
            0x7BC7,0x6A4E,0x58D5,0x495C,0x3DE3,0x2C6A,0x1EF1,0x0F78
        };

        public static ushort CalculateCRC16(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            ushort newCrc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                ushort lookup = CRCLUT[(newCrc ^ data[i]) & 0xFF];
                newCrc = (ushort)((newCrc >> 8) ^ lookup);
            }

            return (ushort)(~newCrc);
        }

    }

}
