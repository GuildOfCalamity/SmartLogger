using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Guildsoft;

public interface ISmartLogger
{
    /// <summary>
    /// Occurs when a write operation fails.
    /// </summary>
    /// <remarks>This event is triggered when an exception is encountered during a write operation.</remarks>
    event Action<string, Exception>? WriteFailure;

    /// <summary>
    /// Synchronously writes a log entry to the log file.
    /// </summary>
    /// <param name="message">the string to log</param>
    /// <param name="level">the indicated <see cref="LogLevel"/></param>
    /// <remarks>If <see cref="LogLevel.None"/> then file write is skipped and output will be to console only.</remarks>
    void Write(string message, LogLevel level = LogLevel.Info);

    /// <summary>
    /// Deferred file writing on another thread - this method will wait for the file to become available before writing.
    /// </summary>
    /// <param name="message">the string to log</param>
    /// <param name="level">the indicated <see cref="LogLevel"/></param>
    /// <param name="retries">the number of times to try and write, if the file is locked</param>
    /// <remarks>The order of writes is not guaranteed, as this is threaded and may experience other re-entry operations.</remarks>
    void WriteDeferred(string message, LogLevel level = LogLevel.Info, int retries = 100);

    /// <summary>
    /// Asynchronously writes a log entry to the log file.
    /// </summary>
    /// <param name="message">the string to log</param>
    /// <param name="level">the indicated <see cref="LogLevel"/></param>
    /// <remarks>If <see cref="LogLevel.None"/> then file write is skipped and output will be to console only.</remarks>
    Task WriteAsync(string message, LogLevel level = LogLevel.Info);

    /// <summary>
    /// Returns the current logging path.
    /// </summary>
    string GetLogPath();

    /// <summary>
    /// Returns the full log file path with file name.
    /// </summary>
    string GetLogName();

    /// <summary>
    /// Clears the log history, removing all stored log entries.
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// Clears any queue items and calls the GC.
    /// </summary>
    void Dispose();
}

public class SmartLogger : ISmartLogger, IDisposable
{
    #region [Members]
    bool _disposed = false;
    protected string _logFilePath;
    protected bool _usingStartDate = false;
    protected DateTime _startDate;
    readonly string _timeFormat;
    readonly Queue<LogEntry> _logHistory = new();
    readonly uint _maxHistory;
    readonly TimeSpan _timeWindow;
    readonly object _lockObj = new();
    public event Action<string, Exception>? WriteFailure;
    #endregion

    /// <summary>
    /// <para>
    ///   Initializes a new instance of the <see cref="SmartLogger"/>.
    /// </para>
    /// <para>
    ///   <see cref="SmartLogger"/> checks for duplicate log entries and only writes unique messages to the log file.
    /// </para>
    /// <para>
    ///   If duplicates are desired then set the <paramref name="maxHistory"/> to 0, or <paramref name="staleTime"/> to 0.
    /// </para>
    /// </summary>
    /// <param name="logFilePath">If no path is provided the local executing folder is used and a rotating date will be used for the file names.</param>
    /// <param name="timeFormat">The <see cref="DateTime"/> format to use during writes.</param>
    /// <param name="maxHistory">The maximum number of log entries to store.</param>
    /// <param name="staleTime">The time for log history validity. If null, then 30 minutes will be used as a default.</param>
    public SmartLogger(string logFilePath, string timeFormat = "yyyy-MM-dd hh:mm:ss.fff tt", uint maxHistory = 50, TimeSpan? staleTime = null)
    {
        if (string.IsNullOrEmpty(logFilePath))
        {
            _usingStartDate = true;
            _startDate = DateTime.Now.Date;
            _logFilePath = GenerateLogName();
        }
        else
        {
            _logFilePath = RemoveInvalidCharacters(logFilePath);
        }
        _timeFormat = string.IsNullOrEmpty(logFilePath) ? "yyyy-MM-dd hh:mm:ss.fff tt" : timeFormat;
        _maxHistory = maxHistory;
        _timeWindow = staleTime ?? TimeSpan.FromMinutes(30);
    }
    
    ~SmartLogger() => Dispose(false);

    #region [Public Methods]
    /// <inheritdoc cref="ISmartLogger.Write(string, LogLevel)"/>
    public void Write(string message, LogLevel level = LogLevel.Info)
    {
        WriteInternalAsync(message, level, sync: true).GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="ISmartLogger.WriteAsync(string, LogLevel)"/>
    public async Task WriteAsync(string message, LogLevel level = LogLevel.Info)
    {
        await WriteInternalAsync(message, level, sync: false).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ISmartLogger.WriteDeferred(string, LogLevel, int)"/>
    public void WriteDeferred(string message, LogLevel level = LogLevel.Info, int retries = 100)
    {
        Task.Run(async () =>
        {
            while (IsFileLocked(new FileInfo(_logFilePath)) && --retries > 0)
            {   // Wait for the file to be available
                await Task.Delay(10).ConfigureAwait(false);
            }
            await WriteInternalAsync(message, level, sync: false).ConfigureAwait(false);
        });
    }

    /// <inheritdoc cref="ISmartLogger.ClearHistory"/>
    public void ClearHistory()
    {
        lock (_lockObj)
        {
            _logHistory.Clear();
        }
    }

    /// <inheritdoc cref="ISmartLogger.GetLogPath"/>
    public string GetLogPath()
    {
        if (_usingStartDate)
        {
            return Path.Combine(System.AppContext.BaseDirectory, $@"Logs\{DateTime.Today.Year}\{DateTime.Today.Month.ToString("00")}-{DateTime.Today.ToString("MMMM")}");
        }
        else
        {
            return _logFilePath.Contains(Path.DirectorySeparatorChar) ? Path.GetDirectoryName(_logFilePath) ?? _logFilePath : _logFilePath;
        }
    }

    /// <inheritdoc cref="ISmartLogger.GetLogName"/>
    public string GetLogName()
    {
        if (_usingStartDate)
        {
            return Path.Combine(GetLogPath(), $@"{Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetEntryAssembly()?.Location)}_{DateTime.Now.ToString("dd")}.log");
        }
        else
        {
            //return _logFilePath.Contains(Path.DirectorySeparatorChar) ? Path.GetFileName(_logFilePath) : _logFilePath;
            return _logFilePath;
        }
    }

    /// <inheritdoc cref="ISmartLogger.Dispose"/>
    public void Dispose()
    {
        // Keep cleanup code in 'Dispose(bool disposing)' method
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    #endregion

    #region [Private Methods]
    async Task WriteInternalAsync(string message, LogLevel level, bool sync)
    {
        bool shouldLog = false;
        var now = DateTime.UtcNow;

        if (level == LogLevel.None)
        {
            Console.WriteLine($"[{DateTime.Now.ToString(_timeFormat)}] [{level}] {message}");
            return;
        }

        try
        {
            CheckFileRotation();

            lock (_lockObj)
            {
                while (_logHistory.Count > 0 &&
                      (now - _logHistory.Peek().TimeStamp > _timeWindow ||
                      _logHistory.Count > _maxHistory))
                {
                    _logHistory.Dequeue();
                }

                shouldLog = !_logHistory.Any(e => e.Message == message && e.Level == level);
                if (shouldLog)
                {
                    _logHistory.Enqueue(new LogEntry { Message = message, Level = level, TimeStamp = now });
                }
            }

            if (!shouldLog || _disposed)
                return;

            string formatted = $"[{DateTime.Now.ToString(_timeFormat)}] [{level}] {message}";

            if (sync)
            {
                //File.AppendAllText(_logFilePath, $"{formatted}{Environment.NewLine}");
                using (var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(formatted);
                    }
                }
            }
            else
            {
                using (var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    using (var writer = new StreamWriter(stream))
                    {
                        await writer.WriteLineAsync(formatted).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteFailure?.Invoke(message, ex);
            Debug.WriteLine($"[ERROR] During log write: {ex}");
        }
    }

    /// <summary>
    /// When allowing the <see cref="SmartLogger"/> to decide the log file 
    /// name for you, this determines if a name change is warranted.
    /// </summary>
    void CheckFileRotation()
    {
        if (_usingStartDate && DateTime.Now.Date != _startDate)
        {
            _startDate = DateTime.Now.Date;
            _logFilePath = GenerateLogName();
        }
    }

    /// <summary>
    /// Decides the log file name for the user.
    /// </summary>
    string GenerateLogName()
    {
        string result = "Application.log";
        try
        {
            if (!Directory.Exists(GetLogPath()))
                Directory.CreateDirectory(GetLogPath());

            result = GetLogName();
        }
        catch (Exception)
        {
            // On error, we'll attempt to determine the caller and use that as the log file's name.
            try
            {
                result = Path.Combine(Directory.GetCurrentDirectory(), $"{Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetEntryAssembly()?.Location)}.log");
            }
            catch (Exception)
            {
                result = Path.Combine(Directory.GetCurrentDirectory(), $"{Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetExecutingAssembly()?.Location)}.log");
            }
        }
        return result;
    }

    /// <summary>
    /// Determines if a file is being accessed by another thread.
    /// </summary>
    /// <param name="file"><see cref="FileInfo"/></param>
    /// <returns>true if file is in use, false otherwise</returns>
    bool IsFileLocked(FileInfo file)
    {
        FileStream? stream = null;
        try
        {
            if (!File.Exists(file.FullName))
                return false;

            stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) // still being written to or being accessed by another process 
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (stream != null)
            {
                stream.Close();
                stream = null;
            }
        }
        return false;
    }

    string RemoveInvalidCharacters(string path) => System.IO.Path.GetInvalidFileNameChars().Aggregate(path, (current, c) => current.Replace(c.ToString(), string.Empty));
    
    bool HasInvalidChars(string path) => (!string.IsNullOrEmpty(path) && path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
                _logHistory.Clear();

            _disposed = true;
        }
    }
    #endregion
}

/// <summary>
/// Enumerations for the level of logging.
/// </summary>
public enum LogLevel
{
    None = 0,
    Debug = 1 << 0,     // 2^0 (1)
    Verbose = 1 << 1,   // 2^2 (2)
    Info = 1 << 2,      // 2^2 (4)
    Warning = 1 << 3,   // 2^3 (8)
    Error = 1 << 4,     // 2^4 (16)
    Success = 1 << 5,   // 2^5 (32)
    Important = 1 << 6, // 2^6 (64)
}

internal class LogEntry
{
    public string Message { get; set; }
    public LogLevel Level { get; set; }
    public DateTime TimeStamp { get; set; }
}
