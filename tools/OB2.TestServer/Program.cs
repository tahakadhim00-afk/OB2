using System.Globalization;
using OB2.Integration.Tests.Infrastructure;

// Usage: dotnet run --project tools/OB2.TestServer [port]
// Serves /file/{size}/{name}?options (see TestHttpServer) until Ctrl+C.
var port = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 18080;
await using var server = await TestHttpServer.StartAsync(port);
Console.WriteLine($"OB2 test server on {server.BaseUri}");
Console.WriteLine($"  e.g. {server.FileUrl(50L * 1024 * 1024, "sample.zip", "bps=1000000")}");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
