using System.Windows;
using System.Windows.Media.Animation;

namespace PenTastic;

partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Logo.Source = Theme.Logo(136);
        Version.Text = "Version " + (typeof(SplashWindow).Assembly.GetName().Version?.ToString(2) ?? "1.0");
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            var sweep = new DoubleAnimation(-Sweep.Width, ((FrameworkElement)Sweep.Parent).ActualWidth, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            SweepX.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, sweep);
        };
    }

    public void Say(string status) => Status.Text = status;

    public Task FadeOutAsync()
    {
        var done = new TaskCompletionSource();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => done.TrySetResult();
        BeginAnimation(OpacityProperty, fade);
        return done.Task;
    }
}
