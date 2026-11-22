using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Theme;
using Temno.Mcp;
using Windows.ApplicationModel.DataTransfer;

namespace Temno.App.Controls;

public sealed partial class McpSetupControl : UserControl
{
    private bool _loading = true;
    private bool _rotateArmed;
    // WP-191: the token is a credential; it stays masked in the field and the snippet until
    // Reveal is pressed. Copy always copies the real value.
    private bool _revealed;
    private string _selectedId = "cursor";
    private IReadOnlyList<McpClientSnippet> _snippets = [];

    private static string Mask(string token) =>
        token.Length <= 8 ? new string('\u2022', 8) : token[..4] + new string('\u2022', 12) + token[^4..];

    private string Display(string token) => _revealed ? token : Mask(token);

    private void RevealToken_Click(object sender, RoutedEventArgs e)
    {
        _revealed = RevealToken.IsChecked == true;
        Refresh();
    }

    public McpSetupControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            Refresh();
            _loading = false;
        };
    }

    private void EnabledSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        App.SetMcpEnabled(EnabledSwitch.IsOn);
        Refresh();
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
        => Copy(App.McpAuthToken is { } token ? $"{McpDefaults.AuthHeaderName}: {token}" : null);

    private void RotateToken_Click(object sender, RoutedEventArgs e)
    {
        if (!_rotateArmed)
        {
            _rotateArmed = true;
            ShowCopied("Click New token again to replace it. Existing client configs will need the new header.");
            return;
        }

        _rotateArmed = false;
        App.RotateMcpAuthToken();
        // T1 finding (WP-191): a replacement token must not inherit the Reveal state.
        _revealed = false;
        Refresh();
        ShowCopied("New token saved (shown masked). Update your client configs.");
    }

    private void Refresh()
    {
        _loading = true;
        EnabledSwitch.IsOn = App.McpPreferenceEnabled;
        EndpointBox.Text = McpDefaults.Endpoint;
        RevealToken.IsChecked = _revealed;
        TokenBox.Text = App.McpAuthToken is { } token
            ? $"{McpDefaults.AuthHeaderName}: {Display(token)}"
            : "(no token)";

        if (App.McpHttpRunning)
        {
            StatusDot.Fill = AppTheme.Status.SuccessBrush;
            StatusLabel.Text = $"Running on {McpDefaults.Host}:{McpDefaults.Port}";
            StatusLabel.Foreground = AppTheme.Text.PrimaryBrush;
        }
        else if (!App.McpPreferenceEnabled)
        {
            StatusDot.Fill = AppTheme.Text.MutedBrush;
            StatusLabel.Text = "Stopped";
            StatusLabel.Foreground = AppTheme.Text.TertiaryBrush;
        }
        else
        {
            StatusDot.Fill = AppTheme.Status.ErrorBrush;
            StatusLabel.Text = App.McpStartError is { } err
                ? $"Failed to bind ({err})"
                : "Not running";
            StatusLabel.Foreground = AppTheme.Status.ErrorBrush;
        }

        RebuildClientPicker();
        _loading = false;
    }

    private void RebuildClientPicker()
    {
        _snippets = McpClientSetup.ForCurrent(App.McpAuthToken);
        ClientPicker.Items.Clear();
        foreach (var snippet in _snippets)
            ClientPicker.Items.Add(new ComboBoxItem { Content = snippet.Name, Tag = snippet.Id });
        if (_snippets.Count == 0)
        {
            ClientPicker.IsEnabled = false;
            SnippetIntro.Text = "";
            SnippetBox.Text = "Waiting for a token…";
            return;
        }

        ClientPicker.IsEnabled = true;
        var index = 0;
        for (var i = 0; i < _snippets.Count; i++)
            if (_snippets[i].Id == _selectedId)
                index = i;
        _selectedId = _snippets[index].Id;
        ClientPicker.SelectedIndex = index;
        UpdateSnippetView();
    }

    private McpClientSnippet? SelectedSnippet
    {
        get
        {
            foreach (var snippet in _snippets)
                if (snippet.Id == _selectedId)
                    return snippet;
            return null;
        }
    }

    private void ClientPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClientPicker.SelectedItem is ComboBoxItem { Tag: string id })
            _selectedId = id;
        if (_loading)
            return;
        UpdateSnippetView();
    }

    private void UpdateSnippetView()
    {
        var snippet = SelectedSnippet;
        if (snippet is null)
            return;
        SnippetIntro.Text = snippet.Value.Intro;
        var body = snippet.Value.Body;
        SnippetBox.Text = App.McpAuthToken is { } t && !_revealed ? body.Replace(t, Mask(t)) : body;
    }

    private void CopySnippet_Click(object sender, RoutedEventArgs e)
    {
        // Copy always carries the real token, even while the view is masked.
        if (SelectedSnippet is { } snippet)
            Copy(snippet.Body);
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        ShowCopied("Copied");
    }

    private void ShowCopied(string message)
    {
        CopyStatus.Text = message;
        CopyStatus.Visibility = Visibility.Visible;
    }
}
