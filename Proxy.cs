using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using BenderProxy;
using BenderProxy.Readers;
using BenderProxy.Writers;
using PassedBall;
using HttpResponseHeader = BenderProxy.Headers.HttpResponseHeader;

namespace SeleniumSslProxyErrorExample
{
    public class ProxyTest : IDisposable
    {
        public ProxyTest(Uri httpEndPoint, Uri sslEndPoint)
        {
            X509Certificate2 cert = BuildSelfSignedServerCertificate(sslEndPoint.Host);

                HttpProxy = new HttpProxyServer(httpEndPoint.Host, httpEndPoint.Port, new HttpProxy());
                SslProxy = new HttpProxyServer(sslEndPoint.Host, sslEndPoint.Port, new SslProxy(cert, 443, null, SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls));
                WaitHandle.WaitAll(
                    new[] {
                        HttpProxy.Start(),
                        SslProxy.Start()
                    });

                HttpProxy.Proxy.OnResponseReceived = ProcessResponse;
                SslProxy.Proxy.OnResponseReceived = ProcessResponse;
            }

        private static X509Certificate2 BuildSelfSignedServerCertificate(string certificateName)
        {
            SubjectAlternativeNameBuilder sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName(Environment.MachineName);

            X500DistinguishedName distinguishedName = new X500DistinguishedName($"CN={certificateName}");

            using (RSA rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));


                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                request.CertificateExtensions.Add(sanBuilder.Build());

                var certificate = request.CreateSelfSigned(new DateTimeOffset(DateTime.UtcNow.AddDays(-1)), new DateTimeOffset(DateTime.UtcNow.AddDays(1)));
                certificate.FriendlyName = certificateName;

                return new X509Certificate2(certificate.Export(X509ContentType.Pfx, "WeNeedASaf3rPassword"), "WeNeedASaf3rPassword", X509KeyStorageFlags.UserKeySet);
            }
        }

        public HttpProxyServer HttpProxy { get; }
        public HttpProxyServer SslProxy { get; }

        public void Dispose()
        {
            HttpProxy.Stop();
            SslProxy.Stop();
        }

        private void ProcessResponse(ProcessingContext context)
        {
            // Only do any processing on the response if the response is 401,
            // or "Unauthorized".
            if (context.ResponseHeader != null && context.ResponseHeader.StatusCode == 401)
            {
                //context.ServerStream.ReadTimeout = 5000;
                //context.ServerStream.WriteTimeout = 5000;
                var reader = new StreamReader(context.ServerStream);
                if (context.ResponseHeader.EntityHeaders.ContentLength != 0)
                {
                    ReadFromStream(reader);
                }
                // We do not want the proxy to do any further processing after
                // handling this message.
                context.StopProcessing();


                if (context.ResponseHeader.WWWAuthenticate.Contains(BasicGenerator.AuthorizationHeaderMarker))
                {
                    // This is the generation of the HTTP Basic authorization header value.
                    var basicAuthHeaderValue = $"user:pass";
                    var encodedHeaderValue = Convert.ToBase64String(Encoding.ASCII.GetBytes(basicAuthHeaderValue));
                    context.RequestHeader.Authorization = "Basic " + encodedHeaderValue;

                    // Resend the request (with the Authorization header) to the server
                    // using BenderProxy's HttpMessageWriter.
                    var writer = new HttpMessageWriter(context.ServerStream);
                    writer.Write(context.RequestHeader);

                    // Get the authorized response, and forward it on to the browser, using
                    // BenderProxy's HttpHeaderReader and support classes.
                    var headerReader = new HttpHeaderReader(reader);
                    var header = new HttpResponseHeader(headerReader.ReadHttpMessageHeader());
                    var body = ReadFromStream(reader);
                    Stream bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(body));
                    new HttpResponseWriter(context.ClientStream).Write(header, bodyStream, bodyStream.Length);
                }
                else if (context.ResponseHeader.WWWAuthenticate.Contains(NtlmGenerator.AuthorizationHeaderMarker))
                {
                    // Read the WWW-Authenticate header. Because of the way the test
                    // web app is configured, it returns multiple headers, with
                    // different schemes. We need to select the correct one.
                    var authHeader = GetAuthenticationHeader(context.ResponseHeader.WWWAuthenticate,
                        NtlmGenerator.AuthorizationHeaderMarker);

                    var type1 = new NtlmNegotiateMessageGenerator();
                    var type1HeaderValue = type1.GenerateAuthorizationHeader();
                    context.RequestHeader.Authorization = type1HeaderValue;
                    var writer = new HttpMessageWriter(context.ServerStream);
                    writer.Write(context.RequestHeader);

                    var headerReader = new HttpHeaderReader(reader);
                    var challengeHeader = new HttpResponseHeader(headerReader.ReadHttpMessageHeader());
                    var challengeAuthHeader = challengeHeader.WWWAuthenticate;
                    var challengeBody = ReadFromStream(reader);

                    if (!string.IsNullOrEmpty(challengeAuthHeader) &&
                        challengeAuthHeader.StartsWith(NtlmGenerator.AuthorizationHeaderMarker))
                    {
                        // If a proper message was received (the "type 2" or "Challenge" message),
                        // parse it, and generate the proper authentication header (the "type 3"
                        // or "Authorization" message).
                        var type2 = new NtlmChallengeMessageGenerator(challengeAuthHeader);
                        var type3 = new NtlmAuthenticateMessageGenerator(null, null, "user", "pass", type2);
                        var type3HeaderValue = type3.GenerateAuthorizationHeader();
                        context.RequestHeader.Authorization = type3HeaderValue;
                        writer.Write(context.RequestHeader);

                        // Get the authorized response from the server, and forward it on to
                        // the browser.
                        var header = new HttpResponseHeader(headerReader.ReadHttpMessageHeader());
                        var body = ReadFromStream(reader);
                        Stream bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(body));
                        new HttpResponseWriter(context.ClientStream).Write(header, bodyStream, bodyStream.Length);
                        context.ClientStream.Flush();
                    }
                }
            }
        }

        // <summary>
        /// Utility method to find the expected authentication header to use when multiple headers are returned.
        /// </summary>
        /// <param name="authHeader">The combined value of all WWW-Authenticate headers.</param>
        /// <param name="expectedAuthScheme">The expected authentication scheme.</param>
        /// <returns>The header for the specified scheme, if one exists</returns>
        /// <exception cref="InvalidOperationException">
        ///     Thrown if the expected authentication scheme is not in the list of headers supplied.
        /// </exception>
        protected static string GetAuthenticationHeader(string authHeader, string expectedAuthScheme)
        {
            if (!authHeader.Contains(expectedAuthScheme))
            {
                var normalizedAuthHeader = authHeader.Replace("\r\n", ", ");
                throw new InvalidOperationException(
                    $"Could not find expected authentication scheme '{expectedAuthScheme}' in WWW-Authenticate header ('{normalizedAuthHeader}')");
            }

            string[] authHeaders = authHeader.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            foreach (var individualHeader in authHeaders)
                if (individualHeader.StartsWith(expectedAuthScheme))
                    return individualHeader;

            return string.Empty;
        }
        protected static string ReadFromStream(StreamReader reader)
        {
            var totalResponse = new List<char>();
            var continueReading = true;
            var bufferSize = 8192;
            var totalBytes = 0;
            while (continueReading)
            {
                var buffer = new char[bufferSize];
                var bytesRead = reader.Read(buffer, 0, bufferSize);
                if (bytesRead >= 0)
                {
                    totalResponse.AddRange(buffer);
                    totalBytes += bytesRead;
                }

                if (bytesRead < bufferSize) continueReading = false;
            }

            var content = new string(totalResponse.ToArray(), 0, totalBytes);
            return content;
        }
    }
}
