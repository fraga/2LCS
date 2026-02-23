using LCS.Cache;
using LCS.JsonObjects;
using LCS.Utils;
using Microsoft.Playwright;
using Spectre.Console;
using System.Net;
using System.Net.Http;
using System.Text;

namespace LCS;

internal static class Program
{
    private static readonly SessionState State = new();
    private static readonly TimeSpan BrowserLoginTimeout = TimeSpan.FromMinutes(6);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            Console.Error.WriteLine("2LCS TUI requires an interactive terminal.");
            return 1;
        }

        ApplySettingsUpgradeIfNeeded();
        URIHandler.RefreshUrls();

        if (CacheUtil.IsCachingEnabled())
        {
            CredentialsCacheHelper.LoadOffLineCredentials();
        }

        RenderBanner();
        if (URIHandler.DetectURILaunch(args))
        {
            AnsiConsole.MarkupLine("[yellow]URI-based launch is not supported in the terminal UI. Use the interactive menu instead.[/]");
        }

        var sessionEstablished = await EstablishSessionAsync();
        if (!sessionEstablished)
        {
            return 1;
        }

        await RunMainMenuAsync();

        if (CacheUtil.IsCachingEnabled() && CacheUtil.SaveCacheToStoreEnabled())
        {
            CredentialsCacheHelper.SaveCredentialsOffline();
        }

        State.Client?.Dispose();
        return 0;
    }

    private static void ApplySettingsUpgradeIfNeeded()
    {
        try
        {
            if (!Properties.Settings.Default.update)
            {
                return;
            }

            Properties.Settings.Default.Upgrade();
            Properties.Settings.Default.update = false;
            Properties.Settings.Default.Save();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Settings upgrade failed: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static void RenderBanner()
    {
        AnsiConsole.Write(new FigletText("2LCS TUI").Color(Color.Cyan1));
        AnsiConsole.Write(new Rule("[grey]Lifecycle Services Terminal Companion[/]").LeftJustified());
    }

    private static async Task<bool> EstablishSessionAsync()
    {
        while (true)
        {
            var menuOptions = new List<string>();
            if (!string.IsNullOrWhiteSpace(Properties.Settings.Default.cookie))
            {
                menuOptions.Add("Use saved cookie");
            }
            menuOptions.Add("Login with browser (Playwright)");
            menuOptions.Add("Paste cookie");
            menuOptions.Add("Edit LCS endpoints");
            menuOptions.Add("Exit");

            var sessionOption = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Authentication[/]: choose a session source")
                    .PageSize(10)
                    .AddChoices(menuOptions));

            switch (sessionOption)
            {
                case "Use saved cookie":
                    if (await BootstrapSessionAsync(Properties.Settings.Default.cookie))
                    {
                        return true;
                    }
                    break;
                case "Login with browser (Playwright)":
                    var cookieFromBrowser = await AcquireCookieFromBrowserAsync();
                    if (!string.IsNullOrWhiteSpace(cookieFromBrowser) &&
                        await BootstrapSessionAsync(cookieFromBrowser))
                    {
                        return true;
                    }
                    break;
                case "Paste cookie":
                    var rawCookie = AnsiConsole.Ask<string>("Paste your LCS cookie header:");
                    if (string.IsNullOrWhiteSpace(rawCookie))
                    {
                        AnsiConsole.MarkupLine("[yellow]Cookie cannot be empty.[/]");
                        break;
                    }

                    if (await BootstrapSessionAsync(rawCookie))
                    {
                        return true;
                    }
                    break;
                case "Edit LCS endpoints":
                    EditEndpoints();
                    break;
                default:
                    return false;
            }
        }
    }

    private static async Task<string> AcquireCookieFromBrowserAsync()
    {
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".2lcs",
            "playwright-profile");
        Directory.CreateDirectory(profileDir);

        IPlaywright playwright = null;
        IBrowserContext context = null;

        try
        {
            playwright = await Playwright.CreateAsync();
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                profileDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    ViewportSize = ViewportSize.NoViewport
                });

            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            var loginUrl = $"{URIHandler.LCS_URL.TrimEnd('/')}/Logon/AdLogon";
            await page.GotoAsync(loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            AnsiConsole.MarkupLine("[green]Browser opened.[/] Complete sign-in (including MFA) in that window.");
            AnsiConsole.MarkupLine("[grey]This screen will wait for an LCS /V2 URL, then capture session cookies.[/]");

            var loginDetected = await WaitForLoginCompletionAsync(page);
            if (!loginDetected)
            {
                var continueAnyway = AnsiConsole.Confirm("Login redirect was not detected. Try capturing cookies anyway?", false);
                if (!continueAnyway)
                {
                    return null;
                }
            }

            var urls = new[] { URIHandler.LCS_URL, URIHandler.LCS_UPDATE_URL, URIHandler.LCS_DIAG_URL };
            var cookies = await context.CookiesAsync(urls);

            var cookieHeader = string.Join(
                "; ",
                cookies
                    .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
                    .GroupBy(cookie => cookie.Name, StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .Select(cookie => $"{cookie.Name}={cookie.Value}"));

            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                AnsiConsole.MarkupLine("[red]No browser cookies were captured. Make sure you completed sign-in.[/]");
                return null;
            }

            return cookieHeader;
        }
        catch (PlaywrightException ex) when (IsMissingPlaywrightBrowserError(ex.Message))
        {
            _ = TryInstallPlaywrightChromium();
            return null;
        }
        catch (Exception ex)
        {
            ShowException("Browser login failed", ex);
            return null;
        }
        finally
        {
            if (context != null)
            {
                await context.CloseAsync();
            }
            playwright?.Dispose();
        }
    }

    private static async Task<bool> WaitForLoginCompletionAsync(IPage page)
    {
        var deadline = DateTime.UtcNow + BrowserLoginTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (page.IsClosed)
            {
                return false;
            }

            if (IsLcsV2Url(page.Url))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return IsLcsV2Url(page.Url);
    }

    private static bool IsLcsV2Url(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("/v2", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingPlaywrightBrowserError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("download new browsers", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
    }

    private static void ShowPlaywrightInstallHelp()
    {
        var ps1Path = Path.Combine(AppContext.BaseDirectory, "playwright.ps1");
        var shPath = Path.Combine(AppContext.BaseDirectory, "playwright.sh");

        AnsiConsole.MarkupLine("[yellow]Playwright browser binaries are not installed yet.[/]");
        if (File.Exists(shPath))
        {
            AnsiConsole.MarkupLine($"Run: [grey]\"{Markup.Escape(shPath)}\" install chromium[/]");
        }
        else if (File.Exists(ps1Path))
        {
            AnsiConsole.MarkupLine($"Run: [grey]pwsh \"{Markup.Escape(ps1Path)}\" install chromium[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Rebuild the project first, then run the generated Playwright install script from the output directory.[/]");
        }
    }

    private static bool TryInstallPlaywrightChromium()
    {
        var installNow = AnsiConsole.Confirm("Playwright browser is missing. Install Chromium now?", true);
        if (!installNow)
        {
            ShowPlaywrightInstallHelp();
            return false;
        }

        try
        {
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode == 0)
            {
                AnsiConsole.MarkupLine("[green]Chromium installed successfully. Run browser login again.[/]");
                return true;
            }

            AnsiConsole.MarkupLine($"[red]Playwright install failed with exit code {exitCode}.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Playwright install failed:[/] {Markup.Escape(ex.Message)}");
        }

        ShowPlaywrightInstallHelp();
        return false;
    }

    private static async Task<bool> BootstrapSessionAsync(string rawCookie)
    {
        var normalizedCookie = NormalizeCookie(rawCookie);
        if (string.IsNullOrWhiteSpace(normalizedCookie))
        {
            AnsiConsole.MarkupLine("[yellow]Cookie value is invalid.[/]");
            return false;
        }

        State.Client?.Dispose();
        var cookieContainer = new CookieContainer();
        cookieContainer.SetCookies(new Uri(URIHandler.LCS_URL), normalizedCookie);
        cookieContainer.SetCookies(new Uri(URIHandler.LCS_UPDATE_URL), normalizedCookie);
        cookieContainer.SetCookies(new Uri(URIHandler.LCS_DIAG_URL), normalizedCookie);

        var client = new HttpClientHelper(cookieContainer)
        {
            LcsUrl = URIHandler.LCS_URL,
            LcsUpdateUrl = URIHandler.LCS_UPDATE_URL,
            LcsDiagUrl = URIHandler.LCS_DIAG_URL
        };

        try
        {
            var projects = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Validating session and loading projects...", _ => client.GetAllProjectsAsync());

            State.Client = client;
            State.Projects.Clear();
            State.Projects.AddRange(projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase));
            State.SelectedProject = State.Projects.FirstOrDefault();

            if (State.SelectedProject != null)
            {
                ConfigureProjectContext(State.SelectedProject);
            }

            State.CheInstances.Clear();
            State.SaasInstances.Clear();

            Properties.Settings.Default.cookie = normalizedCookie;
            Properties.Settings.Default.Save();

            AnsiConsole.MarkupLine($"[green]Session ready.[/] Loaded {State.Projects.Count} projects.");
            if (State.SelectedProject != null)
            {
                AnsiConsole.MarkupLine($"[grey]Default project:[/] {Markup.Escape(State.SelectedProject.Name)}");
            }

            return true;
        }
        catch (Exception ex)
        {
            client.Dispose();
            ShowException("Session setup failed", ex);
            return false;
        }
    }

    private static async Task RunMainMenuAsync()
    {
        while (true)
        {
            RenderSessionSummary();
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Main Menu[/]")
                    .PageSize(12)
                    .AddChoices(
                    [
                        "Select project",
                        "Refresh environments",
                        "Show environments",
                        "Environment actions",
                        "Project users",
                        "Upcoming updates",
                        "Endpoint diagnostics (read-only)",
                        "Session settings",
                        "Exit"
                    ]));

            switch (action)
            {
                case "Select project":
                    SelectProject();
                    break;
                case "Refresh environments":
                    await RefreshEnvironmentsAsync();
                    break;
                case "Show environments":
                    ShowEnvironmentTable();
                    break;
                case "Environment actions":
                    await ShowEnvironmentActionsMenuAsync();
                    break;
                case "Project users":
                    ShowProjectUsers();
                    break;
                case "Upcoming updates":
                    await ShowUpcomingUpdatesAsync();
                    break;
                case "Endpoint diagnostics (read-only)":
                    await RunEndpointDiagnosticsAsync();
                    break;
                case "Session settings":
                    await ShowSessionSettingsAsync();
                    break;
                default:
                    return;
            }
        }
    }

    private static void RenderSessionSummary()
    {
        var summary = new Grid();
        summary.AddColumn();
        summary.AddColumn();
        summary.AddRow("[bold]LCS URL[/]", Markup.Escape(URIHandler.LCS_URL));
        summary.AddRow("[bold]Projects[/]", State.Projects.Count.ToString());
        summary.AddRow("[bold]Selected Project[/]", Markup.Escape(State.SelectedProject?.Name ?? "None"));
        summary.AddRow("[bold]Environments Cached[/]", State.EnvironmentEntries.Count.ToString());

        var panel = new Panel(summary)
        {
            Header = new PanelHeader("Session"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        };

        AnsiConsole.Write(panel);
    }

    private static void SelectProject()
    {
        if (State.Projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No projects are available in this session.[/]");
            return;
        }

        var choices = State.Projects.Select(project => new ProjectChoice(project)).ToList();
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ProjectChoice>()
                .Title("Select an LCS project")
                .PageSize(20)
                .UseConverter(choice => Markup.Escape(choice.Display))
                .AddChoices(choices));

        State.SelectedProject = selected.Project;
        ConfigureProjectContext(selected.Project);
        State.CheInstances.Clear();
        State.SaasInstances.Clear();
        AnsiConsole.MarkupLine($"[green]Project selected:[/] {Markup.Escape(selected.Project.Name)}");
    }

    private static async Task RefreshEnvironmentsAsync()
    {
        if (!EnsureProjectContext())
        {
            return;
        }

        List<CloudHostedInstance> cheInstances = [];
        List<CloudHostedInstance> saasInstances = [];

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Refreshing environments...", async _ =>
                {
                    var cheTask = State.Client!.GetCheInstancesAsync();
                    var saasTask = State.Client.GetHostedInstancesAsync();
                    await Task.WhenAll(cheTask, saasTask);
                    cheInstances = cheTask.Result
                        .OrderBy(instance => instance.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    saasInstances = saasTask.Result
                        .OrderBy(instance => instance.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                });

            State.CheInstances.Clear();
            State.CheInstances.AddRange(cheInstances);
            State.SaasInstances.Clear();
            State.SaasInstances.AddRange(saasInstances);
            AnsiConsole.MarkupLine($"[green]Refresh complete.[/] CHE: {cheInstances.Count}, SAAS: {saasInstances.Count}");
        }
        catch (Exception ex)
        {
            ShowException("Environment refresh failed", ex);
        }
    }

    private static void ShowEnvironmentTable()
    {
        if (!EnsureProjectContext())
        {
            return;
        }

        if (State.EnvironmentEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No environments loaded. Use 'Refresh environments' first.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("Environments");
        table.AddColumn("#");
        table.AddColumn("Type");
        table.AddColumn("Name");
        table.AddColumn("State");
        table.AddColumn("Action");
        table.AddColumn("App");
        table.AddColumn("Platform");
        table.AddColumn("Deployed By");

        var index = 1;
        foreach (var entry in State.EnvironmentEntries)
        {
            table.AddRow(
                index.ToString(),
                entry.Kind,
                Markup.Escape(Truncate(entry.Instance.DisplayName, 38)),
                GetStateMarkup(entry.Instance.DeploymentState),
                Markup.Escape(Truncate(ValueOrDash(entry.Instance.DeploymentAction), 22)),
                Markup.Escape(Truncate(ValueOrDash(entry.Instance.CurrentApplicationBuildVersion), 16)),
                Markup.Escape(Truncate(ValueOrDash(entry.Instance.CurrentPlatformVersion), 16)),
                Markup.Escape(Truncate(ValueOrDash(entry.Instance.DeployedBy), 22)));
            index++;
        }

        AnsiConsole.Write(table);
    }

    private static async Task ShowEnvironmentActionsMenuAsync()
    {
        if (!EnsureProjectContext())
        {
            return;
        }

        if (State.EnvironmentEntries.Count == 0)
        {
            await RefreshEnvironmentsAsync();
            if (State.EnvironmentEntries.Count == 0)
            {
                return;
            }
        }

        var choices = State.EnvironmentEntries
            .Select((entry, index) => new EnvironmentChoice(index + 1, entry))
            .ToList();

        var selectedEnvironment = AnsiConsole.Prompt(
            new SelectionPrompt<EnvironmentChoice>()
                .Title("Choose an environment")
                .PageSize(20)
                .UseConverter(choice => Markup.Escape(choice.Display))
                .AddChoices(choices));

        await ShowSingleEnvironmentActionsMenuAsync(selectedEnvironment.Entry);
    }

    private static async Task ShowSingleEnvironmentActionsMenuAsync(EnvironmentEntry entry)
    {
        while (true)
        {
            RenderEnvironmentSummary(entry);
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Environment action")
                    .PageSize(12)
                    .AddChoices(
                    [
                        "Start",
                        "Stop",
                        "Deallocate",
                        "Delete",
                        "Show credentials",
                        "Show RDP details",
                        "Show ongoing action",
                        "Open details in browser",
                        "Back"
                    ]));

            switch (action)
            {
                case "Start":
                    await ExecuteStartStopActionAsync(entry, "start", entry.Instance.CanStart);
                    break;
                case "Stop":
                    await ExecuteStartStopActionAsync(entry, "stop", entry.Instance.CanStop);
                    break;
                case "Deallocate":
                    await ExecuteStartStopActionAsync(entry, "deallocate", entry.Instance.CanDeallocate);
                    break;
                case "Delete":
                    await ExecuteDeleteActionAsync(entry);
                    break;
                case "Show credentials":
                    ShowCredentials(entry);
                    break;
                case "Show RDP details":
                    ShowRdpDetails(entry);
                    break;
                case "Show ongoing action":
                    await ShowOngoingActionAsync(entry);
                    break;
                case "Open details in browser":
                    OpenEnvironmentDetails(entry);
                    break;
                default:
                    return;
            }
        }
    }

    private static void RenderEnvironmentSummary(EnvironmentEntry entry)
    {
        var summary = new Grid();
        summary.AddColumn();
        summary.AddColumn();
        summary.AddRow("[bold]Name[/]", Markup.Escape(ValueOrDash(entry.Instance.DisplayName)));
        summary.AddRow("[bold]Type[/]", entry.Kind);
        summary.AddRow("[bold]Environment Id[/]", Markup.Escape(ValueOrDash(entry.Instance.EnvironmentId)));
        summary.AddRow("[bold]State[/]", Markup.Escape(entry.Instance.DeploymentState.ToString()));
        summary.AddRow("[bold]Status[/]", Markup.Escape(ValueOrDash(entry.Instance.DeploymentStatus)));
        summary.AddRow("[bold]Action[/]", Markup.Escape(ValueOrDash(entry.Instance.DeploymentAction)));

        var panel = new Panel(summary)
        {
            Header = new PanelHeader("Selected Environment"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        };

        AnsiConsole.Write(panel);
    }

    private static async Task ExecuteStartStopActionAsync(EnvironmentEntry entry, string actionName, bool isSupported)
    {
        if (!isSupported)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(actionName)} is not available for this environment.[/]");
            return;
        }

        var confirm = AnsiConsole.Confirm($"Run '{actionName}' on '{entry.Instance.DisplayName}'?", false);
        if (!confirm)
        {
            return;
        }

        try
        {
            var success = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Executing {actionName}...", _ => State.Client!.StartStopDeployment(entry.Instance, actionName));

            if (success)
            {
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(actionName)} request submitted successfully.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(actionName)} request was rejected by LCS.[/]");
            }
        }
        catch (Exception ex)
        {
            ShowException($"{actionName} failed", ex);
        }
    }

    private static async Task ExecuteDeleteActionAsync(EnvironmentEntry entry)
    {
        if (!entry.Instance.CanDelete)
        {
            AnsiConsole.MarkupLine("[yellow]Delete is not available for this environment.[/]");
            return;
        }

        var confirm = AnsiConsole.Confirm($"Delete '{entry.Instance.DisplayName}'?", false);
        if (!confirm)
        {
            return;
        }

        try
        {
            var success = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Deleting environment...", _ => State.Client!.DeleteEnvironment(entry.Instance));

            if (success)
            {
                AnsiConsole.MarkupLine("[green]Delete request submitted successfully.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Delete request was rejected by LCS.[/]");
            }
        }
        catch (Exception ex)
        {
            ShowException("Delete failed", ex);
        }
    }

    private static void ShowCredentials(EnvironmentEntry entry)
    {
        if (entry.Instance.Instances == null || entry.Instance.Instances.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No deployment items are available for credential lookup.[/]");
            return;
        }

        var itemChoice = entry.Instance.Instances.Length == 1
            ? entry.Instance.Instances[0]
            : AnsiConsole.Prompt(
                new SelectionPrompt<Instance>()
                    .Title("Choose deployment item")
                    .PageSize(10)
                    .UseConverter(instance => Markup.Escape($"{instance.ItemName} ({instance.MachineName})"))
                    .AddChoices(entry.Instance.Instances));

        try
        {
            var credentials = AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("Loading credentials...", _ => State.Client!.GetCredentials(entry.Instance.EnvironmentId, itemChoice.ItemName));

            if (credentials == null || credentials.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No credentials were returned.[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).Title("Credentials");
            table.AddColumn("Name");
            table.AddColumn("Value");
            foreach (var pair in credentials.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(Markup.Escape(pair.Key), Markup.Escape(pair.Value));
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            ShowException("Credential lookup failed", ex);
        }
    }

    private static void ShowRdpDetails(EnvironmentEntry entry)
    {
        try
        {
            var rdpRows = AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("Loading RDP details...", _ => State.Client!.GetRdpConnectionDetails(entry.Instance));

            if (rdpRows.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No RDP details returned for this environment.[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).Title("RDP Details");
            table.AddColumn("Machine");
            table.AddColumn("Address");
            table.AddColumn("Port");
            table.AddColumn("Domain");
            table.AddColumn("Username");
            table.AddColumn("Password");

            foreach (var row in rdpRows)
            {
                table.AddRow(
                    Markup.Escape(ValueOrDash(row.Machine)),
                    Markup.Escape(ValueOrDash(row.Address)),
                    Markup.Escape(ValueOrDash(row.Port)),
                    Markup.Escape(ValueOrDash(row.Domain)),
                    Markup.Escape(ValueOrDash(row.Username)),
                    Markup.Escape(ValueOrDash(row.Password)));
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            ShowException("RDP details failed", ex);
        }
    }

    private static async Task ShowOngoingActionAsync(EnvironmentEntry entry)
    {
        try
        {
            var actionDetails = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Loading ongoing action...", _ => State.Client!.GetOngoingActionDetailsAsync(entry.Instance));

            if (actionDetails == null)
            {
                AnsiConsole.MarkupLine("[yellow]No ongoing action is currently reported.[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).Title("Ongoing Action");
            table.AddColumn("Field");
            table.AddColumn("Value");
            table.AddRow("Name", Markup.Escape(ValueOrDash(actionDetails.Name)));
            table.AddRow("Type", Markup.Escape(ValueOrDash(actionDetails.ActionType)));
            table.AddRow("Status", Markup.Escape(actionDetails.Status.ToString()));
            table.AddRow("Started", Markup.Escape(ValueOrDash(actionDetails.StartDate)));
            table.AddRow("Completed", Markup.Escape(ValueOrDash(actionDetails.CompletionDate)));
            table.AddRow("Environment", Markup.Escape(ValueOrDash(actionDetails.EnvironmentName)));
            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            ShowException("Ongoing action lookup failed", ex);
        }
    }

    private static void OpenEnvironmentDetails(EnvironmentEntry entry)
    {
        try
        {
            var url = State.Client!.GetEnvironmentDetailsUrl(entry.Instance);
            WebBrowserHelper.OpenUri(url);
            AnsiConsole.MarkupLine($"[green]Opened:[/] {Markup.Escape(url)}");
        }
        catch (Exception ex)
        {
            ShowException("Could not open browser link", ex);
        }
    }

    private static void ShowProjectUsers()
    {
        if (!EnsureProjectContext())
        {
            return;
        }

        try
        {
            var users = AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("Loading project users...", _ => State.Client!.GetAllProjectUsers());

            if (users.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No project users returned.[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).Title("Project Users");
            table.AddColumn("Display Name");
            table.AddColumn("Email");
            table.AddColumn("Role");

            foreach (var user in users.OrderBy(user => user.UserProfile?.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(
                    Markup.Escape(ValueOrDash(user.UserProfile?.DisplayName)),
                    Markup.Escape(ValueOrDash(user.UserProfile?.Email)),
                    Markup.Escape(ValueOrDash(user.UserRoleDisplayText)));
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            ShowException("Project users lookup failed", ex);
        }
    }

    private static async Task ShowUpcomingUpdatesAsync()
    {
        if (!EnsureProjectContext())
        {
            return;
        }

        try
        {
            var updates = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Loading upcoming updates...", _ => State.Client!.GetUpcomingCalendarsAsync());

            if (updates == null || updates.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No upcoming update events were found.[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).Title("Upcoming Updates");
            table.AddColumn("Environment");
            table.AddColumn("Release");
            table.AddColumn("UTC Start");
            table.AddColumn("Downtime (min)");
            table.AddColumn("Status");

            foreach (var update in updates.OrderBy(update => update.UtcStartDateTime, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(
                    Markup.Escape(ValueOrDash(update.EnvironmentName)),
                    Markup.Escape(ValueOrDash(update.ReleaseName)),
                    Markup.Escape(ValueOrDash(update.UtcStartDateTime)),
                    update.DownTimeInMinutes.ToString(),
                    Markup.Escape(ValueOrDash(update.Status)));
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            ShowException("Upcoming updates lookup failed", ex);
        }
    }

    private static async Task RunEndpointDiagnosticsAsync()
    {
        if (!EnsureProjectContext())
        {
            return;
        }

        if (State.EnvironmentEntries.Count == 0)
        {
            await RefreshEnvironmentsAsync();
        }

        try
        {
            var checks = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Running endpoint diagnostics...", async _ =>
                {
                    var results = new List<EndpointCheckResult>
                    {
                        await RunEndpointCheckAsync("GetAllProjectsAsync", async () =>
                        {
                            var projects = await State.Client!.GetAllProjectsAsync();
                            return $"{projects?.Count ?? 0} project(s)";
                        }),
                        await RunEndpointCheckAsync("GetAllProjectUsers", () =>
                        {
                            var users = State.Client!.GetAllProjectUsers();
                            return Task.FromResult($"{users?.Count ?? 0} user(s)");
                        }),
                        await RunEndpointCheckAsync("GetUpcomingCalendarsAsync", async () =>
                        {
                            var updates = await State.Client!.GetUpcomingCalendarsAsync();
                            return $"{updates?.Count ?? 0} update event(s)";
                        }),
                        await RunEndpointCheckAsync("GetCheInstancesAsync", async () =>
                        {
                            var instances = await State.Client!.GetCheInstancesAsync();
                            return $"{instances?.Count ?? 0} environment(s)";
                        }),
                        await RunEndpointCheckAsync("GetHostedInstancesAsync", async () =>
                        {
                            var instances = await State.Client!.GetHostedInstancesAsync();
                            return $"{instances?.Count ?? 0} environment(s)";
                        })
                    };

                    var sampleEntries = new List<EnvironmentEntry>();
                    if (State.CheInstances.Count > 0)
                    {
                        sampleEntries.Add(new EnvironmentEntry("CHE", State.CheInstances[0]));
                    }
                    if (State.SaasInstances.Count > 0)
                    {
                        sampleEntries.Add(new EnvironmentEntry("SAAS", State.SaasInstances[0]));
                    }
                    if (sampleEntries.Count == 0 && State.EnvironmentEntries.Count > 0)
                    {
                        sampleEntries.Add(State.EnvironmentEntries[0]);
                    }

                    foreach (var sample in sampleEntries)
                    {
                        var label = $"{sample.Kind}:{sample.Instance.EnvironmentId}";
                        results.Add(await RunEndpointCheckAsync($"{label} GetOngoingActionDetailsAsync", async () =>
                        {
                            var action = await State.Client!.GetOngoingActionDetailsAsync(sample.Instance);
                            return action == null
                                ? "no ongoing action"
                                : ValueOrDash(action.ActionStatusText);
                        }));
                        results.Add(await RunEndpointCheckAsync($"{label} GetEnvironmentHistoryDetailsAsync", async () =>
                        {
                            var history = await State.Client!.GetEnvironmentHistoryDetailsAsync(sample.Instance);
                            return $"{history?.Count ?? 0} history item(s)";
                        }));
                        results.Add(await RunEndpointCheckAsync($"{label} GetRdpConnectionDetails", () =>
                        {
                            var rdpRows = State.Client!.GetRdpConnectionDetails(sample.Instance);
                            return Task.FromResult($"{rdpRows?.Count ?? 0} row(s)");
                        }));
                        results.Add(await RunEndpointCheckAsync($"{label} GetDiagEnvironmentId", () =>
                        {
                            var diagEnvironmentId = State.Client!.GetDiagEnvironmentId(sample.Instance);
                            return Task.FromResult(ValueOrDash(diagEnvironmentId));
                        }));
                    }

                    return results;
                });

            var table = new Table().Border(TableBorder.Rounded).Title("Endpoint Diagnostics");
            table.AddColumn("Endpoint");
            table.AddColumn("Status");
            table.AddColumn("Details");

            foreach (var check in checks)
            {
                table.AddRow(
                    Markup.Escape(check.Endpoint),
                    check.Success ? "[green]OK[/]" : "[red]FAIL[/]",
                    Markup.Escape(ValueOrDash(check.Details)));
            }

            AnsiConsole.Write(table);

            var passed = checks.Count(check => check.Success);
            var failed = checks.Count - passed;
            var summaryColor = failed == 0 ? "green" : "yellow";
            AnsiConsole.MarkupLine($"[{summaryColor}]Diagnostics complete.[/] Passed: {passed}, Failed: {failed}");
        }
        catch (Exception ex)
        {
            ShowException("Endpoint diagnostics failed", ex);
        }
    }

    private static async Task<EndpointCheckResult> RunEndpointCheckAsync(string endpoint, Func<Task<string>> check)
    {
        try
        {
            var details = await check();
            return new EndpointCheckResult(endpoint, true, details);
        }
        catch (Exception ex)
        {
            return new EndpointCheckResult(endpoint, false, GetDiagnosticErrorMessage(ex));
        }
    }

    private static string GetDiagnosticErrorMessage(Exception ex)
    {
        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue)
        {
            return $"{(int)httpRequestException.StatusCode.Value} {httpRequestException.StatusCode.Value}: {httpRequestException.Message}";
        }

        return ex.InnerException == null
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
    }

    private static async Task ShowSessionSettingsAsync()
    {
        while (true)
        {
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Session settings")
                    .PageSize(10)
                    .AddChoices(
                    [
                        "Re-authenticate via browser (Playwright)",
                        "Replace cookie",
                        "Edit LCS endpoints",
                        "Re-authenticate using saved cookie",
                        "Back"
                    ]));

            switch (action)
            {
                case "Re-authenticate via browser (Playwright)":
                    var cookieFromBrowser = await AcquireCookieFromBrowserAsync();
                    if (!string.IsNullOrWhiteSpace(cookieFromBrowser))
                    {
                        await BootstrapSessionAsync(cookieFromBrowser);
                    }
                    break;
                case "Replace cookie":
                    var cookie = AnsiConsole.Ask<string>("Paste updated cookie:");
                    if (!string.IsNullOrWhiteSpace(cookie))
                    {
                        await BootstrapSessionAsync(cookie);
                    }
                    break;
                case "Edit LCS endpoints":
                    EditEndpoints();
                    break;
                case "Re-authenticate using saved cookie":
                    if (string.IsNullOrWhiteSpace(Properties.Settings.Default.cookie))
                    {
                        AnsiConsole.MarkupLine("[yellow]No cookie is stored.[/]");
                        break;
                    }
                    await BootstrapSessionAsync(Properties.Settings.Default.cookie);
                    break;
                default:
                    return;
            }
        }
    }

    private static void EditEndpoints()
    {
        var lcsUrl = AskUrl("LCS URL", URIHandler.LCS_URL);
        var lcsDiagUrl = AskUrl("LCS Diagnostics URL", URIHandler.LCS_DIAG_URL);
        var lcsUpdateUrl = AskUrl("LCS Update URL", URIHandler.LCS_UPDATE_URL);
        var lcsFixUrl = AskUrl("LCS Fix URL", URIHandler.LCS_FIX_URL);

        Properties.Settings.Default.lcsURL = lcsUrl;
        Properties.Settings.Default.lcsDiagURL = lcsDiagUrl;
        Properties.Settings.Default.lcsUpdateURL = lcsUpdateUrl;
        Properties.Settings.Default.lcsFixURL = lcsFixUrl;
        Properties.Settings.Default.Save();
        URIHandler.RefreshUrls();
        AnsiConsole.MarkupLine("[green]Endpoints saved.[/]");
    }

    private static string AskUrl(string label, string currentValue)
    {
        while (true)
        {
            var value = AnsiConsole.Prompt(
                    new TextPrompt<string>($"{label}:")
                        .DefaultValue(currentValue))
                .Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                return value;
            }

            AnsiConsole.MarkupLine("[yellow]Please enter a valid absolute URL.[/]");
        }
    }

    private static bool EnsureProjectContext()
    {
        if (State.Client == null)
        {
            AnsiConsole.MarkupLine("[yellow]No active session. Re-authenticate from session settings.[/]");
            return false;
        }

        if (State.SelectedProject == null)
        {
            if (State.Projects.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No projects available in current session.[/]");
                return false;
            }

            State.SelectedProject = State.Projects[0];
        }

        ConfigureProjectContext(State.SelectedProject);
        return true;
    }

    private static void ConfigureProjectContext(LcsProject project)
    {
        if (State.Client == null)
        {
            return;
        }

        State.Client.ChangeLcsProjectId(project.Id.ToString());
        State.Client.LcsProjectTypeId = project.ProjectTypeId;
    }

    private static string NormalizeCookie(string rawCookie)
    {
        return rawCookie
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace(';', ',')
            .Trim()
            .Trim(',');
    }

    private static string GetStateMarkup(DeploymentState state)
    {
        return state switch
        {
            DeploymentState.Active => $"[green]{state}[/]",
            DeploymentState.Starting or DeploymentState.Stopping or DeploymentState.Servicing or DeploymentState.RestartingServices => $"[yellow]{state}[/]",
            DeploymentState.Deleting or DeploymentState.Deleted or DeploymentState.Disabled => $"[red]{state}[/]",
            _ => Markup.Escape(state.ToString())
        };
    }

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 3)
        {
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength - 3) + "...";
    }

    private static void ShowException(string context, Exception ex)
    {
        var message = ex.InnerException == null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(context)}:[/] {Markup.Escape(message)}");

        var lowered = message.ToLowerInvariant();
        if (lowered.Contains("unauthorized", StringComparison.Ordinal) ||
            lowered.Contains("forbidden", StringComparison.Ordinal) ||
            lowered.Contains("401", StringComparison.Ordinal) ||
            lowered.Contains("403", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[yellow]Your LCS cookie may be expired. Replace it from Session settings.[/]");
        }
    }

    private sealed class SessionState
    {
        public HttpClientHelper Client { get; set; }
        public List<LcsProject> Projects { get; } = [];
        public LcsProject SelectedProject { get; set; }
        public List<CloudHostedInstance> CheInstances { get; } = [];
        public List<CloudHostedInstance> SaasInstances { get; } = [];

        public List<EnvironmentEntry> EnvironmentEntries =>
        [
            .. CheInstances.Select(instance => new EnvironmentEntry("CHE", instance)),
            .. SaasInstances.Select(instance => new EnvironmentEntry("SAAS", instance))
        ];
    }

    private sealed record ProjectChoice(LcsProject Project)
    {
        public string Display => $"{Project.Name} (#{Project.Id}, {Project.OrganizationName})";
    }

    private sealed record EnvironmentChoice(int Index, EnvironmentEntry Entry)
    {
        public string Display =>
            $"{Index,3}. [{Entry.Kind}] {Entry.Instance.DisplayName} ({Entry.Instance.EnvironmentId})";
    }

    private sealed record EndpointCheckResult(string Endpoint, bool Success, string Details);

    private sealed record EnvironmentEntry(string Kind, CloudHostedInstance Instance);
}
