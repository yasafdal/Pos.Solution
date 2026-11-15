using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POS.Client.Services
{
    public enum TerminalConnectivityState
    {
        Unknown,
        OnlineAndHealthy,
        InternetConnectedApiUnavailable,
        ApiReachableDbUnavailable,
        AuthenticationRejected,
        TerminalUnauthorized,
        LocalSqliteUnavailable
    }

    public interface IPosConnectivityService
    {
        Task<TerminalConnectivityState> CheckConnectivityAsync(CancellationToken cancellationToken = default);
        TerminalConnectivityState CurrentState { get; }
        event EventHandler<TerminalConnectivityState>? ConnectivityChanged;
    }

    public class PosConnectivityService : IPosConnectivityService
    {
        private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(4);
        private readonly HttpClient _httpClient;
        private TerminalConnectivityState _currentState = TerminalConnectivityState.Unknown;

        public TerminalConnectivityState CurrentState
        {
            get => _currentState;
            private set
            {
                if (_currentState != value)
                {
                    _currentState = value;
                    ConnectivityChanged?.Invoke(this, _currentState);
                }
            }
        }

        public event EventHandler<TerminalConnectivityState>? ConnectivityChanged;

        public PosConnectivityService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<TerminalConnectivityState> CheckConnectivityAsync(CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HealthProbeTimeout);

            try
            {
                using var response = await _httpClient.GetAsync("api/offline-sync/health", timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    CurrentState = IsHealthyJsonResponse(body)
                        ? TerminalConnectivityState.OnlineAndHealthy
                        : TerminalConnectivityState.InternetConnectedApiUnavailable;
                    return CurrentState;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    CurrentState = TerminalConnectivityState.AuthenticationRejected;
                    return CurrentState;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    CurrentState = TerminalConnectivityState.TerminalUnauthorized;
                    return CurrentState;
                }

                if ((int)response.StatusCode >= 500)
                {
                    CurrentState = TerminalConnectivityState.ApiReachableDbUnavailable;
                    return CurrentState;
                }

                CurrentState = TerminalConnectivityState.InternetConnectedApiUnavailable;
                return CurrentState;
            }
            catch (HttpRequestException)
            {
                CurrentState = TerminalConnectivityState.InternetConnectedApiUnavailable;
                return CurrentState;
            }
            catch (OperationCanceledException)
            {
                CurrentState = TerminalConnectivityState.InternetConnectedApiUnavailable;
                return CurrentState;
            }
            catch (Exception)
            {
                CurrentState = TerminalConnectivityState.InternetConnectedApiUnavailable;
                return CurrentState;
            }
        }

        private static bool IsHealthyJsonResponse(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "Status", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.String)
                    {
                        return string.Equals(prop.Value.GetString(), "ONLINE", StringComparison.OrdinalIgnoreCase);
                    }
                }

                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
