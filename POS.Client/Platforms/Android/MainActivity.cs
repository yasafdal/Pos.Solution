using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace POS.Client
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            if (Window is not { } window)
                return;

            // Keep edge-to-edge consistent across Android API 21-36 and apply system insets explicitly.
            WindowCompat.SetDecorFitsSystemWindows(window, false);

            var rootBg = global::Android.Graphics.Color.Rgb(15, 23, 42); // Matches --pos-bg-root (#0f172a)
            var decorView = window.DecorView;
            decorView.SetBackgroundColor(rootBg);

            if (WindowCompat.GetInsetsController(window, decorView) is { } controller)
            {
                controller.AppearanceLightStatusBars = false;
                controller.AppearanceLightNavigationBars = false;
            }

            if (FindViewById<global::Android.Views.View>(global::Android.Resource.Id.Content) is { } contentView)
            {
                contentView.SetBackgroundColor(rootBg);
                ViewCompat.SetOnApplyWindowInsetsListener(contentView, new SystemBarInsetsListener());
                ViewCompat.RequestApplyInsets(contentView);
            }
        }

        private sealed class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
        {
            public WindowInsetsCompat? OnApplyWindowInsets(global::Android.Views.View? v, WindowInsetsCompat? insets)
            {
                if (v is null || insets is null)
                    return insets;

                var barsAndCutout = insets.GetInsets(
                    WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
                var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime());

                if (barsAndCutout is null)
                    return insets;

                int bottom = ime is not null
                    ? Math.Max(barsAndCutout.Bottom, ime.Bottom)
                    : barsAndCutout.Bottom;

                // Set absolute padding once on the native root view; returning Consumed prevents CSS double-insets.
                v.SetPadding(barsAndCutout.Left, barsAndCutout.Top, barsAndCutout.Right, bottom);
                return WindowInsetsCompat.Consumed;
            }
        }
    }
}
