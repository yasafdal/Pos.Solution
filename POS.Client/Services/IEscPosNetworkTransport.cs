using System.Net.Sockets;
using POS.Client.Data;

namespace POS.Client.Services;

/// <summary>Thrown when opening a transport connection fails BEFORE any ESC/POS bytes were written.</summary>
public sealed class PrinterConnectException : IOException
{
    public PrinterConnectException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Transport abstraction for sending raw ESC/POS bytes to a network thermal printer.</summary>
public interface IEscPosNetworkTransport
{
    Task SendBytesAsync(string host, int port, ReadOnlyMemory<byte> payload, int timeoutMs, CancellationToken cancellationToken = default);
}

/// <summary>
/// Serialized short-lived TCP/IP socket client for raw ESC/POS communication (e.g., Xprinter XP-80T port 9100).
/// </summary>
public sealed class EscPosTcpTransport : IEscPosNetworkTransport, IDisposable
{
    private readonly SemaphoreSlim _socketGate = new(1, 1);

    public async Task<bool> CheckConnectivityAsync(
        string host,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            return false;

        int boundedTimeoutMs = EscPosCommands.ClampTimeoutMs(timeoutMs);
        await _socketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(boundedTimeoutMs);
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host.Trim(), port, timeoutCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
        finally
        {
            _socketGate.Release();
        }
    }

    public async Task SendBytesAsync(
        string host,
        int port,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Printer host/IP is required.", nameof(host));
        if (port < 1 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Printer port must be between 1 and 65535.");
        if (payload.IsEmpty)
            return;

        int boundedTimeoutMs = EscPosCommands.ClampTimeoutMs(timeoutMs);

        await _socketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(boundedTimeoutMs);

            using var client = new TcpClient
            {
                NoDelay = true,
                SendTimeout = boundedTimeoutMs,
                ReceiveTimeout = boundedTimeoutMs
            };

            try
            {
                await client.ConnectAsync(host.Trim(), port, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PrinterConnectException($"Printer {host}:{port} did not respond within {boundedTimeoutMs} ms.");
            }
            catch (SocketException ex)
            {
                throw new PrinterConnectException($"Unable to connect to printer {host}:{port}: {ex.Message}", ex);
            }

            try
            {
                using var stream = client.GetStream();
                await stream.WriteAsync(payload, timeoutCts.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Printer {host}:{port} timed out while writing data after {boundedTimeoutMs} ms.");
            }
        }
        finally
        {
            _socketGate.Release();
        }
    }

    public void Dispose()
    {
        _socketGate.Dispose();
    }
}

/// <summary>Unified Ethernet/LAN ESC/POS connection strategy backed by IEscPosNetworkTransport.</summary>
public sealed class EthernetPrinterConnection : IPrinterConnection
{
    private readonly IEscPosNetworkTransport _transport;

    public EthernetPrinterConnection(IEscPosNetworkTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public PrinterConnectionMode Mode => PrinterConnectionMode.Ethernet;

    public string DescribeEndpoint(LocalPrinterHardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return EscPosCommands.TryValidateEndpoint(config.PrinterIpAddress, config.PrinterPort, out var host, out _)
            ? $"{host}:{config.PrinterPort}"
            : "Ethernet (unconfigured)";
    }

    public (bool Valid, string? Error) ValidateConfiguration(LocalPrinterHardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return EscPosCommands.TryValidateEndpoint(config.PrinterIpAddress, config.PrinterPort, out _, out var error)
            ? (true, null)
            : (false, error);
    }

    public async Task<PrinterConnectionStatus> CheckStatusAsync(LocalPrinterHardwareConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!EscPosCommands.TryValidateEndpoint(config.PrinterIpAddress, config.PrinterPort, out var host, out var err))
        {
            return new PrinterConnectionStatus(
                IsConfigured: false,
                IsConnected: false,
                Mode: PrinterConnectionMode.Ethernet,
                EndpointDisplay: "Unconfigured",
                StatusMessage: err ?? "Configure printer IP address and port (default 9100).");
        }

        string ep = $"{host}:{config.PrinterPort}";
        if (_transport is EscPosTcpTransport tcp)
        {
            bool reachable = await tcp.CheckConnectivityAsync(host, config.PrinterPort, config.ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            return new PrinterConnectionStatus(
                IsConfigured: true,
                IsConnected: reachable,
                Mode: PrinterConnectionMode.Ethernet,
                EndpointDisplay: ep,
                StatusMessage: reachable ? $"Connected ({ep})" : $"Offline / Unreachable ({ep})");
        }

        return new PrinterConnectionStatus(true, true, PrinterConnectionMode.Ethernet, ep, $"Configured ({ep})");
    }

    public async Task SendBytesAsync(
        LocalPrinterHardwareConfig config,
        ReadOnlyMemory<byte> payload,
        bool allowConnectRetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!EscPosCommands.TryValidateEndpoint(config.PrinterIpAddress, config.PrinterPort, out var host, out var err))
            throw new InvalidOperationException(err ?? "Invalid Ethernet printer endpoint.");

        int maxAttempts = allowConnectRetry ? 2 : 1;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _transport.SendBytesAsync(host, config.PrinterPort, payload, config.ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PrinterConnectException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Pre-write connection failure only: zero bytes sent yet, safe single backoff retry.
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (allowConnectRetry && attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Preserve retry only if transport threw TimeoutException without PrinterConnectException? No: post-connect timeouts must NOT retry!
                throw;
            }
        }
    }
}
