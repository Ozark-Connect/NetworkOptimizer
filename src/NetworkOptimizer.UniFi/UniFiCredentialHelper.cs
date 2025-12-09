using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Helper class for managing UniFi controller credentials
/// Supports Windows Credential Manager for secure local storage
/// </summary>
public static class UniFiCredentialHelper
{
    /// <summary>
    /// Retrieves credentials from Windows Credential Manager
    /// Pattern from SeaTurtleGamingBuddy
    /// </summary>
    /// <param name="target">The credential target name (e.g., controller hostname)</param>
    /// <returns>Tuple of (username, password) or null if not found</returns>
    public static (string Username, string Password)? GetCredential(string target)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows");
        }

        const int CRED_TYPE_GENERIC = 1;

        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var username = cred.UserName;
            var password = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                return null;
            }

            return (username, password);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <summary>
    /// Stores credentials in Windows Credential Manager
    /// </summary>
    /// <param name="target">The credential target name (e.g., controller hostname)</param>
    /// <param name="username">The username</param>
    /// <param name="password">The password</param>
    /// <param name="persist">Persistence type (default: LocalMachine)</param>
    /// <returns>True if successful</returns>
    public static bool StoreCredential(
        string target,
        string username,
        string password,
        CredentialPersistence persist = CredentialPersistence.LocalMachine)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows");
        }

        var passwordBytes = System.Text.Encoding.Unicode.GetBytes(password);

        var cred = new CREDENTIAL
        {
            Flags = 0,
            Type = 1, // CRED_TYPE_GENERIC
            TargetName = target,
            Comment = "UniFi Controller Credentials",
            CredentialBlobSize = passwordBytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(passwordBytes.Length),
            Persist = (int)persist,
            AttributeCount = 0,
            Attributes = IntPtr.Zero,
            TargetAlias = null,
            UserName = username
        };

        try
        {
            Marshal.Copy(passwordBytes, 0, cred.CredentialBlob, passwordBytes.Length);
            return CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    /// <summary>
    /// Deletes credentials from Windows Credential Manager
    /// </summary>
    /// <param name="target">The credential target name</param>
    /// <returns>True if successful</returns>
    public static bool DeleteCredential(string target)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows");
        }

        const int CRED_TYPE_GENERIC = 1;
        return CredDelete(target, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>
    /// Creates a UniFiApiClient using credentials from Windows Credential Manager
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="controllerHost">Controller hostname or IP</param>
    /// <param name="site">Site name (default: "default")</param>
    /// <returns>Configured UniFiApiClient or null if credentials not found</returns>
    public static UniFiApiClient? CreateClientFromCredentialManager(
        Microsoft.Extensions.Logging.ILogger<UniFiApiClient> logger,
        string controllerHost,
        string site = "default")
    {
        var credentials = GetCredential(controllerHost);

        if (credentials == null)
        {
            logger.LogError("No credentials found in Credential Manager for target: {Target}", controllerHost);
            return null;
        }

        return new UniFiApiClient(
            logger,
            controllerHost,
            credentials.Value.Username,
            credentials.Value.Password,
            site);
    }

    #region Windows Credential Manager P/Invoke

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(
        string target,
        int type,
        int reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(
        [In] ref CREDENTIAL userCredential,
        [In] uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(
        string target,
        int type,
        int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    #endregion
}

/// <summary>
/// Credential persistence options for Windows Credential Manager
/// </summary>
public enum CredentialPersistence
{
    /// <summary>
    /// Credential persists for current login session only
    /// </summary>
    Session = 1,

    /// <summary>
    /// Credential persists for local machine (all sessions for current user)
    /// </summary>
    LocalMachine = 2,

    /// <summary>
    /// Credential persists for enterprise (domain credentials)
    /// </summary>
    Enterprise = 3
}

/// <summary>
/// Example usage of credential helper
/// </summary>
public static class CredentialHelperExample
{
    public static void StoreCredentialsExample()
    {
        // Store credentials in Windows Credential Manager
        var success = UniFiCredentialHelper.StoreCredential(
            target: "192.168.1.1",  // or "unifi.local"
            username: "admin",
            password: "your-password-here",
            persist: CredentialPersistence.LocalMachine
        );

        if (success)
        {
            Console.WriteLine("Credentials stored successfully");
        }
    }

    public static void RetrieveCredentialsExample()
    {
        // Retrieve credentials from Windows Credential Manager
        var credentials = UniFiCredentialHelper.GetCredential("192.168.1.1");

        if (credentials != null)
        {
            Console.WriteLine($"Username: {credentials.Value.Username}");
            Console.WriteLine("Password: ********");
        }
        else
        {
            Console.WriteLine("Credentials not found");
        }
    }

    public static void CreateClientExample()
    {
        // Create client using stored credentials
        using var loggerFactory = LoggerFactory.Create(
            builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<UniFiApiClient>();

        var client = UniFiCredentialHelper.CreateClientFromCredentialManager(
            logger,
            controllerHost: "192.168.1.1",
            site: "default"
        );

        if (client != null)
        {
            Console.WriteLine("Client created successfully from stored credentials");
            // Use the client...
            client.Dispose();
        }
    }

    public static void DeleteCredentialsExample()
    {
        // Delete credentials from Windows Credential Manager
        var success = UniFiCredentialHelper.DeleteCredential("192.168.1.1");

        if (success)
        {
            Console.WriteLine("Credentials deleted successfully");
        }
    }
}
