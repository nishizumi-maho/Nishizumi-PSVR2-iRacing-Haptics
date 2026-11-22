using System.Reflection;
using PSVR2iRacingHaptics.App;
using PSVR2iRacingHaptics.Core.Configuration;
using PSVR2iRacingHaptics.Core.Services;

namespace PSVR2iRacingHaptics.App.SmokeTests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        ApplicationConfiguration.Initialize();
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PSVR2iRacingHaptics.App.SmokeTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            await MainWindowCanBeCreatedAsync(directory);
            await PendingProfileSelectionSurvivesStateRefreshAsync(directory);
            StartupFailureCreatesDiagnostic(directory);
            GitHubReleaseResponseIsValidated();
            Console.WriteLine("Result: 4/4 Windows application checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("[FAILED] Windows startup smoke test");
            Console.WriteLine(exception);
            return 1;
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task MainWindowCanBeCreatedAsync(string directory)
    {
        var paths = new AppPaths(
            Path.Combine(directory, "data"),
            Path.Combine(directory, "data", "settings.json"),
            Path.Combine(directory, "data", "logs"),
            Path.Combine(directory, "data", "recordings"),
            IsPortable: true);
        paths.EnsureDirectories();
        using var logger = new RotatingFileLogger(paths.LogsDirectory);
        var settingsService = new SettingsService(paths.SettingsFile, logger);
        var settings = await settingsService.LoadAsync();
        await using var coordinator = new AppCoordinator(
            paths,
            settings,
            settingsService,
            logger);
        using var form = new MainForm(coordinator);
        var handle = form.Handle;
        form.PerformLayout();
        form.Size = form.MinimumSize;
        form.PerformLayout();
        if (handle == IntPtr.Zero || !form.IsHandleCreated)
        {
            throw new InvalidOperationException(
                "The main window did not create a native Windows handle.");
        }
        if (form.Controls.Count == 0)
        {
            throw new InvalidOperationException(
                "The main window was created without its interface.");
        }
        Console.WriteLine("[OK] Main window can be created without Toolkit or iRacing.");
    }

    private static async Task PendingProfileSelectionSurvivesStateRefreshAsync(
        string directory)
    {
        var paths = new AppPaths(
            Path.Combine(directory, "profile-selection"),
            Path.Combine(directory, "profile-selection", "settings.json"),
            Path.Combine(directory, "profile-selection", "logs"),
            Path.Combine(directory, "profile-selection", "recordings"),
            IsPortable: true);
        paths.EnsureDirectories();
        using var logger = new RotatingFileLogger(paths.LogsDirectory);
        var settingsService = new SettingsService(paths.SettingsFile, logger);
        var settings = await settingsService.LoadAsync();
        await using var coordinator = new AppCoordinator(
            paths,
            settings,
            settingsService,
            logger);
        using var form = new MainForm(coordinator);
        _ = form.Handle;

        var combo = (ComboBox)typeof(MainForm)
            .GetField("_profileCombo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;
        static string ItemId(object? item) =>
            (string)item!.GetType().GetProperty("Id")!.GetValue(item)!;
        combo.SelectedItem = combo.Items.Cast<object>()
            .First(item => ItemId(item) == ProfileCatalog.StrongProfileId);

        // A routine status refresh (telemetry diagnostics, Toolkit polling)
        // arrives before the user clicks "Apply profile".
        typeof(MainForm)
            .GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [new AppRuntimeState
            {
                ActiveProfileId = coordinator.Settings.ActiveProfileId,
                ActiveProfileName = coordinator.Settings.ActiveProfile
            }]);

        if (ItemId(combo.SelectedItem) != ProfileCatalog.StrongProfileId)
        {
            throw new InvalidOperationException(
                "A state refresh reverted the pending profile selection to "
                + $"'{combo.SelectedItem}' before it could be applied.");
        }

        // An external change (for example an automatic car/track rule) must
        // still move the selector to the newly active profile.
        // Run off the form thread: awaiting here would capture the WinForms
        // synchronization context, which this console host never pumps.
        Task.Run(() => coordinator.ApplyProfileAsync(ProfileCatalog.GentleProfileId))
            .GetAwaiter()
            .GetResult();
        typeof(MainForm)
            .GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [new AppRuntimeState
            {
                ActiveProfileId = coordinator.Settings.ActiveProfileId,
                ActiveProfileName = coordinator.Settings.ActiveProfile
            }]);
        if (ItemId(combo.SelectedItem) != ProfileCatalog.GentleProfileId)
        {
            throw new InvalidOperationException(
                "The profile selector did not follow an externally applied profile.");
        }
        Console.WriteLine("[OK] A pending profile selection survives state refreshes.");
    }

    private static void StartupFailureCreatesDiagnostic(string directory)
    {
        var applicationDirectory = Path.Combine(directory, "portable-app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "portable.mode"),
            "Portable mode for startup smoke test.");
        var path = StartupFailureReporter.Write(
            new InvalidOperationException("Synthetic startup smoke-test failure."),
            applicationDirectory: applicationDirectory);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "The startup diagnostic file was not created.");
        }
        var report = File.ReadAllText(path);
        if (!report.Contains(
                "Synthetic startup smoke-test failure.",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The startup diagnostic did not contain the exception.");
        }
        Console.WriteLine("[OK] Early startup failures create a diagnostic log.");
    }

    private static void GitHubReleaseResponseIsValidated()
    {
        const string newerRelease = """
            {
              "tag_name": "v2.1.0",
              "html_url": "https://github.com/nishizumi-maho/Nishizumi-PSVR2-iRacing-Haptics/releases/tag/v2.1.0"
            }
            """;
        var available = ApplicationIntegrationService.ParseLatestRelease(
            newerRelease,
            "2.0.0+local-build");
        if (!available.UpdateAvailable
            || available.LatestVersion != "2.1.0"
            || !available.ReleaseUrl.EndsWith(
                "/releases/tag/v2.1.0",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A newer official GitHub release was not recognized correctly.");
        }

        const string currentReleaseWithUntrustedUrl = """
            {
              "tag_name": "v2.0.0",
              "html_url": "https://example.invalid/untrusted-download.exe"
            }
            """;
        var current = ApplicationIntegrationService.ParseLatestRelease(
            currentReleaseWithUntrustedUrl,
            "2.0.0");
        if (current.UpdateAvailable
            || current.ReleaseUrl !=
                "https://github.com/nishizumi-maho/Nishizumi-PSVR2-iRacing-Haptics/releases/latest")
        {
            throw new InvalidOperationException(
                "The release check did not reject an untrusted release URL.");
        }
        Console.WriteLine("[OK] GitHub release responses and URLs are validated.");
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not mask the validation result.
        }
    }
}
