using System.IO.Pipes;
using System.Text;

namespace AlphaChannel.WebViewHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--studio")
                return StudioHost.RunAsync(args[1]).GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--ensure-runtime")
                return EvergreenSetup.EnsureAsync(args[1]).GetAwaiter().GetResult();
            if (args.Length is not (3 or 4)) return 2;
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            Application.EnableVisualStyles();
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            using var audio = new NamedPipeClientStream(".", args[2], PipeDirection.Out, PipeOptions.Asynchronous);
            pipe.Connect(10000);
            audio.Connect(10000);
            using var transport = new WebViewTransport(pipe, audio);
            using var window = new WebViewWindow(transport, args[1], args.Length < 4 || args[3] != "false");
            Application.Run(window);
            return window.ExitCode;
        }
        catch { return 3; }
    }
}
