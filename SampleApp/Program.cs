using System.Diagnostics;
using System.Xml.Linq;

namespace SampleApp;

public class Program
{
    static SmartLogger.ISmartLogger? _logger = null;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("✔️ Creating SmartLogger (with memory of 10 seconds) …");

        _logger = new SmartLogger.SmartLogger("", "hh:mm:ss.fff tt", 10, TimeSpan.FromSeconds(10));
        if (_logger is null)
        {
            Console.WriteLine("🔔 Failed to create the logger, abandoning test…");
            Thread.Sleep(2000);
            return;
        }

        #region [Event Handlers]
        _logger.WriteFailure += (msg, ex) =>
        {
            Console.WriteLine($"🚨 Failed during write '{msg}'");
            Console.WriteLine($"🚨 Exception message: {ex.Message}");
        };
        #endregion

        await _logger.WriteAsync($"Starting duplicate write tests…");
        for (int i = 1; i < 51; i++)
        {
            Console.WriteLine($"🔔 Writing duplicate message #{i}");
            await _logger.WriteAsync($"This is a test message for duplicate checking.");
            await Task.Delay(250); // Simulate some delay
        }
        _logger.Write($"Logging test complete.");

        Console.WriteLine($"✔️ Log found here ⇨ {_logger.GetLogName()}");
        Console.WriteLine($"✔️ You should see two \"test message\" entries only, based on the TimeSpan setting of this demo.");

        Console.WriteLine($"✏️ Press any key to dispose of the logger and close the app.");
        var key = Console.ReadKey(true).Key;
        _logger?.Dispose();

        Console.WriteLine("🔔 Logger disposed. Exiting…");
        Thread.Sleep(1500);
    }
}
