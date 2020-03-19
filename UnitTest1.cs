using OpenQA.Selenium.Chrome;
using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace SeleniumSslProxyErrorExample
{
    public class UnitTest1 : IDisposable
    {
        public UnitTest1()
        {
            var httpEndPoint = new Uri("http://127.0.0.1:8080");
            var sslEndPoint = new Uri("https://127.0.0.1:9090");
            Proxy = new ProxyTest(httpEndPoint, sslEndPoint);

            var options = new ChromeOptions();
            var profilePath = Path.Combine(AppContext.BaseDirectory, "tmp", Guid.NewGuid().ToString());
            var downloadPath = Path.Combine(profilePath, "Downloads");
            if (!Directory.Exists(downloadPath)) Directory.CreateDirectory(downloadPath);
            options.AddUserProfilePreference("download.default_directory", downloadPath);
            //--lang=en-US,en headless does not define a language by default
            options.AddArguments("--incognito", "--lang=en-US,en", $@"--user-data-dir={profilePath}", "--ignore-certificate-errors", "--allow-insecure-localhost");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                options.AddArguments("--whitelisted-ips", "--disable-dev-shm-usage");
            options.UnhandledPromptBehavior = UnhandledPromptBehavior.Dismiss;
            options.AddAdditionalCapability("useAutomationExtension", false);
            options.Proxy = new OpenQA.Selenium.Proxy
            {
                HttpProxy = $"{httpEndPoint.Host}:{httpEndPoint.Port}",
                SslProxy = $"{sslEndPoint.Host}:{sslEndPoint.Port}",
                Kind = ProxyKind.Manual,
            };

            var driverService = ChromeDriverService.CreateDefaultService(AppContext.BaseDirectory);
            options.AcceptInsecureCertificates = true;
            options.AddArguments("--start-maximized", "--hide-scrollbars");
            Driver = new ChromeDriver(driverService, options);

        }

        public ChromeDriver Driver { get; }
        public ProxyTest Proxy { get; }

        [Fact]
        public void Test1()
        {
            Driver.Navigate().GoToUrl("https://google.com");
        }

        public void Dispose()
        {
            Driver?.Dispose();
            Proxy?.Dispose();
        }
    }
}
