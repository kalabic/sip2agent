using Microsoft.Extensions.Logging;
using DotBase.Log;

namespace MiniRTICallServer.RTISorcery;

internal sealed class MicrosoftInfoLog : InfoLog
{
    private readonly ILogger _logger;

    public MicrosoftInfoLog(ILogger logger)
    {
        _logger = logger;
    }

    public void Critical(string message) => _logger.LogCritical(message);

    public void Critical(string message, Exception ex) => _logger.LogCritical(ex, message);

    public void Error(string message) => _logger.LogError(message);

    public void Error(string message, Exception ex) => _logger.LogError(ex, message);

    public void Event(string eventType, string message) =>
        _logger.LogDebug("{EventType}: {Message}", eventType, message);

    public void Event(string eventType, string message, object obj) =>
        _logger.LogDebug("{EventType}: {Message}; {ObjectType}", eventType, message, obj?.GetType().ToString() ?? "<null object>");

    public void Info(string message) => _logger.LogInformation(message);

    public void Info(string message, Exception ex) => _logger.LogInformation(ex, message);

    public void Notice(string message) => _logger.LogInformation(message);

    public void Notice(string message, Exception ex) => _logger.LogInformation(ex, message);

    public void Warning(string message) => _logger.LogWarning(message);

    public void Warning(string message, Exception ex) => _logger.LogWarning(ex, message);
}
