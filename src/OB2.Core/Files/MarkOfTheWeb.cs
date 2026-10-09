using System.Text;

namespace OB2.Core.Files;

/// <summary>
/// Writes the <c>Zone.Identifier</c> alternate data stream so Windows SmartScreen and Office
/// treat the file as downloaded from the internet (§11.1).
/// </summary>
public static class MarkOfTheWeb
{
    private const int InternetZone = 3;

    /// <returns>False when the volume does not support alternate streams (FAT32, exFAT).</returns>
    public static bool TryApply(string filePath, string? hostUrl, string? referrerUrl)
    {
        var content = new StringBuilder()
            .Append("[ZoneTransfer]\r\n")
            .Append("ZoneId=").Append(InternetZone).Append("\r\n");
        if (!string.IsNullOrEmpty(referrerUrl))
        {
            content.Append("ReferrerUrl=").Append(StripCredentials(referrerUrl)).Append("\r\n");
        }

        if (!string.IsNullOrEmpty(hostUrl))
        {
            content.Append("HostUrl=").Append(StripCredentials(hostUrl)).Append("\r\n");
        }

        try
        {
            File.WriteAllText(filePath + ":Zone.Identifier", content.ToString(), Encoding.ASCII);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string StripCredentials(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri
            : url;
}
