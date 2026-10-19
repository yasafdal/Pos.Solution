using System;
using System.Net.Http;
using System.Net.Http.Json;
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
            try
            {
                var response = await _httpClient.GetAsync("api/offline-sync/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    CurrentState = TerminalConnectivityState.OnlineAndHealthy;
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
            catch (TaskCanceledException)
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
    }
}
