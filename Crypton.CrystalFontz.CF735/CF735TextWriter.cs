using System.IO;
using System.Text;

namespace Crypton.CrystalFontz
{
    /// <summary>
    /// Provides convenient methods to write to the LCD with text options
    /// </summary>
    /// <remarks>
    /// First usage of this class will reset cursor position to top-left.
    /// Carriage returns (\r) are ignored, newline characters will shift cursor to
    /// next line and first column position.
    /// </remarks>
    public class CF735TextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.ASCII;

        private readonly CF735 driver;
        private byte
            row = 0,
            column = 0;
        public CF735TextWriter(CF735 driver)
        {
            this.driver = driver;
            driver.SetCursorPosition(0, 0);
        }

        public override void Write(char value)
        {
            switch (value)
            {
                case '\r':
                    return; // ignore carriage return
                case '\n':
                    row++;
                    column = 0;
                    break;
                default:
                    driver.SendData(row, column++, value.ToString());
                    break;
            }
            if (column == 20) { column = 0; row++; }
            if (row == 4) row = 0;
            driver.SetCursorPosition(row, column);
        }

    }
}
