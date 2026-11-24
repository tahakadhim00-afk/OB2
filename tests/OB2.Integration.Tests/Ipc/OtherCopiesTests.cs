using OB2.Shared.Ipc;
using OB2.Shared.Protocol;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Ipc;

/// <summary>
/// D-92: a copy of OB2 opened by hand looks for one already running from another folder.
/// Each "copy" here is a folder with settings.json (and portable.flag for a portable one); a
/// running app is a pipe server on the pipe name in its settings.
/// </summary>
public sealed class OtherCopiesTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(500);

    private readonly TempDirectory _temp = new();
    private readonly List<PipeServer> _servers = [];

    private string AppData => Directory.CreateDirectory(_temp.Combine("appdata")).FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }

        _temp.Dispose();
    }

    /// <summary>A folder with OB2 in it: portable (settings beside it) or installed (settings in %APPDATA%\OB2).</summary>
    private string Copy(string name, string? pipe, bool portable = true)
    {
        var folder = Directory.CreateDirectory(_temp.Combine(name)).FullName;
        if (portable)
        {
            File.WriteAllText(Path.Combine(folder, "portable.flag"), "portable");
            if (pipe is not null)
            {
                WriteSettings(Path.Combine(folder, "settings.json"), pipe);
            }
        }
        else if (pipe is not null)
        {
            WriteSettings(Path.Combine(Directory.CreateDirectory(Path.Combine(AppData, "OB2")).FullName, "settings.json"), pipe);
        }

        return folder;
    }

    private static void WriteSettings(string file, string pipe) =>
        File.WriteAllText(file, $$"""{ "version": 3, "ipc": { "pipeName": "{{pipe}}" } }""");

    private static string NewPipe() => "OB2-test-" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>The app of a copy, running: something answering on its pipe.</summary>
    private void Run(string pipe)
    {
        var server = new PipeServer(pipe, new Answer());
        server.Start();
        _servers.Add(server);
    }

    [Fact]
    public async Task A_copy_running_from_another_folder_is_found()
    {
        var mine = Copy("mine", NewPipe());
        var otherPipe = NewPipe();
        var other = Copy("other", otherPipe);
        Run(otherPipe);

        var found = await OtherCopies.FindAsync(mine, AppData, [other + Path.DirectorySeparatorChar], Wait);

        found.Should().Be(new RunningCopy(other, otherPipe));
    }

    [Fact]
    public async Task This_copy_opened_again_is_not_another_copy()
    {
        var pipe = NewPipe();
        var mine = Copy("mine", pipe);
        Run(pipe);

        (await OtherCopies.FindAsync(mine, AppData, [mine.ToUpperInvariant()], Wait)).Should().BeNull("the instance lock and §8.5 handle a second start of the same copy");
    }

    [Fact]
    public async Task Two_installed_copies_share_their_data_folder_and_are_not_other_copies()
    {
        var pipe = NewPipe();
        var mine = Copy("installed-a", pipe, portable: false);
        var other = Copy("installed-b", pipe, portable: false);
        Run(pipe);

        (await OtherCopies.FindAsync(mine, AppData, [other], Wait)).Should().BeNull("both use %APPDATA%\\OB2, so §8.5 already hands over");
    }

    [Fact]
    public async Task A_copy_whose_app_does_not_answer_is_passed_over()
    {
        // Its settings name a pipe, but nothing listens: a command-line download, or exiting.
        var mine = Copy("mine", NewPipe());
        var silent = Copy("silent", NewPipe());
        var otherPipe = NewPipe();
        var other = Copy("other", otherPipe);
        Run(otherPipe);

        var found = await OtherCopies.FindAsync(mine, AppData, [silent, other], Wait);

        found.Should().Be(new RunningCopy(other, otherPipe));
    }

    [Fact]
    public async Task A_copy_without_settings_and_no_running_copies_find_nothing()
    {
        var mine = Copy("mine", NewPipe());
        var fresh = Copy("fresh", pipe: null);

        (await OtherCopies.FindAsync(mine, AppData, [fresh], Wait)).Should().BeNull();
        (await OtherCopies.FindAsync(mine, AppData, [], Wait)).Should().BeNull();
    }

    [Fact]
    public void The_test_process_is_not_an_OB2_copy() =>
        OtherCopies.RunningFolders().Should().NotContain(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            "the process asking is never listed");

    private sealed class Answer : IIpcHandler
    {
        public Task<ProtocolMessage> HandleAsync(ProtocolMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(ProtocolMessage.Simple(MessageTypes.Ack));
    }
}
