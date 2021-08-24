using Crypton.CrystalFontz;
using System;
using System.IO.Ports;
using System.Threading;

namespace HelloWorld
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var port = new SerialPort("COM15", 115200, Parity.None, 8, StopBits.One))
            {
                port.Open();
                using (var cf = new CF735(port))
                {
                    cf.Clear();
                    cf.SetCursorStyle(CursorStyles.None);
                    cf.SetBacklight(75, 25);
                    cf.KeypadKeyEvent += Cf_KeypadKeyEvent;

                    cf.SendData(1, 0, "Hello, world");
                    cf.SetLED(0, 100, 0);
                    cf.SetLED(1, 100, 100);
                    cf.SetLED(2, 0, 100);
                    cf.SetLED(3, 50, 50);

                    var marquee = new Marquee(cf);
                    marquee.Text = "The quick brown fox jumped over the lazy dog";
                    marquee.Row = 0;
                    marquee.Enabled = true;

                    Thread.Sleep(Timeout.Infinite);
                }
            }
        }

        private static void Cf_KeypadKeyEvent(object sender, KeypadEvent e)
        {
            Console.WriteLine($"Buttons!: {e}");
        }
    }
}
