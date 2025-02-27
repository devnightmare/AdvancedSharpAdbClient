﻿// <copyright file="AdbClient.cs" company="The Android Open Source Project, Ryan Conrad, Quamotion">
// Copyright (c) The Android Open Source Project, Ryan Conrad, Quamotion. All rights reserved.
// </copyright>

namespace AdvancedSharpAdbClient
{
    using AdvancedSharpAdbClient.Exceptions;
    using AdvancedSharpAdbClient.Logs;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml;

    /// <summary>
    /// <para>
    ///     Implements the <see cref="IAdvancedAdbClient"/> interface, and allows you to interact with the
    ///     adb server and devices that are connected to that adb server.
    /// </para>
    /// <para>
    ///     For example, to fetch a list of all devices that are currently connected to this PC, you can
    ///     call the <see cref="GetDevices"/> method.
    /// </para>
    /// <para>
    ///     To run a command on a device, you can use the <see cref="ExecuteRemoteCommandAsync(string, DeviceData, IShellOutputReceiver, CancellationToken, int)"/>
    ///     method.
    /// </para>
    /// </summary>
    /// <seealso href="https://github.com/android/platform_system_core/blob/master/adb/SERVICES.TXT">SERVICES.TXT</seealso>
    /// <seealso href="https://github.com/android/platform_system_core/blob/master/adb/adb_client.c">adb_client.c</seealso>
    /// <seealso href="https://github.com/android/platform_system_core/blob/master/adb/adb.c">adb.c</seealso>
    public class AdvancedAdbClient : IAdvancedAdbClient
    {
        /// <summary>
        /// The port at which the Android Debug Bridge server listens by default.
        /// </summary>
        public const int AdbServerPort = 5037;

        /// <summary>
        /// The default port to use when connecting to a device over TCP/IP.
        /// </summary>
        public const int DefaultPort = 5555;

        private Func<EndPoint, IAdbSocket> adbSocketFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdvancedAdbClient"/> class.
        /// </summary>
        public AdvancedAdbClient()
            : this(new IPEndPoint(IPAddress.Loopback, AdbServerPort), Factories.AdbSocketFactory)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AdvancedAdbClient"/> class.
        /// </summary>
        /// <param name="endPoint">
        /// The <see cref="EndPoint"/> at which the adb server is listening.
        /// </param>
        public AdvancedAdbClient(EndPoint endPoint, Func<EndPoint, IAdbSocket> adbSocketFactory)
        {
            if (endPoint == null)
            {
                throw new ArgumentNullException();
            }

            if (!(endPoint is IPEndPoint || endPoint is DnsEndPoint))
            {
                throw new NotSupportedException("Only TCP endpoints are supported");
            }

            if (adbSocketFactory == null)
            {
                throw new ArgumentNullException(nameof(adbSocketFactory));
            }

            this.EndPoint = endPoint;
            this.adbSocketFactory = adbSocketFactory;
        }

        /// <summary>
        /// Get or set default encoding
        /// </summary>
        public static Encoding Encoding = Encoding.UTF8;

        public static EndPoint DefaultEndPoint
        {
            get
            {
                return new IPEndPoint(IPAddress.Loopback, DefaultPort);
            }
        }

        /// <summary>
        /// Gets the <see cref="EndPoint"/> at which the adb server is listening.
        /// </summary>
        public EndPoint EndPoint
        {
            get;
            private set;
        }

        /// <summary>
        /// Create an ASCII string preceded by four hex digits. The opening "####"
        /// is the length of the rest of the string, encoded as ASCII hex(case
        /// doesn't matter).
        /// </summary>
        /// <param name="req">The request to form.
        /// </param>
        /// <returns>
        /// An array containing <c>####req</c>.
        /// </returns>
        public static byte[] FormAdbRequest(string req)
        {
            int payloadLength = Encoding.GetByteCount(req);
            string resultStr = string.Format("{0}{1}", payloadLength.ToString("X4"), req);
            byte[] result = Encoding.GetBytes(resultStr);
            return result;
        }

        /// <summary>
        /// Creates the adb forward request.
        /// </summary>
        /// <param name="address">The address.</param>
        /// <param name="port">The port.</param>
        /// <returns>
        /// This returns an array containing <c>"####tcp:{port}:{addStr}"</c>.
        /// </returns>
        public static byte[] CreateAdbForwardRequest(string address, int port)
        {
            string request;

            if (address == null)
            {
                request = "tcp:" + port;
            }
            else
            {
                request = "tcp:" + port + ":" + address;
            }

            return FormAdbRequest(request);
        }

        /// <inheritdoc/>
        public int GetAdbVersion()
        {
            using (var socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest("host:version");
                var response = socket.ReadAdbResponse();
                var version = socket.ReadString();

                return int.Parse(version, NumberStyles.HexNumber);
            }
        }

        /// <inheritdoc/>
        public void KillAdb()
        {
            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest("host:kill");

                // The host will immediately close the connection after the kill
                // command has been sent; no need to read the response.
            }
        }

        /// <inheritdoc/>
        public List<DeviceData> GetDevices()
        {
            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest("host:devices-l");
                socket.ReadAdbResponse();
                var reply = socket.ReadString();

                string[] data = reply.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                return data.Select(d => DeviceData.CreateFromAdbData(d)).ToList();
            }
        }

        /// <inheritdoc/>
        public int CreateReverseForward(DeviceData device, string remote, string local, bool allowRebind)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                string rebind = allowRebind ? string.Empty : "norebind:";

                socket.SendAdbRequest($"reverse:forward:{rebind}{remote};{local}");
                var response = socket.ReadAdbResponse();
                response = socket.ReadAdbResponse();
                var portString = socket.ReadString();

                if (portString != null && int.TryParse(portString, out int port))
                {
                    return port;
                }

                return 0;
            }
        }

        public void RemoveReverseForward(DeviceData device, string remote)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                socket.SendAdbRequest($"reverse:killforward:{remote}");
                var response = socket.ReadAdbResponse();
            }
        }

        public void RemoveAllReverseForwards(DeviceData device)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                socket.SendAdbRequest($"reverse:killforward-all");
                var response = socket.ReadAdbResponse();
            }
        }

        /// <inheritdoc/>
        public int CreateForward(DeviceData device, string local, string remote, bool allowRebind)
        {
            
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                string rebind = allowRebind ? string.Empty : "norebind:";

                socket.SendAdbRequest($"host-serial:{device.Serial}:forward:{rebind}{local};{remote}");
                var response = socket.ReadAdbResponse();
                response = socket.ReadAdbResponse();
                var portString = socket.ReadString();

                if (portString != null && int.TryParse(portString, out int port))
                {
                    return port;
                }

                return 0;
            }
        }

        /// <inheritdoc/>
        public int CreateForward(DeviceData device, ForwardSpec local, ForwardSpec remote, bool allowRebind)
        {
            return this.CreateForward(device, local?.ToString(), remote?.ToString(), allowRebind);
        }

        /// <inheritdoc/>
        public void RemoveForward(DeviceData device, int localPort)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest($"host-serial:{device.Serial}:killforward:tcp:{localPort}");
                var response = socket.ReadAdbResponse();
            }
        }

        /// <inheritdoc/>
        public void RemoveAllForwards(DeviceData device)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest($"host-serial:{device.Serial}:killforward-all");
                var response = socket.ReadAdbResponse();
            }
        }

        /// <inheritdoc/>
        public IEnumerable<ForwardData> ListForward(DeviceData device)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest($"host-serial:{device.Serial}:list-forward");
                var response = socket.ReadAdbResponse();

                var data = socket.ReadString();

                var parts = data.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                return parts.Select(p => ForwardData.FromString(p));
            }
        }

        public IEnumerable<ForwardData> ListReverseForward(DeviceData device)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                socket.SendAdbRequest($"reverse:list-forward");
                var response = socket.ReadAdbResponse();

                var data = socket.ReadString();

                var parts = data.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                return parts.Select(p => ForwardData.FromString(p));
            }
        }

        /// <inheritdoc/>
        public Task ExecuteRemoteCommandAsync(string command, DeviceData device, IShellOutputReceiver receiver, CancellationToken cancellationToken)
        {
            return this.ExecuteRemoteCommandAsync(command, device, receiver, Encoding, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task ExecuteRemoteCommandAsync(string command, DeviceData device, IShellOutputReceiver receiver, Encoding encoding, CancellationToken cancellationToken)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                cancellationToken.Register(() => socket.Dispose());

                socket.SetDevice(device);
                socket.SendAdbRequest($"shell:{command}");
                var response = socket.ReadAdbResponse();

                try
                {
                    using (StreamReader reader = new StreamReader(socket.GetShellStream(), encoding))
                    {
                        // Previously, we would loop while reader.Peek() >= 0. Turns out that this would
                        // break too soon in certain cases (about every 10 loops, so it appears to be a timing
                        // issue). Checking for reader.ReadLine() to return null appears to be much more robust
                        // -- one of the integration test fetches output 1000 times and found no truncations.
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var line =
#if !NET35
                                await reader.ReadLineAsync().ConfigureAwait(false);
#else
                                reader.ReadLine();
#endif

                            if (line == null)
                            {
                                break;
                            }

                            if (receiver != null)
                            {
                                receiver.AddOutput(line);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    // If a cancellation was requested, this main loop is interrupted with an exception
                    // because the socket is closed. In that case, we don't need to throw a ShellCommandUnresponsiveException.
                    // In all other cases, something went wrong, and we want to report it to the user.
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ShellCommandUnresponsiveException(e);
                    }
                }
                finally
                {
                    if (receiver != null)
                    {
                        receiver.Flush();
                    }
                }
            }
        }

        /// <inheritdoc/>
        public Framebuffer CreateRefreshableFramebuffer(DeviceData device)
        {
            this.EnsureDevice(device);

            return new Framebuffer(device, this);
        }

        /// <inheritdoc/>
        public async Task<Image> GetFrameBufferAsync(DeviceData device, CancellationToken cancellationToken)
        {
            this.EnsureDevice(device);

            using (Framebuffer framebuffer = this.CreateRefreshableFramebuffer(device))
            {
                await framebuffer.RefreshAsync(cancellationToken).ConfigureAwait(false);

                // Convert the framebuffer to an image, and return that.
                return framebuffer.ToImage();
            }
        }

        /// <inheritdoc/>
        public async Task RunLogServiceAsync(DeviceData device, Action<LogEntry> messageSink, CancellationToken cancellationToken, params LogId[] logNames)
        {
            if (messageSink == null)
            {
                throw new ArgumentException(nameof(messageSink));
            }

            this.EnsureDevice(device);

            // The 'log' service has been deprecated, see
            // https://android.googlesource.com/platform/system/core/+/7aa39a7b199bb9803d3fd47246ee9530b4a96177
            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                StringBuilder request = new StringBuilder();
                request.Append("shell:logcat -B");

                foreach (var logName in logNames)
                {
                    request.Append($" -b {logName.ToString().ToLowerInvariant()}");
                }

                socket.SendAdbRequest(request.ToString());
                var response = socket.ReadAdbResponse();

                using (Stream stream = socket.GetShellStream())
                {
                    LogReader reader = new LogReader(stream);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        LogEntry entry = null;

                        try
                        {
                            entry = await reader.ReadEntry(cancellationToken).ConfigureAwait(false);
                        }
                        catch (EndOfStreamException)
                        {
                            // This indicates the end of the stream; the entry will remain null.
                        }

                        if (entry != null)
                        {
                            messageSink(entry);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Reboot(string into, DeviceData device)
        {
            this.EnsureDevice(device);

            var request = $"reboot:{into}";

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(request);
                var response = socket.ReadAdbResponse();
            }
        }

        /// <inheritdoc/>
        public void Connect(DnsEndPoint endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest($"host:connect:{endpoint.Host}:{endpoint.Port}");
                var response = socket.ReadAdbResponse();
            }
        }

        /// <inheritdoc/>
        public void Root(DeviceData device)
        {
            this.Root("root:", device);
        }

        /// <inheritdoc/>
        public void Unroot(DeviceData device)
        {
            this.Root("unroot:", device);
        }

        /// <inheritdoc/>
        protected void Root(string request, DeviceData device)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(request);
                var response = socket.ReadAdbResponse();

                // ADB will send some additional data
                byte[] buffer = new byte[1024];
                int read = socket.Read(buffer);

                var responseMessage = Encoding.UTF8.GetString(buffer, 0, read);

                // See https://android.googlesource.com/platform/system/core/+/master/adb/commandline.cpp#1026 (adb_root)
                // for more information on how upstream does this.
                if (!string.Equals(responseMessage, "restarting", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AdbException(responseMessage);
                }
                else
                {
                    // Give adbd some time to kill itself and come back up.
                    // We can't use wait-for-device because devices (e.g. adb over network) might not come back.
                    Utilities.Delay(3000).GetAwaiter().GetResult();
                }
            }
        }

        /// <inheritdoc/>
        public List<string> GetFeatureSet(DeviceData device)
        {
            using (var socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest($"host-serial:{device.Serial}:features");

                var response = socket.ReadAdbResponse();
                var features = socket.ReadString();

                var featureList = features.Split(new char[] { '\n', ',' }).ToList();
                return featureList;
            }
        }

        /// <inheritdoc/>
        public void Install(DeviceData device, Stream apk, params string[] arguments)
        {
            this.EnsureDevice(device);

            if (apk == null)
            {
                throw new ArgumentNullException(nameof(apk));
            }

            if (!apk.CanRead || !apk.CanSeek)
            {
                throw new ArgumentOutOfRangeException(nameof(apk), "The apk stream must be a readable and seekable stream");
            }

            StringBuilder requestBuilder = new StringBuilder();
            requestBuilder.Append("exec:cmd package 'install' ");

            if (arguments != null)
            {
                foreach (var argument in arguments)
                {
                    requestBuilder.Append(" ");
                    requestBuilder.Append(argument);
                }
            }

            // add size parameter [required for streaming installs]
            // do last to override any user specified value
            requestBuilder.Append($" -S {apk.Length}");

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                socket.SendAdbRequest(requestBuilder.ToString());
                var response = socket.ReadAdbResponse();

                byte[] buffer = new byte[32 * 1024];
                int read = 0;

                while ((read = apk.Read(buffer, 0, buffer.Length)) > 0)
                {
                    socket.Send(buffer, read);
                }

                read = socket.Read(buffer);
                var value = Encoding.UTF8.GetString(buffer, 0, read);

                if (!value.Contains("Success"))
                {
                    throw new AdbException(value);
                }
            }
        }

        /// <inheritdoc/>
        public string InstallCreated(DeviceData device, string packageName = null, params string[] arguments)
        {
            this.EnsureDevice(device);

            StringBuilder requestBuilder = new StringBuilder();
            requestBuilder.Append("exec:cmd package 'install-create' ");
            requestBuilder.Append(packageName.IsNullOrWhiteSpace() ? string.Empty : $"-p {packageName}");

            if (arguments != null)
            {
                foreach (var argument in arguments)
                {
                    requestBuilder.Append(" ");
                    requestBuilder.Append(argument);
                }
            }

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                socket.SendAdbRequest(requestBuilder.ToString());
                var response = socket.ReadAdbResponse();

                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    var result = reader.ReadLine();

                    if (!result.Contains("Success"))
                    {
                        throw new AdbException(reader.ReadToEnd());
                    }

                    int arr = result.IndexOf("]") - 1 - result.IndexOf("[");
                    string session = result.Substring(result.IndexOf("[") + 1, arr);
                    return session;
                }
            }
        }

        /// <inheritdoc/>
        public void InstallWrite(DeviceData device, Stream apk, string apkname, string session)
        {
            this.EnsureDevice(device);

            if (apk == null)
            {
                throw new ArgumentNullException(nameof(apk));
            }

            if (!apk.CanRead || !apk.CanSeek)
            {
                throw new ArgumentOutOfRangeException(nameof(apk), "The apk stream must be a readable and seekable stream");
            }

            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (apkname == null)
            {
                throw new ArgumentNullException(nameof(apkname));
            }

            StringBuilder requestBuilder = new StringBuilder();
            requestBuilder.Append($"exec:cmd package 'install-write' ");

            // add size parameter [required for streaming installs]
            // do last to override any user specified value
            requestBuilder.Append($" -S {apk.Length}");

            requestBuilder.Append($" {session} {apkname}.apk");

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                socket.SendAdbRequest(requestBuilder.ToString());
                var response = socket.ReadAdbResponse();

                byte[] buffer = new byte[32 * 1024];
                int read = 0;

                while ((read = apk.Read(buffer, 0, buffer.Length)) > 0)
                {
                    socket.Send(buffer, read);
                }

                read = socket.Read(buffer);
                var value = Encoding.UTF8.GetString(buffer, 0, read);

                if (!value.Contains("Success"))
                {
                    throw new AdbException(value);
                }
            }
        }

        /// <inheritdoc/>
        public void InstallCommit(DeviceData device, string session)
        {
            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);

                socket.SendAdbRequest($"exec:cmd package 'install-commit' {session}");
                var response = socket.ReadAdbResponse();

                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    var result = reader.ReadLine();
                    if (!result.Contains("Success"))
                    {
                        throw new AdbException(reader.ReadToEnd());
                    }
                }
            }
        }

        /// <summary>
        /// Push multiple APKs to the device and install them.
        /// </summary>
        /// <param name="device">The device on which to install the application.</param>
        /// <param name="baseapk">A <see cref="Stream"/> which represents the baseapk to install.</param>
        /// <param name="splitapks"><see cref="Stream"/>s which represents the splitapks to install.</param>
        /// <param name="arguments">The arguments to pass to <c>adb instal-create</c>.</param>
        public void InstallMultiple(DeviceData device, Stream baseapk, Stream[] splitapks, params string[] arguments)
        {
            this.EnsureDevice(device);

            if (baseapk == null)
            {
                throw new ArgumentNullException(nameof(baseapk));
            }

            if (!baseapk.CanRead || !baseapk.CanSeek)
            {
                throw new ArgumentOutOfRangeException(nameof(baseapk), "The apk stream must be a readable and seekable stream");
            }

            string session = InstallCreated(device, null, arguments);

            InstallWrite(device, baseapk, nameof(baseapk), session);

            int i = 0;
            foreach(var splitapk in splitapks)
            {
                if (splitapk == null || !splitapk.CanRead || !splitapk.CanSeek)
                {
                    Debug.WriteLine("The apk stream must be a readable and seekable stream");
                    continue;
                }

                try
                {
                    InstallWrite(device, splitapk, $"{nameof(splitapk)}{i++}", session);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }

            InstallCommit(device, session);
        }

        /// <summary>
        /// Push multiple APKs to the device and install them.
        /// </summary>
        /// <param name="device">The device on which to install the application.</param>
        /// <param name="splitapks"><see cref="Stream"/>s which represents the splitapks to install.</param>
        /// <param name="packageName">The packagename of the baseapk to install.</param>
        /// <param name="arguments">The arguments to pass to <c>adb instal-create</c>.</param>
        public void InstallMultiple(DeviceData device, Stream[] splitapks, string packageName, params string[] arguments)
        {
            this.EnsureDevice(device);

            if (packageName == null)
            {
                throw new ArgumentNullException(nameof(packageName));
            }

            string session = InstallCreated(device, packageName, arguments);

            int i = 0;
            foreach (var splitapk in splitapks)
            {
                if (splitapk == null || !splitapk.CanRead || !splitapk.CanSeek)
                {
                    Debug.WriteLine("The apk stream must be a readable and seekable stream");
                    continue;
                }

                try
                {
                    InstallWrite(device, splitapk, $"{nameof(splitapk)}{i++}", session);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }

            InstallCommit(device, session);
        }

        /// <inheritdoc/>
        public XmlDocument DumpScreen(DeviceData device)
        {
            XmlDocument doc = new XmlDocument();
            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest("shell:uiautomator dump /dev/tty");
                var response = socket.ReadAdbResponse();
                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    string xmlString = reader.ReadToEnd().Replace("Events injected: 1\r\n", "").Replace("UI hierchary dumped to: /dev/tty", "");
                    if (xmlString != "" && !xmlString.Contains("ERROR"))
                    {
                        doc.LoadXml(xmlString);
                        return doc;
                    }
                }
            }
            return null;
        }


        /// <inheritdoc/>
        public void Click(DeviceData device, Cords cords)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(String.Format("shell:input tap {0} {1}", cords.x, cords.y));
                var response = socket.ReadAdbResponse();
                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    if (reader.ReadToEnd().ToUpper().Contains("ERROR")) // error or ERROR
                    {
                        throw new ElementNotFoundException("Coordinates of element is invalid");
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Click(DeviceData device, int x, int y)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(String.Format("shell:input tap {0} {1}", x, y));
                var response = socket.ReadAdbResponse();
                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    if (reader.ReadToEnd().ToUpper().Contains("ERROR"))
                    {
                        throw new ElementNotFoundException("Coordinates of element is invalid");
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Swipe(DeviceData device, Element first, Element second, long speed)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(String.Format("shell:input swipe {0} {1} {2} {3} {4}", first.cords.x, first.cords.y, second.cords.x, second.cords.y, speed));
                var response = socket.ReadAdbResponse();
                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    if (reader.ReadToEnd().ToUpper().Contains("ERROR"))
                    {
                        throw new ElementNotFoundException("Coordinates of element is invalid");
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Swipe(DeviceData device, int x1, int y1, int x2, int y2, long speed)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(String.Format("shell:input swipe {0} {1} {2} {3} {4}", x1, y1, x2, y2, speed));
                var response = socket.ReadAdbResponse();
                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    if (reader.ReadToEnd().ToUpper().Contains("ERROR"))
                    {
                        throw new ElementNotFoundException("Coordinates of element is invalid");
                    }
                }
            }
        }

        /// <inheritdoc/>
        public Element FindElement(DeviceData device, string xpath, TimeSpan timeout = default)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            while (timeout == TimeSpan.Zero || stopwatch.Elapsed < timeout)
            {
                XmlDocument doc = DumpScreen(device);
                if (doc != null)
                {
                    XmlNode xmlNode = doc.SelectSingleNode(xpath);
                    if (xmlNode != null)
                    {
                        string bounds = xmlNode.Attributes["bounds"].Value;
                        if (bounds != null)
                        {
                            int[] cords = bounds.Replace("][", ",").Replace("[", "").Replace("]", "").Split(',').Select(int.Parse).ToArray(); // x1, y1, x2, y2
                            Dictionary<string, string> attributes = new Dictionary<string, string>();
                            foreach (XmlAttribute at in  xmlNode.Attributes)
                            {
                                attributes.Add(at.Name, at.Value);
                            }
                            Cords cord = new Cords(((cords[0] + cords[2]) / 2), ((cords[1] + cords[3]) / 2)); // Average x1, y1, x2, y2
                            return new Element(this, device, cord, attributes);
                        }
                    }
                }
                if (timeout == TimeSpan.Zero)
                    break;
            }
            return null;
        }

        /// <inheritdoc/>
        public Element[] FindElements(DeviceData device, string xpath, TimeSpan timeout = default)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            while (timeout == TimeSpan.Zero || stopwatch.Elapsed < timeout)
            {
                XmlDocument doc = DumpScreen(device);
                if (doc != null)
                {
                    XmlNodeList xmlNodes = doc.SelectNodes(xpath);
                    if (xmlNodes != null)
                    {
                        Element[] elements = new Element[xmlNodes.Count];
                        for(int i = 0;i < elements.Length;i++)
                        {
                            string bounds = xmlNodes[i].Attributes["bounds"].Value;
                            if (bounds != null)
                            {
                                int[] cords = bounds.Replace("][", ",").Replace("[", "").Replace("]", "").Split(',').Select(int.Parse).ToArray(); // x1, y1, x2, y2
                                Dictionary<string, string> attributes = new Dictionary<string, string>();
                                foreach (XmlAttribute at in xmlNodes[i].Attributes)
                                {
                                    attributes.Add(at.Name, at.Value);
                                }
                                Cords cord = new Cords((cords[0] + cords[2]) / 2, (cords[1] + cords[3]) / 2); // Average x1, y1, x2, y2
                                elements[i] = new Element(this, device, cord, attributes);
                            }
                        }
                        if (elements.Length == 0)
                        {
                            return null;
                        } else
                        {
                            return elements;
                        }
                    }
                }
                if (timeout == TimeSpan.Zero)
                    break;
            }
            return null;
        }

        /// <inheritdoc/>
        public void SendKeyEvent(DeviceData device , string key)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(String.Format("shell:input keyevent {0}", key));
                var response = socket.ReadAdbResponse();
                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    if (reader.ReadToEnd().ToUpper().Contains("ERROR"))
                    {
                        throw new InvalidKeyEventException("KeyEvent is invalid");
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void SendText(DeviceData device, string text)
        {
            this.EnsureDevice(device);

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SetDevice(device);
                socket.SendAdbRequest(String.Format("shell:input text {0}", text));
                var response = socket.ReadAdbResponse();
                using (StreamReader reader = new StreamReader(socket.GetShellStream(), Encoding))
                {
                    if (reader.ReadToEnd().ToUpper().Contains("ERROR"))
                    {
                        throw new InvalidTextException();
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void ClearInput(DeviceData device, int charcount)
        {
            SendKeyEvent(device, "KEYCODE_MOVE_END");
            ExecuteRemoteCommandAsync("input keyevent " +
#if !NET35
                string
#else
                StringEx
#endif
                .Join(" ", Enumerable.Repeat("KEYCODE_DEL ", charcount)), device, null, CancellationToken.None).Wait();
        }

        /// <inheritdoc/>
        public async void StartApp(DeviceData device, string packagename)
        {
            await ExecuteRemoteCommandAsync($"monkey -p {packagename} 1", device, null, CancellationToken.None);
        }

        /// <inheritdoc/>
        public async void StopApp(DeviceData device, string packagename)
        {
            await ExecuteRemoteCommandAsync($"am force-stop {packagename}", device, null, CancellationToken.None);
        }

        /// <inheritdoc/>
        public void BackBtn(DeviceData device)
        {
            SendKeyEvent(device, "KEYCODE_BACK");
        }

        /// <inheritdoc/>
        public void HomeBtn(DeviceData device)
        {
            SendKeyEvent(device, "KEYCODE_HOME");
        }

        /// <summary>
        /// Sets default encoding (default - UTF8)
        /// </summary>
        /// <param name="encoding"></param>
        public static void SetEncoding(Encoding encoding)
        {
            Encoding = encoding;
        }

        /// <inheritdoc/>
        public void Disconnect(DnsEndPoint endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            using (IAdbSocket socket = this.adbSocketFactory(this.EndPoint))
            {
                socket.SendAdbRequest($"host:disconnect:{endpoint.Host}:{endpoint.Port}");
                var response = socket.ReadAdbResponse();
            }
        }

        /// <summary>
        /// Throws an <see cref="ArgumentNullException"/> if the <paramref name="device"/>
        /// parameter is <see langword="null"/>, and a <see cref="ArgumentOutOfRangeException"/>
        /// if <paramref name="device"/> does not have a valid serial number.
        /// </summary>
        /// <param name="device">
        /// A <see cref="DeviceData"/> object to validate.
        /// </param>
        protected void EnsureDevice(DeviceData device)
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            if (string.IsNullOrEmpty(device.Serial))
            {
                throw new ArgumentOutOfRangeException(nameof(device), "You must specific a serial number for the device");
            }
        }
    }
}
