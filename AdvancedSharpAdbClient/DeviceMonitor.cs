﻿// <copyright file="DeviceMonitor.cs" company="The Android Open Source Project, Ryan Conrad, Quamotion">
// Copyright (c) The Android Open Source Project, Ryan Conrad, Quamotion. All rights reserved.
// </copyright>

namespace AdvancedSharpAdbClient
{
    using Exceptions;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

#if !NET35 && !NET40
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
#endif

#if NET452
    using AdvancedSharpAdbClient.Logs;
#endif

    /// <summary>
    /// <para>
    ///     A Device monitor. This connects to the Android Debug Bridge and get device and
    ///     debuggable process information from it.
    /// </para>
    /// </summary>
    /// <example>
    /// <para>
    ///     To receive notifications when devices connect to or disconnect from your PC, you can use the following code:
    /// </para>
    /// <code>
    /// void Test()
    /// {
    ///     var monitor = new DeviceMonitor(new AdbSocket());
    ///     monitor.DeviceConnected += this.OnDeviceConnected;
    ///     monitor.Start();
    /// }
    ///
    /// void OnDeviceConnected(object sender, DeviceDataEventArgs e)
    /// {
    ///     Console.WriteLine($"The device {e.Device.Name} has connected to this PC");
    /// }
    /// </code>
    /// </example>
    public class DeviceMonitor : IDeviceMonitor, IDisposable
    {
#if !NET35 && !NET40
        /// <summary>
        /// The logger to use when logging messages.
        /// </summary>
        private readonly ILogger<DeviceMonitor> logger;
#endif

        /// <summary>
        /// The list of devices currently connected to the Android Debug Bridge.
        /// </summary>
        private readonly List<DeviceData> devices;

        /// <summary>
        /// When the <see cref="Start"/> method is called, this <see cref="ManualResetEvent"/>
        /// is used to block the <see cref="Start"/> method until the <see cref="DeviceMonitorLoopAsync"/>
        /// has processed the first list of devices.
        /// </summary>
        private readonly ManualResetEvent firstDeviceListParsed = new ManualResetEvent(false);

        /// <summary>
        /// A <see cref="CancellationToken"/> that can be used to cancel the <see cref="monitorTask"/>.
        /// </summary>
        private readonly CancellationTokenSource monitorTaskCancellationTokenSource = new CancellationTokenSource();

        /// <summary>
        /// The <see cref="Task"/> that monitors the <see cref="Socket"/> and waits for device notifications.
        /// </summary>
        private Task monitorTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceMonitor"/> class.
        /// </summary>
        /// <param name="socket">
        /// The <see cref="IAdbSocket"/> that manages the connection with the adb server.
        /// </param>
        /// <param name="logger">
        /// The logger to use when logging.
        /// </param>
        public DeviceMonitor(IAdbSocket socket
#if !NET35 && !NET40
            , ILogger<DeviceMonitor> logger = null
#endif
            )
        {
            if (socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            this.Socket = socket;
            this.devices = new List<DeviceData>();
            this.Devices = this.devices.AsReadOnly();
#if !NET35 && !NET40
            this.logger = logger ?? NullLogger<DeviceMonitor>.Instance;
#endif
        }

        /// <inheritdoc/>
        public event EventHandler<DeviceDataEventArgs> DeviceChanged;

        /// <inheritdoc/>
        public event EventHandler<DeviceDataEventArgs> DeviceConnected;

        /// <inheritdoc/>
        public event EventHandler<DeviceDataEventArgs> DeviceDisconnected;

        /// <inheritdoc/>
        public
#if !NET35 && !NET40
            IReadOnlyCollection
#else
            IEnumerable
#endif
            <DeviceData> Devices { get; private set; }

        /// <summary>
        /// Gets the <see cref="IAdbSocket"/> that represents the connection to the
        /// Android Debug Bridge.
        /// </summary>
        public IAdbSocket Socket { get; private set; }

        /// <summary>
        /// Gets a value indicating whether this instance is running.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if this instance is running; otherwise, <see langword="false"/>.
        /// </value>
        public bool IsRunning { get; private set; }

        /// <inheritdoc/>
        public void Start()
        {
            if (this.monitorTask == null)
            {
                this.firstDeviceListParsed.Reset();

                this.monitorTask = Utilities.Run(() => this.DeviceMonitorLoopAsync(this.monitorTaskCancellationTokenSource.Token));

                // Wait for the worker thread to have read the first list
                // of devices.
                this.firstDeviceListParsed.WaitOne();
            }
        }

        /// <summary>
        /// Stops the monitoring
        /// </summary>
        public void Dispose()
        {
            // First kill the monitor task, which has a dependency on the socket,
            // then close the socket.
            if (this.monitorTask != null)
            {
                this.IsRunning = false;

                // Stop the thread. The tread will keep waiting for updated information from adb
                // eternally, so we need to forcefully abort it here.
                this.monitorTaskCancellationTokenSource.Cancel();
                this.monitorTask.Wait();

                this.monitorTask.Dispose();
                this.monitorTask = null;
            }

            // Close the connection to adb. To be done after the monitor task exited.
            if (this.Socket != null)
            {
                this.Socket.Dispose();
                this.Socket = null;
            }

#if !NET35
            this.firstDeviceListParsed.Dispose();
#else
            this.firstDeviceListParsed.Close();
#endif
            this.monitorTaskCancellationTokenSource.Dispose();
        }

        /// <summary>
        /// Raises the <see cref="DeviceChanged"/> event.
        /// </summary>
        /// <param name="e">The <see cref="DeviceDataEventArgs"/> instance containing the event data.</param>
        protected void OnDeviceChanged(DeviceDataEventArgs e)
        {
            if (this.DeviceChanged != null)
            {
                this.DeviceChanged(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="DeviceConnected"/> event.
        /// </summary>
        /// <param name="e">The <see cref="DeviceDataEventArgs"/> instance containing the event data.</param>
        protected void OnDeviceConnected(DeviceDataEventArgs e)
        {
            if (this.DeviceConnected != null)
            {
                this.DeviceConnected(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="DeviceDisconnected"/> event.
        /// </summary>
        /// <param name="e">The <see cref="DeviceDataEventArgs"/> instance containing the event data.</param>
        protected void OnDeviceDisconnected(DeviceDataEventArgs e)
        {
            if (this.DeviceDisconnected != null)
            {
                this.DeviceDisconnected(this, e);
            }
        }

        /// <summary>
        /// Monitors the devices. This connects to the Debug Bridge
        /// </summary>
        private async Task DeviceMonitorLoopAsync(CancellationToken cancellationToken)
        {
            this.IsRunning = true;

            // Set up the connection to track the list of devices.
            this.InitializeSocket();

            do
            {
                try
                {
                    var value = await this.Socket.ReadStringAsync(cancellationToken).ConfigureAwait(false);
                    this.ProcessIncomingDeviceData(value);

                    this.firstDeviceListParsed.Set();
                }
                catch (TaskCanceledException ex)
                {
                    // We get a TaskCanceledException on Windows
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // The DeviceMonitor is shutting down (disposing) and Dispose()
                        // has called cancellationToken.Cancel(). This exception is expected,
                        // so we can safely swallow it.
                    }
                    else
                    {
                        // The exception was unexpected, so log it & rethrow.
#if !NET35 && !NET40
                        this.logger.LogError(ex, ex.Message);
#endif
                        throw;
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    // ... but an ObjectDisposedException on .NET Core on Linux and macOS.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // The DeviceMonitor is shutting down (disposing) and Dispose()
                        // has called cancellationToken.Cancel(). This exception is expected,
                        // so we can safely swallow it.
                    }
                    else
                    {
                        // The exception was unexpected, so log it & rethrow.
#if !NET35 && !NET40
                        this.logger.LogError(ex, ex.Message);
#endif
                        throw;
                    }
                }
                catch (AdbException adbException)
                {
                    if (adbException.ConnectionReset)
                    {
                        // The adb server was killed, for whatever reason. Try to restart it and recover from this.
                        AdbServer.Instance.RestartServer();
                        this.Socket.Reconnect();
                        this.InitializeSocket();
                    }
                    else
                    {
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    // The exception was unexpected, so log it & rethrow.
#if !NET35 && !NET40
                    this.logger.LogError(ex, ex.Message);
#endif
                    throw;
                }
            }
            while (!cancellationToken.IsCancellationRequested);
        }

        private void InitializeSocket()
        {
            // Set up the connection to track the list of devices.
            this.Socket.SendAdbRequest("host:track-devices");
            this.Socket.ReadAdbResponse();
        }

        /// <summary>
        /// Processes the incoming device data.
        /// </summary>
        private void ProcessIncomingDeviceData(string result)
        {
            List<DeviceData> list = new List<DeviceData>();

            string[] deviceValues = result.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            List<DeviceData> currentDevices = deviceValues.Select(d => DeviceData.CreateFromAdbData(d)).ToList();
            this.UpdateDevices(currentDevices);
        }

        private void UpdateDevices(List<DeviceData> devices)
        {
            lock (this.devices)
            {
                // For each device in the current list, we look for a matching the new list.
                // * if we find it, we update the current object with whatever new information
                //   there is
                //   (mostly state change, if the device becomes ready, we query for build info).
                //   We also remove the device from the new list to mark it as "processed"
                // * if we do not find it, we remove it from the current list.
                // Once this is done, the new list contains device we aren't monitoring yet, so we
                // add them to the list, and start monitoring them.

                // Add or update existing devices
                foreach (var device in devices)
                {
                    var existingDevice = this.Devices.SingleOrDefault(d => d.Serial == device.Serial);

                    if (existingDevice == null)
                    {
                        this.devices.Add(device);
                        this.OnDeviceConnected(new DeviceDataEventArgs(device));
                    }
                    else
                    {
                        existingDevice.State = device.State;
                        this.OnDeviceChanged(new DeviceDataEventArgs(existingDevice));
                    }
                }

                // Remove devices
                foreach (var device in this.Devices.Where(d => !devices.Any(e => e.Serial == d.Serial)).ToArray())
                {
                    this.devices.Remove(device);
                    this.OnDeviceDisconnected(new DeviceDataEventArgs(device));
                }
            }
        }
    }
}
