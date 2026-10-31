namespace POS.Api.Services;

public sealed class ZReadingException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
