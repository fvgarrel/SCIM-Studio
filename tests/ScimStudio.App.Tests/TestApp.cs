using Avalonia;
using Avalonia.Headless;
using ScimStudio.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

// The language and the theme belong to the whole application; two tests switching them at once would each see the other's.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ScimStudio.App.Tests;

/// <summary>The application as the tests run it: headless, but drawn by Skia, so a frame can be captured and looked at.</summary>
public static class TestApp {
    public static AppBuilder BuildAvaloniaApp() {
        return AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
