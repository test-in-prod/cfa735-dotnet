# CrystalFontz CFA-735 .NET Driver

This is a simple class that can be used to drive a [CFA-735](https://www.crystalfontz.com/c/usb-lcd-displays/character/5) USB Serial character display made by CrystalFontz.

The display features 4 lines of 20 characters each, a keypad with buttons, and 4 bi-color LEDs.

![](docs/cfa735.webp)

Not all of the command set is implemented (FBSCAB commands, fan info, etc.) but most of the necessary commands for
displaying text, reading keypad (event-driven or polled), LEDs, is implemented.

-----

# Usage

Compile using `dotnet` commands for your platform, then import into your project.
HelloWorld directory contains sample usage console application.

```csharp
using (var port = new SerialPort("COM15", 115200, Parity.None, 8, StopBits.One))
{
    port.Open();
    using (var cf = new CF735(port))
    {
        cf.Clear(); // clear display
        cf.SetCursorStyle(CursorStyles.None); // set cursor style
        cf.SetBacklight(75, 25); // set backlight/keypad brightness
        cf.KeypadKeyEvent += Cf_KeypadKeyEvent; // subscribe to key press events

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
```

## TextWriter

There is a simple TextWriter implementation that allows displaying large amount of text, for example using as very simple console/logging output.

## Marquee

Allows text scrolling right to left across one of the rows.

