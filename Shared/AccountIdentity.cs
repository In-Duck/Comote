using System;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Comote.Shared
{
    internal static class AccountIdentity
    {
        private const string InternalDomain = "accounts.kymote.app";
        private static readonly Regex AccountIdPattern = new(
            "^[a-z0-9._-]{4,32}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool TryNormalize(string? value, out string email)
        {
            email = "";
            var normalized = value?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized)) return false;

            if (normalized.Contains('@'))
            {
                try
                {
                    var address = new MailAddress(normalized);
                    if (!string.Equals(address.Address, normalized, StringComparison.OrdinalIgnoreCase))
                        return false;
                    email = address.Address.ToLowerInvariant();
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            }

            if (!AccountIdPattern.IsMatch(normalized)) return false;
            email = $"{normalized}@{InternalDomain}";
            return true;
        }
    }
}