using System.Security.Cryptography;
using System.Text;

namespace TrayX.Contracts;

public static class TrayXIpc
{
    public const string PipeNamePrefix = "TrayX.Core";
    public const int ProtocolVersion = 1;

    public static string GetPipeName()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{PipeNamePrefix}.{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    public static string GetMutexName(string suffix) => $"Local\\TrayX.{suffix}.{GetPipeName().Split('.').Last()}";
}
