namespace Host
{
    public static class InputBackendFactory
    {
        public const string EnvironmentVariableName =
            "COMOTE_INPUT_BACKEND";

        public static IInputBackend Create(
            IReadOnlyList<string> args,
            int screenLeft,
            int screenTop,
            int screenWidth,
            int screenHeight)
        {
            var mode = ResolveMode(
                args,
                Environment.GetEnvironmentVariable(
                    EnvironmentVariableName));
            IInputBackend backend = mode switch
            {
                InputBackendMode.SendInput =>
                    new SendInputBackend(screenWidth, screenHeight),
                InputBackendMode.VirtualHid =>
                    new VirtualHidInputBackend(
                        screenLeft,
                        screenTop,
                        screenWidth,
                        screenHeight),
                _ => throw new InvalidOperationException(
                    $"Unsupported input backend mode: {mode}."),
            };

            backend.UpdateScreenBounds(
                screenLeft,
                screenTop,
                screenWidth,
                screenHeight);
            Console.WriteLine(
                $"[Input] Selected backend: {backend.Name} " +
                $"(mode={(int)backend.Mode}).");
            return backend;
        }

        public static InputBackendMode ResolveMode(
            IReadOnlyList<string> args,
            string? environmentValue = null)
        {
            ArgumentNullException.ThrowIfNull(args);
            InputBackendMode? selected = null;

            for (var index = 0; index < args.Count; index++)
            {
                var argument = args[index];
                if (argument.Equals(
                        "--virtual-hid",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Select(
                        ref selected,
                        InputBackendMode.VirtualHid,
                        argument);
                    continue;
                }
                if (argument.Equals(
                        "--send-input",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Select(
                        ref selected,
                        InputBackendMode.SendInput,
                        argument);
                    continue;
                }

                const string prefix = "--input-backend=";
                if (argument.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Select(
                        ref selected,
                        ParseMode(argument[prefix.Length..]),
                        argument);
                    continue;
                }
                if (!argument.Equals(
                        "--input-backend",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (index + 1 >= args.Count ||
                    args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "--input-backend requires sendinput or virtualhid.",
                        nameof(args));
                }

                index++;
                Select(
                    ref selected,
                    ParseMode(args[index]),
                    "--input-backend");
            }

            if (selected.HasValue)
            {
                return selected.Value;
            }
            if (string.IsNullOrWhiteSpace(environmentValue))
            {
                return InputBackendMode.SendInput;
            }
            return ParseMode(environmentValue);
        }

        private static InputBackendMode ParseMode(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "sendinput" or "send-input" or "default" =>
                    InputBackendMode.SendInput,
                "virtualhid" or "virtual-hid" or "vhf" =>
                    InputBackendMode.VirtualHid,
                _ => throw new ArgumentException(
                    $"Unsupported input backend '{value}'. " +
                    "Use sendinput or virtualhid."),
            };
        }

        private static void Select(
            ref InputBackendMode? selected,
            InputBackendMode candidate,
            string source)
        {
            if (selected.HasValue && selected.Value != candidate)
            {
                throw new ArgumentException(
                    "Conflicting input backend options were supplied; " +
                    $"the conflict includes '{source}'.");
            }
            selected = candidate;
        }
    }
}
