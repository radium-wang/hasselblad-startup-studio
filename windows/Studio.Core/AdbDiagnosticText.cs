using System.Text.RegularExpressions;
namespace StartupStudio;

public static class AdbDiagnosticText
{
    public static string Sanitize(string text, IEnumerable<string> sensitive)
    {
        // Redact full arguments first (including paths with spaces), then unknown quoted
        // paths/identities and unquoted paths. Keep the error description, never stdout.
        foreach (var value in sensitive.Where(v => v.Length > 2).OrderByDescending(v => v.Length))
            text = text.Replace(value, "<redacted>", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, "'[^']*'|\"[^\"]*\"", "<redacted>");
        text = Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\|/)[^\r\n,;<>]*", "<path>");
        foreach (var value in new[] { Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
            if (value.Length > 1) text = text.Replace(value, "<user>", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"[\x00-\x1f\x7f]+", " ").Trim();
        return text.Length == 0 ? "未提供错误详情" : text[..Math.Min(2048, text.Length)];
    }
}
