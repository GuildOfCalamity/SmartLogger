using Guildsoft;

namespace SampleApp;

public class Program
{
    static ISmartLogger _logger;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("✔️ Creating SmartLogger (with memory of 10 seconds) …");

        // Configure the logger with no file name, allowing it to generate a path and name based on the current date/time.
        _logger = new SmartLogger("", "hh:mm:ss.fff tt", 10, TimeSpan.FromSeconds(10));

        #region [Event Handlers]
        _logger.WriteFailure += (msg, ex) =>
        {
            Console.WriteLine($"🚨 Failed during write '{msg}'");
            Console.WriteLine($"🚨 Exception message: {ex.Message}");
        };
        #endregion

        #region [Asynchronous Write Test]
        _logger.Write($"Starting duplicate write test…");
        for (int i = 1; i < 51; i++)
        {
            await Task.Delay(250); // Simulate some delay
            Console.WriteLine($"🔔 Writing duplicate message #{i}");
            await _logger.WriteAsync($"This is a test message for duplicate checking.");
        }
        #endregion

        #region [Deferred Write Test]
        _logger.WriteDeferred($"Starting deferred write test…");
        for (int i = 1; i < 51; i++)
        {
            Thread.Sleep(250); // Simulate some delay
            Console.WriteLine($"🔔 Writing deferred message #{i}");
            _logger.WriteDeferred($"This is a test message for deferred writing.");
        }
        #endregion

        _logger.Write($"Logging tests completed.");

        Console.WriteLine($"✔️ Log found here ⇨ {_logger.GetLogName()}");
        Console.WriteLine($"✔️ Per each test you should see two \"test message\" entries only, based on the TimeSpan setting of this demo.");

        Console.WriteLine($"✏️ Press any key to dispose of the logger and close the app.");
        var key = Console.ReadKey(true).Key;
        _logger?.Dispose();

        Console.WriteLine("🔔 Logger disposed. Exiting…");
        Thread.Sleep(1500);
    }
}
