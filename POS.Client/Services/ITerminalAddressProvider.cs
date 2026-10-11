namespace POS.Client.Services;
public interface ITerminalAddressProvider
{
    Task<string> GetAddressAsync();
}
