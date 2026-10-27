using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShapeLine = System.Windows.Shapes.Line;
using ShapePolygon = System.Windows.Shapes.Polygon;
using ShapePolyline = System.Windows.Shapes.Polyline;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Windows.Threading;

namespace GenshinWishTracker;

public partial class MainWindow : Window
{
    private const string InteractiveMapUrl = "https://act.hoyolab.com/ys/app/interactive-map/index.html";
    private const string OfficialNewsUrl = "https://genshin.hoyoverse.com/en/news";
    private const string DiscordUrl = "https://discord.com/invite/WQknYbRKBt";
    private const string KofiUrl = "https://ko-fi.com/greyfoxdevelopment";
    private const string HoyolabHomeUrl = "https://www.hoyolab.com/home";
    private const int PageSize = 20;
    private const int DelayBetweenPagesMs = 350;
    private bool _mapInitializationStarted;
    private bool _newsInitializationStarted;
    private bool _supportInitializationStarted;
    private bool _buildWebViewInitializationStarted;
    private bool _hoyolabInitializationStarted;
    private bool _hoyolabRequestRunning;
    private DateTimeOffset _lastHoyolabRequestUtc;
    private DateTimeOffset _lastHoyolabUpdateUtc;
    private HoyolabDailyNote? _dailyNote;
    private readonly DispatcherTimer _resinCountdownTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _releaseRetryTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly HashSet<long> _handledReleaseSequences = new();
    private readonly HashSet<Version> _promptedUpdateVersions = new();
    private static readonly HashSet<string> StandardFiveStarCharacters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dehya", "Diluc", "Jean", "Keqing", "Mona", "Qiqi", "Tighnari"
    };
    private DateTime? _plannerHistoryAppliedSyncUtc;
    private bool _plannerHistoryApplied;
    private bool _plannerAutoSyncAttempted;
    private ReleaseNotificationClient? _releaseNotificationClient;
    private string? _buildProfileUrl;

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinWishTracker");
    private static readonly string HistoryPath = Path.Combine(AppDir, "wish_history.json");
    private static readonly string SettingsPath = Path.Combine(AppDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly BannerDef[] Banners =
    {
        new("character", "Character Event", "Limited 5★ · hard pity 90",  "301", 90, 74, Color.FromRgb(0xB7, 0x9C, 0xFF)),
        new("weapon",    "Weapon Event",    "Limited 5★ · hard pity 80",   "302", 80, 63, Color.FromRgb(0x6E, 0xC1, 0xFF)),
        new("standard",  "Standard Wanderlust", "Permanent · hard pity 90", "200", 90, 74, Color.FromRgb(0x8F, 0xE3, 0xA6)),
    };

    private static readonly ReleaseNote[] CurrentReleaseNotes =
    [
        new()
        {
            Title = "Primogem Planner pity and guarantee sync",
            Details = "Planner pity and featured guarantee are filled from saved Character Event wish history, with a live pity-curve chart and wishes-to-guarantee tracker."
        },
        new()
        {
            Title = "Live release notifications",
            Details = "The app checks the Genshin Companion release service every five minutes and listens for live update announcements. GitHub is checked once at launch and remains the source for verified update downloads."
        },
        new()
        {
            Title = "Wish tracking now includes 4-star pulls",
            Details = "See 4-star pull counts and your latest 4-star item for Character Event, Weapon Event, and Standard banners."
        },
        new()
        {
            Title = "Official Genshin Impact news",
            Details = "Browse the official news page in its own tab, with Back, Refresh, and Open in browser controls."
        },
        new()
        {
            Title = "Genshin Companion rebrand and updater fixes",
            Details = "Updated app and installer names, release assets, and update checks for the Genshin Companion release."
        }
    ];

    private static string BannerKeyOf(string gachaType) => gachaType switch
    {
        "301" or "400" => "character",
        "302" => "weapon",
        "200" => "standard",
        _ => "other"
    };

    private WishStore _store = new();
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        ReleaseNotesList.ItemsSource = CurrentReleaseNotes;
        ReleaseNotesSubtitle.Text = $"Changes in version {CurrentVersionText()}";
        ReleaseNotesStatusPanel.Visibility = Visibility.Collapsed;
        _releaseRetryTimer.Tick += ReleaseRetryTimer_Tick;
        Closed += MainWindow_Closed;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GenshinWishTracker/1.0");
        return client;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppDir);
        _settings = LoadJson<AppSettings>(SettingsPath) ?? new AppSettings();
        _store = LoadJson<WishStore>(HistoryPath) ?? new WishStore();
        RefreshUi();

        if (_store.Items.Count > 0)
        {
            string when = _store.LastSyncUtc?.ToLocalTime().ToString("g") ?? "never";
            SetStatus($"Loaded {_store.Items.Count} saved wishes · last sync: {when}");
        }
        else
        {
            SetStatus("No history yet. Open Paimon Menu → Wish → History in-game, then click Sync Wishes.");
        }

        UpdatePlanner();
        BuildUidInput.Text = _store.LastUid ?? "";
        _resinCountdownTimer.Tick += (_, _) => RenderDailyNote();
        await InitializeReleaseNotificationsAsync();
    }

    private async void HoyolabSignInButton_Click(object sender, RoutedEventArgs e)
    {
        HoyolabAuthView.Visibility = Visibility.Visible;
        HoyolabAuthPlaceholder.Visibility = Visibility.Collapsed;
        HoyolabStatusText.Text = "Opening HoYoLAB sign-in…";

        try
        {
            if (!_hoyolabInitializationStarted)
            {
                _hoyolabInitializationStarted = true;
                string profilePath = Path.Combine(AppDir, "WebView2");
                Directory.CreateDirectory(profilePath);
                HoyolabAuthView.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = profilePath
                };
                await HoyolabAuthView.EnsureCoreWebView2Async();
            }

            HoyolabAuthView.CoreWebView2.Navigate(HoyolabHomeUrl);
            HoyolabStatusText.Text = "Sign in on the HoYoLAB page, then click “Load accounts”.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                   or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _hoyolabInitializationStarted = false;
            HoyolabStatusText.Text =
                $"Could not open the HoYoLAB sign-in page. Check that the Microsoft Edge WebView2 Runtime is installed.\n{ex.Message}";
        }
    }

    private async void HoyolabLoadAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await BeginHoyolabRequestAsync()) return;

        try
        {
            string cookieHeader = await GetHoyolabCookieHeaderAsync();
            IReadOnlyList<HoyolabRole> roles = await HoyolabService.GetRolesAsync(cookieHeader);
            if (roles.Count == 0)
            {
                HoyolabRoleSelector.ItemsSource = null;
                HoyolabStatusText.Text =
                    "No global Genshin accounts were found for this HoYoLAB login. Confirm the correct account is signed in.";
                return;
            }

            HoyolabRoleSelector.ItemsSource = roles;
            HoyolabRoleSelector.DisplayMemberPath = nameof(HoyolabRole.DisplayName);
            HoyolabRoleSelector.SelectedIndex = 0;
            HoyolabStatusText.Text = $"Found {roles.Count} Genshin account(s). Loading real-time notes…";
            await LoadSelectedDailyNoteAsync(cookieHeader);
        }
        catch (HoyolabRequestException ex)
        {
            HoyolabStatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            HoyolabStatusText.Text = $"Could not load HoYoLAB accounts: {ex.Message}";
        }
        finally
        {
            EndHoyolabRequest();
        }
    }

    private async void HoyolabRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (HoyolabRoleSelector.SelectedItem is not HoyolabRole)
        {
            HoyolabStatusText.Text = "Load your HoYoLAB accounts and select a Genshin account first.";
            return;
        }

        if (!await BeginHoyolabRequestAsync()) return;
        try
        {
            string cookieHeader = await GetHoyolabCookieHeaderAsync();
            await LoadSelectedDailyNoteAsync(cookieHeader);
        }
        catch (HoyolabRequestException ex)
        {
            HoyolabStatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            HoyolabStatusText.Text = $"Could not refresh real-time notes: {ex.Message}";
        }
        finally
        {
            EndHoyolabRequest();
        }
    }

    private void HoyolabRoleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HoyolabRoleSelector.SelectedItem is HoyolabRole role)
            HoyolabStatusText.Text = $"Selected {role.Nickname} · UID {role.GameUid}. Click Refresh notes to update.";
    }

    private Task<bool> BeginHoyolabRequestAsync()
    {
        if (_hoyolabRequestRunning) return Task.FromResult(false);
        TimeSpan sinceLastRequest = DateTimeOffset.UtcNow - _lastHoyolabRequestUtc;
        if (_lastHoyolabRequestUtc != default && sinceLastRequest < TimeSpan.FromSeconds(10))
        {
            int wait = (int)Math.Ceiling((TimeSpan.FromSeconds(10) - sinceLastRequest).TotalSeconds);
            HoyolabStatusText.Text = $"Please wait {wait} second(s) before requesting HoYoLAB again.";
            return Task.FromResult(false);
        }

        _hoyolabRequestRunning = true;
        _lastHoyolabRequestUtc = DateTimeOffset.UtcNow;
        HoyolabLoadAccountsButton.IsEnabled = false;
        HoyolabRefreshButton.IsEnabled = false;
        HoyolabStatusText.Text = "Contacting HoYoLAB…";
        return Task.FromResult(true);
    }

    private void EndHoyolabRequest()
    {
        _hoyolabRequestRunning = false;
        HoyolabLoadAccountsButton.IsEnabled = true;
        HoyolabRefreshButton.IsEnabled = true;
    }

    private async Task<string> GetHoyolabCookieHeaderAsync()
    {
        if (!_hoyolabInitializationStarted || HoyolabAuthView.CoreWebView2 == null)
            throw new HoyolabRequestException("Click Sign in to HoYoLAB and sign in before loading accounts.");

        string[] cookieScopes =
        [
            "https://www.hoyolab.com/",
            "https://api-account-os.hoyolab.com/",
            "https://bbs-api-os.hoyolab.com/"
        ];
        string[] permittedCookieNames =
        [
            "ltoken", "ltoken_v2", "ltuid", "ltuid_v2",
            "cookie_token", "cookie_token_v2", "account_id", "account_id_v2"
        ];
        var cookiesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string scope in cookieScopes)
        {
            IReadOnlyList<CoreWebView2Cookie> cookies =
                await HoyolabAuthView.CoreWebView2.CookieManager.GetCookiesAsync(scope);
            foreach (CoreWebView2Cookie cookie in cookies)
            {
                if (permittedCookieNames.Contains(cookie.Name, StringComparer.OrdinalIgnoreCase) &&
                    !cookiesByName.ContainsKey(cookie.Name))
                    cookiesByName.Add(cookie.Name, cookie.Value);
            }
        }

        if (!cookiesByName.ContainsKey("ltoken") && !cookiesByName.ContainsKey("ltoken_v2"))
            throw new HoyolabRequestException("No HoYoLAB login was found. Sign in in the embedded page and retry.");

        return string.Join("; ", cookiesByName.Select(cookie => $"{cookie.Key}={cookie.Value}"));
    }

    private async Task LoadSelectedDailyNoteAsync(string cookieHeader)
    {
        if (HoyolabRoleSelector.SelectedItem is not HoyolabRole role)
        {
            HoyolabStatusText.Text = "Select a Genshin account before refreshing notes.";
            return;
        }

        _dailyNote = await HoyolabService.GetDailyNoteAsync(cookieHeader, role);
        _lastHoyolabUpdateUtc = DateTimeOffset.UtcNow;
        RenderDailyNote();
        _resinCountdownTimer.Start();
        HoyolabStatusText.Text = $"Updated {DateTime.Now.ToString("t")} · {role.Nickname} · UID {role.GameUid}.";
    }

    private void RenderDailyNote()
    {
        if (_dailyNote is not HoyolabDailyNote note) return;

        ResinAmountText.Text = $"{note.CurrentResin} / {note.MaxResin}";
        ResinRecoveryText.Text = note.CurrentResin >= note.MaxResin
            ? "At maximum"
            : $"Full in {FormatRemaining(note.ResinRecoveryTime)}";

        CommissionsText.Text = $"{note.FinishedTaskNum} / {note.TotalTaskNum}";
        CommissionRewardText.Text = note.IsExtraTaskRewardReceived
            ? "Completion reward claimed"
            : note.FinishedTaskNum >= note.TotalTaskNum
                ? "Claim the completion reward"
                : $"{Math.Max(0, note.TotalTaskNum - note.FinishedTaskNum)} commission(s) remaining";

        RealmCurrencyText.Text = $"{note.CurrentHomeCoin:N0} / {note.MaxHomeCoin:N0}";
        RealmCurrencyRecoveryText.Text = note.CurrentHomeCoin >= note.MaxHomeCoin
            ? "At maximum"
            : $"Full in {FormatRemaining(note.HomeCoinRecoveryTime)}";

        ExpeditionsText.Text = $"{note.CurrentExpeditionNum} / {note.MaxExpeditionNum}";
        IReadOnlyList<HoyolabExpedition> expeditions = note.Expeditions ?? [];
        int? nextExpedition = expeditions.Count == 0
            ? null
            : expeditions.Min(expedition => expedition.RemainedTime);
        ExpeditionsRecoveryText.Text = nextExpedition is null
            ? "No expeditions running"
            : nextExpedition <= 0
                ? "Expedition(s) complete"
                : $"Next complete in {FormatRemaining(nextExpedition.Value)}";
    }

    private string FormatRemaining(int seconds)
    {
        int elapsed = (int)Math.Max(0, (DateTimeOffset.UtcNow - _lastHoyolabUpdateUtc).TotalSeconds);
        int remaining = Math.Max(0, seconds - elapsed);
        TimeSpan time = TimeSpan.FromSeconds(remaining);
        return time.TotalDays >= 1
            ? $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m"
            : time.TotalHours >= 1
                ? $"{(int)time.TotalHours}h {time.Minutes}m"
                : $"{time.Minutes}m";
    }

    private async void BuildLookupButton_Click(object sender, RoutedEventArgs e) =>
        await LoadBuildShowcaseAsync(BuildUidInput.Text);

    private async void UseSavedUid_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_store.LastUid))
        {
            BuildStatusText.Text = "No saved wish UID is available. Enter your public in-game UID.";
            BuildStatusPanel.Visibility = Visibility.Visible;
            BuildOpenBrowserButton.Visibility = Visibility.Collapsed;
            return;
        }

        BuildUidInput.Text = _store.LastUid;
        await LoadBuildShowcaseAsync(_store.LastUid);
    }

    private async void BuildUidInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadBuildShowcaseAsync(BuildUidInput.Text);
    }

    private async Task LoadBuildShowcaseAsync(string input)
    {
        string uid = input.Trim();
        if (uid.Length != 9 || !uid.All(char.IsAsciiDigit))
        {
            BuildStatusText.Text = "Enter a valid 9-digit Genshin UID.";
            BuildStatusPanel.Visibility = Visibility.Visible;
            BuildOpenBrowserButton.Visibility = Visibility.Collapsed;
            return;
        }

        BuildLookupButton.IsEnabled = false;
        BuildStatusPanel.Visibility = Visibility.Visible;
        BuildOpenBrowserButton.Visibility = Visibility.Collapsed;
        BuildStatusText.Text = $"Loading Enka.Network showcase for UID {uid}…";
        _buildProfileUrl = $"https://enka.network/u/{uid}/";

        try
        {
            if (!_buildWebViewInitializationStarted)
            {
                string profilePath = Path.Combine(AppDir, "WebView2");
                Directory.CreateDirectory(profilePath);
                BuildWebView.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = profilePath
                };
                await BuildWebView.EnsureCoreWebView2Async();
                BuildWebView.CoreWebView2.NavigationStarting += BuildWebView_NavigationStarting;
                BuildWebView.CoreWebView2.NavigationCompleted += BuildWebView_NavigationCompleted;
                _buildWebViewInitializationStarted = true;
            }

            BuildWebView.CoreWebView2.Navigate(_buildProfileUrl);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                   or InvalidOperationException or IOException
                                   or UnauthorizedAccessException or ArgumentException)
        {
            BuildStatusText.Text =
                $"Could not initialize the embedded Enka.Network showcase. Install the Microsoft Edge WebView2 Runtime or open it in your browser.\n\n{ex.Message}";
            BuildOpenBrowserButton.Visibility = Visibility.Visible;
        }
        finally
        {
            BuildLookupButton.IsEnabled = true;
        }
    }

    private void BuildWebView_NavigationStarting(
        object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        BuildStatusPanel.Visibility = Visibility.Visible;
        BuildStatusText.Text = "Loading Enka.Network showcase…";
        BuildOpenBrowserButton.Visibility = Visibility.Collapsed;
    }

    private void BuildWebView_NavigationCompleted(
        object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            BuildStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        BuildStatusText.Text =
            $"The embedded showcase could not finish loading ({e.WebErrorStatus}). Check your connection or open it in your browser.";
        BuildOpenBrowserButton.Visibility = Visibility.Visible;
    }

    private void BuildOpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buildProfileUrl is not string profileUrl)
        {
            BuildStatusText.Text = "Load a valid UID before opening a showcase.";
            BuildStatusPanel.Visibility = Visibility.Visible;
            BuildOpenBrowserButton.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(profileUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, $"Could not open the showcase in your browser: {ex.Message}",
                "Character showcase", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlannerInput_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) UpdatePlanner();
    }

    private void PlannerInput_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdatePlanner();
    }

    private void UpdatePlanner()
    {
        if (PlannerPrimogemsSlider == null) return;

        int primogems = (int)Math.Round(PlannerPrimogemsSlider.Value);
        int fates = (int)Math.Round(PlannerFatesSlider.Value);
        int pity = (int)Math.Round(PlannerPitySlider.Value);
        bool guaranteed = PlannerGuaranteedCheckBox.IsChecked == true;
        int wishes = primogems / 160 + fates;
        double chance = CalculateFeaturedChance(wishes, pity, guaranteed, targetCopies: 1);
        int worstCaseWishes = guaranteed ? 90 - pity : 180 - pity;

        PlannerPrimogemsValue.Text = $"{primogems:N0} Primogems";
        PlannerFatesValue.Text = $"{fates} Fates";
        PlannerPityValue.Text = $"{pity} / 90";
        PlannerAvailableWishes.Text = wishes.ToString();
        PlannerChance.Text = $"{chance:P1}";
        PlannerChanceBar.Value = chance * 100;
        PlannerGuaranteePulls.Text = worstCaseWishes.ToString();

        bool guaranteedSecured = wishes >= worstCaseWishes;
        PlannerGuaranteeCard.Background = guaranteedSecured
            ? new SolidColorBrush(Color.FromRgb(31, 62, 50))
            : (Brush)FindResource("CardBrush");
        PlannerGuaranteedBadge.Visibility = guaranteedSecured ? Visibility.Visible : Visibility.Collapsed;

        RenderPlannerPityChart(Math.Min(90, pity + wishes));
    }

    private async Task InitializePlannerAsync()
    {
        ApplyPlannerHistory();
        if (_plannerAutoSyncAttempted) return;
        _plannerAutoSyncAttempted = true;

        PlannerHistoryStatus.Text = "Refreshing wish history from the local game cache…";
        SyncButton.IsEnabled = false;
        try
        {
            await SyncAsync();
            PlannerHistoryStatus.Text = "Wish history refreshed from the local game cache.";
        }
        catch (AuthKeyExpiredException)
        {
            PlannerHistoryStatus.Text = "The cached wish link is expired. Using saved wish history; click Sync Wishes to retry.";
        }
        catch (CacheNotFoundException ex)
        {
            PlannerHistoryStatus.Text = $"Could not refresh from the game cache: {ex.Message} Using saved history when available.";
        }
        catch (ApiException ex)
        {
            PlannerHistoryStatus.Text = $"Wish history refresh failed: {ex.Message} Using saved history when available.";
        }
        catch (TaskCanceledException)
        {
            PlannerHistoryStatus.Text = "Wish history refresh timed out. Using saved history when available.";
        }
        catch (HttpRequestException ex)
        {
            PlannerHistoryStatus.Text = $"Wish history refresh failed: {ex.Message} Using saved history when available.";
        }
        catch (InvalidDataException ex)
        {
            PlannerHistoryStatus.Text = $"Wish history refresh failed: {ex.Message} Using saved history when available.";
        }
        catch (IOException ex)
        {
            PlannerHistoryStatus.Text = $"Could not save refreshed wish history: {ex.Message} Using saved history when available.";
        }
        catch (UnauthorizedAccessException ex)
        {
            PlannerHistoryStatus.Text = $"Could not access wish history files: {ex.Message} Using saved history when available.";
        }
        finally
        {
            RefreshUi();
            SyncButton.IsEnabled = true;
        }
    }

    private void ApplyPlannerHistory()
    {
        if (_plannerHistoryApplied && _plannerHistoryAppliedSyncUtc == _store.LastSyncUtc)
            return;

        _plannerHistoryApplied = true;
        _plannerHistoryAppliedSyncUtc = _store.LastSyncUtc;

        List<WishItem> characterWishes = _store.Items
            .Where(item => item.Uid == _store.LastUid && BannerKeyOf(item.GachaType) == "character")
            .OrderBy(item => ParseId(item.Id))
            .ToList();

        if (characterWishes.Count == 0)
        {
            PlannerPitySlider.Value = 0;
            PlannerGuaranteedCheckBox.IsChecked = false;
            PlannerHistoryStatus.Text = "No saved Character Event wish history yet. Sync wishes to auto-fill pity.";
            UpdatePlanner();
            return;
        }

        int lastFiveStarIndex = characterWishes.FindLastIndex(item => item.RankType == "5");
        int currentPity = Math.Min(89, characterWishes.Count - lastFiveStarIndex - 1);
        bool guaranteed = lastFiveStarIndex >= 0 &&
            StandardFiveStarCharacters.Contains(characterWishes[lastFiveStarIndex].Name.Trim());

        PlannerPitySlider.Value = currentPity;
        PlannerGuaranteedCheckBox.IsChecked = guaranteed;
        PlannerHistoryStatus.Text = _store.LastSyncUtc is DateTime syncedAt
            ? $"Auto-filled from saved wish history · last sync {syncedAt.ToLocalTime():g}."
            : "Auto-filled from saved wish history.";
        UpdatePlanner();
    }

    private void PlannerPityChart_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RenderPlannerPityChart((int)Math.Min(90, Math.Round(PlannerPitySlider.Value +
            (int)Math.Round(PlannerPrimogemsSlider.Value) / 160 +
            (int)Math.Round(PlannerFatesSlider.Value))));

    private void RenderPlannerPityChart(int projectedPity)
    {
        if (PlannerPityChart == null) return;

        double width = PlannerPityChart.ActualWidth;
        double height = PlannerPityChart.ActualHeight;
        if (width <= 80 || height <= 80) return;

        const double left = 36;
        const double right = 10;
        const double top = 12;
        const double bottom = 28;
        double plotWidth = width - left - right;
        double plotHeight = height - top - bottom;
        PlannerPityChart.Children.Clear();

        foreach (int percent in new[] { 0, 25, 50, 75, 100 })
        {
            double y = top + plotHeight * (1 - percent / 100d);
            PlannerPityChart.Children.Add(new ShapeLine
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(55, 59, 82)),
                StrokeThickness = 1
            });
            AddPlannerChartLabel($"{percent}%", 0, y - 8, 31, TextAlignment.Right);
        }

        double softX = left + ((74 - 1) / 89d) * plotWidth;
        double hardX = left + plotWidth;
        AddPlannerChartMarker(softX, top, top + plotHeight, Color.FromRgb(232, 194, 106), true);
        AddPlannerChartMarker(hardX, top, top + plotHeight, Color.FromRgb(255, 122, 122), true);
        AddPlannerChartLabel("74", softX - 12, height - bottom + 7, 24, TextAlignment.Center);
        AddPlannerChartLabel("90", hardX - 22, height - bottom + 7, 24, TextAlignment.Right);

        var areaPoints = new PointCollection
        {
            new(left, top + plotHeight)
        };
        var curvePoints = new PointCollection();
        for (int pull = 1; pull <= 90; pull++)
        {
            double x = left + ((pull - 1) / 89d) * plotWidth;
            double chance = GetFiveStarChance(pull);
            double y = top + (1 - chance) * plotHeight;
            curvePoints.Add(new Point(x, y));
            areaPoints.Add(new Point(x, y));
        }
        areaPoints.Add(new Point(left + plotWidth, top + plotHeight));

        PlannerPityChart.Children.Add(new ShapePolygon
        {
            Points = areaPoints,
            Fill = new SolidColorBrush(Color.FromArgb(45, 89, 217, 142)),
            StrokeThickness = 0
        });
        PlannerPityChart.Children.Add(new ShapePolyline
        {
            Points = curvePoints,
            Stroke = new SolidColorBrush(Color.FromRgb(89, 217, 142)),
            StrokeThickness = 2.5
        });

        double trackerX = left + (Math.Clamp(projectedPity, 0, 90) / 90d) * plotWidth;
        AddPlannerChartMarker(trackerX, top, top + plotHeight, Color.FromRgb(255, 255, 255), false);
        AddPlannerChartLabel($"Projected: {projectedPity} pity",
            Math.Clamp(trackerX - 52, left, left + plotWidth - 108), 0, 108, TextAlignment.Center);
    }

    private void AddPlannerChartMarker(double x, double top, double bottom, Color color, bool dashed)
    {
        var marker = new ShapeLine
        {
            X1 = x,
            X2 = x,
            Y1 = top,
            Y2 = bottom,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = dashed ? 1 : 2
        };
        if (dashed)
            marker.StrokeDashArray = new DoubleCollection { 3, 3 };
        PlannerPityChart.Children.Add(marker);
    }

    private void AddPlannerChartLabel(string text, double left, double top, double width, TextAlignment alignment)
    {
        var label = new TextBlock
        {
            Text = text,
            Width = width,
            Foreground = new SolidColorBrush(Color.FromRgb(184, 188, 208)),
            FontSize = 10,
            TextAlignment = alignment
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        PlannerPityChart.Children.Add(label);
    }

    private static double CalculateFeaturedChance(int availableWishes, int currentPity, bool guaranteed, int targetCopies)
    {
        const int hardPity = 90;
        int remainingTargets = Math.Max(1, targetCopies);
        var states = new double[remainingTargets, hardPity, 2];
        states[0, currentPity, guaranteed ? 1 : 0] = 1;

        for (int wish = 0; wish < availableWishes; wish++)
        {
            var next = new double[remainingTargets, hardPity, 2];
            for (int obtained = 0; obtained < remainingTargets; obtained++)
            {
                for (int pity = 0; pity < hardPity; pity++)
                {
                    for (int isGuaranteed = 0; isGuaranteed <= 1; isGuaranteed++)
                    {
                        double stateChance = states[obtained, pity, isGuaranteed];
                        if (stateChance == 0) continue;

                        int nextPity = Math.Min(pity + 1, hardPity - 1);
                        double fiveStarChance = GetFiveStarChance(pity + 1);
                        next[obtained, nextPity, isGuaranteed] += stateChance * (1 - fiveStarChance);

                        if (isGuaranteed == 1)
                        {
                            if (obtained + 1 < remainingTargets)
                                next[obtained + 1, 0, 0] += stateChance * fiveStarChance;
                        }
                        else
                        {
                            if (obtained + 1 < remainingTargets)
                                next[obtained + 1, 0, 0] += stateChance * fiveStarChance * 0.5;
                            next[obtained, 0, 1] += stateChance * fiveStarChance * 0.5;
                        }
                    }
                }
            }
            states = next;
        }

        double noTargetChance = 0;
        for (int obtained = 0; obtained < remainingTargets; obtained++)
            for (int pity = 0; pity < hardPity; pity++)
                for (int guarantee = 0; guarantee <= 1; guarantee++)
                    noTargetChance += states[obtained, pity, guarantee];

        return Math.Clamp(1 - noTargetChance, 0, 1);
    }

    private static double GetFiveStarChance(int pullNumber)
    {
        if (pullNumber >= 90) return 1;
        if (pullNumber < 74) return 0.006;
        return Math.Min(1, 0.006 + (pullNumber - 73) * 0.06);
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, sender)) return;
        if (MainTabs.SelectedItem == PrimogemPlannerTab)
        {
            await InitializePlannerAsync();
            return;
        }
        if (MainTabs.SelectedItem == SupportTab)
        {
            await InitializeSupportAsync();
            return;
        }
        if (MainTabs.SelectedItem == NewsTab)
        {
            await InitializeNewsAsync();
            return;
        }
        if (MainTabs.SelectedItem != InteractiveMapTab) return;
        if (_mapInitializationStarted) return;
        _mapInitializationStarted = true;
        MapStatusPanel.Visibility = Visibility.Visible;
        MapStatusText.Text = "Initializing the embedded map…";

        try
        {
            string profilePath = Path.Combine(AppDir, "WebView2");
            Directory.CreateDirectory(profilePath);
            InteractiveMap.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = profilePath
            };

            await InteractiveMap.EnsureCoreWebView2Async();
            InteractiveMap.CoreWebView2.NavigationCompleted += InteractiveMap_NavigationCompleted;
            MapStatusText.Text = "Loading HoYoLAB Interactive Map…";
            InteractiveMap.CoreWebView2.Navigate(InteractiveMapUrl);
        }
        catch (Exception ex)
        {
            MapStatusText.Text =
                $"Could not initialize the embedded map. Install the Microsoft Edge WebView2 Runtime or open the map in your browser.\n\n{ex.Message}";
            MapOpenBrowserButton.Visibility = Visibility.Visible;
        }
    }

    private async Task InitializeNewsAsync()
    {
        if (_newsInitializationStarted) return;
        _newsInitializationStarted = true;
        NewsStatusPanel.Visibility = Visibility.Visible;
        NewsStatusText.Text = "Initializing the official news page…";

        try
        {
            string profilePath = Path.Combine(AppDir, "WebView2-News");
            Directory.CreateDirectory(profilePath);
            NewsWebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = profilePath
            };

            await NewsWebView.EnsureCoreWebView2Async();
            NewsWebView.CoreWebView2.NavigationCompleted += NewsWebView_NavigationCompleted;
            NewsStatusText.Text = "Loading official Genshin Impact news…";
            NewsWebView.CoreWebView2.Navigate(OfficialNewsUrl);
        }
        catch (Exception ex)
        {
            _newsInitializationStarted = false;
            NewsStatusText.Text =
                $"Could not initialize the embedded news page. Install the Microsoft Edge WebView2 Runtime or open it in your browser.\n\n{ex.Message}";
        }
    }

    private void NewsWebView_NavigationCompleted(
        object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        NewsBackButton.IsEnabled = NewsWebView.CoreWebView2?.CanGoBack == true;
        if (e.IsSuccess)
        {
            NewsStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        NewsStatusText.Text =
            $"The official news page could not finish loading ({e.WebErrorStatus}). Check your connection or open it in your browser.";
        NewsStatusPanel.Visibility = Visibility.Visible;
    }

    private void NewsBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (NewsWebView.CoreWebView2?.CanGoBack == true)
            NewsWebView.CoreWebView2.GoBack();
    }

    private async void NewsRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (NewsWebView.CoreWebView2 is null)
        {
            await InitializeNewsAsync();
            return;
        }

        NewsStatusText.Text = "Refreshing official Genshin Impact news…";
        NewsStatusPanel.Visibility = Visibility.Visible;
        NewsWebView.CoreWebView2.Reload();
    }

    private void NewsOpenBrowserButton_Click(object sender, RoutedEventArgs e) =>
        OpenInBrowser(OfficialNewsUrl, "official Genshin Impact news");

    // ---------------------------------------------------------------------
    // Dashboard release notes (update these when preparing a release)
    // ---------------------------------------------------------------------
    private static string CurrentVersionText()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? "unknown" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    // ---------------------------------------------------------------------
    // Discord / Ko-fi buttons and the Support tab
    // ---------------------------------------------------------------------
    private void DiscordButton_Click(object sender, RoutedEventArgs e) =>
        OpenInBrowser(DiscordUrl, "Discord");

    private void KofiButton_Click(object sender, RoutedEventArgs e) =>
        OpenInBrowser(KofiUrl, "Ko-fi");

    private void OpenInBrowser(string url, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, $"Could not open {what} in your browser: {ex.Message}",
                "Genshin Companion", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private const string KofiWidgetHtml = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          html, body { margin: 0; background: #12131C; }
          iframe { display: block; box-sizing: border-box; }
        </style>
        </head>
        <body>
        <iframe id='kofiframe' src='https://ko-fi.com/greyfoxdevelopment/?hidefeed=true&widget=true&embed=true&preview=true' style='border:none;width:100%;padding:4px;background:#f9f9f9;' height='712' title='greyfoxdevelopment'></iframe>
        </body>
        </html>
        """;

    private async Task InitializeSupportAsync()
    {
        if (_supportInitializationStarted) return;
        _supportInitializationStarted = true;
        SupportStatusPanel.Visibility = Visibility.Visible;
        SupportStatusText.Text = "Loading the Ko-fi widget…";

        try
        {
            // Its own profile keeps Ko-fi's cookies apart from the HoYoLAB sign-in.
            string profilePath = Path.Combine(AppDir, "WebView2", "Support");
            Directory.CreateDirectory(profilePath);
            SupportWebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = profilePath
            };
            SupportWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x12, 0x13, 0x1C);

            await SupportWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = SupportWebView.CoreWebView2;
            core.NavigationStarting += SupportWebView_NavigationStarting;
            core.NewWindowRequested += SupportWebView_NewWindowRequested;
            core.NavigationCompleted += SupportWebView_NavigationCompleted;
            core.NavigateToString(KofiWidgetHtml);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                   or InvalidOperationException or IOException
                                   or UnauthorizedAccessException or ArgumentException)
        {
            SupportStatusText.Text =
                $"Could not load the Ko-fi widget in the app. Install the Microsoft Edge WebView2 Runtime, or use the Ko-fi button.\n\n{ex.Message}";
        }
    }

    private void SupportWebView_NavigationStarting(
        object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The tab only ever hosts the widget page; anything else is refused.
        if (!IsAllowedSupportUri(e.Uri)) e.Cancel = true;
    }

    private static bool IsAllowedSupportUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme is "data" or "about") return true;
        return parsed.Scheme == Uri.UriSchemeHttps &&
               (parsed.Host.Equals("ko-fi.com", StringComparison.OrdinalIgnoreCase) ||
                parsed.Host.EndsWith(".ko-fi.com", StringComparison.OrdinalIgnoreCase));
    }

    private void SupportWebView_NewWindowRequested(
        object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Pop-ups (such as the donation page) open in the default browser, never inside the app.
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
            OpenInBrowser(uri.AbsoluteUri, "Ko-fi");
    }

    private void SupportWebView_NavigationCompleted(
        object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            SupportStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // A refused navigation is reported as cancelled; that is not a loading problem.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;

        SupportStatusText.Text =
            $"The Ko-fi widget could not finish loading ({e.WebErrorStatus}). Check your connection or use the Ko-fi button.";
        SupportStatusPanel.Visibility = Visibility.Visible;
    }

    private void InteractiveMap_NavigationCompleted(
        object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            MapStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        MapStatusText.Text =
            $"The embedded map could not finish loading ({e.WebErrorStatus}). Check your connection or open it in your browser.";
        MapOpenBrowserButton.Visibility = Visibility.Visible;
    }

    private void MapOpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(InteractiveMapUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, $"Could not open the map in your browser: {ex.Message}",
                "Interactive map", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool CanSelfUpdate()
    {
        string? executablePath = Environment.ProcessPath;
        string? applicationName = Assembly.GetExecutingAssembly().GetName().Name;
        return executablePath != null &&
            !string.IsNullOrEmpty(applicationName) &&
            string.Equals(Path.GetFileNameWithoutExtension(executablePath), applicationName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task InitializeReleaseNotificationsAsync()
    {
        if (!CanSelfUpdate()) return;

        _releaseNotificationClient = new ReleaseNotificationClient(
            _settings.LastReleaseSequence, HandleServerReleaseAsync);
        _releaseNotificationClient.ErrorOccurred += ReleaseNotificationClient_ErrorOccurred;

        bool serverAvailable = await _releaseNotificationClient.CheckLatestAndConnectAsync();
        if (!serverAvailable)
            SetStatus("Release service unavailable; checking GitHub for updates.");

        await CheckGitHubForUpdatesAsync();
        _releaseRetryTimer.Start();
    }

    private async void ReleaseRetryTimer_Tick(object? sender, EventArgs e)
    {
        if (_releaseNotificationClient == null) return;

        await _releaseNotificationClient.ReconnectAndCatchUpAsync();
    }

    private void ReleaseNotificationClient_ErrorOccurred(Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
            SetStatus($"Release service check failed: {exception.Message}", isError: true));
    }

    private async Task HandleServerReleaseAsync(PublicRelease release)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => HandleServerReleaseAsync(release)).Task.Unwrap();
            return;
        }

        ReleaseNotesList.ItemsSource =
        new[]
        {
            new ReleaseNote
            {
                Title = $"{release.Version} — {release.Title}",
                Details = string.IsNullOrWhiteSpace(release.Notes)
                    ? "No release notes were provided."
                    : release.Notes
            }
        };
        ReleaseNotesSubtitle.Text =
            $"Latest release · published {release.PublishedAt.ToLocalTime():g}";

        _settings.LastReleaseSequence = Math.Max(_settings.LastReleaseSequence, release.Sequence);
        try
        {
            SaveJson(SettingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save release-check progress: {ex.Message}", isError: true);
        }

        if (!_handledReleaseSequences.Add(release.Sequence)) return;

        string? executablePath = Environment.ProcessPath;
        if (executablePath == null) return;

        Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version
            ?? throw new InvalidDataException("The application version is unavailable.");
        if (!Version.TryParse(release.Version, out Version? releaseVersion) ||
            NormalizeVersion(releaseVersion).CompareTo(NormalizeVersion(currentVersion)) <= 0)
        {
            return;
        }

        if (!_promptedUpdateVersions.Add(NormalizeVersion(releaseVersion)))
            return;

        string notes = string.IsNullOrWhiteSpace(release.Notes) ? "(No release notes provided.)" : release.Notes;
        MessageBoxResult result = MessageBox.Show(
            this,
            $"Version {release.Version} is available (you have {CurrentVersionText()}).\n\n{release.Title}\n\n{notes}\n\nDownload and install it now?",
            "Genshin Companion update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            AvailableUpdate? update = await UpdateService.GetAvailableUpdateAsync(currentVersion, releaseVersion);
            if (update == null) return;

            SetStatus($"Downloading and verifying version {update.Tag}…");
            string downloadedPath = await UpdateService.DownloadAsync(update);
            UpdateService.InstallWhenClosed(downloadedPath, Environment.ProcessId, executablePath);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   or InvalidDataException or IOException or UnauthorizedAccessException
                                   or Win32Exception)
        {
            SetStatus($"Update check failed: {ex.Message}", isError: true);
        }
    }

    private async Task CheckGitHubForUpdatesAsync()
    {
        string? executablePath = Environment.ProcessPath;
        if (executablePath == null) return;

        try
        {
            Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version
                ?? throw new InvalidDataException("The application version is unavailable.");
            AvailableUpdate? update = await UpdateService.GetAvailableUpdateAsync(currentVersion);
            if (update == null) return;
            if (!_promptedUpdateVersions.Add(NormalizeVersion(update.Version))) return;

            MessageBoxResult result = MessageBox.Show(
                this,
                $"Version {update.Tag} is available (you have {CurrentVersionText()}). Download and install it now?",
                "Genshin Companion update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes) return;

            SetStatus($"Downloading and verifying version {update.Tag}…");
            string downloadedPath = await UpdateService.DownloadAsync(update);
            UpdateService.InstallWhenClosed(downloadedPath, Environment.ProcessId, executablePath);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   or InvalidDataException or IOException or UnauthorizedAccessException
                                   or Win32Exception)
        {
            SetStatus($"Update check failed: {ex.Message}", isError: true);
        }
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _releaseRetryTimer.Stop();
        if (_releaseNotificationClient != null)
            await _releaseNotificationClient.DisposeAsync();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            Point cursor = PointToScreen(e.GetPosition(this));
            WindowState = WindowState.Normal;
            Left = cursor.X - ActualWidth / 2;
            Top = cursor.Y - 14;
        }

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Foreground = Topmost ? (Brush)FindResource("GoldBrush") : new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xD0));
        PinButton.ToolTip = Topmost ? "Always on top: ON" : "Keep on top";
    }

    private void FolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the 'Genshin Impact Game' folder (the one containing the *_Data folder)" };
        if (dlg.ShowDialog(this) == true)
        {
            _settings.GameFolder = dlg.FolderName;
            SaveJson(SettingsPath, _settings);
            SetStatus($"Game folder saved: {dlg.FolderName}");
        }
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        try
        {
            await SyncAsync();
        }
        catch (AuthKeyExpiredException)
        {
            SetStatus("Authkey expired. In-game: Paimon Menu → Wish → History, then click Sync again.", isError: true);
            await TryManualLinkSyncAsync();
        }
        catch (CacheNotFoundException ex)
        {
            SetStatus(ex.Message, isError: true);
            await TryManualLinkSyncAsync();
        }
        catch (ApiException ex)
        {
            SetStatus(ex.Message, isError: true);
            await TryManualLinkSyncAsync();
        }
        catch (TaskCanceledException)
        {
            SetStatus("The request timed out. Check your connection and try again.", isError: true);
            await TryManualLinkSyncAsync();
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Network error: {ex.Message}", isError: true);
            await TryManualLinkSyncAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Unexpected error: {ex.Message}", isError: true);
            await TryManualLinkSyncAsync();
        }
        finally
        {
            RefreshUi();
            SyncButton.IsEnabled = true;
        }
    }

    private async Task TryManualLinkSyncAsync()
    {
        var dialog = new WishLinkFallbackWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.WishUrl)) return;

        try
        {
            string apiUrl = NormalizeManualWishUrl(dialog.WishUrl);
            SetStatus("Syncing from the wish link copied by PowerShell…");
            await SyncAsync(apiUrl);
        }
        catch (AuthKeyExpiredException)
        {
            SetStatus("The copied wish link has expired. Open Wish → History in-game, run the command again, and retry.", isError: true);
        }
        catch (ApiException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        catch (TaskCanceledException)
        {
            SetStatus("The request timed out. Check your connection and try again.", isError: true);
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Network error: {ex.Message}", isError: true);
        }
        catch (InvalidDataException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Unexpected error: {ex.Message}", isError: true);
        }
    }

    private async Task SyncAsync(string? suppliedApiUrl = null)
    {
        SetStatus("Step 1/3 · Scanning the game's web cache…");
        string apiUrl = suppliedApiUrl ?? await FindWishApiUrlAsync();

        var template = ParseQuery(apiUrl);
        string baseUrl = apiUrl[..apiUrl.IndexOf('?')];

        var known = new HashSet<string>(_store.Items.Select(i => i.Key));
        int totalAdded = 0;

        foreach (var banner in Banners)
        {
            SetStatus($"Step 2/3 · Fetching {banner.Title} history…");
            FetchResult result = await FetchBannerAsync(baseUrl, template, banner, known);

            foreach (var item in result.NewItems)
            {
                if (known.Add(item.Key))
                {
                    _store.Items.Add(item);
                    totalAdded++;
                }
            }

            if (result.Uid != null) _store.LastUid = result.Uid;
            _store.LastSyncUtc = DateTime.UtcNow;
            _store.Items.Sort(CompareItems);

            SaveJson(HistoryPath, _store);
        }

        SetStatus($"Step 3/3 · Done — {totalAdded} new wish(es) · {_store.Items.Count} stored in total.");
    }

    // =====================================================================
    // STEP 3 — Find a WORKING wish link (same strategy as MadeBaruna's paimon.moe script)
    //   1. Game folder  : read from output_log.txt (works for HoYoPlay / custom installs)
    //   2. Cache file   : newest webCaches\<ver>\Cache\Cache_Data\data_2 (then the other data_* files)
    //   3. Candidates   : split on "1/0/", keep chunks containing "webview_gacha",
    //                     take "https...game_biz=" from each, newest first
    //   4. Validate     : call the real API with every candidate until one returns retcode 0
    // The cache holds MANY old/expired links, so "take the last one" is not good enough.
    // =====================================================================

    private static readonly Regex HostRegex =
        new(@"^https://([A-Za-z0-9\-]+\.)*(hoyoverse|mihoyo)\.com/", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Drive-letter path ending in the game's data folder; the tempered dot stops it swallowing a second path.
    private static readonly Regex GameDirRegex = new(
        @"[A-Za-z]:[/\\](?:(?![A-Za-z]:[/\\]).)*?(?:GenshinImpact_Data|YuanShen_Data)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WishLinkRegex = new(@"https.+?game_biz=", RegexOptions.Compiled);

    private static readonly string[] DataFolderNames = { "GenshinImpact_Data", "YuanShen_Data" };
    private const int MaxLinkCandidates = 40;

    // Diagnostics (paths, counts, retcodes — never links or authkeys) saved to sync_log.txt
    private static readonly string SyncLogPath = Path.Combine(AppDir, "sync_log.txt");
    private readonly List<string> _syncLog = new();
    private void Diag(string line) => _syncLog.Add($"{DateTime.Now:HH:mm:ss}  {line}");

    private void WriteSyncLog()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllLines(SyncLogPath, _syncLog);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<string> FindWishApiUrlAsync()
    {
        _syncLog.Clear();
        try
        {
            // File scanning runs off the UI thread; API validation is async on the UI context.
            List<string> candidates = await Task.Run(CollectWishLinkCandidates);
            Diag($"{candidates.Count} wish link candidate(s) found in cache");

            if (candidates.Count == 0)
            {
                throw new CacheNotFoundException(
                    "No wish link in the game cache. In-game open Paimon Menu → Wish → History, wait for it to load, then Sync. " +
                    "(Details: %LocalAppData%\\GenshinWishTracker\\sync_log.txt)");
            }

            bool sawExpired = false;
            int tested = 0;
            foreach (string candidate in candidates)
            {
                tested++;
                SetStatus($"Step 1/3 · Checking wish link {tested}/{candidates.Count}…");

                int? retcode = await TestWishLinkAsync(candidate);
                Diag($"candidate {tested}: retcode {(retcode?.ToString() ?? "request failed")}");

                if (retcode == 0) return NormalizeWishApiUrl(candidate);
                if (retcode is -100 or -101) sawExpired = true;

                await Task.Delay(500);   // be gentle with the API while probing old links
            }

            if (sawExpired) throw new AuthKeyExpiredException();

            throw new CacheNotFoundException(
                $"Found {candidates.Count} link(s) in the cache but the API rejected all of them. " +
                "Reopen Wish History in-game and retry. (Details: %LocalAppData%\\GenshinWishTracker\\sync_log.txt)");
        }
        finally
        {
            WriteSyncLog();
        }
    }

    /// <summary>Returns the API retcode for a candidate link, or null if the request itself failed.</summary>
    private static async Task<int?> TestWishLinkAsync(string candidate)
    {
        try
        {
            string api = NormalizeWishApiUrl(candidate);
            var template = ParseQuery(api);
            string baseUrl = api[..api.IndexOf('?')];
            string url = BuildPageUrl(baseUrl, template, "301", 1, "0");
            GachaApiResponse resp = await GetPageAsync(url);
            return resp.Retcode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or ApiException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Reads the newest cache files and returns every plausible wish link, newest first.</summary>
    private List<string> CollectWishLinkCandidates()
    {
        List<string> cacheFiles = FindCacheFiles();
        if (cacheFiles.Count == 0)
        {
            throw new CacheNotFoundException(
                "Couldn't find the game's webCaches folder. Click the folder button and pick your game folder " +
                "(the one containing GenshinImpact_Data or YuanShen_Data). (Details: %LocalAppData%\\GenshinWishTracker\\sync_log.txt)");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in cacheFiles)
        {
            string text;
            try
            {
                // FileShare.ReadWrite|Delete so this works while the game is running.
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                text = Encoding.Latin1.GetString(ms.ToArray());   // 1 byte = 1 char, binary-safe
            }
            catch (IOException ex) { Diag($"cannot read {file}: {ex.Message}"); continue; }
            catch (UnauthorizedAccessException ex) { Diag($"no access to {file}: {ex.Message}"); continue; }

            string[] chunks = text.Split("1/0/");
            int before = result.Count;

            for (int i = chunks.Length - 1; i >= 0 && result.Count < MaxLinkCandidates; i--)
            {
                string chunk = chunks[i];
                if (!chunk.Contains("webview_gacha", StringComparison.Ordinal)) continue;

                Match m = WishLinkRegex.Match(chunk);
                if (!m.Success || !m.Value.Contains('?')) continue;

                if (seen.Add(m.Value)) result.Add(m.Value);
            }

            Diag($"{file}: {result.Count - before} candidate(s)");
            if (result.Count >= MaxLinkCandidates) break;
        }

        return result;
    }

    /// <summary>webCaches\(two newest version folders)\Cache\Cache_Data\ — data_2 first, then other data_* files.</summary>
    private List<string> FindCacheFiles()
    {
        var files = new List<string>();

        foreach (string dataDir in FindGameDataDirs())
        {
            string webCaches = Path.Combine(dataDir, "webCaches");
            if (!Directory.Exists(webCaches))
            {
                Diag($"no webCaches folder in {dataDir}");
                continue;
            }

            try
            {
                var versions = new DirectoryInfo(webCaches).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .Take(2);

                foreach (DirectoryInfo version in versions)
                {
                    string cacheData = Path.Combine(version.FullName, "Cache", "Cache_Data");
                    if (!Directory.Exists(cacheData)) continue;

                    var dataFiles = new DirectoryInfo(cacheData).GetFiles("data_*")
                        .OrderByDescending(f => f.Name.Equals("data_2", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(f => f.LastWriteTimeUtc);

                    foreach (FileInfo f in dataFiles) files.Add(f.FullName);
                }
            }
            catch (IOException ex) { Diag($"cannot list {webCaches}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Diag($"no access to {webCaches}: {ex.Message}"); }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>All existing GenshinImpact_Data / YuanShen_Data folders: user choice → output_log.txt → default paths.</summary>
    private List<string> FindGameDataDirs()
    {
        var dirs = new List<string>();

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            try
            {
                string trimmed = root.TrimEnd('\\', '/');
                if (DataFolderNames.Contains(Path.GetFileName(trimmed), StringComparer.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(trimmed)) dirs.Add(trimmed);   // the *_Data folder itself
                    return;
                }
                foreach (string name in DataFolderNames)
                {
                    string a = Path.Combine(trimmed, name);
                    string b = Path.Combine(trimmed, "Genshin Impact Game", name);
                    if (Directory.Exists(a)) dirs.Add(a);
                    if (Directory.Exists(b)) dirs.Add(b);
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        // 1) Folder the user chose with the folder button
        AddRoot(_settings.GameFolder);

        // 2) The game writes its own install path into output_log.txt (this is what paimon.moe's script does)
        string lowLevel = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "miHoYo");
        string[] logs =
        {
            Path.Combine(lowLevel, "Genshin Impact", "output_log.txt"),
            Path.Combine(lowLevel, "\u539f\u795e", "output_log.txt")          // China client
        };

        foreach (string log in logs)
        {
            if (!File.Exists(log)) { Diag($"log not found: {log}"); continue; }
            try
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.Contains("_Data", StringComparison.OrdinalIgnoreCase)) continue;
                    Match m = GameDirRegex.Match(line);
                    if (!m.Success) continue;

                    string dir = m.Value.Replace('/', '\\');
                    if (Directory.Exists(dir))
                    {
                        dirs.Add(dir);
                        Diag($"game folder from output_log.txt: {dir}");
                        break;
                    }
                }
            }
            catch (IOException ex) { Diag($"cannot read {log}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Diag($"no access to {log}: {ex.Message}"); }
        }

        // 3) Common install locations, including the newer HoYoPlay launcher layout
        var roots = new List<string>();
        foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string pf = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(pf)) continue;
            roots.Add(Path.Combine(pf, "HoYoPlay", "games", "Genshin Impact game"));
            roots.Add(Path.Combine(pf, "Genshin Impact", "Genshin Impact Game"));
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            string r = drive.RootDirectory.FullName;
            roots.Add(Path.Combine(r, "HoYoPlay", "games", "Genshin Impact game"));
            roots.Add(Path.Combine(r, "Program Files", "HoYoPlay", "games", "Genshin Impact game"));
            roots.Add(Path.Combine(r, "Games", "HoYoPlay", "games", "Genshin Impact game"));
            roots.Add(Path.Combine(r, "Genshin Impact", "Genshin Impact Game"));
            roots.Add(Path.Combine(r, "Program Files", "Genshin Impact", "Genshin Impact Game"));
            roots.Add(Path.Combine(r, "Games", "Genshin Impact", "Genshin Impact Game"));
        }
        foreach (string root in roots) AddRoot(root);

        var distinct = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) Diag("no game data folder found anywhere");
        return distinct;
    }

    private static string NormalizeWishApiUrl(string url)
    {
        var builder = new UriBuilder(url)
        {
            Host = url.Contains("mihoyo.com", StringComparison.OrdinalIgnoreCase)
                ? "public-operation-hk4e.mihoyo.com"
                : "public-operation-hk4e-sg.hoyoverse.com",
            Path = "/gacha_info/api/getGachaLog",
            Fragment = ""
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeManualWishUrl(string url)
    {
        string candidate = url.Trim();
        if (!HostRegex.IsMatch(candidate) ||
            !candidate.Contains("authkey=", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException(
                "The clipboard does not contain a valid Genshin wish-history link. Run the PowerShell command first.");
        }

        return NormalizeWishApiUrl(candidate);
    }
    // =====================================================================
    // STEP 4 — API fetching with end_id pagination
    // =====================================================================
    private sealed record FetchResult(List<WishItem> NewItems, string? Uid);

    private async Task<FetchResult> FetchBannerAsync(
        string baseUrl,
        List<KeyValuePair<string, string>> template,
        BannerDef banner,
        HashSet<string> known)
    {
        var newItems = new List<WishItem>();
        string? uid = null;
        string endId = "0";
        int page = 1;
        while (true)
        {
            string url = BuildPageUrl(baseUrl, template, banner.FetchType, page, endId);
            GachaApiResponse resp = await GetPageAsync(url);
            if (resp.Retcode == -100 || resp.Retcode == -101) throw new AuthKeyExpiredException();
            if (resp.Retcode != 0) throw new ApiException($"API error {resp.Retcode}: {resp.Message}");
            var list = resp.Data?.List ?? new List<WishItem>();
            if (list.Count == 0) break;
            uid ??= list[0].Uid;
            bool reachedKnown = false;
            foreach (var item in list)
            {
                if (known.Contains(item.Key)) reachedKnown = true;
                else newItems.Add(item);
            }
            SetStatus($"Step 2/3 · {banner.Title} · page {page} · {newItems.Count} new");
            if (reachedKnown || list.Count < PageSize) break;
            endId = list[^1].Id;
            page++;
            await Task.Delay(DelayBetweenPagesMs);
        }
        return new FetchResult(newItems, uid);
    }

    private static string BuildPageUrl(
        string baseUrl,
        List<KeyValuePair<string, string>> template,
        string gachaType,
        int page,
        string endId)
    {
        var parts = new List<string>();
        foreach (var kv in template)
        {
            if (kv.Key is "gacha_type" or "page" or "size" or "end_id") continue;
            parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        }
        parts.Add($"gacha_type={gachaType}");
        parts.Add($"page={page}");
        parts.Add($"size={PageSize}");
        parts.Add($"end_id={endId}");
        return baseUrl + "?" + string.Join("&", parts);
    }
    private static async Task<GachaApiResponse> GetPageAsync(string url)
    {
        for (int attempt = 0; ; attempt++)
        {
            using HttpResponseMessage http = await Http.GetAsync(url);
            http.EnsureSuccessStatusCode();
            string json = await http.Content.ReadAsStringAsync();
            var resp = JsonSerializer.Deserialize<GachaApiResponse>(json, JsonOpts)
            ?? throw new ApiException("The server returned an empty response.");
            if (resp.Retcode == -110 && attempt < 3)
            {
                await Task.Delay(2000 * (attempt + 1));
                continue;
            }
            return resp;
        }
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string url)
    {
        var list = new List<KeyValuePair<string, string>>();
        string query = url[(url.IndexOf('?') + 1)..];
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? "" : pair[(eq + 1)..];
            list.Add(new(Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
        }
        return list;
    }

    // =====================================================================
    // STEP 5 — Local JSON storage
    // =====================================================================
    private static T? LoadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch (JsonException)
        {
            try { File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}"); } catch { }
            return null;
        }
    }
    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(AppDir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    private static ulong ParseId(string id) => ulong.TryParse(id, out var v) ? v : 0;

    private static int CompareItems(WishItem a, WishItem b)
    {
        int c = string.CompareOrdinal(a.Uid, b.Uid);
        return c != 0 ? c : ParseId(a.Id).CompareTo(ParseId(b.Id));
    }
    // =====================================================================
    // STEP 6 — Pity maths + dashboard refresh
    // =====================================================================
    private void RefreshUi()
    {
        string? uid = _store.LastUid;
        var mine = _store.Items.Where(i => i.Uid == uid).ToList();
        CardsHost.ItemsSource = Banners
        .Select(b => ComputeStats(b, mine.Where(i => BannerKeyOf(i.GachaType) == b.Key)))
        .ToList();
        UidText.Text = uid == null ? "UID —" : $"UID {uid}";
        ApplyPlannerHistory();
        UpdatePlanner();
    }
    private static BannerStats ComputeStats(BannerDef def, IEnumerable<WishItem> items)
    {
        var ordered = items.OrderBy(i => ParseId(i.Id)).ToList();
        int pity = 0, fourPity = 0, fourCount = 0, fiveCount = 0;
        string lastFour = "—", lastFive = "—";
        foreach (var wish in ordered)
        {
            pity++;
            fourPity++;
            if (wish.RankType == "5")
            {
                fiveCount++;
                lastFive = $"{wish.Name}  ({pity} pulls)";
                pity = 0;
            }
            else if (wish.RankType == "4")
            {
                fourCount++;
                lastFour = $"{wish.Name}  ({fourPity} pulls)";
                fourPity = 0;
            }
        }
        var accent = new SolidColorBrush(def.Accent);
        accent.Freeze();
        return new BannerStats
        {
            Title = def.Title,
            Subtitle = def.Subtitle,
            Accent = accent,
            Pity = pity,
            HardPity = def.HardPity,
            SoftPity = def.SoftPity,
            FourStarPity = Math.Min(fourPity, 10),
            TotalPulls = ordered.Count,
            FourStarCount = fourCount,
            FiveStarCount = fiveCount,
            LastFourStar = lastFour,
            LastFiveStar = lastFive
        };
    }
    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(isError ? "ErrorBrush" : "MutedBrush");
        StatusText.ToolTip = text;
    }
}