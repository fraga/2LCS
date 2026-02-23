using System.Diagnostics;

namespace LCS
{
    public static class WebBrowserHelper
    {
        /// <summary>
        /// Opens a web page in the default browser.
        /// </summary>
        /// <param name="uri">Url of web page.</param>
        /// <remarks>
        /// See https://github.com/microsoft/2LCS/issues/77
        /// </remarks>
        public static void OpenUri(string uri)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            };
            Process.Start(processStartInfo);
        }
    }
}
