using Microsoft.Extensions.Logging;
using SIP2Agent.UserAgentService.Integration.LibRTIC;
using SIP2Agent.UserAgentService.Service;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class SIPEndpointCallAgentSelectionTests
{
    [Fact]
    public async Task MissingLibRticPaths_SelectsPlayback()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace));
        await using var endpoint = new SIPEndpointService(new SIPEndpointConfig(), loggerFactory);

        ICallAgent agent = endpoint.CreateCallAgent();
        try
        {
            Assert.IsType<FilePlaybackCallAgent>(agent);
        }
        finally
        {
            await agent.StopAsync("test cleanup", TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidLibRticConfiguration_SelectsRealtime(bool includeSessionConfiguration)
    {
        using var files = new TemporaryConfigurationFiles();
        files.WriteApi(ValidApiConfiguration);
        string? sessionPath = null;
        if (includeSessionConfiguration)
        {
            sessionPath = files.WriteSession("instructions: test session");
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace));
        await using var endpoint = new SIPEndpointService(
            new SIPEndpointConfig
            {
                LibRTICApiConfigPath = files.ApiPath,
                LibRTICSessionConfigPath = sessionPath,
            },
            loggerFactory);

        ICallAgent agent = endpoint.CreateCallAgent();
        try
        {
            Assert.IsType<LibRTICCallAgent>(agent);
        }
        finally
        {
            await agent.StopAsync("test cleanup", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ValidLibRticWithAnswerAudio_SelectsRealtimeAndWarnsThatAudioIsIgnored()
    {
        using var files = new TemporaryConfigurationFiles();
        files.WriteApi(ValidApiConfiguration);
        File.WriteAllBytes(files.AnswerAudioPath, new byte[160]);
        using var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var endpoint = new SIPEndpointService(
            new SIPEndpointConfig
            {
                LibRTICApiConfigPath = files.ApiPath,
                AnswerAudioFile = files.AnswerAudioPath,
            },
            loggerFactory);

        ICallAgent agent = endpoint.CreateCallAgent();
        try
        {
            Assert.IsType<LibRTICCallAgent>(agent);
            Assert.Contains(logs.Messages, message => message.Contains("answer audio file is ignored", StringComparison.Ordinal));
        }
        finally
        {
            await agent.StopAsync("test cleanup", TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLibRticConfiguration_SelectsPlaybackAndLogsSafeFallback(bool invalidSessionConfiguration)
    {
        using var files = new TemporaryConfigurationFiles();
        files.WriteApi(invalidSessionConfiguration ? ValidApiConfiguration : "provider: invalid");
        string? sessionPath = invalidSessionConfiguration
            ? files.WriteSession("max_output_tokens: 0")
            : null;
        using var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var endpoint = new SIPEndpointService(
            new SIPEndpointConfig
            {
                LibRTICApiConfigPath = files.ApiPath,
                LibRTICSessionConfigPath = sessionPath,
            },
            loggerFactory);

        ICallAgent agent = endpoint.CreateCallAgent();
        try
        {
            Assert.IsType<FilePlaybackCallAgent>(agent);
            Assert.Contains(logs.Messages, message => message.Contains("configuration diagnostic", StringComparison.Ordinal));
            Assert.Contains(logs.Messages, message => message.Contains("file-playback fallback", StringComparison.Ordinal));
        }
        finally
        {
            await agent.StopAsync("test cleanup", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PlaybackFallback_UsesConfiguredAnswerAudioFile()
    {
        using var files = new TemporaryConfigurationFiles();
        files.WriteApi("provider: invalid");
        File.WriteAllBytes(files.AnswerAudioPath, new byte[160]);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace));
        await using var endpoint = new SIPEndpointService(
            new SIPEndpointConfig
            {
                LibRTICApiConfigPath = files.ApiPath,
                AnswerAudioFile = files.AnswerAudioPath,
            },
            loggerFactory);

        ICallAgent agent = endpoint.CreateCallAgent();
        try
        {
            Assert.IsType<FilePlaybackCallAgent>(agent);
        }
        finally
        {
            await agent.StopAsync("test cleanup", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RealtimeFactory_CreatesDistinctPerCallAgents()
    {
        using var files = new TemporaryConfigurationFiles();
        files.WriteApi(ValidApiConfiguration);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace));
        await using var endpoint = new SIPEndpointService(
            new SIPEndpointConfig { LibRTICApiConfigPath = files.ApiPath },
            loggerFactory);

        ICallAgent first = endpoint.CreateCallAgent();
        ICallAgent second = endpoint.CreateCallAgent();
        try
        {
            Assert.IsType<LibRTICCallAgent>(first);
            Assert.IsType<LibRTICCallAgent>(second);
            Assert.NotSame(first, second);
        }
        finally
        {
            await first.StopAsync("test cleanup", TestContext.Current.CancellationToken);
            await second.StopAsync("test cleanup", TestContext.Current.CancellationToken);
        }
    }

    private const string ValidApiConfiguration = """
        provider:
          type: openai
          authentication:
            type: api_key
            api_key: test-key
        """;

    private sealed class TemporaryConfigurationFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryConfigurationFiles() => Directory.CreateDirectory(_directory);

        public string ApiPath => Path.Combine(_directory, "api.yaml");

        public string AnswerAudioPath => Path.Combine(_directory, "answer.raw");

        public void WriteApi(string content) => File.WriteAllText(ApiPath, content);

        public string WriteSession(string content)
        {
            string path = Path.Combine(_directory, "session.yaml");
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose() { }
    }

    private sealed class RecordingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => messages.Add(formatter(state, exception));
    }
}
