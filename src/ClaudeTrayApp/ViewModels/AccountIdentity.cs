using ClaudeTrayApp.Core.Account;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeTrayApp.ViewModels;

/// <summary>
/// Which account this instance shows, as the tooltip, notifications, flyout and settings window name it. The default
/// account has no label and no icon mark, so a single-account install looks exactly as it always did.
/// </summary>
public sealed partial class AccountIdentity : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private string? _label;

    public AccountIdentity(AccountContext account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Account = account;
        _label = AccountLabel.Resolve(account, null);
    }

    public static AccountIdentity Default { get; } = new(AccountContext.Default);

    public AccountContext Account { get; }

    /// <summary>
    /// The letter in the tray icon's bottom-right corner: the label's first letter or digit, upper-cased ("Work" gives
    /// "W"). Null for the default account, whose icon stays exactly as it was.
    /// </summary>
    public string? Badge => BadgeFor(Label);

    internal static string? BadgeFor(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var first = label.FirstOrDefault(char.IsLetterOrDigit);
        return char.ToUpperInvariant(first == default ? label.Trim()[0] : first).ToString();
    }

    /// <summary>Re-resolves the label once the account block of <c>.claude.json</c> is known. UI thread only.</summary>
    public void Update(AccountInfo? info) => Label = AccountLabel.Resolve(Account, info);

    /// <summary>"Claude usage" becomes "Claude usage · Work"; unchanged for the default account.</summary>
    public string Decorate(string title) => Label is { } label ? title + " · " + label : title;
}
