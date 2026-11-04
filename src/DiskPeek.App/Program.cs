using Velopack;

namespace DiskPeek.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Install/update hooks may exit here. Run before WPF or any application work.
        // A downloaded package must never be installed without another explicit consent.
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).Run();
        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
