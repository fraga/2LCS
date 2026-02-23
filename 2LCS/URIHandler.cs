using Microsoft.Win32;
using System.Reflection;

namespace LCS
{
    public class URIHandler
    {
        public const string URI_PROTOCOL_NAME = "MS-2LCS";

        public static string LCS_DIAG_URL = Properties.Settings.Default.lcsDiagURL;
        public static string LCS_UPDATE_URL = Properties.Settings.Default.lcsUpdateURL;
        public static string LCS_URL = Properties.Settings.Default.lcsURL;
        public static string LCS_FIX_URL = Properties.Settings.Default.lcsFixURL;

        public static void RefreshUrls()
        {
            LCS_DIAG_URL = Properties.Settings.Default.lcsDiagURL;
            LCS_UPDATE_URL = Properties.Settings.Default.lcsUpdateURL;
            LCS_URL = Properties.Settings.Default.lcsURL;
            LCS_FIX_URL = Properties.Settings.Default.lcsFixURL;
        }

        public static bool DetectURILaunch(string[] args)
        {
            bool retVal = false;

            foreach (string arg in args)
            {
                if (Uri.TryCreate(arg, UriKind.RelativeOrAbsolute, out Uri srcUri))
                {
                    Uri uri = srcUri.IsAbsoluteUri ? srcUri : new Uri(new Uri("ms-2lcs://lcs.dynamics.com/"), arg);
                    retVal = retVal || uri.Scheme == URI_PROTOCOL_NAME.ToLower();
                    if (retVal) break;
                }
            }

            return retVal;
        }

        public static bool RemoveHandler()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine($"{URI_PROTOCOL_NAME} protocol handler is only available on Windows.");
                return false;
            }

            try
            {
                Registry.ClassesRoot.DeleteSubKeyTree(URI_PROTOCOL_NAME, false);
                Console.WriteLine($"{URI_PROTOCOL_NAME} protocol handler registration removed.");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Administrator privileges are required to remove protocol handler registration.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to remove protocol handler: {ex.Message}");
                return false;
            }
        }

        public static bool RegisterHandler()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine($"{URI_PROTOCOL_NAME} protocol handler is only available on Windows.");
                return false;
            }

            _ = RemoveHandler();

            try
            {
                RegistryKey rootKey = Registry.ClassesRoot.CreateSubKey(URI_PROTOCOL_NAME.ToLower());

                if (rootKey != null)
                {
                    string appAssemblyLocation = Assembly.GetExecutingAssembly().Location;

                    rootKey.SetValue("", $"URL:{URI_PROTOCOL_NAME.ToLower()}");
                    rootKey.SetValue("URL Protocol", "");

                    rootKey.CreateSubKey("DefaultIcon")
                           .SetValue("", appAssemblyLocation);

                    rootKey.CreateSubKey("shell")
                           .CreateSubKey("open")
                           .CreateSubKey("command")
                           .SetValue("", $@"""{appAssemblyLocation}"" ""%1""");
                }

                Console.WriteLine($"{URI_PROTOCOL_NAME} protocol handler registration completed.");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Administrator privileges are required to register protocol handler.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to register protocol handler: {ex.Message}");
                return false;
            }
        }
    }
}
