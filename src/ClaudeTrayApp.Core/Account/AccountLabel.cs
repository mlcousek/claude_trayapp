using System.Text;

namespace ClaudeTrayApp.Core.Account;

/// <summary>
/// The short name a non-default account is shown under: the <c>--label</c> argument, else the organisation from
/// <c>.claude.json</c>, else the Claude home's folder name. The default account has none.
/// </summary>
public static class AccountLabel
{
    public const int MaxLength = 32;

    /// <summary>The label for <paramref name="account"/>, or null for the default account.</summary>
    public static string? Resolve(AccountContext account, AccountInfo? info)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.IsDefault)
        {
            return null;
        }

        // A personal organisation is named after the e-mail address; showing it would get round the e-mail mask.
        var organization = info?.OrganizationName is { } name && !name.Contains('@', StringComparison.Ordinal) ? name : null;
        return account.LabelArgument
            ?? Sanitize(organization)
            ?? Sanitize(Path.GetFileName(account.ClaudeHome)?.TrimStart('.'))
            ?? "account";
    }

    /// <summary>Drops control characters and double quotes, trims, caps the length; null when nothing is left.</summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsControl(c) && c != '"')
            {
                builder.Append(c);
            }
        }

        var clean = builder.ToString().Trim();
        if (clean.Length > MaxLength)
        {
            clean = clean[..MaxLength].TrimEnd();
        }

        return clean.Length == 0 ? null : clean;
    }
}
