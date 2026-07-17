using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService.Service;

internal static class InboundCallRunner
{
    internal static async Task RunAsync(IInboundCall call, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await call.PrepareAgentAsync().ConfigureAwait(false);
            call.CancellationToken.ThrowIfCancellationRequested();

            bool answered = await call.AnswerAsync().ConfigureAwait(false);
            if (!answered || !call.IsCallActive)
            {
                throw new InvalidOperationException("SIP call answer failed.");
            }

            logger.LogInformation(
                "Call {CallId} answered after the agent became ready.",
                call.CallId);

            await call.StartAgentAsync().ConfigureAwait(false);

            Task completed = await Task.WhenAny(call.Termination, call.AgentCompletion)
                .ConfigureAwait(false);
            if (completed == call.AgentCompletion)
            {
                await call.AgentCompletion.ConfigureAwait(false);
                logger.LogInformation("Agent completed for call {CallId}.", call.CallId);
            }
            else
            {
                CallTerminationReason reason = await call.Termination.ConfigureAwait(false);
                LogCallEnd(call.CallId, reason, logger);
            }
        }
        catch (OperationCanceledException) when (call.Termination.IsCompletedSuccessfully)
        {
            LogCallEnd(call.CallId, call.Termination.Result, logger);
        }
        catch (AgentPreparationException exception) when (!call.Answered)
        {
            logger.LogError(
                exception,
                "Agent preparation failed for call {CallId} with category {FailureKind}.",
                call.CallId,
                exception.FailureKind);

            if (!call.Termination.IsCompleted)
            {
                SIPResponseStatusCodesEnum status = exception.FailureKind ==
                    AgentPreparationFailureKind.Configuration
                        ? SIPResponseStatusCodesEnum.InternalServerError
                        : SIPResponseStatusCodesEnum.ServiceUnavailable;
                string reason = exception.FailureKind == AgentPreparationFailureKind.Configuration
                    ? "Internal Server Error"
                    : "Service Unavailable";
                TryReject(call, status, reason, logger);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Inbound call {CallId} failed.", call.CallId);
            if (!call.Answered && !call.Termination.IsCompleted)
            {
                TryReject(
                    call,
                    SIPResponseStatusCodesEnum.InternalServerError,
                    "Internal Server Error",
                    logger);
            }
        }
        finally
        {
            if (call.IsCallActive)
            {
                try
                {
                    call.Hangup();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to hang up call {CallId}.", call.CallId);
                }
            }

            string reason = call.Termination.IsCompletedSuccessfully
                ? $"Inbound call ended: {call.Termination.Result}."
                : "Inbound call ended because the agent completed.";
            await call.StopAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void TryReject(
        IInboundCall call,
        SIPResponseStatusCodesEnum status,
        string reason,
        ILogger logger)
    {
        try
        {
            call.Reject(status, reason);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to reject call {CallId} with SIP status {Status}.",
                call.CallId,
                status);
        }
    }

    private static void LogCallEnd(
        string callId,
        CallTerminationReason reason,
        ILogger logger)
    {
        switch (reason)
        {
            case CallTerminationReason.RemoteCancellation:
                logger.LogInformation("Inbound call {CallId} was cancelled before answer.", callId);
                break;
            case CallTerminationReason.MediaTimeout:
                logger.LogWarning("Call {CallId} ended because RTP timed out.", callId);
                break;
            case CallTerminationReason.RingTimeout:
                logger.LogWarning("Call {CallId} timed out waiting for ACK.", callId);
                break;
            default:
                logger.LogInformation("Call {CallId} ended: {TerminationReason}.", callId, reason);
                break;
        }
    }
}
