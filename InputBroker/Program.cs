using Comote.InputBroker;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    BrokerSelfTest.Run();
    return;
}
if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    var server = new InputBrokerServer(BrokerLog.Write);
    await server.RunAsync(cancellation.Token);
    return;
}

if (Environment.UserInteractive)
{
    Console.Error.WriteLine(
        "Comote.InputBroker must run under the Windows Service Control " +
        "Manager. Use --console only inside a disposable test VM.");
    Environment.ExitCode = 2;
    return;
}

WindowsServiceDispatcher.Run(
    "ComoteInputBroker",
    token => new InputBrokerServer(BrokerLog.Write).RunAsync(token));
