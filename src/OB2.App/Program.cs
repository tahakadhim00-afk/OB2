using OB2.App.Cli;

namespace OB2.App;

/// <summary>Entry point: <c>ob2.exe get …</c> runs headless; anything else starts the window.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (CliCommand.IsCli(args))
        {
            return CliCommand.Run(args);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
