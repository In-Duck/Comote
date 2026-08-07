using System.Text;

namespace Comote.InputBroker;

internal static class BrokerLog
{
    private const long MaximumLength = 1024 * 1024;
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        var line =
            $"{DateTimeOffset.UtcNow:O} {Sanitize(message)}{Environment.NewLine}";
        if (Environment.UserInteractive)
        {
            Console.Write(line);
        }

        try
        {
            lock (Sync)
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "ComoteInputBroker",
                    "Logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "InputBroker.log");
                if (File.Exists(path) &&
                    new FileInfo(path).Length > MaximumLength)
                {
                    File.Move(
                        path,
                        path + ".previous",
                        overwrite: true);
                }
                File.AppendAllText(
                    path,
                    line,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
        }
    }

    private static string Sanitize(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ');
}
