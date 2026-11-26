using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using SecretsManager.Core;
using SecretsManager.Core.App;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace SecretsManager.App;

/// <summary>
/// Root component. Pure rendering of AppStore.State: every handler calls an AppStore method,
/// so behaviour lives (and is tested) in SecretsManager.Core.
/// </summary>
sealed class SecretsApp : Component
{
    static readonly IEqualityComparer<AppState> SameInstance = EqualityComparer<AppState>.Create((a, b) => ReferenceEquals(a, b), s => s.GetHashCode());
    static readonly string Mask = new((char)0x2022, 12); // bullet characters

    AppStore Store => AppServices.Store;

    public override Element Render()
    {
        var s = UseExternalStore(Store.Subscribe, () => Store.State, SameInstance);
        var window = UseWindow();
        var closeReady = UseRef(false);
        var (searchRef, focusSearch) = this.UseElementFocus(FocusState.Keyboard);
        var theme = s.Settings.Theme;
        UseEffect(() => ThemeManager.Apply(theme, window), theme);

        var tray = UseTrayIcon(new TrayIconSpec(AppServices.TrayIcon!, "PowerShell Secrets", IsVisible: s.Settings.MinimizeToTray && AppServices.TrayIcon is not null));
        UseEffect(() =>
        {
            if (window is null || tray is null) return () => { };
            void Restore()
            {
                tray.HideFlyout();
                window.Show();
                (window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Restore();
                window.Activate();
            }
            void OnClick(object? sender, EventArgs e) => Restore();
            void OnRightClick(object? sender, EventArgs e) => tray.ShowFlyout(VStack(4,
                Button("Show", Restore).HAlign(HorizontalAlignment.Stretch),
                Button("Lock now", () => { tray.HideFlyout(); _ = Store.LockAsync(); }).HAlign(HorizontalAlignment.Stretch),
                Button("Exit", () => { tray.HideFlyout(); window.Close(); }).HAlign(HorizontalAlignment.Stretch)
            ).Padding(8).MinWidth(140));
            void OnState(object? sender, WindowState state)
            {
                if (state == WindowState.Minimized && Store.State.Settings.MinimizeToTray) window.Hide();
            }
            tray.Click += OnClick;
            tray.RightClick += OnRightClick;
            window.StateChanged += OnState;
            return () => { tray.Click -= OnClick; tray.RightClick -= OnRightClick; window.StateChanged -= OnState; };
        }, tray?.Id ?? "");

        UseEffect(() => { _ = Store.InitializeAsync(); }, []);

        // On exit, clear a copied secret from the clipboard (only if it is still ours).
        UseEffect(() =>
        {
            if (window is null) return () => { };
            async void OnClosing(object? sender, WindowClosingEventArgs e)
            {
                if (closeReady.Current || Store.State.ClipboardSecondsLeft is null) return;
                e.Cancel = true;
                await Store.ClearClipboardIfOursAsync();
                closeReady.Current = true;
                window.Close();
            }
            window.Closing += OnClosing;
            return () => window.Closing -= OnClosing;
        }, []);

        var newCommand = UseMemo(() => new Command
        {
            Label = "New secret", Execute = Store.OpenNewSecret,
            Accelerator = Accelerator(VirtualKey.N, VirtualKeyModifiers.Control),
        }, []);
        var refreshCommand = UseMemo(() => new Command
        {
            Label = "Refresh", Execute = () => _ = Store.RefreshAsync(),
            Accelerator = Accelerator(VirtualKey.F5),
        }, []);

        var main = Grid(
            columns: [GridSize.Px(290), GridSize.Star()],
            rows: [GridSize.Star()],
            Card(VaultPane(s)).Grid(row: 0, column: 0),
            Card(SecretsPane(s, newCommand, refreshCommand, e => e.Ref(searchRef))).Margin(12, 0, 0, 0).Grid(row: 0, column: 1));

        Element? overlay = s.HasPanel
            ? Border(
                ScrollView(Card(PanelContent(s)).Background(Theme.SolidBackground).MaxWidth(620).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center).Margin(24))
              ).Background(Theme.SmokeFill)
               .OnKeyDown((_, e) => { if (e.Key == VirtualKey.Escape) { ClosePanels(); e.Handled = true; } })
            : null;

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            s.Error is null ? null :
                InfoBar("Something went wrong", s.Error).Error().IsClosable().Closed(Store.DismissError)
                    .AutomationId("ErrorBar").Margin(12, 12, 12, 0).Grid(row: 0),
            Grid(columns: [GridSize.Star()], rows: [GridSize.Star()],
                main.Margin(12),
                overlay
            ).Grid(row: 1),
            StatusBar(s).Grid(row: 2),
            s.Prompt is null ? null : Component<PromptDialog, PromptRequest>(s.Prompt).WithKey($"prompt-{s.Prompt.Id}")
        ).Background(Theme.SolidBackground)
         .RequestedTheme(ThemeManager.ElementThemeFor(theme))
         .OnPreviewKeyDown((_, e) => OnGlobalKey(e, focusSearch))
         .OnPointerMoved((_, _) => Store.NotifyActivity());
    }

    // ------------------------------------------------------------------ vaults

    Element VaultPane(AppState s)
    {
        var vault = s.CurrentVault;
        var selectedIndex = vault is null ? -1 : s.Vaults.IndexOf(vault);
        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                Subtitle("Vaults").VAlign(VerticalAlignment.Center),
                Button(IconGlyph(Cog, 16), Store.OpenSettings).ToolTip("Settings: theme, auto-lock, clipboard, tray (Ctrl+,)")
                    .AutomationName("Settings").AutomationId("SettingsButton").Grid(row: 0, column: 1)
            ).Margin(0, 0, 0, 8).Grid(row: 0),
            ListView<VaultInfo>(s.Vaults, v => v.Name, (v, _) =>
                    Grid(columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto], rows: [GridSize.Auto],
                        VStack(0,
                            TextBlock(v.Name).SemiBold(),
                            Caption(v.KindLabel).Opacity(0.7)),
                        v.IsDefault ? Caption("default").Foreground(Theme.AccentText).VAlign(VerticalAlignment.Center).Margin(0, 0, 8, 0).Grid(row: 0, column: 1) : null,
                        Padlock(s.LockStatusOf(v), 14)?.VAlign(VerticalAlignment.Center).Grid(row: 0, column: 2)
                    ).Padding(0, 6, 0, 6))
                .SelectionChanged(sel => _ = Store.SelectVaultAsync(sel.FirstOrDefault()?.Name))
                .Set(lv => { if (lv.SelectedIndex != selectedIndex) lv.SelectedIndex = selectedIndex; })
                .AutomationId("VaultList")
                .Grid(row: 1),
            ActionsPanel(s, vault).Grid(row: 2));
    }

    /// <summary>Vault and SecretStore actions: equal-width buttons in a 2-column grid that fills the pane.</summary>
    Element ActionsPanel(AppState s, VaultInfo? vault)
    {
        // Buttons stretch to fill two equal columns (8px apart); a full-width cell spans both. Same height everywhere.
        const double ButtonHeight = 36;
        static Element Cell(ButtonElement b, int row, int column, int columnSpan = 1)
        {
            var (left, right) = columnSpan == 2 ? (0, 0) : column == 0 ? (0, 4) : (4, 0);
            return b.HAlign(HorizontalAlignment.Stretch).Height(ButtonHeight).Margin(left, 0, right, 8)
                    .Grid(row: row, column: column, columnSpan: columnSpan);
        }

        static Element SectionLabel(string text, int row) =>
            Caption(text).SemiBold().Opacity(0.7).Margin(0, 0, 0, 6).Grid(row: row, column: 0, columnSpan: 2);

        return VStack(0,
            Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 8, 0, 12),
            Grid(
                columns: [GridSize.Star(), GridSize.Star()],
                rows: [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                SectionLabel("Manage", 0),
                Cell(Button("Add vault…", Store.OpenVaultForm).ToolTip("Create a vault, or add an existing one back to the list").AutomationId("RegisterVaultButton"), 1, 0),
                Cell(Button("Remove…", () => _ = Store.UnregisterSelectedVaultAsync()).IsEnabled(vault is not null)
                    .ToolTip("Remove the vault from the list (its secrets are kept unless you choose to delete them)").AutomationId("UnregisterVaultButton"), 1, 1),
                Cell(Button("Import…", () => _ = ImportAsync()).IsEnabled(vault is not null && !s.IsStoreMissing)
                    .ToolTip("Import secrets from an encrypted export file into this vault").AutomationId("ImportButton"), 2, 0),
                Cell(Button("Export…", () => _ = ExportAsync()).IsEnabled(vault is not null && !s.IsStoreMissing && !s.Secrets.IsEmpty)
                    .ToolTip("Save every secret of this vault to a password-encrypted file").AutomationId("ExportButton"), 2, 1),
                Cell(Button("Set as default", () => _ = Store.SetDefaultVaultAsync()).IsEnabled(vault is { IsDefault: false })
                    .ToolTip("The vault Get-Secret and Set-Secret use when no -Vault is given").AutomationId("DefaultVaultButton"), 3, 0, columnSpan: 2),
                SectionLabel("SecretStore", 4).Margin(0, 8, 0, 6),
                Cell(Button("SecretStore settings…", () => _ = Store.OpenStoreSettingsAsync()).IsEnabled(s.SecretStoreAvailable)
                    .ToolTip("Password, Windows Hello, timeout, reset (shared by every SecretStore vault)").AutomationId("StoreSettingsButton"), 5, 0, columnSpan: 2)
            ));
    }

    // ------------------------------------------------------------------ secrets

    Element SecretsPane(AppState s, Command newCommand, Command refreshCommand, Func<Element, Element> attachSearchRef)
    {
        var vault = s.CurrentVault;
        var hasSecret = s.CurrentSecret is not null;
        var visible = s.VisibleSecrets;
        var selectedIndex = visible.ToList().FindIndex(x => x.Name == s.SelectedSecret);

        var header = Grid(
            columns: [GridSize.Star(), GridSize.Px(300)],
            rows: [GridSize.Auto],
            VStack(2,
                HStack(10,
                    Subtitle(vault?.Name ?? "No vault selected").AutomationId("VaultTitle"),
                    LockControl(s)?.VAlign(VerticalAlignment.Center)),
                Caption(vault is null ? "" : vault.Description is { } d ? $"{vault.KindLabel} - {d}" : vault.KindLabel).Opacity(0.7)),
            attachSearchRef(TextBox(s.Search, Store.SetSearch, placeholderText: "Search name or metadata (key=value)"))
                .ToolTip("Search (Ctrl+F)").AutomationName("Search secrets").AutomationId("SearchBox").VAlign(VerticalAlignment.Center).Grid(row: 0, column: 1));

        var toolbar = HStack(8,
            Button(newCommand).AccentButton().IsEnabled(vault is not null && !s.IsStoreMissing).AutomationId("NewSecretButton"),
            Button("View", () => _ = Store.ViewSecretAsync()).IsEnabled(hasSecret).ToolTip("View (Enter)").AutomationId("ViewSecretButton"),
            Button("Copy", () => _ = Store.CopySelectedSecretAsync()).IsEnabled(hasSecret).ToolTip("Copy the value, or the password of a credential (Ctrl+C)").AutomationId("CopySecretButton"),
            Button("Edit", () => _ = Store.OpenEditSecretAsync()).IsEnabled(hasSecret).ToolTip("Edit (F2 or Ctrl+E)").AutomationId("EditSecretButton"),
            Button("Rename", () => _ = Store.RenameSelectedSecretAsync()).IsEnabled(hasSecret).AutomationId("RenameSecretButton"),
            Button("Copy to vault…", () => _ = Store.CopySelectedSecretToVaultAsync()).IsEnabled(hasSecret && s.Vaults.Length > 1).AutomationId("CopyToVaultButton"),
            Button("Delete", () => _ = Store.DeleteSelectedSecretAsync()).IsEnabled(hasSecret).Foreground(Theme.SystemCritical).ToolTip("Delete (Del)").AutomationId("DeleteSecretButton"),
            Button(refreshCommand).AutomationId("RefreshButton")
        ).Margin(0, 12, 0, 12);

        // Name, Type, one column per frequent metadata key, then the rest of the metadata.
        var metaColumns = s.MetadataColumns;
        GridSize[] columns = [GridSize.Star(2), GridSize.Px(120), .. metaColumns.Select(_ => GridSize.Star(1)), GridSize.Star(2)];
        var restColumn = 2 + metaColumns.Count;
        Element Row(Element name, Element type, IEnumerable<Element> meta, Element rest) =>
            Grid(columns: columns, rows: [GridSize.Auto],
                [name, type.Grid(row: 0, column: 1), .. meta.Select((m, i) => m.Grid(row: 0, column: 2 + i)), rest.Grid(row: 0, column: restColumn)]);

        var columnHeader = Row(
            Caption("Name").SemiBold(),
            Caption("Type").SemiBold(),
            metaColumns.Select(k => (Element)Caption(k).SemiBold()),
            Caption(metaColumns.Count > 0 ? "Other metadata" : "Metadata").SemiBold()
        ).Padding(12, 0, 12, 6);

        Element list = s.EmptyMessage is { } empty
            ? TextBlock(empty).TextWrapping().TextAlignment(TextAlignment.Center).Opacity(0.7).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center).MaxWidth(420).AutomationId("EmptyText")
            : ListView<SecretEntry>(visible, x => x.Name, (x, index) =>
                    Row(
                        NameCell(x, s.ExpiryOf(x)),
                        TextBlock(x.Kind.ToString()).Opacity(0.8),
                        metaColumns.Select(k => (Element)TextBlock(AppState.MetadataValue(x, k)).Opacity(0.8).Margin(0, 0, 8, 0)),
                        TextBlock(AppState.OtherMetadataText(x, metaColumns)).Opacity(0.8)
                    ).Padding(0, 6, 0, 6))
                .SelectionChanged(sel => Store.SelectSecret(sel.FirstOrDefault()?.Name))
                .Set(lv => { if (lv.SelectedIndex != selectedIndex) lv.SelectedIndex = selectedIndex; })
                .OnPreviewKeyDown((_, e) => OnListKey(e)) // preview: the ListView consumes Enter before KeyDown
                // On the list, not the row: a row's grid has no background, so only clicks on its text reached it.
                .OnDoubleTapped((_, _) => { if (Store.State.CurrentSecret is not null) _ = Store.ViewSecretAsync(); })
                .AutomationId("SecretList");

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
            header.Grid(row: 0),
            toolbar.Grid(row: 1),
            columnHeader.Grid(row: 2),
            list.Grid(row: 3));
    }

    /// <summary>The name, plus a warning when its "expires" metadata date has passed or is near.</summary>
    static Element NameCell(SecretEntry x, SecretExpiry expiry) => expiry.Warning is not { } warning
        ? TextBlock(x.Name).AutomationName(x.Name)
        : HStack(6,
            TextBlock(x.Name).AutomationName(x.Name).VAlign(VerticalAlignment.Center),
            IconGlyph(WarningGlyph, 12).Foreground(expiry.State == ExpiryState.Expired ? Theme.SystemCritical : Theme.SystemCaution).VAlign(VerticalAlignment.Center),
            Caption(warning).Foreground(expiry.State == ExpiryState.Expired ? Theme.SystemCritical : Theme.SystemCaution).VAlign(VerticalAlignment.Center).AutomationId("ExpiryText")
        ).ToolTip($"Expiry date (metadata): {expiry.Date:yyyy-MM-dd}");

    /// <summary>Fire-and-forget for UI handlers; AppStore methods handle and surface their own errors.</summary>
    static void Fire(Task task) { }

    void OnListKey(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = IsCtrlDown();
        switch (e.Key)
        {
            case VirtualKey.Enter: _ = Store.ViewSecretAsync(); break;
            case VirtualKey.Delete: _ = Store.DeleteSelectedSecretAsync(); break;
            case VirtualKey.F2: _ = Store.OpenEditSecretAsync(); break;
            case VirtualKey.C when ctrl: _ = Store.CopySelectedSecretAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    Element StatusBar(AppState s)
    {
        return Border(
            Grid(
                columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                rows: [GridSize.Auto],
                TextBlock(s.Status).TextTrimming(TextTrimming.CharacterEllipsis).VAlign(VerticalAlignment.Center).AutomationId("StatusText"),
                s.ExpirySummary is { Length: > 0 } expiring
                    ? Caption(expiring).Foreground(Theme.SystemCaution).Margin(12, 0, 0, 0).VAlign(VerticalAlignment.Center)
                        .ToolTip("From the secrets' \"expires\" metadata").AutomationId("ExpirySummary").Grid(row: 0, column: 4)
                    : null,
                s.UpdateAvailable is { } update
                    ? HyperlinkButton($"Version {update.Version.ToString(3)} is available", update.Url)
                        .ToolTip("Opens the release page on GitHub").Padding(6, 2, 6, 2).Margin(12, 0, 0, 0).VAlign(VerticalAlignment.Center)
                        .AutomationId("UpdateLink").Grid(row: 0, column: 3)
                    : null,
                s.ClipboardSecondsLeft is { } left
                    ? TextBlock($"Clipboard clears in {left}s").Foreground(Theme.AccentText).Margin(12, 0, 12, 0).VAlign(VerticalAlignment.Center).AutomationId("ClipboardText").Grid(row: 0, column: 1)
                    : null,
                s.IsBusy ? ProgressRing().Width(16).Height(16).Grid(row: 0, column: 2) : null)
        ).Background(Theme.LayerFill).Padding(14, 8, 14, 8);
    }

    const string IconFont = "Segoe Fluent Icons";
    static string Glyph(int codePoint) => ((char)codePoint).ToString();
    static readonly string ClosedPadlock = Glyph(0xE72E), OpenPadlock = Glyph(0xE785), Cog = Glyph(0xE713), WarningGlyph = Glyph(0xE7BA);

    /// <summary>
    /// An icon-font glyph as text (TextBlock takes a themed Foreground; IconElement doesn't). Tight line bounds drop the
    /// font's empty ascent/descent, so the glyph centres vertically like the text beside it.
    /// </summary>
    static TextBlockElement IconGlyph(string glyph, double size) =>
        TextBlock(glyph).FontFamily(IconFont).FontSize(size)
            .Set(tb => tb.TextLineBounds = Microsoft.UI.Xaml.TextLineBounds.Tight);

    /// <summary>Closed padlock (locked), open padlock (unlocked), nothing otherwise.</summary>
    static Element? Padlock(LockStatus status, double size) => status switch
    {
        LockStatus.Locked => IconGlyph(ClosedPadlock, size).Foreground(Theme.SystemCaution).ToolTip("Locked").AutomationName("Locked"),
        LockStatus.Unlocked => IconGlyph(OpenPadlock, size).Foreground(Theme.SystemSuccess).ToolTip("Unlocked").AutomationName("Unlocked"),
        _ => null,
    };

    /// <summary>Next to the vault name: the padlock, its state, and the one action that changes it.</summary>
    Element? LockControl(AppState s) => s.CurrentLockStatus switch
    {
        LockStatus.Locked => HStack(8,
            Padlock(LockStatus.Locked, 16)!.VAlign(VerticalAlignment.Center), Caption("Locked").Foreground(Theme.SystemCaution).VAlign(VerticalAlignment.Center),
            Button("Unlock…", () => _ = Store.UnlockSelectedVaultAsync()).AccentButton()
                .ToolTip(s.HelloEnabled && s.CurrentVault is { IsSecretStore: true } ? "Unlock with Windows Hello" : "Enter the vault's password")
                .AutomationId("UnlockButton")),
        LockStatus.Unlocked => HStack(8,
            Padlock(LockStatus.Unlocked, 16)!.VAlign(VerticalAlignment.Center), Caption("Unlocked").Foreground(Theme.SystemSuccess).VAlign(VerticalAlignment.Center),
            Button("Lock", () => _ = Store.LockAsync())
                .ToolTip("Lock now (Ctrl+L): also closes the open secret and clears a copied value").AutomationId("LockButton")),
        LockStatus.NoPassword => Caption("No password: protected by your Windows account").Opacity(0.7).VAlign(VerticalAlignment.Center)
            .AutomationId("NoPasswordText"),
        _ => null,
    };

    /// <summary>Window-wide shortcuts (Ctrl+N and F5 are accelerators on their buttons). Every key press also counts as activity.</summary>
    void OnGlobalKey(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e, Action focusSearch)
    {
        Store.NotifyActivity();
        var s = Store.State;
        if (s.Prompt is not null) return;
        var ctrl = IsCtrlDown();
        switch (e.Key)
        {
            case VirtualKey.F when ctrl && !s.HasPanel: focusSearch(); break;
            case VirtualKey.L when ctrl: _ = Store.LockAsync(); break;
            case VirtualKey.E when ctrl && !s.HasPanel && s.CurrentSecret is not null: _ = Store.OpenEditSecretAsync(); break;
            case VirtualKey.F1: _ = Store.ShowShortcutsAsync(); break;
            case VirtualKey.Escape when s.HasPanel: ClosePanels(); break;
            case (VirtualKey)188 when ctrl && !s.HasPanel: Store.OpenSettings(); break; // Ctrl+, (VK_OEM_COMMA)
            default: return;
        }
        e.Handled = true;
    }

    static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    async Task ExportAsync()
    {
        if (Store.State.CurrentVault is not { } vault) return;
        if (AppServices.UiTestFilePath is { } testPath) { await Store.ExportSelectedVaultAsync(testPath); return; }
        var target = await PickSaveFileAsync(new SaveFilePickerOptions(
            new[] { new FileTypeChoice("Secrets Manager export", new[] { SecretsExport.FileExtension }) }, $"{vault.Name}-{DateTime.Now:yyyy-MM-dd}"));
        if (target is null) return;
        await Store.ExportSelectedVaultAsync(target.Path);
    }

    async Task ImportAsync()
    {
        if (AppServices.UiTestFilePath is { } testPath) { await Store.ImportIntoSelectedVaultAsync(testPath); return; }
        var file = await PickFileAsync(new FilePickerOptions(FileTypeFilter: new[] { SecretsExport.FileExtension }) { Title = "Import secrets" });
        if (file is null) return;
        await Store.ImportIntoSelectedVaultAsync(file.Path);
    }

    // ------------------------------------------------------------------ panels (overlay)

    void ClosePanels()
    {
        Store.CloseEditor();
        Store.CloseViewer();
        Store.CloseVaultForm();
        Store.CloseStoreSettings();
        Store.CloseSettings();
    }

    Element PanelContent(AppState s) =>
        s.Editor is { } e ? EditorPanel(e) :
        s.Viewer is { } v ? ViewerPanel(v) :
        s.VaultForm is { } f ? VaultFormPanel(f, s) :
        s.StoreSettings is { } st ? StoreSettingsPanel(st, s) :
        s.IsSettingsOpen ? SettingsPanel(s.Settings) :
        Empty();

    Element SettingsPanel(AppSettings st)
    {
        void Set(Func<AppSettings, AppSettings> change) => _ = Store.UpdateSettingsAsync(change);
        static int[] With(IEnumerable<int> choices, int current) => choices.Contains(current) ? [.. choices] : [.. choices, current];

        var themes = AppThemes.All;
        var lockChoices = With(AppStore.AutoLockChoices, st.AutoLockMinutes);
        var clipChoices = With(AppStore.ClipboardChoices, st.ClipboardClearSeconds);
        return VStack(12,
            Subtitle("Settings"),
            ComboBox(themes.Select(AppThemes.DisplayName).ToArray(), themes.ToList().IndexOf(st.Theme),
                    i => { if (i >= 0 && themes[i] != Store.State.Settings.Theme) Set(x => x with { Theme = themes[i] }); })
                .Header("Theme").AutomationId("ThemeCombo").HAlign(HorizontalAlignment.Stretch),
            ComboBox(lockChoices.Select(m => m == 0 ? "Never" : $"After {m} minute(s) without activity").ToArray(), Array.IndexOf(lockChoices, st.AutoLockMinutes),
                    i => { if (i >= 0 && lockChoices[i] != Store.State.Settings.AutoLockMinutes) _ = Store.SetAutoLockMinutesAsync(lockChoices[i]); })
                .Header("Auto-lock").AutomationId("AutoLockCombo").HAlign(HorizontalAlignment.Stretch),
            Caption("Locking closes open secrets, clears a copied value and locks SecretStore.").TextWrapping().Opacity(0.7),
            ComboBox(clipChoices.Select(x => $"After {x} seconds").ToArray(), Array.IndexOf(clipChoices, st.ClipboardClearSeconds),
                    i => { if (i >= 0 && clipChoices[i] != Store.State.Settings.ClipboardClearSeconds) Set(x => x with { ClipboardClearSeconds = clipChoices[i] }); })
                .Header("Clear copied values from the clipboard").AutomationId("ClipboardCombo").HAlign(HorizontalAlignment.Stretch),
            CheckBox(st.MinimizeToTray, v => Set(x => x with { MinimizeToTray = v }), "Minimize to the notification area (tray icon)")
                .AutomationId("TrayCheck"),
            CheckBox(st.CheckForUpdates, v => Set(x => x with { CheckForUpdates = v }), "Check GitHub for a new version at startup")
                .AutomationId("UpdateCheck"),
            Caption(JsonFileSettingsStore.DefaultPath).FontFamily("Consolas").Opacity(0.6),
            Caption($"PowerShell Secrets {typeof(AppServices).Assembly.GetName().Version?.ToString(3)}").Opacity(0.6).AutomationId("VersionText"),
            // AGPL section 5(d): interactive programs show the license, the lack of warranty and where to get the source.
            Caption("Copyright (C) 2026 Angelo Dias. Free software under the GNU AGPL v3 or later, with ABSOLUTELY NO WARRANTY.")
                .TextWrapping().Opacity(0.6).AutomationId("LicenseText"),
            HStack(4,
                HyperlinkButton("Source code", new Uri("https://github.com/amsdias/secrets-manager")).Padding(4, 0, 4, 0).AutomationId("SourceLink"),
                HyperlinkButton("License", new Uri("https://www.gnu.org/licenses/agpl-3.0.html")).Padding(4, 0, 4, 0).AutomationId("LicenseLink")),
            Buttons(Button("Close", Store.CloseSettings).AccentButton().AutomationId("SettingsCloseButton"))
        ).MinWidth(480).AutomationId("SettingsPanel");
    }

    static Element? ErrorText(string? error) =>
        error is null ? null : TextBlock(error).TextWrapping().Foreground(Theme.SystemCritical).AutomationId("PanelError");

    static Element Buttons(params Element?[] buttons) => HStack(8, buttons!).HAlign(HorizontalAlignment.Right).Margin(0, 8, 0, 0);

    Element EditorPanel(EditorState e)
    {
        var kinds = EditorState.EditableKinds;
        void Edit(Func<EditorState, EditorState> f) => Store.UpdateEditor(f);

        Element SecretField(string value, Action<string> set, string header, string id) => e.Reveal
            ? TextBox(value, set, header: header).AutomationId(id).WithKey(id + "-plain")
            : PasswordBox(value, set).Header(header).AutomationId(id).WithKey(id + "-masked");

        Element revealAndGenerate = HStack(12,
            CheckBox(e.Reveal, v => Edit(x => x with { Reveal = v }), "Show").AutomationId("RevealCheck"),
            Button("Generate", Store.GeneratePassword).ToolTip("Fill with a random 24-character password").AutomationId("GenerateButton"));

        Element valueEditor = e.Kind switch
        {
            SecretKind.String => TextBox(e.Text, v => Edit(x => x with { Text = v }), header: "Value")
                .AcceptsReturn().TextWrapping().MinHeight(90).AutomationId("ValueBox"),
            SecretKind.SecureString => VStack(8,
                SecretField(e.Text, v => Edit(x => x with { Text = v }), "Value", "SecureBox"),
                revealAndGenerate,
                Caption("Get-Secret returns this as [securestring] unless -AsPlainText is used.").Opacity(0.7)),
            SecretKind.PSCredential => VStack(8,
                TextBox(e.UserName, v => Edit(x => x with { UserName = v }), header: "User name").AutomationId("UserBox"),
                SecretField(e.Password, v => Edit(x => x with { Password = v }), "Password", "CredPasswordBox"),
                revealAndGenerate),
            SecretKind.Hashtable => TextBox(e.HashText, v => Edit(x => x with { HashText = v }), header: "Entries (key=value per line; values are stored as strings)")
                .AcceptsReturn().MinHeight(120).AutomationId("HashBox"),
            SecretKind.ByteArray => VStack(8,
                HStack(12,
                    Button("Load file…", async () => await LoadFileAsync()).AutomationId("LoadFileButton"),
                    Caption(e.BytesInfo.Length > 0 ? e.BytesInfo : "Base64 content, or load a file (max 1 MB).").Opacity(0.7).VAlign(VerticalAlignment.Center)),
                TextBox(e.BytesBase64, v => Edit(x => x with { BytesBase64 = v }), header: "Base64")
                    .AcceptsReturn().TextWrapping().MinHeight(120).FontFamily("Consolas").AutomationId("BytesBox")),
            _ => Empty(),
        };

        return VStack(12,
            Subtitle(e.IsNew ? "New secret" : "Edit secret"),
            Caption($"Vault: {e.VaultName}").Opacity(0.7),
            TextBox(e.Name, v => Edit(x => x with { Name = v }), header: "Name").AutomationId("NameBox"),
            ComboBox(kinds.Select(k => k.ToString()).ToArray(), kinds.ToList().IndexOf(e.Kind), i => Edit(x => x with { Kind = kinds[i] }))
                .Header("Type").AutomationId("TypeCombo").HAlign(HorizontalAlignment.Stretch),
            valueEditor,
            TextBox(e.MetadataText, v => Edit(x => x with { MetadataText = v }), header: "Metadata (optional, key=value per line, not encrypted; expires=2027-01-31 warns before it expires)")
                .AcceptsReturn().MinHeight(70).AutomationId("MetadataBox"),
            ErrorText(e.Error),
            Buttons(
                Button(e.IsSaving ? "Saving…" : "Save", () => _ = Store.SaveEditorAsync()).AccentButton().IsEnabled(!e.IsSaving).AutomationId("SaveButton"),
                Button("Cancel", Store.CloseEditor).AutomationId("CancelButton"))
        ).MinWidth(480).AutomationId("EditorPanel");
    }

    async Task LoadFileAsync()
    {
        var file = await PickFileAsync(new FilePickerOptions(FileTypeFilter: new[] { "*" }) { Title = "Load file as secret" });
        if (file is null) return;
        Store.LoadEditorBytes(await File.ReadAllBytesAsync(file.Path), file.Path);
    }

    Element ViewerPanel(ViewerState v)
    {
        var value = v.Value;
        var masked = value is CredentialSecret c ? $"UserName: {c.UserName}{Environment.NewLine}Password: {Mask}" : Mask;
        var metadata = v.Entry.MetadataText;
        return VStack(12,
            Subtitle(v.Entry.Name).AutomationId("ViewerTitle"),
            Caption($"{value.Kind} in vault '{v.Entry.VaultName}'").Opacity(0.7),
            metadata.Length > 0 ? Caption(metadata).FontFamily("Consolas").Opacity(0.7) : null,
            TextBox(v.Reveal ? value.DisplayText : masked, _ => { }, header: "Value")
                .IsReadOnly().AcceptsReturn().TextWrapping().MinHeight(120).FontFamily("Consolas").AutomationId("ViewerValue"),
            CheckBox(v.Reveal, on => { if (on != v.Reveal) Store.ToggleViewerReveal(); }, "Reveal value").AutomationId("RevealValueCheck"),
            Buttons(
                value is CredentialSecret ? Button("Copy user name", () => _ = Store.CopyViewerUserNameAsync()).AutomationId("CopyUserButton") : null,
                Button(value switch { CredentialSecret => "Copy password", BytesSecret => "Copy Base64", _ => "Copy" }, () => _ = Store.CopyViewerValueAsync())
                    .AccentButton().AutomationId("ViewerCopyButton"),
                value is BytesSecret b ? Button("Save to file…", async () => await SaveBytesAsync(v.Entry.Name, b.Data)).AutomationId("SaveFileButton") : null,
                Button("Close", Store.CloseViewer).AutomationId("CloseViewerButton"))
        ).MinWidth(480).AutomationId("ViewerPanel");
    }

    async Task SaveBytesAsync(string name, byte[] data)
    {
        var target = await PickSaveFileAsync(new SaveFilePickerOptions(new[] { new FileTypeChoice("All files", new[] { "." }) }, name));
        if (target is null) return;
        await File.WriteAllBytesAsync(target.Path, data);
    }

    Element VaultFormPanel(VaultFormState f, AppState s)
    {
        void Edit(Func<VaultFormState, VaultFormState> change) => Store.UpdateVaultForm(change);
        VaultKind[] kinds = [VaultKind.SecretStore, VaultKind.WindowsAccount, VaultKind.Other];
        string[] kindLabels = ["SecretStore", "Windows account (never asks)", "Other extension module"];
        var about = f.Kind switch
        {
            VaultKind.SecretStore => !s.SecretStoreAvailable ? "Microsoft.PowerShell.SecretStore is not installed."
                : (s.Vaults.FirstOrDefault(v => v.IsSecretStore) is { } existing
                    ? $"'{existing.Name}' already uses the SecretStore. There is one SecretStore per Windows user, so another " +
                      "SecretStore vault shows the same secrets. For a separate set of keys, choose Windows account."
                    : "Encrypted locally, with a password (which Windows Hello can unlock) or protected only by your Windows account. " +
                      "There is one SecretStore per Windows user. Good for production keys."),
            VaultKind.WindowsAccount => "Encrypted for your Windows account, with no password: the app, scripts and " +
                "'secrets get' read it without asking. Good for development keys. Anything running as you can read it.",
            _ => "Any installed SecretManagement extension (e.g. Az.KeyVault, SecretManagement.KeePass), by name or path.",
        };
        return VStack(12,
            Subtitle("Add vault"),
            Caption("Adding a vault with the name and type of one you removed shows its secrets again.").TextWrapping().Opacity(0.7),
            ComboBox(kindLabels, Array.IndexOf(kinds, f.Kind), i => { if (i >= 0) Edit(x => x with { Kind = kinds[i] }); })
                .Header("Type").AutomationId("VaultKindCombo").HAlign(HorizontalAlignment.Stretch),
            Caption(about).TextWrapping().Opacity(0.7),
            TextBox(f.Name, v => Edit(x => x with { Name = v }), header: "Name").AutomationId("VaultNameBox"),
            f.Kind == VaultKind.Other
                ? TextBox(f.ModuleName, v => Edit(x => x with { ModuleName = v }), header: "Extension module (name or path)").AutomationId("VaultModuleBox")
                : null,
            TextBox(f.Description, v => Edit(x => x with { Description = v }), header: "Description (optional)").AutomationId("VaultDescriptionBox"),
            f.Kind == VaultKind.Other
                ? TextBox(f.ParametersText, v => Edit(x => x with { ParametersText = v }), header: "Vault parameters (optional, key=value per line)")
                    .AcceptsReturn().MinHeight(60).AutomationId("VaultParametersBox")
                : null,
            CheckBox(f.MakeDefault, v => Edit(x => x with { MakeDefault = v }), "Make this the default vault").AutomationId("VaultDefaultCheck"),
            ErrorText(f.Error),
            Buttons(
                Button(f.IsSaving ? "Adding…" : "Add", () => _ = Store.SaveVaultFormAsync()).AccentButton().IsEnabled(!f.IsSaving).AutomationId("VaultSaveButton"),
                Button("Cancel", Store.CloseVaultForm).AutomationId("VaultCancelButton"))
        ).MinWidth(480).AutomationId("VaultFormPanel");
    }

    Element StoreSettingsPanel(StoreSettingsState st, AppState s)
    {
        void Edit(Func<StoreSettingsState, StoreSettingsState> change) => Store.UpdateStoreSettings(change);
        StoreAuthentication[] auths = [StoreAuthentication.Password, StoreAuthentication.None];
        StoreInteraction[] interactions = [StoreInteraction.None, StoreInteraction.Prompt];
        return VStack(12,
            Subtitle("SecretStore settings"),
            Caption("These apply to every vault that uses Microsoft.PowerShell.SecretStore (CurrentUser scope).").TextWrapping().Opacity(0.7),
            Caption(SecretsService.DefaultSecretStoreFolder).FontFamily("Consolas").Opacity(0.6),
            ComboBox(["Password", "None"], Array.IndexOf(auths, st.Authentication), i => Edit(x => x with { Authentication = auths[i] }))
                .Header("Authentication").AutomationId("AuthCombo").HAlign(HorizontalAlignment.Stretch),
            Caption("Password: encrypted with your password. None: protected only by your Windows account.").TextWrapping().Opacity(0.7),
            TextBox(st.TimeoutText, v => Edit(x => x with { TimeoutText = v }), header: "Password timeout (seconds)").AutomationId("TimeoutBox"),
            ComboBox(["None", "Prompt"], Array.IndexOf(interactions, st.Interaction), i => Edit(x => x with { Interaction = interactions[i] }))
                .Header("Interaction").AutomationId("InteractionCombo").HAlign(HorizontalAlignment.Stretch),
            Caption(st.Interaction == StoreInteraction.Prompt
                ? "Prompt: SecretStore asks on the console, which this app has none of; it will ask for the password itself. Choose None."
                : "None: SecretStore never prompts; this app asks for the password when the store is locked (recommended).").TextWrapping().Opacity(0.7),
            Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 4, 0, 4),
            s.HelloAvailable
                ? CheckBox(s.HelloEnabled, on => _ = Store.SetHelloEnabledAsync(on), "Unlock with Windows Hello")
                    .IsEnabled(st.Original.Authentication == StoreAuthentication.Password).AutomationId("HelloCheck")
                : Caption("Windows Hello isn't set up on this PC (Settings > Accounts > Sign-in options).").TextWrapping().Opacity(0.7),
            Caption(st.Original.Authentication == StoreAuthentication.Password
                ? "The store password is saved sealed by a Windows Hello key on this PC. Every unlock, in this app or with " +
                  "'secrets get', then asks for your PIN, face or fingerprint. Keep keys that should never ask in a Windows-account vault."
                : "Only for a store with a password.").TextWrapping().Opacity(0.7),
            ErrorText(st.Error),
            Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                HStack(8,
                    Button("Change password…", () => _ = Store.ChangeStorePasswordAsync())
                        .IsEnabled(st.Original.Authentication == StoreAuthentication.Password).AutomationId("ChangePasswordButton"),
                    Button("Reset store…", () => _ = Store.ResetStoreAsync()).Foreground(Theme.SystemCritical).AutomationId("ResetStoreButton")),
                HStack(8,
                    Button(st.IsSaving ? "Saving…" : "Save", () => _ = Store.SaveStoreSettingsAsync()).AccentButton().IsEnabled(!st.IsSaving).AutomationId("StoreSaveButton"),
                    Button("Close", Store.CloseStoreSettings).AutomationId("StoreCloseButton")).Grid(row: 0, column: 1)
            ).Margin(0, 8, 0, 0)
        ).MinWidth(480).AutomationId("StoreSettingsPanel");
    }
}
