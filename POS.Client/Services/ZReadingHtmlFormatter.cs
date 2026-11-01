using System.Globalization;
using System.Net;
using System.Text;
using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>Formats a saved, supervisor-authorized Z snapshot; delegates to POS.Shared formatter.</summary>
public static class ZReadingHtmlFormatter
{
    public static string Format(ZReadingReportDto report, bool isReprint = false) =>
        POS.Shared.Services.ZReadingHtmlFormatter.Format(report, isReprint);
}
