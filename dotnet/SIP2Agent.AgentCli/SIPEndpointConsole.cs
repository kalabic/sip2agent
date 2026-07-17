using Microsoft.Extensions.Logging;
using SIP2Agent.UserAgentService;
using SIP2Agent.UserAgentService.Service;

namespace SIP2Agent.AgentCli;

internal sealed class SIPEndpointConsole : IDisposable
{
    private readonly SIPEndpointService _endpoint;
    private readonly SIPEndpointConfig _config;
    private readonly ILogger<SIPEndpointConsole> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _disposed;

    public SIPEndpointConsole(SIPEndpointService endpoint, SIPEndpointConfig config, ILoggerFactory loggerFactory)
    {
        _endpoint = endpoint;
        _config = config;
        _logger = loggerFactory.CreateLogger<SIPEndpointConsole>();
    }

    public async Task RunAsync()
    {
        ThrowIfDisposed();

        _endpoint.Start();

        ConsoleCancelEventHandler sessionCanceler = (sender, e) =>
        {
            _shutdownCts.Cancel();
#if DEBUG
            System.Console.Write("[ Ctrl-C ]");
#endif
            e.Cancel = true;
        };
        System.Console.CancelKeyPress += sessionCanceler;

        try
        {
            if (_config.Headless)
            {
                _logger.LogInformation("Running in headless mode. Press Ctrl+C to exit.");
                await WaitForShutdownAsync().ConfigureAwait(false);
            }
            else
            {
                PrintInteractiveCommands();
                await RunInteractiveLoopAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            System.Console.CancelKeyPress -= sessionCanceler;
        }
    }

    private async Task RunInteractiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            char key = await ReadCommandKeyAsync(cancellationToken).ConfigureAwait(false);
            if (key == '\0')
            {
                return;
            }

            await HandleCommandKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
#if DEBUG
        System.Console.WriteLine("Async task 'RunInteractiveLoopAsync' exiting.");
#endif
    }

    private static async Task<char> ReadCommandKeyAsync(CancellationToken cancellationToken)
    {
        if (!System.Console.IsInputRedirected)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (System.Console.KeyAvailable)
                {
                    return System.Console.ReadKey(intercept: true).KeyChar;
                }

                try
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return '\0';
                }
            }

            return '\0';
        }

        char[] buffer = new char[1];
        Task<int> readTask = System.Console.In.ReadAsync(buffer, 0, 1);
        Task completedTask = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completedTask != readTask || readTask.Result == 0)
        {
            return '\0';
        }

        return buffer[0];
    }

    private async Task HandleCommandKeyAsync(char key, CancellationToken cancellationToken)
    {
        switch (key)
        {
            case 'l':
                _endpoint.ListCallsToLog();
                break;

            case 's':
                _endpoint.LogStatus();
                break;

            case 'c':
                _endpoint.Connect();
                break;

            case 'x':
                _endpoint.Disconnect();
                break;

            case 'h':
                _endpoint.HangupOldest();
                break;

            case 'H':
                _endpoint.HangupAll();
                break;

            case 'q':
            case '\x3':
                _shutdownCts.Cancel();
                break;
        }
    }

    private Task WaitForShutdownAsync()
    {
        return Task.Run(() =>
        {
            _shutdownCts.Token.WaitHandle.WaitOne();
#if DEBUG
            System.Console.WriteLine("Async task 'WaitForShutdownAsync' exiting.");
#endif
        });
    }

    private static void PrintInteractiveCommands()
    {
        System.Console.WriteLine("Press 'h' to hangup the oldest call.");
        System.Console.WriteLine("Press 'H' to hangup all calls.");
        System.Console.WriteLine("Press 'l' to list current calls.");
        System.Console.WriteLine("Press 's' to show registration state.");
        System.Console.WriteLine("Press 'c' to connect/reconnect registration.");
        System.Console.WriteLine("Press 'x' to unregister and stay disconnected.");
        System.Console.WriteLine("Press 'q' to quit.");
        System.Console.WriteLine("Press Ctrl+C to exit.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }
}
