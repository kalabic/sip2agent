using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace SIP2Agent.UserAgentService.Service;

internal sealed class FilePlaybackCallAgent : ICallAgent
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly AudioEncoder _audioEncoder;
    private readonly AudioExtrasSource _audioSource;
    private readonly FileStream? _fileStream;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _startTask;
    private Task? _playbackObserverTask;
    private Task? _stopTask;

    internal FilePlaybackCallAgent(
        ILogger logger,
        string? resolvedAudioFile,
        bool acceptRtpFromAny,
        PortRange? rtpPortRange = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _audioEncoder = new AudioEncoder();
        _audioSource = new AudioExtrasSource(
            _audioEncoder,
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        ((IAudioSource)_audioSource).RestrictFormats(
            format => format.Codec is
                AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA or AudioCodecsEnum.G722);
        MediaSession = new VoIPMediaSession(new VoIPMediaSessionConfig
        {
            MediaEndPoint = new MediaEndPoints { AudioSource = _audioSource },
            RtpPortRange = rtpPortRange,
        })
        {
            AcceptRtpFromAny = acceptRtpFromAny,
        };
        MediaSession.OnAudioFormatsNegotiated += AudioFormatsNegotiated;

        if (!string.IsNullOrWhiteSpace(resolvedAudioFile))
        {
            _fileStream = new FileStream(
                resolvedAudioFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
    }

    public VoIPMediaSession MediaSession { get; }

    public Task Completion => _completion.Task;

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                throw new InvalidOperationException("File playback is stopping.");
            }

            _startTask ??= StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    public Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync(reason);
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    private Task StartCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fileStream is null)
        {
            return Task.CompletedTask;
        }

        Task playbackTask = _audioSource.SendAudioFromStream(
            _fileStream,
            AudioSamplingRatesEnum.Rate8KHz);
        lock (_gate)
        {
            _playbackObserverTask ??= ObservePlaybackAsync(playbackTask);
        }

        return Task.CompletedTask;
    }

    private async Task ObservePlaybackAsync(Task playbackTask)
    {
        try
        {
            await playbackTask.ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            try
            {
                _audioSource.Close();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to close the file-playback audio source.");
            }
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        try
        {
            _audioSource.CancelSendAudioFromStream();
            _audioSource.Close();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop answer-file playback.");
        }

        Task? playbackObserver;
        lock (_gate)
        {
            playbackObserver = _playbackObserverTask;
        }
        if (playbackObserver is not null)
        {
            try
            {
                await playbackObserver.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Answer-file playback stopped with an error.");
            }
        }

        MediaSession.OnAudioFormatsNegotiated -= AudioFormatsNegotiated;
        try
        {
            MediaSession.Close(reason);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to close file-playback media.");
        }
        MediaSession.Dispose();
        _audioEncoder.Dispose();
        _fileStream?.Dispose();
        _completion.TrySetResult();
    }

    private void AudioFormatsNegotiated(List<AudioFormat> formats)
    {
        if (formats.Count > 0)
        {
            AudioFormat format = formats[0];
            _logger.LogTrace(
                "File audio format negotiated as {FormatID}:{Codec} {ClockRate} (RTP clock {RtpClockRate}).",
                format.FormatID,
                format.Codec,
                format.ClockRate,
                format.RtpClockRate);
        }
    }
}
