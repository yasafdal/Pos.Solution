using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class TerminalActivationRequestDto
{
    [Required(ErrorMessage = "Activation code is required.")]
    [StringLength(10, MinimumLength = 6, ErrorMessage = "Activation code must be 6 digits.")]
    public string ActivationCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Client-generated Device UUID is required.")]
    [StringLength(100)]
    public string DeviceUuid { get; set; } = string.Empty;

    [StringLength(100)]
    public string? DeviceIdentifier { get; set; }
}

public class TerminalActivationResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Token { get; set; }
    public TerminalDto? Terminal { get; set; }
    public TerminalConfigDto? Config { get; set; }
    public List<OfflineCashierDto> Cashiers { get; set; } = new();
}

public class TerminalSessionTokenRequestDto
{
    public int TerminalId { get; set; }
    public string DeviceUuid { get; set; } = string.Empty;
    public string? CashierUsername { get; set; }
}

public class TerminalSessionTokenResponseDto
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? Message { get; set; }
}
