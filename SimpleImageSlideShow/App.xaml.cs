using Microsoft.Extensions.DependencyInjection;

namespace SimpleImageSlideShow
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            MainPage = MauiProgram.Services.GetService<AppShell>() ?? new AppShell();
        }
    }
}
