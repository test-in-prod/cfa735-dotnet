using System;
using System.Threading;

namespace Crypton.CrystalFontz
{
    /// <summary>
    /// Provides marquee functionality for text
    /// </summary>
    public sealed class Marquee : IDisposable
    {
        private readonly CF735 driver;

        private Timer timer = null;
        private string text = null;
        private int offset = 0;
        private bool running = false;

        public Marquee(CF735 driver)
        {
            this.driver = driver;
        }

        public byte Row
        {
            get;
            set;
        } = 0;

        public string Text
        {
            get;
            set;
        }

        public bool Enabled
        {
            get { return timer != null; }
            set
            {
                if (value)
                {
                    text = Text;
                    offset = 0;
                    timer = new Timer(marqueeScroll, null, 0, 350);
                }
                else
                {
                    timer?.Dispose();
                    timer = null;
                }
            }
        }

        private void marqueeScroll(object state)
        {
            if (running) return;
            running = true;
            string display = text.Substring(offset, text.Length - offset);
            if (display.Length < 19)
            {
                display += " " + text.Substring(0, Math.Min(text.Length, 20 - display.Length));
            }
            else
            {
                display = display.PadRight(20, ' ');
            }
            driver.SendData(Row, 0, display.PadRight(20, ' '));
            offset++;
            if (offset > text.Length) offset = 0;
            running = false;
        }

        public void Dispose()
        {
            timer?.Dispose();
            timer = null;
        }
    }
}
