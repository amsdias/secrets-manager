using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SecretsManager.Core.App;
using static Microsoft.UI.Reactor.Factories;

namespace SecretsManager.App;

/// <summary>
/// Renders AppState.Prompt as a ContentDialog. Keyed by prompt Id, so every prompt starts with empty
/// fields. The answer is reported from OnClosed, i.e. after the dialog has fully closed, so the next
/// prompt (e.g. "wrong password, try again") can open safely.
/// </summary>
sealed class PromptDialog : Component<PromptRequest>
{
    public override Element Render()
    {
        var p = Props;
        var (text, setText) = UseState(p.InitialText ?? "");
        var (password, setPassword) = UseState("");
        var (confirm, setConfirm) = UseState("");
        var (current, setCurrent) = UseState("");
        var (option, setOption) = UseState(false);
        var (choice, setChoice) = UseState(0);

        var valid = p.Kind switch
        {
            PromptKind.Password => password.Length > 0,
            PromptKind.NewPassword => option || (password.Length > 0 && password == confirm),
            PromptKind.ChangePassword => current.Length > 0 && password.Length > 0 && password == confirm,
            PromptKind.Text => p.RequiredText is null ? text.Trim().Length > 0 : text == p.RequiredText,
            PromptKind.Choice => choice >= 0 && choice < p.Choices.Count,
            _ => true,
        };
        var mismatch = p.Kind is PromptKind.NewPassword or PromptKind.ChangePassword && !option && confirm.Length > 0 && password != confirm;
        var newPasswordEnabled = !option;

        Element body = p.Kind switch
        {
            PromptKind.Password => PasswordBox(password, setPassword, "Password").AutomationName("Password").AutomationId("PromptPassword"),
            PromptKind.NewPassword => VStack(8,
                PasswordBox(password, setPassword, "Password").AutomationName("Password").IsEnabled(newPasswordEnabled).AutomationId("PromptPassword"),
                PasswordBox(confirm, setConfirm, "Confirm password").AutomationName("Confirm password").IsEnabled(newPasswordEnabled).AutomationId("PromptConfirm"),
                p.OptionText is null ? null : CheckBox(option, setOption, p.OptionText).AutomationId("PromptOption")),
            PromptKind.ChangePassword => VStack(8,
                PasswordBox(current, setCurrent, "Current password").AutomationName("Current password").AutomationId("PromptCurrent"),
                PasswordBox(password, setPassword, "New password").AutomationName("New password").AutomationId("PromptPassword"),
                PasswordBox(confirm, setConfirm, "Confirm new password").AutomationName("Confirm new password").AutomationId("PromptConfirm")),
            PromptKind.Text => TextBox(text, setText).AutomationName(p.Title).AutomationId("PromptText"),
            PromptKind.Choice => ComboBox(p.Choices.ToArray(), choice, setChoice).AutomationName(p.Title).AutomationId("PromptChoice").HAlign(HorizontalAlignment.Stretch),
            _ => Empty(),
        };

        return ContentDialog(p.Title,
            VStack(12,
                TextBlock(p.Message).TextWrapping().IsTextSelectionEnabled(p.Kind == PromptKind.Info),
                body,
                mismatch ? TextBlock("The passwords do not match.").Foreground(Theme.SystemCritical) : null
            ).MinWidth(360),
            p.PrimaryText) with
        {
            IsOpen = true,
            CloseButtonText = p.Kind switch { PromptKind.Info => null, PromptKind.Confirm when !p.IsDanger => "Not now", _ => "Cancel" },
            DefaultButton = p.IsDanger ? ContentDialogButton.Close : ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = valid,
            OnClosed = result => AppServices.Store.CompletePrompt(result != ContentDialogResult.Primary ? null : new PromptResult(
                Text: text, Password: option ? null : password, CurrentPassword: current, Option: option, ChoiceIndex: choice)),
        };
    }
}
